using Cordis;

namespace Cordis.Conformance;

public static partial class Scenarios
{
    public static IReadOnlyList<(string Id, Func<Task<string[]>> Run)> Cases { get; } =
    [
        ("C01-greeting-lifecycle", GreetingLifecycle),
        ("C02-effect-immediate-single-shot", ImmediateEffect),
        ("C03-reentrant-owner-disposal", ReentrantDisposal),
        ("C04-pending-owned-effect", PendingEffect),
        ("C05-dispose-before-load-checkpoint", BeforeCheckpoint),
        ("C06-async-setup-and-cleanup", AsyncSetup),
        ("C07-owner-joins-started-cleanup", OwnerJoins),
        ("C08-nested-effect-reverse-order", NestedEffects),
        ("C09-generator-setup-rollback", SetupRollback),
        ("C10-failed-group-does-not-starve-sibling", CleanupFailure),
        ("C11-reject-effect-during-unload", RejectDuringUnload),
        ("C12-apply-failure-cleanup", ApplyFailure),
        ("C13-loading-provider-invisible", ProviderVisibility),
        ("C14-inject-child-not-parent-restart", InjectChild),
        ("C15-pending-and-active-config-update", ConfigUpdate),
        ("C16-multiple-dependencies", MultipleDependencies),
        ("C17-owner-joins-disposing-child", DisposingChild),
        ("C18-stable-context-on-restart", StableContext),
        ("U01-effects-dispose-by-plugin", UpstreamDisposeByPlugin),
        ("U02-effects-dispose-manually", UpstreamDisposeManually),
        ("U03-effects-return-with-error", UpstreamReturnWithError),
        ("U04-effects-yield-with-error", UpstreamYieldWithError),
        ("U05-effects-async-return-with-error", UpstreamAsyncReturnWithError),
    ];

    private static Plugin<object?> Plugin(Action<Context> apply, params string[] inject) => new()
    {
        Inject = inject,
        Apply = (ctx, _) => apply(ctx),
    };

    private static async Task<string[]> InContext(Func<Context, List<string>, Task> run)
    {
        var trace = new List<string>();
        await using var root = new Context();
        await root.RunAsync(ctx => run(ctx, trace));
        return trace.ToArray();
    }

    private static Task<string[]> GreetingLifecycle() => InContext(async (ctx, log) =>
    {
        var a = new MessageSource();
        var b = new MessageSource();
        var plugin = new Plugin<string>
        {
            Inject = ["messages"],
            Apply = (c, prefix) =>
            {
                MessageSource source = c.Get<MessageSource>("messages")!;
                c.Effect(() => source.Subscribe(name => log.Add($"{prefix}, {name}!")));
            },
        };
        Fiber fiber = ctx.Plugin(plugin, "Hello");
        await fiber.WaitAsync();
        Equal(FiberState.Pending, fiber.State);
        log.Add("pending");
        EffectHandle first = ctx.Provide("messages", a);
        await fiber.WaitAsync();
        Equal(1, a.Count);
        a.Emit("Otto");
        fiber.Update("Hi");
        await fiber.WaitAsync();
        Equal(1, a.Count);
        a.Emit("Otto");
        await first.DisposeAsync();
        Equal(0, a.Count);
        Equal(FiberState.Pending, fiber.State);
        log.Add("provider-removed");
        EffectHandle second = ctx.Provide("messages", b);
        await fiber.WaitAsync();
        b.Emit("Otto");
        await fiber.DisposeAsync();
        Equal(0, b.Count);
        b.Emit("ignored");
        await second.DisposeAsync();
        log.Add("disposed");
        Equal("Hello, Otto!|Hi, Otto!", string.Join('|', log.Where(s => s.EndsWith('!')).Take(2)));
    });

    private static Task<string[]> ImmediateEffect() => InContext(async (ctx, log) =>
    {
        EffectHandle effect = ctx.Effect(() =>
        {
            log.Add("setup");
            return () => log.Add("cleanup");
        });
        log.Add("returned");
        await effect.Ready;
        await effect.DisposeAsync();
        await effect.DisposeAsync();
        log.Add("disposed-twice");
        Equal("setup|returned|cleanup|disposed-twice", string.Join('|', log));
    });

    private static Task<string[]> ReentrantDisposal() => InContext(async (ctx, log) =>
    {
        Task? stopping = null;
        ctx.Effect(() =>
        {
            log.Add("setup-start");
            stopping = ctx.Fiber.DisposeAsync().AsTask();
            log.Add("setup-end");
            return () => log.Add("cleanup");
        });
        log.Add("returned");
        await stopping!;
        Equal(FiberState.Active, ctx.Fiber.State); // Root disposal is restart, not deletion.
        log.Add("root-active");
    });

    private static Task<string[]> PendingEffect() => InContext(async (ctx, log) =>
    {
        Fiber fiber = ctx.Plugin(Plugin(_ => throw new Exception("must not apply"), "absent"));
        fiber.Context.Effect(() => { log.Add("setup"); return () => log.Add("cleanup"); });
        await fiber.WaitAsync();
        Equal(FiberState.Pending, fiber.State);
        await fiber.DisposeAsync();
        Equal(FiberState.Disposed, fiber.State);
        log.Add("disposed");
    });

    private static Task<string[]> BeforeCheckpoint() => InContext(async (ctx, log) =>
    {
        Fiber fiber = ctx.Plugin(Plugin(_ => log.Add("must-not-apply")));
        await fiber.DisposeAsync();
        Equal(FiberState.Disposed, fiber.State);
        Equal(0, log.Count);
        log.Add("disposed-before-apply");
    });

    private static Task<string[]> AsyncSetup() => InContext(async (ctx, log) =>
    {
        var setup = Gate();
        var cleanup = Gate();
        var cleanupEntered = Gate();
        EffectHandle effect = ctx.Effect(async () =>
        {
            log.Add("setup-start");
            await setup.Task;
            log.Add("setup-end");
            return (IAsyncDisposable)new AsyncCallback(async () =>
            {
                log.Add("cleanup-start");
                cleanupEntered.SetResult();
                await cleanup.Task;
                log.Add("cleanup-end");
            });
        });
        try
        {
            Task stopping = ctx.Fiber.DisposeAsync().AsTask();
            Equal(false, stopping.IsCompleted);
            log.Add("waits-for-setup");
            setup.SetResult();
            await cleanupEntered.Task;
            Equal(false, stopping.IsCompleted);
            log.Add("waits-for-cleanup");
            cleanup.SetResult();
            await stopping;
            await effect.Ready;
            log.Add("stopped");
        }
        finally
        {
            setup.TrySetResult();
            cleanup.TrySetResult();
        }
    });

    private static Task<string[]> OwnerJoins() => InContext(async (ctx, log) =>
    {
        var entered = Gate();
        var release = Gate();
        EffectHandle effect = ctx.Effect(() => new AsyncCallback(async () =>
        {
            log.Add("cleanup-start");
            entered.SetResult();
            await release.Task;
            log.Add("cleanup-end");
        }));
        try
        {
            Task first = effect.DisposeAsync().AsTask();
            await entered.Task;
            await effect.DisposeAsync();
            log.Add("second-public-call-returned");
            Task stop = ctx.Fiber.DisposeAsync().AsTask();
            Equal(false, stop.IsCompleted);
            log.Add("owner-waits");
            release.SetResult();
            await first;
            await stop;
            log.Add("stopped");
        }
        finally
        {
            release.TrySetResult();
        }
    });

    private static Task<string[]> NestedEffects() => InContext(async (ctx, log) =>
    {
        EffectHandle outer = ctx.Effect(() => new IAsyncDisposable[]
        {
            ctx.Effect(() => { log.Add("setup-a"); return () => log.Add("cleanup-a"); }),
            ctx.Effect(() => { log.Add("setup-b"); return () => log.Add("cleanup-b"); }),
        });
        await outer.DisposeAsync();
        await ctx.Fiber.DisposeAsync();
        Equal("setup-a|setup-b|cleanup-b|cleanup-a", string.Join('|', log));
    });

    private static Task<string[]> SetupRollback() => InContext(async (ctx, log) =>
    {
        IEnumerable<IAsyncDisposable> Setup()
        {
            yield return ctx.Effect(() => { log.Add("setup-a"); return () => log.Add("cleanup-a"); });
            yield return ctx.Effect(() => { log.Add("setup-b"); return () => log.Add("cleanup-b"); });
            throw new InvalidOperationException("setup-failed");
        }
        Throws<InvalidOperationException>(() => ctx.Effect(Setup));
        await ctx.Fiber.DisposeAsync();
        log.Add("failure-observed");
        Equal("setup-a|setup-b|cleanup-b|cleanup-a|failure-observed", string.Join('|', log));
    });

    private static async Task<string[]> CleanupFailure()
    {
        var errors = new List<Exception>();
        var log = new List<string>();
        await using var ctx = new Context(errors.Add);
        await ctx.RunAsync(async root =>
        {
            root.Effect(() => () => log.Add("sibling"));
            root.Effect(() => new IAsyncDisposable[]
            {
                new AsyncCallback(() => { log.Add("must-not-run"); return Task.CompletedTask; }),
                new AsyncCallback(() => { log.Add("failed-cleanup"); throw new InvalidOperationException("cleanup"); }),
            });
            await root.Fiber.DisposeAsync();
            Equal("failed-cleanup|sibling", string.Join('|', log));
            Equal(1, errors.Count);
            log.Add("one-diagnostic");
        });
        return log.ToArray();
    }

    private static Task<string[]> RejectDuringUnload() => InContext(async (ctx, log) =>
    {
        ctx.Effect(() => () =>
        {
            var error = Throws<CordisException>(() =>
                ctx.Effect(() => { log.Add("must-not-setup"); return () => { }; }));
            Equal("INACTIVE_EFFECT", error.Code);
            log.Add("rejected");
        });
        await ctx.Fiber.DisposeAsync();
        Equal(1, log.Count);
    });

    private static Task<string[]> ApplyFailure() => InContext(async (ctx, log) =>
    {
        Fiber fiber = ctx.Plugin(Plugin(c =>
        {
            c.Effect(() => { log.Add("setup"); return () => log.Add("cleanup"); });
            throw new InvalidOperationException("apply-failed");
        }));
        await ThrowsAsync<InvalidOperationException>(fiber.WaitAsync);
        Equal(FiberState.Failed, fiber.State);
        log.Add("failed");
        await fiber.DisposeAsync();
        Equal(FiberState.Disposed, fiber.State);
        log.Add("disposed");
    });

    private static Task<string[]> ProviderVisibility() => InContext(async (ctx, log) =>
    {
        var published = Gate();
        var release = Gate();
        var value = new object();
        Fiber consumer = ctx.Plugin(Plugin(_ => log.Add("consumer-active"), "service"));
        Fiber provider = ctx.Plugin(new Plugin<object?>
        {
            ApplyAsync = async (c, _) =>
            {
                c.Provide("service", value);
                published.SetResult();
                await release.Task;
            },
        });
        try
        {
            await published.Task;
            Equal(FiberState.Loading, provider.State);
            Equal<object?>(null, ctx.Get<object>("service"));
            Equal(true, ReferenceEquals(value, ctx.Get<object>("service", strict: false)));
            await consumer.WaitAsync();
            Equal(FiberState.Pending, consumer.State);
            log.Add("loading-not-visible");
            release.SetResult();
            await provider.WaitAsync();
            await consumer.WaitAsync();
            Equal(FiberState.Active, consumer.State);
            log.Add("ready");
        }
        finally
        {
            release.TrySetResult();
        }
    });

    private static Task<string[]> InjectChild() => InContext(async (ctx, log) =>
    {
        Fiber? child = null;
        int parents = 0;
        Fiber parent = ctx.Plugin(Plugin(c =>
        {
            parents++;
            child = c.Inject(["service"], cc =>
            {
                cc.Effect(() => { log.Add("child-setup"); return () => log.Add("child-cleanup"); });
            });
        }));
        await parent.WaitAsync();
        Equal(FiberState.Pending, child!.State);
        EffectHandle service = ctx.Provide("service", new object());
        await child.WaitAsync();
        await service.DisposeAsync();
        Equal(1, parents);
        Equal(FiberState.Active, parent.State);
        Equal(FiberState.Pending, child.State);
        log.Add("parent-unchanged");
        await parent.DisposeAsync();
        Equal(FiberState.Disposed, child.State);
        log.Add("child-disposed-with-parent");
    });

    private static Task<string[]> ConfigUpdate() => InContext(async (ctx, log) =>
    {
        int validations = 0;
        var plugin = new Plugin<string>
        {
            Inject = ["service"],
            Config = value =>
            {
                validations++;
                return value is string text && text == "good"
                    ? ConfigResult<string>.Success(text)
                    : ConfigResult<string>.Failure("expected good");
            },
            Apply = (_, config) => log.Add("apply:" + config),
        };
        Fiber fiber = ctx.Plugin(plugin, "bad");
        await fiber.WaitAsync();
        Equal(0, validations);
        fiber.Update("good");
        Equal(0, validations);
        log.Add("deferred-validation");
        ctx.Provide("service", new object());
        await fiber.WaitAsync();
        Throws<ConfigurationValidationException>(() => fiber.Update("bad"));
        Equal("good", (string)fiber.Config!);
        Equal("bad", (string)fiber.RawConfig!);
        Equal(FiberState.Active, fiber.State);
        log.Add("invalid-update-preserves-active-config");
        await ThrowsAsync<ConfigurationValidationException>(fiber.RestartAsync);
        Equal(FiberState.Failed, fiber.State);
        log.Add("restart-uses-raw-config");
        fiber.Update("good");
        await fiber.WaitAsync();
        Equal(FiberState.Active, fiber.State);
        log.Add("recovered");
    });

    private static Task<string[]> MultipleDependencies() => InContext(async (ctx, log) =>
    {
        int starts = 0;
        Fiber fiber = ctx.Plugin(Plugin(_ => starts++, "a", "b"));
        EffectHandle a = ctx.Provide("a", new object());
        await fiber.WaitAsync();
        Equal(FiberState.Pending, fiber.State);
        ctx.Provide("b", new object());
        await fiber.WaitAsync();
        Equal(1, starts);
        await a.DisposeAsync();
        Equal(FiberState.Pending, fiber.State);
        ctx.Provide("a", new object());
        await fiber.WaitAsync();
        Equal(2, starts);
        log.Add("both-required;two-activations");
    });

    private static Task<string[]> DisposingChild() => InContext(async (ctx, log) =>
    {
        var entered = Gate();
        var release = Gate();
        Fiber child = ctx.Plugin(Plugin(c => c.Effect(() => new AsyncCallback(async () =>
        {
            log.Add("child-cleanup-start");
            entered.SetResult();
            await release.Task;
            log.Add("child-cleanup-end");
        }))));
        try
        {
            await child.WaitAsync();
            Task first = child.DisposeAsync().AsTask();
            await entered.Task;
            Task rootStop = ctx.Fiber.DisposeAsync().AsTask();
            Equal(false, rootStop.IsCompleted);
            log.Add("root-waits");
            release.SetResult();
            await first;
            await rootStop;
            log.Add("stopped");
        }
        finally
        {
            release.TrySetResult();
        }
    });

    private static Task<string[]> StableContext() => InContext(async (ctx, log) =>
    {
        var seen = new List<Context>();
        Fiber fiber = ctx.Plugin(Plugin(seen.Add));
        await fiber.WaitAsync();
        long? uid = fiber.Uid;
        await fiber.RestartAsync();
        Equal(2, seen.Count);
        Equal(true, ReferenceEquals(seen[0], seen[1]));
        Equal(uid, fiber.Uid);
        log.Add("same-context;same-fiber;two-applies");
    });

    internal static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }

    internal static T Throws<T>(Action operation) where T : Exception
    {
        try { operation(); }
        catch (T error) { return error; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    internal static async Task ThrowsAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class AsyncCallback(Func<Task> callback) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(callback());
    }

    private sealed class Callback(Action action) : IDisposable
    {
        private Action? _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    private sealed class MessageSource
    {
        private readonly List<Action<string>> _handlers = [];
        public int Count => _handlers.Count;
        public IDisposable Subscribe(Action<string> handler)
        {
            _handlers.Add(handler);
            return new Callback(() => _handlers.Remove(handler));
        }
        public void Emit(string name)
        {
            foreach (Action<string> callback in _handlers.ToArray()) callback(name);
        }
    }
}
