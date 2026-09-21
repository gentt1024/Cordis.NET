using Xunit;

namespace Cordis.Core.Tests;

public sealed class CoreTests
{
    private sealed class Cleanup(Action action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AsyncCleanup(Func<Task> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(action());
    }

    [Theory]
    [InlineData("functional")]
    [InlineData("object")]
    public async Task PluginConfigIdentity(string shape)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var config = new object();
            int calls = 0;
            IPlugin plugin = new Plugin<object>
            {
                Name = shape,
                Apply = (_, actual) =>
                {
                    calls++;
                    Assert.Same(config, actual);
                }
            };
            var fiber = ctx.Plugin(plugin, config);
            await fiber.WaitAsync();
            Assert.Equal(1, calls);
            Assert.Same(config, fiber.RawConfig);
        });
    }

    [Fact]
    public async Task RegistryIdentityNestedOwnershipAndRepeatedDispose()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0;
            ctx.On("event", (_, _) => ++calls);
            var plugin = new Plugin<object?>
            {
                Name = "same",
                ApplyAsync = async (c, _) =>
                {
                    c.On("event", (_, _) => ++calls);
                    await c.Plugin(new Plugin<object?> { Name = "same", Apply = (child, _) => child.On("event", (_, _) => ++calls) }).WaitAsync();
                }
            };
            var fiber = ctx.Plugin(plugin);
            await fiber.WaitAsync();
            Assert.Equal(2, ctx.Registry.Count);
            ctx.Emit("event");
            Assert.Equal(3, calls);
            await fiber.DisposeAsync();
            await fiber.DisposeAsync();
            Assert.Equal(0, ctx.Registry.Count);
            ctx.Emit("event");
            Assert.Equal(4, calls);
            var one = ctx.Plugin(plugin);
            var two = ctx.Plugin(plugin);
            await Task.WhenAll(one.WaitAsync(), two.WaitAsync());
            Assert.Equal(2, ctx.Registry.Get(plugin)!.Fibers.Count);
            Assert.True(await ctx.Registry.DeleteAsync(plugin));
            Assert.False(ctx.Registry.Has(plugin));
        });
    }

    [Fact]
    public async Task ImmediateDisposeSkipsApplyAndPendingEffectsDrain()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0, disposed = 0;
            ctx.On("internal/plugin", (_, args) =>
            {
                var f = (Fiber)args[0]!;
                if (f.Uid is not null)
                    f.Context.Effect(() => (Action)(() => disposed++));
                return null;
            });
            var fiber = ctx.Plugin(new Plugin<object?> { Apply = (_, _) => calls++ });
            await fiber.DisposeAsync();
            Assert.Equal(0, calls);
            Assert.Equal(1, disposed);
            Assert.Equal(FiberState.Disposed, fiber.State);
            var pending = ctx.Inject(["missing"], _ => calls++);
            await pending.WaitAsync();
            Assert.Equal(FiberState.Pending, pending.State);
            await pending.DisposeAsync();
            Assert.Equal(2, disposed);
        });
    }

    [Fact]
    public async Task RootFiberRestartsAndHostDisposalIsPermanent()
    {
        var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int cleanup = 0;
            var fiber = ctx.Plugin(new Plugin<object?> { Apply = (c, _) => c.Effect(() => (Action)(() => cleanup++)) });
            await fiber.WaitAsync();
            await ctx.Fiber.DisposeAsync();
            Assert.Equal(1, cleanup);
            Assert.Equal(0, ctx.Fiber.Uid);
            Assert.Null(fiber.Uid);
            var fresh = ctx.Plugin(new Plugin<object?>
            {
                Apply = (_, _) =>
            {
            }
            });
            await fresh.WaitAsync();
            Assert.Equal(FiberState.Active, fresh.State);
        });
        await root.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => root.RunAsync(_ => Task.CompletedTask));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolationSeparatesRealmsAndSharedLabelsJoin(bool share)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0, cleanup = 0;
            var plugin = new Plugin<object?>
            {
                Inject = ["foo"],
                Apply = (c, _) =>
                {
                    calls++;
                    c.Effect(() => (Action)(() => cleanup++));
                }
            };
            object label = new();
            var a = ctx.Isolate("foo", label);
            var b = ctx.Isolate("foo", share ? label : new object());
            var f0 = ctx.Plugin(plugin);
            var f1 = a.Plugin(plugin);
            var f2 = b.Plugin(plugin);
            ctx.Provide("foo", 100);
            await f0.WaitAsync();
            await f1.WaitAsync();
            Assert.Equal(1, calls);
            Assert.Null(a.Get("foo"));
            var registration = a.Provide("foo", 200);
            await f1.WaitAsync();
            await f2.WaitAsync();
            Assert.Equal(share ? 3 : 2, calls);
            Assert.Equal(share ? 200 : null, b.Get("foo"));
            await registration.DisposeAsync();
            Assert.Equal(share ? 2 : 1, cleanup);
            Assert.Equal(100, ctx.Get("foo"));
        });
    }

    [Fact]
    public async Task ProviderReadinessReplacementAndRawConfigResolution()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var ready = new TaskCompletionSource();
            var entered = new TaskCompletionSource();
            var values = new List<int>();
            object raw = new();
            ctx.On("internal/config", (e, args) => e.Receiver is Fiber f && f.Name == "consumer" ? f.Context.Get<int>("value") : e.Next(), new(Global: true));
            var consumer = ctx.Plugin(new Plugin<int> { Name = "consumer", Inject = ["value"], Apply = (_, v) => values.Add(v) }, raw);
            await consumer.WaitAsync();
            Assert.Equal(FiberState.Pending, consumer.State);
            var provider = ctx.Plugin(new Plugin<object?>
            {
                ApplyAsync = async (c, _) =>
            {
                c.Provide("value", 1);
                entered.SetResult();
                await ready.Task;
            }
            });
            await entered.Task;
            Assert.Null(ctx.Get("value"));
            Assert.Equal(1, ctx.Get("value", false));
            Assert.Empty(values);
            ready.SetResult();
            await provider.WaitAsync();
            await consumer.WaitAsync();
            Assert.Equal([1], values);
            await provider.DisposeAsync();
            Assert.Equal(FiberState.Pending, consumer.State);
            var next = ctx.Plugin(new Plugin<object?> { Apply = (c, _) => c.Provide("value", 2) });
            await next.WaitAsync();
            await consumer.WaitAsync();
            Assert.Equal([1, 2], values);
            Assert.Same(raw, consumer.RawConfig);
        });
    }

    [Fact]
    public async Task AvailabilityCheckAndSelfAccess()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            bool ready = false;
            ctx.Provide("foo", 42, () => ready);
            int calls = 0;
            var dependent = ctx.Inject(["foo"], c =>
            {
                calls++;
                Assert.Equal(42, c.Reflect.Read("foo"));
            });
            await dependent.WaitAsync();
            Assert.Equal(0, calls);
            ready = true;
            ctx.Reflect.Notify("foo");
            await dependent.WaitAsync();
            Assert.Equal(1, calls);
            var provider = ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) =>
            {
                c.Provide("own", 7);
                Assert.Equal(7, c.Reflect.Read("own"));
                Assert.Null(c.Get("own"));
            }
            });
            await provider.WaitAsync();
            await dependent.DisposeAsync();
            Assert.Throws<InvalidOperationException>(() => dependent.Context.Reflect.Read("foo"));
        });
    }

    [Fact]
    public async Task ConfigFailureAndUpdateVetoPreserveDistinctRawAndResolved()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var values = new List<int>();
            var fiber = ctx.Plugin(new Plugin<int>
            {
                Config = x => x is int n && n >= 0 ? ConfigResult<int>.Success(n) : ConfigResult<int>.Failure("negative"),
                Apply = (c, n) =>
            {
                values.Add(n);
                c.On("internal/update", (e, args) => (int)args[0]! == 9 ? null : e.Next());
            }
            }, 1);
            await fiber.WaitAsync();
            Assert.Throws<ConfigurationValidationException>(() => fiber.Update(-1));
            Assert.Equal(-1, fiber.RawConfig);
            Assert.Equal(1, fiber.Config);
            fiber.Update(9);
            await fiber.WaitAsync();
            Assert.Equal(9, fiber.RawConfig);
            Assert.Equal(1, fiber.Config);
            Assert.Equal([1], values);
            fiber.Update(2);
            await fiber.WaitAsync();
            Assert.Equal([1, 2], values);
        });
    }

    [Theory]
    [InlineData("on")]
    [InlineData("once")]
    public async Task ListenerOwnership(string kind)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0;
            var handle = kind == "on" ? ctx.On("event", (_, _) => ++calls) : ctx.Once("event", (_, _) => ++calls);
            ctx.Emit("event");
            ctx.Emit("event");
            Assert.Equal(kind == "on" ? 2 : 1, calls);
            await handle.DisposeAsync();
            ctx.Emit("event");
            Assert.Equal(kind == "on" ? 2 : 1, calls);
        });
    }

    [Theory]
    [InlineData("emit")]
    [InlineData("parallel")]
    [InlineData("serial")]
    [InlineData("bail")]
    public async Task EventFiltersAndReceiver(string mode)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0;
            var target = ctx.Extend();
            target.Metadata["selected"] = true;
            target.On("event", (e, _) =>
            {
                calls++;
                Assert.NotNull(e.Receiver);
                return null;
            });
            var no = ctx.Extend();
            no.Filter = _ => false;
            var yes = ctx.Extend();
            yes.Filter = c => c.Metadata.ContainsKey("selected");
            async Task Send(Context receiver)
            {
                switch (mode)
                {
                    case "emit":
                        ctx.Events.EmitWith(receiver, "event");
                        break;
                    case "parallel":
                        await ctx.Events.ParallelWithAsync(receiver, "event");
                        break;
                    case "serial":
                        await ctx.Events.SerialWithAsync(receiver, "event");
                        break;
                    default:
                        ctx.Events.BailWith(receiver, "event");
                        break;
                }
            }

            await Send(no);
            Assert.Equal(0, calls);
            await Send(yes);
            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public async Task ParallelWaitsForAllFailures()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            bool settled = false;
            ctx.On("event", (_, _) => throw new InvalidOperationException("sync"));
            ctx.On("event", (_, _) => Work());
            async Task Work()
            {
                await Task.Yield();
                settled = true;
                throw new ArgumentException("async");
            }

            var error = await Assert.ThrowsAsync<AggregateException>(() => ctx.ParallelAsync("event"));
            Assert.True(settled);
            Assert.Equal(2, error.InnerExceptions.Count);
        });
    }

    [Theory]
    [InlineData("serial")]
    [InlineData("bail")]
    public async Task BailDistinguishesFalseNullUndefinedAndZero(string mode)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0;
            foreach (var value in new object?[]
            {
                null,
                false,
                Undefined.Value,
                0,
                42
            }

            )
                ctx.On("event", (_, _) =>
                {
                    calls++;
                    return value;
                });
            var result = mode == "serial" ? await ctx.SerialAsync("event") : ctx.Bail("event");
            Assert.Equal(0, result);
            Assert.Equal(4, calls);
        });
    }

    [Fact]
    public async Task WaterfallComposesAndVetoes()
    {
        await using var root = new Context();
        await root.RunAsync(ctx =>
        {
            ctx.On("event", (e, args) => (int)args[0]! + (int)e.Next()!);
            ctx.On("event", (e, args) => (int)args[0]! + (int)e.Next()!);
            Assert.Equal(4, ctx.Waterfall("event", () => 2, 1));
            ctx.On("event", (_, args) => args[0]);
            ctx.On("event", (_, _) => throw new Exception("unreachable"));
            Assert.Equal(3, ctx.Waterfall("event", () => 2, 1));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EffectGroupsAdoptReverseOrderAndRollback()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var order = new List<int>();
            var outer = ctx.Effect(() => new IAsyncDisposable[] { new Cleanup(() => order.Add(1)), ctx.Effect(() => (Action)(() => order.Add(2))), new Cleanup(() => order.Add(3)) });
            Assert.Single(ctx.Fiber.GetEffects());
            Assert.Single(ctx.Fiber.GetEffects()[0].Children);
            await outer.DisposeAsync();
            Assert.Equal([3, 2, 1], order);
            IEnumerable<IAsyncDisposable> Broken()
            {
                yield return new Cleanup(() => order.Add(4));
                throw new Exception("setup");
            }

            Assert.Throws<Exception>(() => ctx.Effect(Broken));
            Assert.Equal([3, 2, 1, 4], order);
        });
    }

    [Fact]
    public async Task ReentrantSetupDisposalWaitsAndRejectsCleanupEffects()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            Task? disposal = null;
            int cleanup = 0;
            var fiber = ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) => c.Effect(() =>
            {
                disposal = c.Fiber.DisposeAsync().AsTask();
                return (Action)(() =>
                {
                    cleanup++;
                    Assert.Throws<CordisException>(() => c.Effect(() => (Action)(() =>
                    {
                    })));
                });
            })
            });
            await fiber.WaitAsync();
            await disposal!;
            Assert.Equal(1, cleanup);
            Assert.Equal(FiberState.Disposed, fiber.State);
        });
    }

    [Fact]
    public async Task AsyncEffectPublicRepeatAndOwnerJoin()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var entered = new TaskCompletionSource();
            var release = new TaskCompletionSource();
            EffectHandle? effect = null;
            var fiber = ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) => effect = c.Effect(() => new AsyncCleanup(async () =>
            {
                entered.SetResult();
                await release.Task;
            }))
            });
            await fiber.WaitAsync();
            var first = effect!.DisposeAsync().AsTask();
            await entered.Task;
            await effect.DisposeAsync();
            var owner = fiber.DisposeAsync().AsTask();
            Assert.False(owner.IsCompleted);
            release.SetResult();
            await Task.WhenAll(first, owner);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncGeneratorCleanupAndAbort(bool abort)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var order = new List<int>();
            var release = new TaskCompletionSource();
            var started = new TaskCompletionSource();
            async IAsyncEnumerable<IAsyncDisposable> Generate()
            {
                started.SetResult();
                await release.Task;
                order.Add(1);
                yield return new Cleanup(() => order.Add(2));
                order.Add(3);
                yield return new Cleanup(() => order.Add(4));
            }

            var handle = ctx.Effect(Generate);
            await started.Task;
            Task? disposed = abort ? handle.DisposeAsync().AsTask() : null;
            release.SetResult();
            await handle.Ready;
            if (disposed is null)
                await handle.DisposeAsync();
            else
                await disposed;
            Assert.Equal(abort ? [1, 2] : [1, 3, 4, 2], order);
        });
    }

    [Fact]
    public async Task SiblingCleanupFailuresDoNotStarveOthers()
    {
        var errors = new List<Exception>();
        await using var root = new Context(errors.Add);
        await root.RunAsync(async ctx =>
        {
            int cleanup = 0;
            var fiber = ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) =>
            {
                c.Effect(() => (Action)(() => cleanup++));
                c.Effect(() => (Action)(() => throw new Exception("cleanup")));
            }
            });
            await fiber.WaitAsync();
            await fiber.DisposeAsync();
            Assert.Equal(1, cleanup);
            Assert.Single(errors);
        });
    }

    [Fact]
    public async Task LoggerExporterDisposalRetainsLaterRegistrations()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var a = new List<LogMessage>();
            var b = new List<LogMessage>();
            var first = ctx.Logger.Exporter(new DelegateLogExporter(a.Add, 3));
            ctx.Logger.Exporter(new DelegateLogExporter(b.Add, 3));
            await first.DisposeAsync();
            ctx.Logger.Debug("root");
            ctx.Logger.Create("custom").Debug("%s %d", "hello", 2.7);
            ctx.Intercept("logger", new Dictionary<string, object?> { { "name", "outer" } }).Intercept("logger", new Dictionary<string, object?> { { "name", "inner" } }).Logger.Debug("x");
            Assert.Empty(a);
            Assert.Equal(new[] { "root", "custom", "inner" }, b.Select(m => m.Name));
            Assert.Equal("hello 2", Logger.Format(b[1]));
        });
    }

    [Fact]
    public async Task AccessorAndExplicitLookupRemainSeparate()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int value = 1;
            ctx.Reflect.Accessor("alias", new((_, _) => value, (_, v, _) =>
            {
                value = (int)v!;
                return true;
            }));
            Assert.True(ctx.Reflect.Has("alias"));
            Assert.Null(ctx.Get("alias"));
            Assert.Equal(1, ctx.Reflect.Read("alias"));
            ctx.Reflect.Write("alias", 2);
            Assert.Equal(2, ctx.Reflect.Read("alias"));
            await ctx.Plugin(new Plugin<object?>
            {
                Apply = (c, _) =>
            {
                Assert.Throws<InvalidOperationException>(() => c.Reflect.Read("missing"));
                Assert.Throws<InvalidOperationException>(() => c.Set("missing", 0));
                c.Provide("own", 1);
                Assert.Throws<InvalidOperationException>(() => c.Provide("own", 2));
                c.Set("own", 3);
                Assert.Equal(3, c.Reflect.Read("own"));
            }
            }).WaitAsync();
        });
    }

    [Fact]
    public async Task PropertyWritesUseInternalSetWaterfallWhileDirectSetBypassesIt()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            ctx.Provide("value", 1);
            ctx.Filter = _ => false;
            var calls = 0;
            var allow = ctx.On("internal/set", (evt, args) =>
            {
                calls++;
                Assert.Null(evt.Receiver);
                Assert.Same(ctx, args[0]);
                Assert.Equal("value", args[1]);
                Assert.Equal(2, args[2]);
                Assert.IsType<InvalidOperationException>(args[3]);
                return evt.Next();
            });

            ctx.Reflect.Write("value", 2);
            Assert.Equal(2, ctx.Get<int>("value"));
            Assert.Equal(1, calls);
            ctx.Set("value", 3);
            Assert.Equal(3, ctx.Get<int>("value"));
            Assert.Equal(1, calls);
            await allow.DisposeAsync();

            var veto = ctx.On("internal/set", (_, _) => Undefined.Value);
            ctx.Reflect.Write("value", 4);
            Assert.Equal(3, ctx.Get<int>("value"));
            await veto.DisposeAsync();

            var failure = new InvalidOperationException("interceptor failed");
            ctx.On("internal/set", (_, _) => throw failure);
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => ctx.Reflect.Write("value", 5)));
            Assert.Equal(3, ctx.Get<int>("value"));
        });
    }

    [Fact]
    public async Task DuplicateInjectNamesNormalizeBeforeObjectOverridesAndLifecycle()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var applies = 0;
            var disposes = 0;
            var intercept = new Dictionary<string, object?> { ["mode"] = "override" };
            var plugin = new Plugin<object?>
            {
                Inject = ["messages", "messages"],
                InjectConfig = new Dictionary<string, object?> { ["messages"] = intercept },
                Apply = (child, _) =>
                {
                    applies++;
                    child.Effect(() => (Action)(() => disposes++));
                }
            };

            var dependency = Assert.Single(((IPlugin)plugin).Dependencies);
            Assert.Equal("messages", dependency.Key);
            Assert.Same(intercept, dependency.Value);

            var consumer = ctx.Plugin(plugin);
            await consumer.WaitAsync();
            Assert.Equal(FiberState.Pending, consumer.State);
            Assert.Equal(0, applies);

            var provider = ctx.Provide("messages", new object());
            await consumer.WaitAsync();
            Assert.Equal(FiberState.Active, consumer.State);
            Assert.Equal(1, applies);

            await provider.DisposeAsync();
            await consumer.WaitAsync();
            Assert.Equal(FiberState.Pending, consumer.State);
            Assert.Equal(1, applies);
            Assert.Equal(1, disposes);
        });
    }

    [Theory]
    [InlineData("emit")]
    [InlineData("parallel")]
    [InlineData("serial")]
    [InlineData("bail")]
    public async Task OriginalEventDispatchFilteringAndFailure(string mode)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            async Task Send(object? receiver)
            {
                switch (mode)
                {
                    case "emit":
                        ctx.Events.EmitWith(receiver, "event");
                        break;
                    case "parallel":
                        await ctx.Events.ParallelWithAsync(receiver, "event");
                        break;
                    case "serial":
                        await ctx.Events.SerialWithAsync(receiver, "event");
                        break;
                    default:
                        ctx.Events.BailWith(receiver, "event");
                        break;
                }
            }

            await Send(null);
            int calls = 0;
            bool fail = false;
            var listener = ctx.Extend();
            listener.Metadata["selected"] = true;
            listener.On("event", (_, _) =>
            {
                if (fail)
                    throw new InvalidOperationException("test");
                calls++;
                return null;
            });
            await Send(null);
            Assert.Equal(1, calls);
            var no = ctx.Extend();
            no.Filter = _ => false;
            await Send(no);
            Assert.Equal(1, calls);
            var yes = ctx.Extend();
            yes.Filter = c => c.Metadata.ContainsKey("selected");
            await Send(yes);
            Assert.Equal(2, calls);
            fail = true;
            if (mode == "parallel")
                await Assert.ThrowsAsync<AggregateException>(() => Send(null));
            else if (mode == "serial")
                await Assert.ThrowsAsync<InvalidOperationException>(() => Send(null));
            else if (mode == "emit")
                Assert.Throws<InvalidOperationException>(() => ctx.Emit("event"));
            else
                Assert.Throws<InvalidOperationException>(() => ctx.Bail("event"));
        });
    }
}
