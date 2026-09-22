using Cordis.Example.Probes;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ServiceAuthoringTests
{
    private static IProbeRegistry Registry(Context context, string route) => route switch
    {
        "cast" => (IProbeRegistry)context.Reflect.Read(ProbeContract.Name)!,
        "generic" => context.Reflect.Read<IProbeRegistry>(ProbeContract.Name),
        "extension" => context.Probes,
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    private static IProbeFormatter Formatter(Context context, string route) => route switch
    {
        "cast" => (IProbeFormatter)context.Reflect.Read(ProbeContract.Formatter)!,
        "generic" => context.Reflect.Read<IProbeFormatter>(ProbeContract.Formatter),
        "extension" => context.ProbeFormatter,
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    [Fact]
    public async Task ActualExampleLifecycleHasCompleteTraceParityAcrossAuthoringRoutes()
    {
        var before = await Lifecycle("cast");
        Assert.Equal(new[]
        {
            "a:start:first:a", "b:start:first:b", "both:a=first:a,b=first:b",
            "early:b=first:b", "b:stop", "after-b:", "a:stop", "a:start:first:a",
            "restart:a=first:a", "a:stop", "provider-removed:Pending:",
            "a:start:second:a", "replacement:a=second:a", "a:stop", "final:"
        }, before);
        Assert.Equal(before, await Lifecycle("generic"));
        Assert.Equal(before, await Lifecycle("extension"));
    }

    private static async Task<string[]> Lifecycle(string route)
    {
        await using var root = new Context();
        var trace = new List<string>();
        await root.RunAsync(async ctx =>
        {
            var registrations = new List<string>();
            ctx.On("internal/service", (_, args) =>
            {
                if (args[0] is string name && name is ProbeContract.Name or ProbeContract.Formatter)
                    registrations.Add(name + (args[1] is null ? ":remove" : ":provide"));
                return Undefined.Value;
            }, new(Global: true));
            var provider = ctx.Plugin(ProbeModule.Create(), new EntryOptions { ["Prefix"] = "first" });
            await provider.WaitAsync();
            var providerNotifications = registrations.ToArray();
            var views = new List<IProbeRegistry>();
            var formatters = new List<IProbeFormatter>();
            var handles = new Dictionary<string, IAsyncDisposable>();
            Plugin<object?> Consumer(string name) => new()
            {
                Inject = [ProbeContract.Name, ProbeContract.Formatter],
                Apply = (caller, _) =>
                {
                    var probes = Registry(caller, route);
                    var formatter = Formatter(caller, route);
                    views.Add(probes);
                    formatters.Add(formatter);
                    var value = formatter.Format(name);
                    trace.Add($"{name}:start:{value}");
                    handles[name] = probes.Register(name, () => value);
                    caller.Effect(() => (Action)(() => trace.Add(name + ":stop")));
                }
            };
            var a = ctx.Plugin(Consumer("a"));
            await a.WaitAsync();
            var b = ctx.Plugin(Consumer("b"));
            await b.WaitAsync();
            Assert.NotSame(views[0], views[1]);
            Assert.Same(formatters[0], formatters[1]);
            Assert.IsNotAssignableFrom<Service>(formatters[0]);
            Assert.Same(provider.Context, Assert.IsAssignableFrom<Service>(views[0]).Provider);
            Assert.Same(provider.Context, Assert.IsAssignableFrom<Service>(views[1]).Provider);
            Assert.Equal(providerNotifications, registrations);
            trace.Add("both:" + Snapshot(views[0]));
            Assert.Equal(Snapshot(views[0]), Snapshot(views[1]));

            await handles["a"].DisposeAsync();
            await handles["a"].DisposeAsync();
            trace.Add("early:" + Snapshot(views[1]));
            Assert.Equal(FiberState.Active, a.State);
            await b.DisposeAsync();
            trace.Add("after-b:" + Snapshot(views[0]));
            Assert.Equal(FiberState.Active, provider.State);
            await a.RestartAsync();
            trace.Add("restart:" + Snapshot(Registry(a.Context, route)));
            Assert.Equal(providerNotifications, registrations); // Reads and view creation never notify or re-register a provider.

            var stale = views[^1];
            await provider.DisposeAsync();
            await a.WaitAsync();
            trace.Add($"provider-removed:{a.State}:{Snapshot(stale)}");
            var replacement = ctx.Plugin(ProbeModule.Create(), new EntryOptions { ["Prefix"] = "second" });
            await replacement.WaitAsync();
            await a.WaitAsync();
            Assert.Equal(FiberState.Active, a.State);
            Assert.NotSame(stale, views[^1]);
            Assert.Same(replacement.Context, Assert.IsAssignableFrom<Service>(views[^1]).Provider);
            // Retained views keep their old state; callers reacquire on activation, without an auto-routing promise.
            Assert.Empty(stale.Snapshot());
            Assert.Equal(1, registrations.Count(item => item == "probes:remove"));
            Assert.Equal(1, registrations.Count(item => item == "probe-formatter:remove"));
            trace.Add("replacement:" + Snapshot(views[^1]));
            await a.DisposeAsync();
            trace.Add("final:" + Snapshot(Registry(replacement.Context, route)));
        });
        return trace.ToArray();
    }

    private static string Snapshot(IProbeRegistry registry) => string.Join(",", registry.Snapshot().OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));

    [Theory]
    [InlineData("cast")]
    [InlineData("generic")]
    [InlineData("extension")]
    public async Task RealmsKeepSameNamedContractsIndependentAndCleanupOwnedByEachCaller(string route)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var left = ctx.Isolate(ProbeContract.Name).Isolate(ProbeContract.Formatter);
            var right = ctx.Isolate(ProbeContract.Name).Isolate(ProbeContract.Formatter);
            var leftProvider = left.Plugin(ProbeModule.Create(), new EntryOptions { ["Prefix"] = "left" });
            var rightProvider = right.Plugin(ProbeModule.Create(), new EntryOptions { ["Prefix"] = "right" });
            await Task.WhenAll(leftProvider.WaitAsync(), rightProvider.WaitAsync());
            Plugin<object?> Consumer() => new()
            {
                Inject = [ProbeContract.Name, ProbeContract.Formatter],
                Apply = (caller, _) => Registry(caller, route).Register("same", () => Formatter(caller, route).Format("ready"))
            };
            var a = left.Plugin(Consumer());
            var b = right.Plugin(Consumer());
            await Task.WhenAll(a.WaitAsync(), b.WaitAsync());
            Assert.Equal("same=left:ready", Snapshot(Registry(a.Context, route)));
            Assert.Equal("same=right:ready", Snapshot(Registry(b.Context, route)));
            Assert.Null(ctx.Get<IProbeRegistry>(ProbeContract.Name));
            await a.DisposeAsync();
            Assert.Empty(Registry(leftProvider.Context, route).Snapshot());
            Assert.Equal("same=right:ready", Snapshot(Registry(b.Context, route)));
            await leftProvider.DisposeAsync();
            Assert.Equal(FiberState.Active, b.State);
            Assert.Equal(FiberState.Active, rightProvider.State);
        });
    }

    [Theory]
    [InlineData("cast")]
    [InlineData("generic")]
    [InlineData("extension")]
    public async Task TypedAccessDoesNotAddInjectAndPreservesInheritedDependencySnapshots(string route)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var provider = ctx.Plugin(ProbeModule.Create(), new EntryOptions());
            await provider.WaitAsync();
            var undeclared = ctx.Plugin(new Plugin<object?> { Apply = (_, _) => { } });
            await undeclared.WaitAsync();
            Assert.NotNull(undeclared.Context.Get<IProbeRegistry>(ProbeContract.Name));
            Assert.Contains("without inject", Assert.Throws<InvalidOperationException>(() => Registry(undeclared.Context, route)).Message);
            Assert.Null(undeclared.Context.Get<IProbeRegistry>("missing"));
            Assert.Null(ctx.Reflect.Read<IProbeRegistry>("missing"));

            Fiber child = null!;
            Fiber isolated = null!;
            Fiber isolatedRequired = null!;
            var parent = ctx.Plugin(new Plugin<object?>
            {
                Inject = [ProbeContract.Name, ProbeContract.Formatter],
                Apply = (caller, _) =>
                {
                    child = caller.Plugin(new Plugin<object?>
                    {
                        Apply = (nested, _) => Registry(nested, route).Register("inherited", () => Formatter(nested, route).Format("ok"))
                    });
                    isolated = caller.Isolate(ProbeContract.Name).Plugin(new Plugin<object?> { Apply = (_, _) => { } });
                    isolatedRequired = caller.Isolate(ProbeContract.Name).Plugin(new Plugin<object?>
                    {
                        Inject = [ProbeContract.Name],
                        Apply = (_, _) => throw new InvalidOperationException("The isolated dependency is unavailable.")
                    });
                }
            });
            await parent.WaitAsync();
            await Task.WhenAll(child.WaitAsync(), isolated.WaitAsync(), isolatedRequired.WaitAsync());
            Assert.Equal("inherited=status:ok", Snapshot(Registry(child.Context, route)));
            Assert.Null(isolated.Context.Get<IProbeRegistry>(ProbeContract.Name));
            // Property reads inherit the already captured parent snapshot before checking the next realm boundary.
            // Optional Get resolves the current realm, while an explicit isolated Inject remains Pending.
            Assert.Equal("inherited=status:ok", Snapshot(Registry(isolated.Context, route)));
            Assert.Same(provider.Context, Assert.IsAssignableFrom<Service>(Registry(isolated.Context, route)).Provider);
            Assert.Equal(FiberState.Pending, isolatedRequired.State);
            await parent.DisposeAsync();
            Assert.Equal(FiberState.Disposed, child.State);
            Assert.Empty(Registry(provider.Context, route).Snapshot());
        });
    }

    [Fact]
    public async Task GenericReadPreservesInterceptorArgumentsOutcomesAndAccessorReceiverExactly()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var caller = ctx.Plugin(new Plugin<object?> { Apply = (_, _) => { } });
            await caller.WaitAsync();
            var explicitReceiver = new object();
            var replacement = new FormatterFixture();
            var interceptions = new List<(object? Receiver, object?[] Arguments)>();
            object? outcome = null;
            var hook = ctx.On("internal/get", (evt, args) =>
            {
                interceptions.Add((evt.Receiver, args));
                return outcome;
            });
            foreach (var value in new object?[] { null, replacement, Undefined.Value, "wrong type", false, 0 })
            {
                outcome = value;
                Assert.Same(value, caller.Context.Reflect.Read(ProbeContract.Formatter, explicitReceiver));
                var oldError = Record.Exception(() => _ = (IProbeFormatter)caller.Context.Reflect.Read(ProbeContract.Formatter, explicitReceiver)!);
                var newError = Record.Exception(() => _ = caller.Context.Reflect.Read<IProbeFormatter>(ProbeContract.Formatter, explicitReceiver));
                var extensionError = Record.Exception(() => _ = caller.Context.ProbeFormatter);
                Assert.Equal(oldError?.GetType(), newError?.GetType());
                Assert.Equal(oldError?.GetType(), extensionError?.GetType());
                if (value is null) Assert.Null(caller.Context.Reflect.Read<IProbeFormatter>(ProbeContract.Formatter));
                else if (ReferenceEquals(value, replacement)) Assert.Same(replacement, caller.Context.ProbeFormatter);
                else Assert.IsType<InvalidCastException>(newError);
            }
            Assert.All(interceptions, invocation =>
            {
                Assert.Null(invocation.Receiver); // Existing internal/get carries the caller in args, not receiver.
                Assert.Equal(2, invocation.Arguments.Length);
                Assert.Same(caller.Context, invocation.Arguments[0]);
                Assert.Equal(ProbeContract.Formatter, invocation.Arguments[1]);
            });
            var interceptionCount = interceptions.Count;
            var accessorCalls = new List<(Context Caller, object? Receiver)>();
            ctx.Reflect.Accessor("accessor", new((accessorCaller, receiver) =>
            {
                accessorCalls.Add((accessorCaller, receiver));
                return replacement;
            }));
            Assert.Same(replacement, (IProbeFormatter)caller.Context.Reflect.Read("accessor", explicitReceiver)!);
            Assert.Same(replacement, caller.Context.Reflect.Read<IProbeFormatter>("accessor", explicitReceiver));
            Assert.Equal(2, accessorCalls.Count);
            Assert.All(accessorCalls, call => { Assert.Same(caller.Context, call.Caller); Assert.Same(explicitReceiver, call.Receiver); });
            Assert.Equal(interceptionCount, interceptions.Count); // Accessors bypass internal/get on both routes.
            await hook.DisposeAsync();

            ctx.Provide("null-value", null);
            Assert.Null(ctx.Reflect.Read<IProbeFormatter>("null-value"));
            Assert.Throws<NullReferenceException>(() => ctx.Reflect.Read<int>("null-value"));
            ctx.Provide("wrong-value", "string");
            Assert.Throws<InvalidCastException>(() => ctx.Reflect.Read<IProbeFormatter>("wrong-value"));
        });
    }

    private sealed class FormatterFixture : IProbeFormatter
    {
        public string Format(string value) => value;
    }
}
