using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class LoaderOriginalTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GroupBasicSupport(int lastStep)
    {
        await using var f = await Fixture.Start();
        var outer = await f.Loader.CreateAsync(Group("outer", [new() { Id = "a", Name = "foo" }]));
        var inner = await f.Loader.CreateAsync(Group("inner", [new() { Id = "b", Name = "foo" }]), outer);
        await f.Loader.WaitAsync(); f.Counts(2, 0); Assert.Equal(4, f.Loader.Entries().Count());
        var changes = new (string Id, object? Disabled, int Apply, int Dispose)[] { (inner, true, 0, 1), (outer, true, 0, 1), (inner, null, 0, 0), (outer, null, 2, 0) };
        for (var step = 0; step < lastStep; step++) { f.Reset(); var change = changes[step]; await f.Loader.UpdateAsync(change.Id, new() { Disabled = change.Disabled }); await f.Loader.WaitAsync(); f.Counts(change.Apply, change.Dispose); Assert.Equal(4, f.Loader.Entries().Count()); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task GroupTransfer(int lastStep)
    {
        await using var f = await Fixture.Start(); await f.Loader.CreateAsync(new() { Id = "row", Name = "foo" }); await f.Loader.CreateAsync(Group("alpha", [])); var beta = Group("beta", []); beta.Disabled = true; await f.Loader.CreateAsync(beta, "alpha"); await f.Loader.CreateAsync(Group("gamma", []), "beta"); await f.Loader.WaitAsync(); f.Counts(1, 0);
        var changes = new (string? Parent, int Apply, int Dispose)[] { ("alpha", 0, 0), ("beta", 0, 1), ("gamma", 0, 0), (null, 1, 0) };
        for (var step = 0; step < lastStep; step++) { f.Reset(); var change = changes[step]; await f.Loader.UpdateAsync("row", new(), change.Parent, move: true); await f.Loader.WaitAsync(); f.Counts(change.Apply, change.Dispose); Assert.Equal(4, f.Loader.Entries().Count()); }
    }
    [Fact]
    public async Task GroupInterceptPrototypeChain()
    {
        await using var f = await Fixture.Start(); var outer = Group("outer", []); outer["intercept"] = new EntryOptions { ["foo"] = new EntryOptions { ["a"] = 1 } }; var inner = Group("inner", []); inner["intercept"] = new EntryOptions { ["foo"] = new EntryOptions { ["b"] = 2 } };
        await f.Loader.CreateAsync(outer); await f.Loader.CreateAsync(inner, "outer"); await f.Loader.CreateAsync(new() { Id = "row", Name = "foo", ["intercept"] = new EntryOptions { ["foo"] = new EntryOptions { ["c"] = 3 } } }, "inner"); await f.Loader.WaitAsync();
        var chain = f.Loader.Resolve("row").Fiber!.Context.Intercepts("foo").Cast<EntryOptions>().ToArray(); Assert.Equal(3, chain.Length); Assert.Equal(1, chain[0]["a"]); Assert.Equal(2, chain[1]["b"]); Assert.Equal(3, chain[2]["c"]);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task LoaderBasicSupport(int lastStep)
    {
        await using var f = await Fixture.StartBasic();
        await f.Loader.Root.UpdateAsync([new() { Id = "1", Name = "foo" }, new() { Id = "2", Name = "cordis:group", Config = new[] { new EntryOptions { Id = "3", Name = "bar", Config = new EntryOptions { ["a"] = 1 } }, new EntryOptions { Id = "4", Name = "qux", Disabled = true } } }]);
        await f.Loader.WaitAsync();
        f.PluginCounts("foo", 1, 0); f.PluginCounts("bar", 1, 0); f.PluginCounts("qux", 0, 0);
        f.ExpectEnabled("foo"); f.ExpectEnabled("bar"); f.ExpectDisabled("qux");
        Assert.Null(f.Loader.Resolve("4").Fiber);
        if (lastStep == 0) return;
        f.ResetPlugins();
        await f.Loader.Root.UpdateAsync([new() { Id = "1", Name = "foo" }, new() { Id = "4", Name = "qux" }]);
        await f.Loader.WaitAsync();
        f.PluginCounts("foo", 0, 0); f.PluginCounts("bar", 0, 1); f.PluginCounts("qux", 1, 0);
        f.ExpectEnabled("foo"); f.ExpectDisabled("bar"); f.ExpectEnabled("qux");
        Assert.True(Data.DeepEquals(new[] { new EntryOptions { Id = "1", Name = "foo" }, new EntryOptions { Id = "4", Name = "qux" } }, f.Loader.Root.Data));
        if (lastStep == 1) return;
        await f.Context.RunAsync(_ => { f.Loader.Resolve("1").Fiber!.Update(new EntryOptions { ["a"] = 3 }); return Task.CompletedTask; });
        await f.Loader.WaitAsync();
        Assert.True(Data.DeepEquals(new[] { new EntryOptions { Id = "1", Name = "foo", Config = new EntryOptions { ["a"] = 3 } }, new EntryOptions { Id = "4", Name = "qux" } }, f.Loader.Root.Data));
        if (lastStep == 2) return;
        await f.Loader.Resolve("1").Fiber!.DisposeAsync();
        Assert.True(Data.DeepEquals(new[] { new EntryOptions { Id = "1", Name = "foo", Disabled = true, Config = new EntryOptions { ["a"] = 3 } }, new EntryOptions { Id = "4", Name = "qux" } }, f.Loader.Root.Data));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoaderInterceptReadiness(bool finish)
    {
        await using var f = await Fixture.Start(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Modules.Register("slow", new Plugin<object?> { ApplyAsync = (_, _) => gate.Task }); f.Modules.Register("never", new Plugin<object?> { Inject = ["never"], Apply = (_, _) => { } });
        await f.Loader.CreateAsync(new() { Id = "slow", Name = "slow" }); await f.Loader.CreateAsync(new() { Id = "never", Name = "never" }); await f.Loader.CreateAsync(new() { Id = "qux", Name = "foo", ["inject"] = new EntryOptions { ["loader"] = true }, ["intercept"] = new EntryOptions { ["loader"] = new EntryOptions { ["await"] = true } } });
        Assert.Equal(FiberState.Loading, f.Loader.Resolve("slow").Fiber!.State); Assert.Equal(FiberState.Pending, f.Loader.Resolve("never").Fiber!.State); Assert.Equal(FiberState.Pending, f.Loader.Resolve("qux").Fiber!.State);
        gate.SetResult(); await f.Loader.WaitAsync(); if (finish) { Assert.Equal(FiberState.Active, f.Loader.Resolve("slow").Fiber!.State); Assert.Equal(FiberState.Active, f.Loader.Resolve("qux").Fiber!.State); Assert.Equal(FiberState.Pending, f.Loader.Resolve("never").Fiber!.State); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task IsolationBasic(int lastStep)
    {
        await using var f = await Fixture.Start(true); await f.Loader.CreateAsync(new() { Id = "provider", Name = "bar" }); await f.Loader.CreateAsync(new() { Id = "injector", Name = "foo" }); await f.Loader.WaitAsync(); f.Counts(1, 0);
        var changes = new (string Id, EntryOptions? Isolate, int Apply, int Dispose)[] { ("injector", new() { ["bar"] = true }, 0, 1), ("injector", new() { ["bar"] = true, ["qux"] = true }, 0, 0), ("injector", new() { ["qux"] = true }, 1, 0), ("injector", null, 0, 0), ("provider", new() { ["bar"] = true }, 0, 1), ("provider", new() { ["bar"] = true, ["qux"] = true }, 0, 0), ("provider", new() { ["qux"] = true }, 1, 0), ("provider", null, 0, 0) };
        for (var step = 0; step < lastStep; step++) { f.Reset(); var change = changes[step]; await f.Loader.UpdateAsync(change.Id, new() { ["isolate"] = change.Isolate }); await f.Loader.WaitAsync(); f.Counts(change.Apply, change.Dispose); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task IsolationTransfer(int lastStep)
    {
        await using var f = await Fixture.Start(true); var group = RealmGroup("group", []); group["isolate"] = new EntryOptions { ["bar"] = true }; await f.Loader.CreateAsync(group); await f.Loader.CreateAsync(new() { Id = "provider", Name = "bar" }); await f.Loader.CreateAsync(new() { Id = "injector", Name = "foo" }); await f.Loader.WaitAsync(); f.Counts(1, 0);
        var changes = new (string Id, string? Parent, int Apply, int Dispose)[] { ("injector", "group", 0, 1), ("provider", "group", 1, 0), ("injector", null, 0, 1), ("provider", null, 1, 0) };
        for (var step = 0; step < lastStep; step++) { f.Reset(); var change = changes[step]; await f.Loader.UpdateAsync(change.Id, new(), change.Parent, move: true); await f.Loader.WaitAsync(); f.Counts(change.Apply, change.Dispose); }
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task IsolationRealmReferences(int lastStep)
    {
        await using var f = await Fixture.Start(true); var alpha = RealmGroup("alpha", [new() { Id = "pa", Name = "bar", Config = "alpha" }]); alpha["isolate"] = new EntryOptions { ["bar"] = true }; var beta = RealmGroup("beta", [new() { Id = "pb", Name = "bar", Config = "beta" }]); beta["isolate"] = new EntryOptions { ["bar"] = "beta" };
        await f.Loader.CreateAsync(alpha); await f.Loader.CreateAsync(beta); await f.Loader.WaitAsync(); f.Counts(0, 0); Assert.Equal(2, f.Context.Registry.Get(f.Provider)!.Fibers.Count);
        if (lastStep == 0) return;
        await f.Loader.UpdateAsync("alpha", new() { ["isolate"] = new EntryOptions { ["bar"] = true } }); await f.Loader.WaitAsync(); f.Counts(0, 0); Assert.Equal(2, f.Context.Registry.Get(f.Provider)!.Fibers.Count);
        if (lastStep == 1) return;
        await f.Loader.CreateAsync(new() { Id = "one", Name = "foo" }, "alpha"); await f.Loader.CreateAsync(new() { Id = "two", Name = "foo", ["isolate"] = new EntryOptions { ["bar"] = "beta" } }, "alpha"); await f.Loader.CreateAsync(new() { Id = "three", Name = "foo", ["isolate"] = new EntryOptions { ["bar"] = true } }, "alpha"); await f.Loader.WaitAsync(); f.Counts(2, 0);
        await f.Context.RunAsync(_ => { Assert.Equal("alpha", f.Loader.Resolve("one").Fiber!.Context.Get("bar")); Assert.Equal("beta", f.Loader.Resolve("two").Fiber!.Context.Get("bar")); Assert.Null(f.Loader.Resolve("three").Fiber!.Context.Get("bar")); return Task.CompletedTask; }); Assert.Equal(FiberState.Pending, f.Loader.Resolve("three").Fiber!.State);
    }
    [Fact]
    public async Task IsolationNestedRealmsDoNotRestartUnaffectedFibers()
    {
        await using var f = await Fixture.Start(true); await f.Loader.CreateAsync(RealmGroup("outer", [])); var inner = RealmGroup("inner", []); inner["isolate"] = new EntryOptions { ["bar"] = "custom" }; await f.Loader.CreateAsync(inner, "outer"); await f.Loader.CreateAsync(new() { Id = "provider", Name = "bar", Config = "custom" }, "inner"); await f.Loader.CreateAsync(new() { Id = "one", Name = "foo", ["isolate"] = new EntryOptions { ["bar"] = "custom" } }); await f.Loader.CreateAsync(new() { Id = "two", Name = "foo" }, "inner"); await f.Loader.WaitAsync();
        f.Reset(); await f.Loader.UpdateAsync("outer", new() { ["isolate"] = new EntryOptions { ["bar"] = "custom" } }); await f.Loader.WaitAsync(); f.Counts(0, 0); await f.Loader.UpdateAsync("outer", new() { ["isolate"] = new EntryOptions() }); await f.Loader.WaitAsync(); f.Counts(0, 0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolationSwitchProviderOrInjector(bool moveProvider)
    {
        await using var f = await Fixture.Start(true); var group = RealmGroup("group", []); group["isolate"] = new EntryOptions { ["bar"] = "alpha" }; await f.Loader.CreateAsync(group);
        if (moveProvider)
        {
            await f.Loader.CreateAsync(new() { Id = "alpha", Name = "foo", ["isolate"] = new EntryOptions { ["bar"] = "alpha" } }); await f.Loader.CreateAsync(new() { Id = "beta", Name = "foo", ["isolate"] = new EntryOptions { ["bar"] = "beta" } }); await f.Loader.CreateAsync(new() { Id = "provider", Name = "bar", Config = "provided" }, "group");
        }
        else
        {
            await f.Loader.CreateAsync(new() { Id = "alpha", Name = "bar", Config = "alpha", ["isolate"] = new EntryOptions { ["bar"] = "alpha" } }); await f.Loader.CreateAsync(new() { Id = "beta", Name = "bar", Config = "beta", ["isolate"] = new EntryOptions { ["bar"] = "beta" } }); await f.Loader.CreateAsync(new() { Id = "consumer", Name = "foo" }, "group");
        }
        await f.Loader.WaitAsync(); f.Counts(1, 0); f.Reset(); await f.Loader.UpdateAsync("group", new() { ["isolate"] = new EntryOptions { ["bar"] = "beta" } }); await f.Loader.WaitAsync(); f.Counts(1, 1);
        await f.Context.RunAsync(_ => { if (moveProvider) { Assert.Null(f.Loader.Resolve("alpha").Fiber!.Context.Get("bar")); Assert.Equal("provided", f.Loader.Resolve("beta").Fiber!.Context.Get("bar")); } else Assert.Equal("beta", f.Loader.Resolve("consumer").Fiber!.Context.Get("bar")); return Task.CompletedTask; });
    }
    private static EntryOptions Group(string id, EntryOptions[] children) => new() { Id = id, Name = "cordis:group", Group = true, Config = children };
    private static EntryOptions RealmGroup(string id, EntryOptions[] children) => new() { Id = id, Name = "cordis:group", Config = children };
    private sealed class Fixture : IAsyncDisposable
    {
        public Context Context { get; } = new(); public StaticModuleResolver Modules { get; } = new(); public Loader Loader { get; private set; } = null!; public Plugin<object?> Provider { get; } = new() { Apply = (ctx, raw) => ctx.Provide("bar", raw ?? new object()) }; private int apply; private int dispose;
        private readonly Dictionary<string, (int Apply, int Dispose)> pluginCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IPlugin> plugins = new(StringComparer.Ordinal);
        public static async Task<Fixture> Start(bool inject = false)
        {
            var f = new Fixture(); f.Modules.Register("bar", f.Provider).Register("foo", new Plugin<object?> { Inject = inject ? ["bar"] : [], Apply = (ctx, _) => { f.apply++; ctx.Effect(() => (Action)(() => f.dispose++)); } });
            await f.Context.RunAsync(_ => { f.Loader = new Loader(f.Context, f.Modules); return Task.CompletedTask; }); return f;
        }
        public static async Task<Fixture> StartBasic()
        {
            var f = new Fixture();
            foreach (var name in new[] { "foo", "bar", "qux" })
            {
                var plugin = f.TrackedPlugin(name);
                f.plugins[name] = plugin;
                f.Modules.Register(name, plugin);
            }
            await f.Context.RunAsync(_ => { f.Loader = new Loader(f.Context, f.Modules); return Task.CompletedTask; });
            return f;
        }
        private Plugin<object?> TrackedPlugin(string name) => new()
        {
            Name = name,
            Apply = (ctx, _) =>
            {
                var counts = pluginCounts.GetValueOrDefault(name); pluginCounts[name] = (counts.Apply + 1, counts.Dispose);
                ctx.On("internal/update", (_, _) => Undefined.Value);
                ctx.Effect(() => (Action)(() => { var current = pluginCounts.GetValueOrDefault(name); pluginCounts[name] = (current.Apply, current.Dispose + 1); }));
            }
        };
        public void ExpectEnabled(string name) => Assert.NotNull(Context.Registry.Get(plugins[name]));
        public void ExpectDisabled(string name) => Assert.Null(Context.Registry.Get(plugins[name]));
        public void PluginCounts(string name, int applied, int disposed) { var counts = pluginCounts.GetValueOrDefault(name); Assert.Equal(applied, counts.Apply); Assert.Equal(disposed, counts.Dispose); }
        public void ResetPlugins() { foreach (var name in pluginCounts.Keys.ToArray()) pluginCounts[name] = default; }
        public void Counts(int applied, int disposed) { Assert.Equal(applied, apply); Assert.Equal(disposed, dispose); }
        public void Reset() { apply = 0; dispose = 0; }
        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }
}
