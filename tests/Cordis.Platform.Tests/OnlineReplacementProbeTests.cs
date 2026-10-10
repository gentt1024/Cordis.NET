using System.Runtime.Loader;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Fixtures;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class OnlineReplacementProbeTests
{
    [Fact]
    public async Task Cooperative_replacement_drains_calls_revokes_callbacks_and_preserves_other_entries()
    {
        await using var host = await ProbeHost.StartAsync();
        var pid = Environment.ProcessId;
        var old = await host.ServicesAsync();
        var callback = old[0].CaptureCallback();
        var retainedType = old[0].GetType();
        var inFlight = old[0].ExecuteAsync();
        await host.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Probe.Pulse();
        var otherFiber = host.Loader.Resolve("other").Fiber;
        var replacement = host.ReplaceAsync("v2", guard: true);
        try
        {
            await host.Probe.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(replacement.IsCompleted);
            Assert.All(old, service => Assert.Throws<ObjectDisposedException>(() => service.Execute()));
            Assert.Throws<ObjectDisposedException>(callback);
        }
        finally
        {
            host.Probe.Release.TrySetResult();
        }

        Assert.Equal("v1", await inFlight);
        var observation = await replacement.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(pid, Environment.ProcessId);
        Assert.Same(otherFiber, host.Loader.Resolve("other").Fiber);
        Assert.All(await host.ServicesAsync(), service => Assert.Equal("v2", service.Execute()));
        Assert.Throws<ObjectDisposedException>(callback);
        var oldTicks = host.Probe.History.Count(item => item.StartsWith("tick:v1:") && !item.EndsWith(":other"));
        host.Probe.Pulse();
        Assert.Equal(
            oldTicks,
            host.Probe.History.Count(item => item.StartsWith("tick:v1:") && !item.EndsWith(":other")));
        Assert.Contains("tick:v2:a", host.Probe.History);
        Assert.Equal(3, host.Probe.History.Count(item => item.StartsWith("stop:v1:") && !item.EndsWith(":other")));
        Assert.True(observation.UnloadRequested);
        Assert.Same(AssemblyLoadContext.GetLoadContext(retainedType.Assembly), observation.LoadContext.Target);
        Assert.False(observation.IsCollected);
        Assert.False(observation.TryDeleteShadow());
        GC.KeepAlive(retainedType);
    }

    [Fact]
    public async Task Unsafe_retirement_is_refused_by_the_owner_before_any_live_fiber_changes()
    {
        await using var host = await ProbeHost.StartAsync();
        host.Probe.AllowRetirement = false;
        var old = await host.ServicesAsync();
        var oldFiber = host.Loader.Resolve("a").Fiber;
        var inFlight = old[0].ExecuteAsync();
        await host.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync("v2", guard: true));
            Assert.Equal("retirement not confirmed", error.Message);
            Assert.Same(oldFiber, host.Loader.Resolve("a").Fiber);
            Assert.All(old, service => Assert.Equal("v1", service.Execute()));
            Assert.False(inFlight.IsCompleted);
            Assert.DoesNotContain(host.Probe.History, item => item.StartsWith("stop:"));
            Assert.DoesNotContain(host.Probe.History, item => item.StartsWith("start:v2:"));
            Assert.True(Assert.Single(host.Resolver.Unloads).UnloadRequested);
        }
        finally
        {
            host.Probe.Release.TrySetResult();
        }

        Assert.Equal("v1", await inFlight);
    }

    [Fact]
    public async Task Old_cleanup_failure_refuses_candidate_commit_and_preserves_the_failure()
    {
        await using var host = await ProbeHost.StartAsync(exclusiveResources: false);
        var old = (await host.ServicesAsync())[0];
        host.Probe.FailStop = true;
        try
        {
            var failure = await Record.ExceptionAsync(() => host.ReplaceAsync("v2", guard: false));
            Assert.NotNull(failure);
            Assert.Contains("old generation refused to stop", failure.ToString());
            Assert.DoesNotContain(host.Probe.History, item => item.StartsWith("start:v2:"));
            Assert.Contains(host.Errors, error => error.Message == "old generation refused to stop");
            Assert.Equal(PluginReplacementPhase.Retirement, PluginReplacementFailure.FromException(failure)!.Phase);
            Assert.Contains("stop:v1:b", host.Probe.History);
            Assert.Contains("stop:v1:direct", host.Probe.History);
            // A rejected replacement alone cannot fence a plugin's retained references or event source.
            Assert.Equal("v1", old.Execute());
            var ticks = host.Probe.History.Count(item => item == "tick:v1:a");
            host.Probe.Pulse();
            Assert.Equal(ticks + 1, host.Probe.History.Count(item => item == "tick:v1:a"));
        }
        finally
        {
            // Stop the deliberately retained fixture after recording the unsafe result.
            host.Probe.FailStop = false;
            await old.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_failure_restores_services_or_releases_owned_resources_on_shutdown(bool failRecovery)
    {
        await using var host = await ProbeHost.StartAsync();
        var old = await host.ServicesAsync();
        host.Probe.FailRecovery = failRecovery;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync("bad", guard: true));
        Assert.Equal("retirement candidate activation failed", error.Message);
        Assert.Equal(
            failRecovery ? PluginRecoveryState.Failed : PluginRecoveryState.Succeeded,
            PluginReplacementFailure.FromException(error)!.Recovery);
        Assert.All(old, service => Assert.Throws<ObjectDisposedException>(() => service.Execute()));
        Assert.True(Assert.Single(host.Resolver.Unloads).UnloadRequested);
        if (failRecovery)
        {
            Assert.Contains(host.Errors, diagnostic => diagnostic.Message == "V1 recovery refused");
            await host.Context.DisposeAsync();
            var history = host.Probe.History.ToArray();
            host.Probe.Pulse();
            Assert.Equal(history, host.Probe.History.ToArray());
            foreach (var id in new[] { "a", "b", "direct", "other" })
            {
                using var resource = File.Open(
                    Path.Combine(host.Probe.ResourceDirectory, id + ".lock"),
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
        }
        else
        {
            Assert.All(await host.ServicesAsync(), service => Assert.Equal("v1", service.Execute()));
            Assert.All(new[] { "a", "b" }, id => Assert.Equal(FiberState.Active, host.Loader.Resolve(id).Fiber!.State));
            Assert.Contains("stop:bad-v2:b", host.Probe.History);
        }
    }

    private sealed class ProbeHost : IAsyncDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("cordis-online-probe-").FullName;

        public Context Context
        {
            get;
        }

        public ClrModuleResolver Resolver
        {
            get;
        }

        public RetirementProbe Probe
        {
            get;
        }

        public Loader Loader
        {
            get;
            private set;
        } = null!;

        public List<Exception> Errors
        {
            get;
        } = [];

        private ProbeHost(bool exclusiveResources)
        {
            Context = new Context(Errors.Add);
            Resolver = new ClrModuleResolver(Path.Combine(directory, "shadow"), [typeof(IRetirementService).Assembly]);
            Probe = new RetirementProbe(directory)
            {
                ExclusiveResources = exclusiveResources
            };
        }

        public static async Task<ProbeHost> StartAsync(bool exclusiveResources = true)
        {
            var host = new ProbeHost(exclusiveResources);
            host.Resolver.Register("fixture", Definition("v1"));
            host.Resolver.Register("other", Definition("v1"));
            await host.Context.RunAsync(async ctx =>
            {
                ctx.Provide("retirement-probe", host.Probe);
                host.Loader = new Loader(ctx, host.Resolver, diagnostic: host.Errors.Add);
                foreach (var id in new[] { "a", "b", "other" })
                    await host.Loader.CreateAsync(
                        new()
                        {
                            Id = id,
                            Name = id == "other" ? "other" : "fixture",
                            Config = id
                        });
                await host.Loader.WaitAsync();
                await ctx
                    .Plugin(await host.Resolver.ResolveAsync("fixture", new Uri("file:///")), "direct")
                    .WaitAsync();
            });
            return host;
        }

        public async Task<IRetirementService[]> ServicesAsync()
        {
            IRetirementService[] services = [];
            await Context.RunAsync(ctx =>
            {
                services = new[] { "a", "b", "direct" }
                    .Select(id => ctx.Get<IRetirementService>("retirement-" + id)!)
                    .ToArray();
                Assert.Equal("v1", ctx.Get<IRetirementService>("retirement-other")!.Execute());
                return Task.CompletedTask;
            });
            return services;
        }

        public async Task<ClrUnloadObservation> ReplaceAsync(string version, bool guard)
        {
            var old = await ServicesAsync();
            return await Resolver.ReplaceAsync(
                "fixture",
                Definition(version),
                async (previous, candidate) =>
                {
                    if (guard)
                    {
                        if (!Probe.AllowRetirement)
                            throw new InvalidOperationException("retirement not confirmed");
                        foreach (var service in old)
                            service.CloseAdmission();
                    }

                    await Loader.ReplacePluginAsync(previous, candidate);
                });
        }

        private static ClrModuleDefinition Definition(string version) =>
            new(
                Path.Combine(AppContext.BaseDirectory, "fixtures", version),
                "VersionedPlugin.dll",
                "VersionedPlugin.RetirementEntry")
            {
                LoadMode = ClrModuleLoadMode.ShadowCopy
            };

        public async ValueTask DisposeAsync()
        {
            Probe.Release.TrySetResult();
            await Context.DisposeAsync();
            await Resolver.DisposeAsync();
            foreach (var observation in Resolver.Unloads)
                observation.TryDeleteShadow();
            // Retained collectible references can keep shadow files locked; this is not a success condition.
            if (!Directory.Exists(Path.Combine(directory, "shadow")) ||
                !Directory.EnumerateFileSystemEntries(Path.Combine(directory, "shadow")).Any())
                Directory.Delete(directory, recursive: true);
        }
    }
}
