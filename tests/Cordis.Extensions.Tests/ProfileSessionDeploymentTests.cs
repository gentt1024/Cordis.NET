using Cordis.Composition;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class ProfileSessionDeploymentTests
{
    [Fact]
    public async Task Dropped_rows_finish_disposal_with_old_mapping_before_the_successor_publishes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Worker(async _ =>
        {
            Assert.Same(fixture.Initial, fixture.Packages.Generation);
            Assert.Equal(fixture.WorkerDirectory, fixture.Packages.PackageDirectory("worker", fixture.Parent));
            entered.TrySetResult();
            await release.Task;
        });
        await using var session = await fixture.StartAsync();
        var sibling = session.Loader.Resolve("root:stable").Fiber;
        fixture.Next = fixture.WithoutWorker();
        await File.WriteAllTextAsync(fixture.Config, "- id: stable\n  name: stable\n");
        var refreshing = session.RefreshAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(refreshing.IsCompleted);
            Assert.Same(fixture.Initial, fixture.Packages.Generation);
        }
        finally
        {
            release.TrySetResult();
        }

        var result = await refreshing;
        Assert.False(result.RequiresRestart);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Pending);
        Assert.Same(fixture.Next, fixture.Packages.Generation);
        Assert.Null(fixture.Packages.PackageDirectory("worker", fixture.Parent));
        Assert.Same(sibling, session.Loader.Resolve("root:stable").Fiber);
        Assert.DoesNotContain(session.Loader.Entries(), entry => entry.Options.Id == "worker");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_publication_restores_group_children_and_original_row_positions(bool failRecovery)
    {
        await using var fixture = await Fixture.CreateAsync();
        const string original = """
                                - id: before
                                  name: stable
                                - id: group
                                  name: cordis:group
                                  group: true
                                  config:
                                    - id: first
                                      name: worker
                                      config: first
                                    - id: second
                                      name: worker
                                      config: second
                                - id: after
                                  name: stable
                                """;
        await File.WriteAllTextAsync(fixture.Config, original);
        var starts = new List<string>();
        var stops = new List<string>();
        DeploymentGeneration? raced = null;
        fixture.Worker(
            id =>
            {
                stops.Add(id);
                if (raced is null)
                {
                    raced = new(
                        fixture.Initial.ProfilesDirectory,
                        fixture.Initial.ProfileDirectory,
                        fixture.Initial.Entries.Append(
                            new(
                                "host",
                                fixture.WorkerDirectory,
                                "1",
                                fixture.Config,
                                DeploymentPackageScope.Installation)));
                    fixture.Packages.Replace(raced);
                }

                return Task.CompletedTask;
            },
            id =>
            {
                starts.Add(id);
                if (failRecovery && starts.Count > 2 && id == "first")
                    throw new InvalidOperationException("recovery activation failed");
            });
        await using var session = await fixture.StartAsync();
        var errors = new List<Exception>();
        session.Error += errors.Add;
        var before = session.Loader.Resolve("root:before").Fiber;
        var after = session.Loader.Resolve("root:after").Fiber;
        fixture.Next = fixture.WithoutWorker();
        await File.WriteAllTextAsync(
            fixture.Config,
            "- id: before\n  name: stable\n- id: after\n  name: stable\n");
        var result = await session.RefreshAsync();
        Assert.Equal("host-module", result.RestartReason);
        Assert.True(session.RequiresRestart);
        Assert.Same(raced, fixture.Packages.Generation);
        Assert.Equal(["before", "group", "after"], session.Include.Root.Data.Select(row => row.Id));
        var group = session.Loader.Resolve("root:group").Subgroup!;
        Assert.Equal(["first", "second"], group.Data.Select(row => row.Id));
        Assert.Equal(["first", "second"], stops.Order(StringComparer.Ordinal));
        Assert.Equal(["first", "second", "first", "second"], starts);
        Assert.Equal(
            failRecovery ? FiberState.Failed : FiberState.Active,
            session.Loader.Resolve("root:first").Fiber!.State);
        if (failRecovery)
            Assert.Contains(
                errors,
                error => error.Message.Contains("recovery activation failed", StringComparison.Ordinal));
        Assert.Same(before, session.Loader.Resolve("root:before").Fiber);
        Assert.Same(after, session.Loader.Resolve("root:after").Fiber);
        Assert.Equal(
            "- id: before\n  name: stable\n- id: after\n  name: stable\n",
            await File.ReadAllTextAsync(fixture.Config));
    }

    [Theory]
    [InlineData(DeploymentPackageScope.Profile, "package-moved")]
    [InlineData(DeploymentPackageScope.Installation, "host-module")]
    public async Task Invalid_successor_is_refused_before_stopping_rows(
        DeploymentPackageScope scope,
        string reason)
    {
        await using var fixture = await Fixture.CreateAsync(scope);
        var stops = 0;
        fixture.Worker(_ =>
        {
            stops++;
            return Task.CompletedTask;
        });
        await using var session = await fixture.StartAsync();
        var worker = session.Loader.Resolve("root:worker").Fiber;
        fixture.Next = new(
            fixture.Initial.ProfilesDirectory,
            fixture.Initial.ProfileDirectory,
            fixture.Initial.Entries.Select(entry => entry.Name == "worker"
                ? entry with
                {
                    Directory = fixture.StableDirectory
                }
                : entry));
        await File.WriteAllTextAsync(fixture.Config, "- id: stable\n  name: stable\n");
        var result = await session.RefreshAsync();
        Assert.Equal(reason, result.RestartReason);
        Assert.Equal(0, stops);
        Assert.Same(worker, session.Loader.Resolve("root:worker").Fiber);
        Assert.Same(fixture.Initial, fixture.Packages.Generation);
    }

    [Fact]
    public async Task Cleanup_failure_blocks_publication_restoration_and_later_activation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var failure = new IOException("retirement failed");
        var starts = 0;
        fixture.Worker(_ => Task.FromException(failure), _ => starts++);
        await using var session = await fixture.StartAsync();
        fixture.Next = fixture.WithoutWorker();
        await File.WriteAllTextAsync(fixture.Config, "- id: stable\n  name: stable\n");
        var error = await Assert.ThrowsAsync<AggregateException>(() => session.RefreshAsync());
        Assert.Contains(failure, error.InnerExceptions);
        Assert.Same(fixture.Initial, fixture.Packages.Generation);
        Assert.Equal(1, starts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshAsync());
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_import_retries_when_registration_or_dependency_arrives_without_input_edits(
        bool missingPackage)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (missingPackage)
        {
            fixture.Initial = fixture.WithoutWorker();
            fixture.Next = fixture.Initial;
            fixture.ResetResolver();
        }

        var starts = 0;
        if (missingPackage)
            fixture.Worker(start: _ => starts++);
        await using var session = await fixture.StartAsync();
        Assert.Null(session.Loader.Resolve("root:worker").Fiber);
        var historical = await session.RefreshAsync();
        Assert.Single(historical.Warnings);
        if (missingPackage)
            fixture.Next = new(
                fixture.Initial.ProfilesDirectory,
                fixture.Initial.ProfileDirectory,
                fixture.Initial.Entries.Append(fixture.WorkerEntry));
        else
            fixture.Worker(start: _ => starts++);
        var result = await session.RefreshAsync();
        Assert.Empty(result.Warnings);
        Assert.False(result.RequiresRestart);
        Assert.Equal(FiberState.Active, session.Loader.Resolve("root:worker").Fiber!.State);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task Retried_import_with_a_new_activation_failure_is_rejected_then_remains_a_warning()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var session = await fixture.StartAsync();
        fixture.Resolver.Register(
            "worker",
            new Plugin<object?>
            {
                Apply = (_, _) => throw new InvalidOperationException("new activation failure")
            });
        var failure = await Assert.ThrowsAsync<StartupException>(() => session.RefreshAsync());
        Assert.Contains(failure.Diagnostics, entry => entry.Error?.Message == "new activation failure");
        var repeated = await session.RefreshAsync();
        Assert.Single(repeated.Warnings);
        var required = await session.ConfigurationOperations.SetPluginEnabledAsync("root:worker", true);
        Assert.Equal("failed", required.Application);
    }

    [Fact]
    public async Task A_new_import_failure_from_an_edited_base_file_rejects_the_refresh()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Worker();
        await using var session = await fixture.StartAsync();
        var successful = session.LastSuccessfulRefresh;
        await File.AppendAllTextAsync(fixture.Config, "- id: broken\n  name: not-registered\n");
        var failure = await Assert.ThrowsAsync<StartupException>(() => session.RefreshAsync());
        Assert.Equal("root:broken", Assert.Single(failure.Diagnostics).Id);
        Assert.Same(failure, session.LastError);
        Assert.Equal(successful, session.LastSuccessfulRefresh);
        var repeated = await session.RefreshAsync();
        Assert.Equal("root:broken", Assert.Single(repeated.Warnings).Id);
    }

    [Fact]
    public async Task A_previously_active_retained_entry_cannot_become_a_historical_warning_during_withdrawal()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Resolver.Register(
            "worker",
            new Plugin<string>
            {
                Apply = (ctx, _) => ctx.Provide("dependency", new object())
            });
        fixture.Resolver.Register(
            "stable",
            new Plugin<object?>
            {
                Inject = ["dependency"],
                Apply = (_, _) =>
                {
                }
            });
        await using var session = await fixture.StartAsync();
        Assert.Equal(FiberState.Active, session.Loader.Resolve("root:stable").Fiber!.State);
        var successful = session.LastSuccessfulRefresh;
        fixture.Next = fixture.WithoutWorker();
        await File.WriteAllTextAsync(fixture.Config, "- id: stable\n  name: stable\n");
        var failure = await Assert.ThrowsAsync<StartupException>(() => session.RefreshAsync());
        Assert.Equal("root:stable", Assert.Single(failure.Diagnostics).Id);
        Assert.Equal(successful, session.LastSuccessfulRefresh);
        Assert.Same(fixture.Next, fixture.Packages.Generation);
        fixture.Next = fixture.Initial;
        await File.WriteAllTextAsync(
            fixture.Config,
            "- id: worker\n  name: worker\n  config: worker\n- id: stable\n  name: stable\n");
        Assert.Empty((await session.RefreshAsync()).Warnings);
        Assert.Equal(FiberState.Active, session.Loader.Resolve("root:stable").Fiber!.State);
    }

    [Fact]
    public async Task Retained_dependent_cleanup_failure_blocks_publication_before_mapping_withdrawal()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Resolver.Register(
            "worker",
            new Plugin<string>
            {
                Apply = (ctx, _) => ctx.Provide("dependency", new object())
            });
        var failure = new IOException("dependent retirement failed");
        fixture.Resolver.Register(
            "stable",
            new Plugin<object?>
            {
                Inject = ["dependency"],
                Apply = (ctx, _) => ctx.Effect(() => new Cleanup(() => Task.FromException(failure)))
            });
        await using var session = await fixture.StartAsync();
        Assert.Equal(FiberState.Active, session.Loader.Resolve("root:stable").Fiber!.State);
        fixture.Next = fixture.WithoutWorker();
        await File.WriteAllTextAsync(fixture.Config, "- id: stable\n  name: stable\n");
        var rejected = await Assert.ThrowsAsync<AggregateException>(() => session.RefreshAsync());
        Assert.Contains(failure, rejected.InnerExceptions);
        Assert.Same(fixture.Initial, fixture.Packages.Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RefreshAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Newly_selected_bundle_requires_its_declared_row_even_when_that_id_was_already_inactive(
        bool nested)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Resolver.Register(
            "worker",
            new Plugin<string>
            {
                Inject = ["missing-service"],
                Apply = (_, _) =>
                {
                }
            });
        await using var session = await fixture.StartAsync();
        Assert.Equal(FiberState.Pending, session.Loader.Resolve("root:worker").Fiber!.State);
        var bundle = Path.Combine(fixture.Directory, "bundle");
        System.IO.Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "package.json"),
            "{\"name\":\"bundle\",\"dsh\":{\"bundle\":{\"patch\":\"patch.yml\"}}}");
        await File.WriteAllTextAsync(
            Path.Combine(bundle, "patch.yml"),
            nested
                ? "- insert:\n    - id: group\n      name: cordis:group\n      group: true\n      config:\n        - id: worker\n          name: worker\n          config: worker\n"
                : "- insert:\n    - id: worker\n      name: worker\n      config: worker\n");
        ((Dictionary<string, string>)fixture.Launch.InstallationBundles)["bundle"] = bundle;
        await File.WriteAllTextAsync(
            Path.Combine(fixture.Launch.Profile.Directory, "package.json"),
            "{\"dsh\":{\"profile\":{\"bundles\":[\"bundle\"]}}}");
        var failure = await Assert.ThrowsAsync<StartupException>(() => session.RefreshAsync());
        Assert.Contains(failure.Diagnostics, entry => entry.Id == "root:worker" && entry.Required);
        Assert.Empty(session.SelectedBundles);
    }

    [Fact]
    public async Task Refresh_awaits_captured_retirement_already_started_by_another_caller()
    {
        await using var fixture = await Fixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Worker(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        await using var session = await fixture.StartAsync();
        var entry = session.Loader.Resolve("root:worker");
        await session.Context.RunAsync(_ =>
        {
            // Suppress the Loader's ordinary persistent switch for this independently initiated disposal.
            entry.Options.Disabled = true;
            return Task.CompletedTask;
        });
        var disposing = entry.Fiber!.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Next = fixture.WithoutWorker();
        await File.WriteAllTextAsync(fixture.Config, "- id: stable\n  name: stable\n");
        var refreshing = session.RefreshAsync();
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => refreshing.WaitAsync(TimeSpan.FromMilliseconds(150)));
            Assert.Same(fixture.Initial, fixture.Packages.Generation);
        }
        finally
        {
            release.TrySetResult();
        }

        await disposing;
        Assert.False((await refreshing).RequiresRestart);
        Assert.Same(fixture.Next, fixture.Packages.Generation);
    }

    [Theory]
    [InlineData("local-remove", null)]
    [InlineData("local-move", "package-moved")]
    [InlineData("installation-remove", "host-module")]
    public async Task Mapping_changes_without_a_recomputable_resolver_return_their_specific_restart_reason(
        string change,
        string? reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Worker();
        var installation = new Dictionary<string, string>();
        var local = new Dictionary<string, string>();
        (change == "installation-remove" ? installation : local)["bundle"] = fixture.WorkerDirectory;
        var launch = fixture.Launch with
        {
            InstallationBundles = installation,
            LocalBundles = local
        };
        await using var session = await ProfileSession.StartAsync(fixture.Config, launch, fixture.Modules);
        var worker = session.Loader.Resolve("root:worker").Fiber;
        if (change == "local-move")
            local["bundle"] = fixture.StableDirectory;
        else
            (change == "installation-remove" ? installation : local).Clear();
        var result = await session.RefreshAsync();
        Assert.Equal(reason, result.RestartReason);
        Assert.Equal(reason is not null, result.RequiresRestart);
        Assert.Same(worker, session.Loader.Resolve("root:worker").Fiber);
    }

    private sealed class Cleanup(Func<Task> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(action());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required string Directory
        {
            get;
            init;
        }

        public required string Config
        {
            get;
            init;
        }

        public required ProfileLaunch Launch
        {
            get;
            init;
        }

        public required string WorkerDirectory
        {
            get;
            init;
        }

        public required string StableDirectory
        {
            get;
            init;
        }

        public required DeploymentEntry WorkerEntry
        {
            get;
            init;
        }

        public required DeploymentGeneration Initial
        {
            get;
            set;
        }

        public required DeploymentGeneration Next
        {
            get;
            set;
        }

        public DeploymentPackageResolver Packages
        {
            get;
            private set;
        } = null!;

        public DeploymentModuleResolver Modules
        {
            get;
            private set;
        } = null!;

        public StaticModuleResolver Resolver
        {
            get;
        } = new();

        public Uri Parent => new(Path.Combine(Launch.Profile.Directory, "entry.cs"));

        public static async Task<Fixture> CreateAsync(DeploymentPackageScope scope = DeploymentPackageScope.Profile)
        {
            var directory = System.IO.Directory.CreateTempSubdirectory("cordis-profile-deployment-").FullName;
            var home = Path.Combine(directory, "home");
            var profile = Path.Combine(home, "profiles", "test");
            Profiles.Initialize(profile, []);
            var worker = System.IO.Directory.CreateDirectory(Path.Combine(directory, "worker")).FullName;
            var stable = System.IO.Directory.CreateDirectory(Path.Combine(directory, "stable")).FullName;
            var config = Path.Combine(profile, "base.yml");
            await File.WriteAllTextAsync(
                config,
                "- id: worker\n  name: worker\n  config: worker\n- id: stable\n  name: stable\n");
            var entry = new DeploymentEntry("worker", worker, "1", Path.Combine(worker, "package.json"), scope);
            var generation = new DeploymentGeneration(
                Path.Combine(home, "profiles"),
                profile,
                [
                    entry,
                    new(
                        "stable",
                        stable,
                        "1",
                        Path.Combine(stable, "package.json"),
                        DeploymentPackageScope.Installation)
                ]);
            var fixture = new Fixture
            {
                Directory = directory,
                Config = config,
                WorkerDirectory = worker,
                StableDirectory = stable,
                WorkerEntry = entry,
                Initial = generation,
                Next = generation,
                Launch = new(
                    await Profiles.LoadAsync(profile, new Dictionary<string, string>()),
                    home,
                    [],
                    new Dictionary<string, string>())
            };
            fixture.Resolver.Register(
                "stable",
                new Plugin<object?>
                {
                    Apply = (_, _) =>
                    {
                    }
                });
            fixture.ResetResolver();
            return fixture;
        }

        public void ResetResolver()
        {
            Packages = new(Initial);
            Modules = new DeploymentModuleResolver(Packages, Resolver)
                .Register(WorkerDirectory, ".", "worker")
                .Register(StableDirectory, ".", "stable");
        }

        public void Worker(Func<string, Task>? cleanup = null, Action<string>? start = null) =>
            Resolver.Register(
                "worker",
                new Plugin<string>
                {
                    Apply = (ctx, id) =>
                    {
                        start?.Invoke(id);
                        if (cleanup is not null)
                            ctx.Effect(() => new Cleanup(() => cleanup(id)));
                    }
                });

        public DeploymentGeneration WithoutWorker() => new(
            Initial.ProfilesDirectory,
            Initial.ProfileDirectory,
            Initial.Entries.Where(entry => entry.Name != "worker"));

        public Task<ProfileSession> StartAsync() => ProfileSession.StartAsync(
            Config,
            Launch,
            Modules,
            refreshDeployment: () => Task.FromResult(Next));

        public ValueTask DisposeAsync()
        {
            System.IO.Directory.Delete(Directory, true);
            return ValueTask.CompletedTask;
        }
    }
}
