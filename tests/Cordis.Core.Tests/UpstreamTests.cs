using Xunit;

namespace Cordis.Core.Tests;

public sealed class UpstreamTests
{
    private sealed class Cleanup(Action action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class OriginService : Service
    {
        public OriginService(Context ctx) : base(ctx, "foo")
        {
        }

        private OriginService(OriginService provider, Context caller) : base(provider, caller)
        {
        }

        protected override Service CreateView(Context caller) => new OriginService(this, caller);
        public void Log() => Context.Logger.Debug("service");
        public EffectHandle Own(Action cleanup) => Context.Effect(() => cleanup);
        public object? ReadCounter() => Context.Reflect.Read("counter");
        public object? Associated(string member) => Associate(member);
        public IReadOnlyDictionary<string, object?> Invoke(IReadOnlyDictionary<string, object?>? head = null) => ResolveConfig(new Dictionary<string, object?> { { "a", 1 } }, head);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("explicit")]
    [InlineData("intercept")]
    [InlineData("service")]
    [InlineData("service-override")]
    [InlineData("init")]
    public async Task LoggerNames(string scenario)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var captured = new List<LogMessage>();
            ctx.Logger.Exporter(new DelegateLogExporter(captured.Add, 3));
            if (scenario == "root")
                ctx.Logger.Debug("x");
            else if (scenario == "explicit")
                ctx.Logger.Create("custom").Debug("x");
            else if (scenario == "intercept")
                ctx.Intercept("logger", new Dictionary<string, object?> { { "name", "intercepted" } }).Logger.Debug("x");
            else
            {
                await ctx.Plugin(new Plugin<object?>
                {
                    Name = "foo:driver",
                    Apply = (c, _) =>
                {
                    new OriginService(c);
                    if (scenario == "init")
                        c.Logger.Debug("x");
                }
                }).WaitAsync();
                if (scenario != "init")
                {
                    var caller = scenario == "service-override" ? ctx.Intercept("logger", new Dictionary<string, object?> { { "name", "caller-override" } }) : ctx;
                    caller.Get<OriginService>("foo")!.Log();
                }
            }

            Assert.Equal(scenario switch
            {
                "root" => "root",
                "explicit" => "custom",
                "intercept" => "intercepted",
                "service-override" => "caller-override",
                _ => "foo:driver"
            }, Assert.Single(captured).Name);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TraceableEffectsUseCallerAndReadsUseProvider(bool inject)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            ctx.Provide("counter", 42);
            int cleanup = 0;
            var provider = ctx.Plugin(new Plugin<object?> { Inject = inject ? ["counter"] : [], Apply = (c, _) => new OriginService(c) });
            await provider.WaitAsync();
            var consumer = ctx.Inject(["foo"], c =>
            {
                var service = c.Get<OriginService>("foo")!;
                service.Own(() => cleanup++);
                Assert.Equal(42, service.ReadCounter());
            });
            await consumer.WaitAsync();
            await consumer.DisposeAsync();
            Assert.Equal(1, cleanup);
            Assert.Equal(FiberState.Active, provider.State);
        });
    }

    [Fact]
    public async Task FunctionalServiceInterceptionAndHeadOverride()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new OriginService(c) }).WaitAsync();
            var service = ctx.Get<OriginService>("foo")!;
            Assert.Equal(1, service.Invoke()["a"]);
            var caller = ctx.Intercept("foo", new Dictionary<string, object?> { { "b", 2 } }).Intercept("foo", new Dictionary<string, object?> { { "a", 3 } });
            var view = caller.Get<OriginService>("foo")!;
            var config = view.Invoke(new Dictionary<string, object?> { { "c", 4 } });
            Assert.Equal(3, config["a"]);
            Assert.Equal(2, config["b"]);
            Assert.Equal(4, config["c"]);
            Assert.False(service.Invoke().ContainsKey("b"));
        });
    }

    [Fact]
    public async Task AssociatedServicesAndAccessorsUseReceiver()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new OriginService(c) }).WaitAsync();
            var registration = ctx.Provide("foo.bar", 12);
            var service = ctx.Get<OriginService>("foo")!;
            Assert.Equal(12, service.Associated("bar"));
            ctx.Reflect.Accessor("foo.answer", new((_, receiver) => ReferenceEquals(receiver, service) ? 42 : 0));
            Assert.Equal(42, service.Associated("answer"));
            await registration.DisposeAsync();
            Assert.Null(service.Associated("bar"));
        });
    }

    [Fact]
    public async Task IsolatedServiceEventsFilterListeners()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int outer = 0, inner = 0;
            ctx.On("event", (_, _) => ++outer);
            var isolated = ctx.Isolate("foo");
            isolated.On("event", (_, _) => ++inner);
            await isolated.Plugin(new Plugin<object?>
            {
                Apply = (c, _) =>
            {
                var service = new OriginService(c);
                c.Events.EmitWith(service, "event");
            }
            }).WaitAsync();
            Assert.Equal(0, outer);
            Assert.Equal(1, inner);
        });
    }

    [Fact]
    public async Task InjectMethodAdaptationRunsAndCleansWithDependencies()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0, cleanup = 0;
            await ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) => c.Inject(["foo"], nested =>
            {
                calls++;
                nested.Effect(() => (Action)(() => cleanup++));
            })
            }).WaitAsync();
            Assert.Equal(0, calls);
            var provider = ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new OriginService(c) });
            await provider.WaitAsync();
            foreach (var fiber in ctx.Registry.Values.SelectMany(v => v.Fibers).ToArray())
                await fiber.WaitAsync();
            Assert.Equal(1, calls);
            await provider.DisposeAsync();
            Assert.Equal(1, cleanup);
        });
    }

    [Fact]
    public async Task MultipleDependenciesActivateTopologically()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var calls = new List<string>();
            var foo = ctx.Plugin(new Plugin<object?>
            {
                Inject = ["qux"],
                Apply = (c, _) =>
            {
                calls.Add("foo");
                c.Provide("foo", 1);
            }
            });
            var bar = ctx.Plugin(new Plugin<object?>
            {
                Inject = ["foo", "qux"],
                Apply = (c, _) =>
            {
                calls.Add("bar");
                c.Provide("bar", 1);
            }
            });
            var qux = ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) =>
            {
                calls.Add("qux");
                c.Provide("qux", 1);
            }
            });
            await qux.WaitAsync();
            await foo.WaitAsync();
            await bar.WaitAsync();
            Assert.Equal(["qux", "foo", "bar"], calls);
        });
    }

    [Fact]
    public async Task FailedPluginReleasesEffectsWhileSiblingRemains()
    {
        var errors = new List<Exception>();
        await using var root = new Context(errors.Add);
        await root.RunAsync(async ctx =>
        {
            int calls = 0;
            var plugin = new Plugin<bool>
            {
                Apply = (c, pass) =>
                {
                    c.On("event", (_, _) => ++calls);
                    if (!pass)
                        throw new Exception("plugin error");
                }
            };
            var failed = ctx.Plugin(plugin, false);
            var good = ctx.Plugin(plugin, true);
            await Assert.ThrowsAsync<Exception>(() => failed.WaitAsync());
            await good.WaitAsync();
            Assert.Equal(FiberState.Failed, failed.State);
            Assert.Equal(FiberState.Active, good.State);
            Assert.Single(errors);
            ctx.Emit("event");
            Assert.Equal(1, calls);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncSetupCanDisposeBeforeOrAfterSettlement(bool before)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var signal = new TaskCompletionSource();
            var order = new List<int>();
            var effect = ctx.Effect(async () =>
            {
                await signal.Task;
                order.Add(1);
                return (IAsyncDisposable)new Cleanup(() => order.Add(2));
            });
            var disposal = before ? effect.DisposeAsync().AsTask() : null;
            Assert.Empty(order);
            signal.SetResult();
            await effect.Ready;
            if (disposal is null)
            {
                Assert.Equal([1], order);
                await effect.DisposeAsync();
            }
            else
                await disposal;
            Assert.Equal([1, 2], order);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncSetupErrorsRollback(bool stream)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int cleanup = 0;
            async IAsyncEnumerable<IAsyncDisposable> Generator()
            {
                yield return new Cleanup(() => cleanup++);
                await Task.Yield();
                throw new InvalidOperationException("setup");
            }

            async Task<IAsyncDisposable> Setup()
            {
                await Task.Yield();
                throw new InvalidOperationException("setup");
            }

            var effect = stream ? ctx.Effect(Generator) : ctx.Effect(Setup);
            await Assert.ThrowsAsync<InvalidOperationException>(() => effect.Ready);
            await Task.Yield();
            Assert.Equal(stream ? 1 : 0, cleanup);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task InertiaSerializesLoadAndUnload(int scenario)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var loaded = new TaskCompletionSource();
            var entered = new TaskCompletionSource();
            var clean = new TaskCompletionSource();
            var provider = ctx.Provide("foo", 1);
            int starts = 0;
            var consumer = ctx.Inject(["foo"], async c =>
            {
                starts++;
                entered.TrySetResult();
                await loaded.Task;
                c.Effect(() => new AwaitCleanup(clean.Task));
            });
            await entered.Task;
            Assert.Equal(FiberState.Loading, consumer.State);
            var remove = provider.DisposeAsync().AsTask();
            Assert.Equal(FiberState.Loading, consumer.State);
            if (scenario == 2)
                ctx.Provide("foo", 2);
            loaded.SetResult();
            if (scenario == 2)
            {
                await consumer.WaitAsync();
                Assert.Equal(FiberState.Active, consumer.State);
                Assert.Equal(1, starts);
                clean.SetResult();
                await remove;
                return;
            }

            await Task.Yield();
            Assert.Equal(FiberState.Unloading, consumer.State);
            if (scenario == 1)
                ctx.Provide("foo", 2);
            clean.SetResult();
            await remove;
            await consumer.WaitAsync();
            Assert.Equal(scenario == 1 ? FiberState.Active : FiberState.Pending, consumer.State);
            Assert.Equal(scenario == 1 ? 2 : 1, starts);
        });
    }

    private sealed class AwaitCleanup(Task task) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(task);
    }

    [Fact]
    public async Task UpdateKeepsFiberAndContextIdentity()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var configs = new List<string>();
            var fiber = ctx.Plugin(new Plugin<string> { Apply = (_, v) => configs.Add(v) }, "hello");
            var identity = fiber.Context;
            await fiber.WaitAsync();
            fiber.Update("world");
            await fiber.WaitAsync();
            fiber.Update("!!!");
            await fiber.WaitAsync();
            Assert.Equal(["hello", "world", "!!!"], configs);
            Assert.Same(identity, fiber.Context);
        });
    }

    [Fact]
    public async Task PublicationCanAddInjectOrDisposeAndTeardownObserversAreContained()
    {
        var errors = new List<Exception>();
        await using var root = new Context(errors.Add);
        await root.RunAsync(async ctx =>
        {
            int apply = 0, notified = 0;
            ctx.On("internal/plugin", (_, args) =>
            {
                var f = (Fiber)args[0]!;
                if (f.Uid is not null)
                    f.Inject["foo"] = null;
                else
                    throw new Exception("observer");
                return null;
            });
            ctx.On("internal/plugin", (_, args) =>
            {
                if (((Fiber)args[0]!).Uid is null)
                    notified++;
                return null;
            });
            var fiber = ctx.Plugin(new Plugin<object?> { Apply = (_, _) => apply++ });
            await fiber.WaitAsync();
            Assert.Equal(FiberState.Pending, fiber.State);
            ctx.Provide("foo", null);
            await fiber.WaitAsync();
            Assert.Equal(1, apply);
            await fiber.DisposeAsync();
            Assert.Equal(1, notified);
            Assert.Single(errors);
        });
    }

    [Fact]
    public async Task ReparentMovesServiceRealmWithoutReplacingContext()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var a = ctx.Isolate("foo");
            var b = ctx.Isolate("foo");
            var entry = a.Extend();
            var provider = entry.Plugin(new Plugin<object?> { Apply = (c, _) => c.Provide("foo", 42) });
            await provider.WaitAsync();
            Assert.Equal(42, a.Get("foo"));
            entry.Reparent(b);
            Assert.Null(a.Get("foo"));
            Assert.Equal(42, b.Get("foo"));
            await provider.DisposeAsync();
            Assert.Null(b.Get("foo"));
        });
    }

    [Fact]
    public async Task PluginValidationContextIdentityAndRegistrySnapshot()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            Assert.True(Context.Is(ctx));
            Assert.False(Context.Is(new object()));
            Assert.Equal("Context <root>", ctx.ToString());
            Assert.Throws<ArgumentNullException>(() => ctx.Plugin<object?>(null!));
            Assert.Throws<ArgumentException>(() => ctx.Plugin(new Plugin<object?>()));
            var before = ctx.Fiber.GetEffects().Count;
            var plugin = new Plugin<object?>
            {
                Name = "named",
                Apply = (c, _) =>
                {
                    Assert.Equal("Context <named>", c.ToString());
                    c.On("event", (_, _) => null);
                }
            };
            var fiber = ctx.Plugin(plugin);
            await fiber.WaitAsync();
            Assert.Single(ctx.Registry.Values);
            Assert.Single(fiber.GetEffects());
            await ctx.Registry.DeleteAsync(plugin);
            Assert.Empty(ctx.Registry.Values);
            Assert.Equal(before, ctx.Fiber.GetEffects().Count);
            var next = ctx.Plugin(plugin);
            await next.WaitAsync();
            Assert.Single(next.GetEffects());
        });
    }

    [Fact]
    public async Task ExplicitInstanceInitializationOwnsReturnedCleanup()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int start = 0, stop = 0;
            var fiber = ctx.Plugin(new Plugin<object?>
            {
                ApplyEffect = (_, _) =>
            {
                start++;
                return new Cleanup(() => stop++);
            }
            });
            await fiber.WaitAsync();
            Assert.Equal(1, start);
            Assert.Equal(0, stop);
            await fiber.DisposeAsync();
            Assert.Equal(1, start);
            Assert.Equal(1, stop);
        });
    }

    [Fact]
    public async Task AsyncGeneratorAbortAfterFirstYieldCollectsInFlightYield()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var first = new TaskCompletionSource();
            var second = new TaskCompletionSource();
            var entered = new TaskCompletionSource();
            var order = new List<int>();
            async IAsyncEnumerable<IAsyncDisposable> Generate()
            {
                await first.Task;
                order.Add(1);
                yield return new Cleanup(() => order.Add(2));
                entered.SetResult();
                await second.Task;
                order.Add(3);
                yield return new Cleanup(() => order.Add(4));
                order.Add(5);
                yield return new Cleanup(() => order.Add(6));
            }

            var effect = ctx.Effect(Generate);
            first.SetResult();
            await entered.Task;
            Assert.Equal([1], order);
            var disposal = effect.DisposeAsync().AsTask();
            second.SetResult();
            await disposal;
            Assert.Equal([1, 3, 4, 2], order);
        });
    }

    [Fact]
    public async Task LoggerFormattingPortableDataAndSeverity()
    {
        await using var root = new Context();
        await root.RunAsync(ctx =>
        {
            var records = new List<LogMessage>();
            ctx.Logger.Exporter(new DelegateLogExporter(records.Add, 3));
            ctx.Logger.Info("%o %d %f %% %q", new Dictionary<string, object?> { { "a", 1 }, { "missing", Undefined.Value } }, Undefined.Value, "not-number");
            Assert.Equal("{\"a\":1} NaN NaN % %q", Logger.Format(records[0]));
            ctx.Logger.Info(new Dictionary<string, object?> { { "a", false } });
            Assert.Equal("{\"a\":false}", Logger.Format(records[1]));
            ctx.Logger.Info("%z", 12);
            Assert.Equal("value=12", Logger.Format(records[2], formatters: new Dictionary<char, Func<object?, string>> { { 'z', x => "value=" + x } }));
            Assert.Equal("val...", Logger.Format(records[2], 3, new Dictionary<char, Func<object?, string>> { { 'z', x => "value=" + x } }));
            int info = 0;
            ctx.Logger.Exporter(new DelegateLogExporter(_ => info++));
            ctx.Logger.Warn("warn");
            ctx.Logger.Info("info");
            Assert.Equal(1, info);
            var cycle = new List<object?>();
            cycle.Add(cycle);
            Assert.Throws<InvalidOperationException>(() => Logger.FormatData(cycle));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EventsTraceServiceArgumentsToListenerOwner()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int cleanup = 0;
            await ctx.Plugin(new Plugin<object?> { Apply = (c, _) => new OriginService(c) }).WaitAsync();
            var listener = ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) => c.On("use", (e, args) =>
            {
                ((OriginService)args[0]!).Own(() => cleanup++);
                ((OriginService)e.Receiver!).Own(() => cleanup++);
                return null;
            })
            });
            await listener.WaitAsync();
            var service = ctx.Get<OriginService>("foo")!;
            ctx.Events.EmitWith(service, "use", service);
            Assert.Equal(0, cleanup);
            await listener.DisposeAsync();
            Assert.Equal(2, cleanup);
            Assert.NotNull(ctx.Get("foo"));
        });
    }

    [Fact]
    public async Task ServiceNotificationsRespectIsolationAndGlobalOption()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int outer = 0, inner = 0, global = 0;
            var isolated = ctx.Isolate("foo");
            ctx.On("internal/service", (_, _) => ++outer);
            isolated.On("internal/service", (_, _) => ++inner);
            ctx.On("internal/service", (_, _) => ++global, new(Global: true));
            var registration = isolated.Provide("foo", 42);
            Assert.Equal(0, outer);
            Assert.Equal(1, inner);
            Assert.Equal(1, global);
            await registration.DisposeAsync();
            Assert.Equal(0, outer);
            Assert.Equal(2, inner);
            Assert.Equal(2, global);
        });
    }

    [Fact]
    public async Task DependencySnapshotSurvivesRealmChangeUntilCleanupCompletes()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var a = ctx.Isolate("bar");
            var b = ctx.Isolate("bar");
            var pa = a.Plugin(new Plugin<object?> { Apply = (c, _) => c.Provide("bar", "alpha") });
            var pb = b.Plugin(new Plugin<object?> { Apply = (c, _) => c.Provide("bar", "beta") });
            await pa.WaitAsync();
            await pb.WaitAsync();
            var scope = a.Extend();
            var clean = new TaskCompletionSource();
            var entered = new TaskCompletionSource();
            var values = new List<string>();
            var consumer = scope.Inject(["bar"], c =>
            {
                values.Add((string)c.Reflect.Read("bar")!);
                c.Effect(() => new VerifyCleanup(async () =>
                {
                    entered.TrySetResult();
                    await clean.Task;
                    values.Add((string)c.Reflect.Read("bar")!);
                }));
            });
            await consumer.WaitAsync();
            scope.Reparent(b);
            await entered.Task;
            Assert.Equal(FiberState.Unloading, consumer.State);
            clean.SetResult();
            await consumer.WaitAsync();
            Assert.Equal(["alpha", "alpha", "beta"], values);
        });
    }

    private sealed class VerifyCleanup(Func<Task> cleanup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(cleanup());
    }

    [Fact]
    public async Task TrackedNestedAndReturnedObjectsAreBoundToReader()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var nested = new TrackedObject(ctx, "child");
            nested.Set("identity", new TrackedCallback((receiver, _) => receiver));
            var outer = new TrackedObject(ctx, "parent");
            outer.Set("nested", nested);
            outer.Set("factory", new TrackedCallback((_, _) => nested));
            var scope = ctx.Extend();
            scope.Metadata["scope"] = 42;
            scope.Reflect.Accessor("child.caller", new((caller, _) => caller.FindMetadata("scope")));
            var view = (TrackedObject)outer.ForContext(scope);
            Assert.Equal(42, ((TrackedObject)view.Get("nested")!).Get("caller"));
            Assert.Equal(42, ((TrackedObject)view.Call("factory")!).Get("caller"));
            Assert.IsType<TrackedObject>(nested.Call("identity"));
            await Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LocalUpdateHooksPersistAndExplicitDisposalUnregisters()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var sequence = new List<string>();
            var handles = new List<EffectHandle>();
            var fiber = ctx.Plugin(new Plugin<int>
            {
                Apply = (owner, value) =>
            {
                sequence.Add($"apply{value}");
                handles.Add(owner.On("internal/update", (evt, _) =>
                {
                    sequence.Add($"hook{value}");
                    return evt.Next();
                }));
            }
            }, 1);
            await fiber.WaitAsync();
            fiber.Update(2);
            await fiber.WaitAsync();
            fiber.Update(3);
            await fiber.WaitAsync();
            Assert.Equal(["apply1", "hook1", "apply2", "hook1", "hook2", "apply3"], sequence);
            await handles[0].DisposeAsync();
            sequence.Clear();
            fiber.Update(4);
            await fiber.WaitAsync();
            Assert.Equal(["hook2", "hook3", "apply4"], sequence);
            Assert.False(ctx.Events.ListenerCounts.ContainsKey("internal/update"));
        });
    }

    [Fact]
    public async Task DisposedFiberLocalHooksAreNotRootedByEventBus()
    {
        await using var root = new Context();
        var weak = await RegisterAndDispose(root);
        await root.RunAsync(_ => Task.CompletedTask);
        for (int attempt = 0; attempt < 8 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.False(weak.IsAlive);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RegisterAndDispose(Context root)
    {
        WeakReference result = null!;
        await root.RunAsync(async ctx =>
        {
            var fiber = ctx.Plugin(new Plugin<object?> { Apply = (owner, _) => owner.On("internal/update", (evt, _) => evt.Next()) });
            await fiber.WaitAsync();
            result = new WeakReference(fiber);
            await fiber.DisposeAsync();
        });
        return result;
    }

    [Fact]
    public async Task HostOwnedLogSubscriptionObservesAsyncRootTeardown()
    {
        var root = new Context();
        var messages = new List<LogMessage>();
        LogSubscription subscription = null!;
        await root.RunAsync(ctx =>
        {
            subscription = ctx.Logger.Subscribe(new DelegateLogExporter(messages.Add, 3));
            ctx.Effect(() => new VerifyCleanup(async () =>
            {
                await Task.Yield();
                ctx.Logger.Warn("teardown");
            }));
            return Task.CompletedTask;
        });
        await root.DisposeAsync();
        Assert.Equal("teardown", Assert.Single(messages).Arguments[0]);
        await subscription.DisposeAsync();
        await subscription.DisposeAsync();
    }
}
