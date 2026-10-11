using System.Net;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Fixtures;
using Cordis.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class ReplacementHostTests
{
    [Theory]
    [InlineData(ClrModuleLoadMode.ShadowCopy)]
    [InlineData(ClrModuleLoadMode.StableDirectory)]
    public async Task Live_host_replaces_two_entries_and_direct_fiber_without_restart(ClrModuleLoadMode mode)
    {
        await using var host = await FixtureApplication.StartAsync(mode);
        var pid = Environment.ProcessId;
        var old = host.Probe.Contributions.ToArray();
        var other = host.Loader.Resolve("other").Fiber;
        await host.ReplaceAsync("v2");
        Assert.Equal(pid, Environment.ProcessId);
        Assert.Same(other, host.Loader.Resolve("other").Fiber);
        foreach (var id in new[] { "a", "b", "direct" })
            Assert.Equal("v2", await host.Client.GetStringAsync("work/" + id));
        Assert.Equal("v1", await host.Client.GetStringAsync("work/other"));
        AssertInactive(old.Where(item => item.Id != "other"));
        var ticks = host.Probe.History.Count(item => item.StartsWith("tick:v1:") && !item.EndsWith(":other"));
        host.Probe.Pulse();
        Assert.Equal(ticks, host.Probe.History.Count(item => item.StartsWith("tick:v1:") && !item.EndsWith(":other")));
        Assert.Contains("tick:v2:direct", host.Probe.History);
        Assert.True(Assert.Single(host.Resolver.Unloads).UnloadRequested);
        if (mode == ClrModuleLoadMode.StableDirectory)
        {
            var observation = Assert.Single(host.Resolver.Unloads);
            Assert.Null(observation.ShadowDirectory);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "fixtures", "v1"), observation.LoadDirectory);
            Assert.True(observation.TryDeleteShadow());
            Assert.True(File.Exists(Path.Combine(observation.LoadDirectory, "VersionedPlugin.dll")));
        }
    }

    [Fact]
    public async Task Pre_retirement_refusal_preserves_live_host_graph_and_business()
    {
        await using var host = await FixtureApplication.StartAsync();
        var fibers = new[] { "a", "b", "other" }.Select(id => host.Loader.Resolve(id).Fiber).ToArray();
        var callback = host.Probe.Contributions.First(item => item.Id == "a").Service.CaptureCallback();
        host.Probe.AllowRetirement = false;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync("v2"));
        Assert.Null(PluginReplacementFailure.FromException(failure));
        Assert.Equal(fibers, new[] { "a", "b", "other" }.Select(id => host.Loader.Resolve(id).Fiber));
        foreach (var id in new[] { "a", "b", "direct", "other" })
            Assert.Equal("v1", await host.Client.GetStringAsync("work/" + id));
        callback();
        host.Probe.Pulse();
        Assert.Contains("tick:v1:a", host.Probe.History);
        Assert.DoesNotContain(host.Probe.History, item => item.StartsWith("stop:") || item.StartsWith("start:v2:"));
    }

    [Fact]
    public async Task Retirement_failure_closes_host_entries_and_invalidates_late_business_without_closing_context()
    {
        await using var host = await FixtureApplication.StartAsync();
        var old = host.Probe.Contributions.First(item => item.Id == "a").Service;
        var callback = old.CaptureCallback();
        var lateCall = old.ExecuteAsync();
        await host.Probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        host.Probe.FailStop = true;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync("v2"));
        Assert.Equal("old generation refused to stop", failure.Message);
        var outcome = PluginReplacementFailure.FromException(failure)!;
        Assert.Equal(PluginReplacementPhase.Retirement, outcome.Phase);
        Assert.Equal(PluginRecoveryState.NotAttempted, outcome.Recovery);
        Assert.False(host.RecoveryVerified);
        Assert.DoesNotContain(host.Probe.History, item => item.StartsWith("start:v2:"));
        Assert.Contains("stop:v1:b", host.Probe.History);
        Assert.Contains("stop:v1:direct", host.Probe.History);
        await AssertClosedAsync(host);
        Assert.Throws<ObjectDisposedException>(callback);
        host.Probe.Release.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => lateCall);
        Assert.DoesNotContain("drained:v1:a", host.Probe.History);
    }

    [Theory]
    [InlineData(false, false, PluginRecoveryState.Succeeded, ClrModuleLoadMode.ShadowCopy)]
    [InlineData(true, false, PluginRecoveryState.Failed, ClrModuleLoadMode.ShadowCopy)]
    [InlineData(false, true, PluginRecoveryState.NotAttempted, ClrModuleLoadMode.ShadowCopy)]
    [InlineData(false, false, PluginRecoveryState.Succeeded, ClrModuleLoadMode.StableDirectory)]
    [InlineData(true, false, PluginRecoveryState.Failed, ClrModuleLoadMode.StableDirectory)]
    [InlineData(false, true, PluginRecoveryState.NotAttempted, ClrModuleLoadMode.StableDirectory)]
    public async Task Candidate_failure_requires_actual_recovery_or_scoped_host_failure_closure(
        bool failRecovery,
        bool failCandidateStop,
        PluginRecoveryState expectedRecovery,
        ClrModuleLoadMode mode)
    {
        await using var host = await FixtureApplication.StartAsync(mode);
        var old = host.Probe.Contributions.Where(item => item.Id != "other").ToArray();
        IPlugin? original = null;
        await host.Context.RunAsync(async _ =>
            original = await host.Resolver.ResolveAsync("fixture", new Uri("file:///")));
        var verifiedAfterRollback = false;
        host.BusinessVerification = async () =>
        {
            Assert.Equal(PluginRecoveryState.Succeeded, expectedRecovery);
            Assert.True(Assert.Single(host.Resolver.Unloads).UnloadRequested);
            Assert.False(host.RecoveryVerified);
            await AssertClosedAsync(host);
            await host.Context.RunAsync(async _ =>
                Assert.Same(original, await host.Resolver.ResolveAsync("fixture", new Uri("file:///"))));
            verifiedAfterRollback = true;
        };
        host.Probe.FailRecovery = failRecovery;
        host.Probe.FailCandidateStop = failCandidateStop;
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync("bad"));
        Assert.Equal("retirement candidate activation failed", failure.Message);
        var outcome = PluginReplacementFailure.FromException(failure)!;
        Assert.Equal(PluginReplacementPhase.Activation, outcome.Phase);
        Assert.Equal(expectedRecovery, outcome.Recovery);
        Assert.Equal(expectedRecovery == PluginRecoveryState.Succeeded, verifiedAfterRollback);
        var candidate = host.Probe.Contributions.Where(item => item.Version == "bad-v2").ToArray();
        Assert.NotEmpty(candidate);
        AssertInactive(old.Concat(candidate));
        if (expectedRecovery == PluginRecoveryState.Succeeded)
        {
            Assert.True(host.RecoveryVerified);
            foreach (var id in new[] { "a", "b", "direct" })
                Assert.Equal("v1", await host.Client.GetStringAsync("work/" + id));
            var badTicks = host.Probe.History.Count(item => item.StartsWith("tick:bad-v2:"));
            host.Probe.Pulse();
            Assert.Equal(badTicks, host.Probe.History.Count(item => item.StartsWith("tick:bad-v2:")));
            Assert.Contains("tick:v1:a", host.Probe.History);
        }
        else
        {
            Assert.False(host.RecoveryVerified);
            await AssertClosedAsync(host);
            if (failCandidateStop)
            {
                Assert.Contains(
                    outcome.CleanupErrors,
                    error => error.Message == "candidate generation refused to stop");
                Assert.Equal(
                    3,
                    host.Probe.History.Count(item => item.StartsWith("start:v1:") && !item.EndsWith(":other")));
            }
            else
            {
                Assert.Contains(outcome.RecoveryErrors, error => error.Message == "V1 recovery refused");
                foreach (var id in new[] { "a", "direct" })
                {
                    Assert.Equal(2, host.Probe.History.Count(item => item == "start:v1:" + id));
                    Assert.Equal(2, host.Probe.History.Count(item => item == "stop:v1:" + id));
                }
            }
        }

        Assert.Equal("v1", await host.Client.GetStringAsync("work/other"));
        await host.Context.RunAsync(async _ =>
        {
            var original = await host.Resolver.ResolveAsync("fixture", new Uri("file:///"));
            if (expectedRecovery != PluginRecoveryState.Succeeded)
                Assert.Null(host.Context.Registry.Get(original));
        });
    }

    [Fact]
    public async Task Business_verification_keeps_admission_closed_while_events_and_reopening_overlap()
    {
        await using var host = await FixtureApplication.StartAsync();
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IPlugin? original = null;
        await host.Context.RunAsync(async _ =>
            original = await host.Resolver.ResolveAsync("fixture", new Uri("file:///")));
        host.BusinessVerification = async () =>
        {
            verifying.TrySetResult();
            await release.Task;
        };
        host.BeforeResolverCommit = async () =>
        {
            verified.TrySetResult();
            await commit.Task;
        };
        var replacing = host.ReplaceAsync("v2");
        try
        {
            await verifying.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertClosedAsync(host);
            var services = host.Probe.Contributions.Where(item => item.Version == "v2").ToArray();
            Assert.Equal(3, services.Length);
            var pulses = Task.Run(() =>
            {
                for (var index = 0;index < 128;index++)
                {
                    host.Probe.Pulse();
                }
            });
            await Task
                .Run(() =>
                {
                    foreach (var item in services)
                    {
                        Assert.Equal("v2", item.Service.VerifyReady());
                        Assert.Throws<ObjectDisposedException>(() => item.Service.Execute());
                    }
                })
                .WaitAsync(TimeSpan.FromSeconds(5));
            release.TrySetResult();
            await verified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertClosedAsync(host);
            await host.Context.RunAsync(async ctx =>
            {
                Assert.Same(original, await host.Resolver.ResolveAsync("fixture", new Uri("file:///")));
                Assert.Equal("v2", ctx.Get<IRetirementService>("retirement-a")!.VerifyReady());
            });
            Assert.False(replacing.IsCompleted);
            Assert.Empty(host.Resolver.Unloads);
            commit.TrySetResult();
            await Task.WhenAll(pulses, replacing).WaitAsync(TimeSpan.FromSeconds(5));
            await host.Context.RunAsync(async _ =>
                Assert.NotSame(original, await host.Resolver.ResolveAsync("fixture", new Uri("file:///"))));
            foreach (var id in new[] { "a", "b", "direct" })
                Assert.Equal("v2", await host.Client.GetStringAsync("work/" + id));
            host.Probe.Pulse();
            Assert.Contains("tick:v2:a", host.Probe.History);
            Assert.False(host.RequiresIntervention);
        }
        finally
        {
            release.TrySetResult();
            commit.TrySetResult();
        }
    }

    [Fact]
    public async Task Concurrent_host_replacements_wait_until_business_admission_is_settled()
    {
        await using var host = await FixtureApplication.StartAsync();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var verifyingSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var openings = 0;
        host.BeforeBusinessOpen = async () =>
        {
            if (Interlocked.Increment(ref openings) == 1)
            {
                committed.TrySetResult();
                await openFirst.Task;
            }
        };
        var first = host.ReplaceAsync("v2");
        Task? second = null;
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertClosedAsync(host);
            IPlugin? firstCandidate = null;
            await host.Context.RunAsync(async _ =>
                firstCandidate = await host.Resolver.ResolveAsync("fixture", new Uri("file:///")));
            var fibers = new[] { "a", "b", "other" }.Select(id => host.Loader.Resolve(id).Fiber).ToArray();
            var history = host.Probe.History.ToArray();
            Assert.Single(host.Resolver.Unloads);
            host.BusinessVerification = async () =>
            {
                verifyingSecond.TrySetResult();
                await finishSecond.Task;
            };
            second = host.ReplaceAsync("v2");
            await Assert.ThrowsAsync<TimeoutException>(() =>
                verifyingSecond.Task.WaitAsync(TimeSpan.FromMilliseconds(250)));
            Assert.False(second.IsCompleted);
            Assert.Equal(fibers, new[] { "a", "b", "other" }.Select(id => host.Loader.Resolve(id).Fiber));
            Assert.Equal(history, host.Probe.History);
            Assert.Single(host.Resolver.Unloads);
            openFirst.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            await verifyingSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await AssertClosedAsync(host);
            finishSecond.TrySetResult();
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var id in new[] { "a", "b", "direct" })
                Assert.Equal("v2", await host.Client.GetStringAsync("work/" + id));
            await host.Context.RunAsync(async _ =>
                Assert.NotSame(firstCandidate, await host.Resolver.ResolveAsync("fixture", new Uri("file:///"))));
            Assert.Equal(2, host.Resolver.Unloads.Count);
            Assert.False(host.RequiresIntervention);
        }
        finally
        {
            openFirst.TrySetResult();
            finishSecond.TrySetResult();
            await Task.WhenAll(first, second ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Business_failure_after_loader_success_requires_intervention_and_refuses_further_replacement()
    {
        await using var host = await FixtureApplication.StartAsync();
        IPlugin? original = null;
        await host.Context.RunAsync(async _ =>
            original = await host.Resolver.ResolveAsync("fixture", new Uri("file:///")));
        var businessFailure = new InvalidOperationException("V2 business verification failed");
        host.BusinessVerification = () => host.Context.RunAsync(ctx =>
        {
            foreach (var id in new[] { "a", "b", "direct" })
                Assert.Equal("v2", ctx.Get<IRetirementService>("retirement-" + id)!.VerifyReady());
            throw businessFailure;
        });
        Assert.Same(
            businessFailure,
            await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync("v2")));
        Assert.Null(PluginReplacementFailure.FromException(businessFailure));
        Assert.True(host.RequiresIntervention);
        Assert.False(host.RecoveryVerified);
        await AssertClosedAsync(host);
        await host.Context.RunAsync(async ctx =>
        {
            Assert.Same(original, await host.Resolver.ResolveAsync("fixture", new Uri("file:///")));
            Assert.Null(ctx.Registry.Get(original!));
            Assert.Equal(FiberState.Active, host.Loader.Resolve("a").Fiber!.State);
            Assert.Equal("v2", ctx.Get<IRetirementService>("retirement-a")!.VerifyReady());
        });
        var fibers = new[] { "a", "b", "other" }.Select(id => host.Loader.Resolve(id).Fiber).ToArray();
        var history = host.Probe.History.ToArray();
        var unloads = host.Resolver.Unloads.Count;
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.ReplaceAsync("v2"));
        Assert.Equal("replacement requires manual intervention", refused.Message);
        Assert.Equal(fibers, new[] { "a", "b", "other" }.Select(id => host.Loader.Resolve(id).Fiber));
        Assert.Equal(history, host.Probe.History);
        Assert.Equal(unloads, host.Resolver.Unloads.Count);
        await AssertClosedAsync(host);
    }

    private static void AssertInactive(IEnumerable<RetirementContribution> contributions)
    {
        foreach (var item in contributions)
        {
            Assert.Throws<ObjectDisposedException>(() => item.Service.Execute());
            Assert.Throws<ObjectDisposedException>(item.Service.CaptureCallback());
        }
    }

    private static async Task AssertClosedAsync(FixtureApplication host)
    {
        foreach (var id in new[] { "a", "b", "direct" })
        {
            using var response = await host.Client.GetAsync("work/" + id);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        var business = host.Probe.History.Where(item => item.StartsWith("tick:") && !item.EndsWith(":other")).ToArray();
        host.Probe.Pulse();
        Assert.Equal(business, host.Probe.History.Where(item => item.StartsWith("tick:") && !item.EndsWith(":other")));
        Assert.Equal("v1", await host.Client.GetStringAsync("work/other"));
        // Cordis and the real HTTP Host remain live; this proof does not rely on Context.Dispose.
        await host.Context.RunAsync(ctx =>
        {
            Assert.NotNull(ctx.Fiber.Uid);
            return Task.CompletedTask;
        });
    }

    // Application-owned business authority, borrowed through Generic Host DI. This is a fixture,
    // not a second runtime or a Cordis promise to intercept arbitrary plugin side effects.
    private sealed class BusinessAuthority
    {
        private readonly object gate = new();
        private int state;

        public bool Perform(string id, Action action) => Run(id, action, allowDrain: false);
        public bool Complete(string id, Action action) => Run(id, action, allowDrain: true);

        private bool Run(string id, Action action, bool allowDrain)
        {
            lock (gate)
            {
                if (id != "other" && (state == 2 || (state == 1 && !allowDrain)))
                    return false;
                action();
                return true;
            }
        }

        public void Drain()
        {
            lock (gate)
                state = 1;
        }

        public void Close()
        {
            lock (gate)
                state = 2;
        }

        public void Open()
        {
            lock (gate)
                state = 0;
        }
    }

    private sealed class FixtureApplication : IAsyncDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("cordis-replacement-host-").FullName;
        private readonly BusinessAuthority authority = new();
        private readonly SemaphoreSlim updates = new(1, 1);
        private WebApplication application = null!;
        private volatile bool requiresIntervention;
        private readonly ClrModuleLoadMode loadMode;

        public Context Context
        {
            get;
        } = new();

        public RetirementProbe Probe
        {
            get;
        }

        public ClrModuleResolver Resolver
        {
            get;
        }

        public Loader Loader
        {
            get;
            private set;
        } = null!;

        public HttpClient Client
        {
            get;
            private set;
        } = null!;

        public bool RecoveryVerified
        {
            get;
            private set;
        }

        public bool RequiresIntervention => requiresIntervention;

        public Func<Task>? BusinessVerification
        {
            get;
            set;
        }

        public Func<Task>? BeforeResolverCommit
        {
            get;
            set;
        }

        public Func<Task>? BeforeBusinessOpen
        {
            get;
            set;
        }

        private FixtureApplication(ClrModuleLoadMode mode)
        {
            loadMode = mode;
            Resolver = new ClrModuleResolver(Path.Combine(directory, "shadow"), [typeof(IRetirementService).Assembly]);
            Probe = new RetirementProbe(directory)
            {
                PerformBusiness = authority.Perform,
                CompleteBusiness = authority.Complete
            };
        }

        public static async Task<FixtureApplication> StartAsync(ClrModuleLoadMode mode = ClrModuleLoadMode.ShadowCopy)
        {
            var host = new FixtureApplication(mode);
            host.Resolver.Register("fixture", host.Definition("v1"));
            host.Resolver.Register("other", host.Definition("other-v1"));
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0));
            builder.Services.AddSingleton(host.Context);
            builder.Services.AddSingleton(host.Probe);
            builder.Services.AddSingleton(host.authority);
            builder.Services.AddCordis(options => options
                .Borrow<RetirementProbe>("retirement-probe")
                .Borrow<BusinessAuthority>("business-authority")
                .Configure(async (ctx, _, cancellationToken) =>
                {
                    host.Loader = new Loader(ctx, host.Resolver);
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
                        .Plugin(
                            await host.Resolver.ResolveAsync("fixture", new Uri("file:///"), cancellationToken),
                            "direct")
                        .WaitAsync();
                }));
            host.application = builder.Build();
            host.application.MapGet(
                "/work/{id}",
                async (string id, BusinessAuthority authority) =>
                {
                    if (!authority.Perform(
                            id,
                            () =>
                            {
                            }))
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    IResult result = Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                    await host.Context.RunAsync(ctx =>
                    {
                        try
                        {
                            var service = ctx.Get<IRetirementService>("retirement-" + id);
                            if (service is not null)
                                result = Results.Text(service.Execute());
                        }
                        catch (ObjectDisposedException)
                        {
                        }

                        return Task.CompletedTask;
                    });
                    return result;
                });
            await host.application.StartAsync();
            var address =
                host.application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                    .Addresses.Single();
            host.Client = new HttpClient
            {
                BaseAddress = new Uri(address + "/"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            return host;
        }

        public async Task ReplaceAsync(string version)
        {
            await updates.WaitAsync();
            try
            {
                await ReplaceCoreAsync(version);
            }
            finally
            {
                updates.Release();
            }
        }

        private async Task ReplaceCoreAsync(string version)
        {
            if (RequiresIntervention)
                throw new InvalidOperationException("replacement requires manual intervention");
            var retirementStarted = false;
            var recoverySucceeded = false;
            try
            {
                await Resolver.ReplaceAsync(
                    "fixture",
                    Definition(version),
                    async (previous, candidate) =>
                    {
                        // Recheck after the resolver's mutation gate for callers already queued.
                        if (RequiresIntervention)
                            throw new InvalidOperationException("replacement requires manual intervention");
                        if (!Probe.AllowRetirement)
                            throw new InvalidOperationException("retirement not confirmed");
                        authority.Drain();
                        retirementStarted = true;
                        try
                        {
                            await Loader.ReplacePluginAsync(previous, candidate);
                        }
                        catch (Exception error)
                        {
                            authority.Close();
                            recoverySucceeded = PluginReplacementFailure.FromException(error)?.Recovery ==
                                PluginRecoveryState.Succeeded;
                            if (!recoverySucceeded)
                                requiresIntervention = true;
                            throw;
                        }

                        try
                        {
                            await VerifyBusinessAsync("v2");
                            if (BeforeResolverCommit is { } beforeCommit)
                                await beforeCommit();
                        }
                        catch
                        {
                            // Loader has committed its graph. Resolver callback failure cannot undo it.
                            authority.Close();
                            requiresIntervention = true;
                            throw;
                        }
                    });
                if (BeforeBusinessOpen is { } beforeOpen)
                    await beforeOpen();
            }
            catch
            {
                if (retirementStarted)
                {
                    authority.Close();
                    if (recoverySucceeded)
                    {
                        try
                        {
                            // Resolver rollback has finished; verify V1 before reopening admission.
                            await VerifyBusinessAsync("v1");
                            if (BeforeBusinessOpen is { } beforeOpen)
                                await beforeOpen();
                            RecoveryVerified = true;
                            authority.Open();
                        }
                        catch
                        {
                            requiresIntervention = true;
                        }
                    }
                    else
                    {
                        requiresIntervention = true;
                    }
                }

                throw;
            }

            // The callback only verifies readiness. Resolver has now committed its mapping.
            authority.Open();
        }

        private async Task VerifyBusinessAsync(string expected)
        {
            authority.Close();
            if (BusinessVerification is { } verification)
                await verification();
            await Context.RunAsync(ctx =>
            {
                foreach (var id in new[] { "a", "b", "direct" })
                    Assert.Equal(expected, ctx.Get<IRetirementService>("retirement-" + id)!.VerifyReady());
                return Task.CompletedTask;
            });
        }

        private ClrModuleDefinition Definition(string version) => new(
            Path.Combine(AppContext.BaseDirectory, "fixtures", version),
            "VersionedPlugin.dll",
            "VersionedPlugin.RetirementEntry")
        {
            LoadMode = loadMode
        };

        public async ValueTask DisposeAsync()
        {
            authority.Close();
            Probe.Release.TrySetResult();
            Probe.FailStop = false;
            Probe.FailCandidateStop = false;
            await application.StopAsync();
            await application.DisposeAsync();
            // Explicit fixture teardown cannot turn an already reported retirement failure into success.
            foreach (var item in Probe.Contributions)
            {
                try
                {
                    await item.Service.DisposeAsync();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            await Resolver.DisposeAsync();
            Client.Dispose();
            updates.Dispose();
            // Keep shadow remnants for separate observation; never force deletion or GC.
        }
    }
}
