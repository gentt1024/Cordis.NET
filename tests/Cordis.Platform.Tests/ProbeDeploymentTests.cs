using System.Runtime.CompilerServices;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Example.Probes;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class ProbeDeploymentTests
{
    [Theory]
    [InlineData("view")]
    [InlineData("binder")]
    [InlineData("callback")]
    public async Task Real_probe_dlls_share_contracts_replace_and_collect_only_after_retained_values_release(string retain)
    {
        var held = await Exercise(retain);
        Collect();
        Assert.True(held.Observations[0].UnloadRequested);
        Assert.False(held.Observations[0].IsCollected);
        Assert.False(held.Observations[0].TryDeleteShadow());
        held.Release();
        for (var attempt = 0; attempt < 12 && held.Observations.Any(item => !item.IsCollected); attempt++)
        { Collect(); await Task.Yield(); }
        Assert.All(held.Observations, item => Assert.True(item.IsCollected, item.ShadowDirectory));
        Assert.All(held.Observations, item => Assert.True(item.TryDeleteShadow()));
        Directory.Delete(held.ShadowRoot);
    }

    private sealed class Held(object value, ClrUnloadObservation[] observations, string shadowRoot)
    {
        private object? retained = value;
        public ClrUnloadObservation[] Observations { get; } = observations;
        public string ShadowRoot { get; } = shadowRoot;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Release() { GC.KeepAlive(retained); retained = null; }
    }

    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Held> Exercise(string retain)
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-probes-" + Guid.NewGuid().ToString("N"));
        await using var root = new Context();
        // No implementation assembly is a host reference; only the shared contract crosses ALCs.
        await using var resolver = new ClrModuleResolver(shadow, [typeof(IProbeRegistry).Assembly]);
        resolver.Register("probes", Definition("v1"));
        var plugin = await resolver.ResolveAsync("probes", new Uri("file:///"));
        Loader? loader = null;
        object? held = null;
        var events = new List<string>();
        await root.RunAsync(async ctx =>
        {
            loader = new Loader(ctx, resolver);
            await loader.Root.UpdateAsync([new EntryOptions { Id = "provider", Name = "probes", Config = new Dictionary<string, object?> { ["Prefix"] = "live" } }]);
            await loader!.WaitAsync();
            var consumer = ctx.Plugin(new Plugin<object?>
            {
                Inject = [ProbeContract.Name, ProbeContract.Formatter],
                Apply = (caller, _) =>
                {
                    caller.Probes.Register("connection", () => caller.ProbeFormatter.Format("up"));
                    caller.Emit(ProbeContract.Changed, new("connection", caller.Probes.Version));
                },
            });
            ctx.On(ProbeContract.Changed, (_, change) => { events.Add(change.Value); });
            await consumer.WaitAsync();
            var view = ctx.Probes;
            Assert.Equal("v1", view.Version);
            Assert.Equal("live:up", view.Snapshot()["connection"]);
            held = retain switch
            {
                "view" => view,
                // Bound config resolution retains Plugin<T>, its binder and generated collectible metadata.
                "binder" => (Func<object?, object?>)plugin.ResolveConfig,
                _ => (Func<string>)(() => view.Version),
            };
        });
        await resolver.ReplaceAsync("probes", Definition("v2"),
            async (old, next) => await loader!.ReplacePluginAsync(old, next));
        await root.RunAsync(ctx =>
        {
            Assert.Equal("v2", ctx.Probes.Version);
            Assert.Equal("live:up", ctx.Probes.Snapshot()["connection"]);
            Assert.Equal(new[] { "v1", "v2" }, events);
            return Task.CompletedTask;
        });
        // A failed dynamic candidate restores the old graph according to existing replacement policy.
        await Assert.ThrowsAnyAsync<Exception>(async () => await resolver.ReplaceAsync("probes", Definition("v1") with { EntryType = "Cordis.ProbeFixture.RejectedEntry" },
            async (old, next) => await loader!.ReplacePluginAsync(old, next)));
        await root.RunAsync(ctx => { Assert.Equal("v2", ctx.Probes.Version); return Task.CompletedTask; });
        // Active-fiber validation rejects before restarting: old Config stays active, new RawConfig remains.
        // This is not the dynamic candidate rollback policy above.
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => loader!.UpdateAsync("provider", new EntryOptions { Config = new Dictionary<string, object?> { ["Prefix"] = "" } }));
        await loader!.WaitAsync();
        await root.RunAsync(ctx =>
        {
            Assert.Equal("live:up", ctx.Probes.Snapshot()["connection"]);
            Assert.Equal(FiberState.Active, loader.Resolve("provider").Fiber!.State);
            Assert.Equal("", Assert.IsAssignableFrom<IDictionary<string, object?>>(loader.Resolve("provider").Fiber!.RawConfig)["Prefix"]);
            return Task.CompletedTask;
        });
        await loader.UpdateAsync("provider", new EntryOptions { Config = new Dictionary<string, object?> { ["Prefix"] = "recovered" } });
        await loader!.WaitAsync();
        await resolver.ReplaceAsync("probes", Definition("v1"),
            async (old, next) => await loader.ReplacePluginAsync(old, next));
        await root.RunAsync(ctx => { Assert.Equal("recovered:up", ctx.Probes.Snapshot()["connection"]); return Task.CompletedTask; });
        await root.DisposeAsync();
        await resolver.DisposeAsync();
        return new Held(held!, resolver.Unloads.ToArray(), shadow);
    }

    private static ClrModuleDefinition Definition(string version)
        => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "probe-" + version), "ProbePlugin.dll", "Cordis.ProbeFixture.Entry");
}
