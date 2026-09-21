using System.Threading.Channels;
using Cordis.Composition;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class ProfileSessionTests
{
    [Fact]
    public async Task Profile_refresh_proceeds_while_package_writer_lock_is_held()
    {
        await using var scenario = await Scenario.CreateAsync();
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver, enableHmr: true);
        await scenario.ExpectAsync("initial");
        var lockPath = Path.Combine(scenario.Launch.Profile.Directory, "package.json.cordis-lock");
        await using var writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        await scenario.WriteAsync(session, scenario.ProfilePatch, "- id: p\n  config: during-install\n");
        await scenario.ExpectAsync("during-install");
        await session.Context.RunAsync(ctx => { Assert.Equal("during-install", ctx.Get("value")); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Unreadable_profile_input_reports_failure_and_recovers_without_rewriting_the_file()
    {
        await using var scenario = await Scenario.CreateAsync();
        var original = await File.ReadAllTextAsync(scenario.ProfilePatch);
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver, enableHmr: true);
        await scenario.ExpectAsync("initial");
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += failure => error.TrySetResult(failure);
        await session.Hmr!.RunExclusiveAsync(() => { File.Delete(scenario.ProfilePatch); System.IO.Directory.CreateDirectory(scenario.ProfilePatch); return Task.CompletedTask; });
        await error.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.Hmr.RunExclusiveAsync(async () => { System.IO.Directory.Delete(scenario.ProfilePatch); await File.WriteAllTextAsync(scenario.ProfilePatch, original); });
        await session.RefreshAsync();
        Assert.Equal(original, await File.ReadAllTextAsync(scenario.ProfilePatch));
        await session.Context.RunAsync(ctx => { Assert.Equal("initial", ctx.Get("value")); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Readiness_gates_watchers_and_replays_edits_made_between_boot_parse_and_registration()
    {
        await using var scenario = await Scenario.CreateAsync();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver,
            enableHmr: true, applicationReady: ready.Task, prepare: _ => File.WriteAllTextAsync(scenario.ProfilePatch, "- id: p\n  config: during-boot\n"));
        await scenario.ExpectAsync("initial");
        await session.Context.RunAsync(ctx => { Assert.Equal("initial", ctx.Get("value")); return Task.CompletedTask; });
        ready.SetResult(true);
        await scenario.ExpectAsync("during-boot");
    }

    [Fact]
    public async Task Startup_interruption_cancels_queued_profile_refreshes()
    {
        await using var scenario = await Scenario.CreateAsync();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver,
            enableHmr: true, applicationReady: ready.Task);
        await scenario.ExpectAsync("initial");
        await File.WriteAllTextAsync(scenario.ProfilePatch, "- id: p\n  config: cancelled\n");
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        ready.SetResult(true);
        Assert.False(scenario.HasApply);
    }

    [Fact]
    public async Task Dependency_only_manifest_changes_do_not_update_the_include()
    {
        await using var scenario = await Scenario.CreateAsync();
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver);
        int updates = 0;
        await session.Context.RunAsync(ctx =>
        {
            ctx.On("internal/update", (e, _) => { updates++; return e.Next(); }, new EventOptions(Global: true, Prepend: true));
            return Task.CompletedTask;
        });
        await File.WriteAllTextAsync(Path.Combine(scenario.Launch.Profile.Directory, "package.json"),
            "{\"dependencies\":{\"added\":\"1.0.0\"},\"dsh\":{\"profile\":{}}}");
        await session.RefreshAsync();
        Assert.Equal(0, updates);
        var bundle = Path.Combine(scenario.Directory, "added");
        System.IO.Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(Path.Combine(bundle, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":\"cordis.patch.yml\"}}}");
        await File.WriteAllTextAsync(Path.Combine(bundle, "cordis.patch.yml"), "- insert:\n    - id: bundled\n      name: counter\n      disabled: true\n");
        scenario.Mappings["added"] = bundle;
        await File.WriteAllTextAsync(Path.Combine(scenario.Launch.Profile.Directory, "package.json"), "{\"dsh\":{\"profile\":{\"bundles\":[\"added\"]}}}");
        await session.RefreshAsync(); Assert.Equal(1, updates);
        await session.RefreshAsync(); Assert.Equal(1, updates);
        Assert.Contains(session.Loader.Entries(), entry => entry.Id.EndsWith(":bundled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unchanged_inactive_entries_are_warned_while_an_unrelated_edit_applies()
    {
        await using var scenario = await Scenario.CreateAsync();
        await File.AppendAllTextAsync(scenario.Config, "- id: missing\n  name: missing-module\n");
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver);
        var warnings = new List<string>(); session.Warning += warnings.Add;
        await scenario.WriteAsync(session, scenario.ProfilePatch, "- id: p\n  config: edited\n");
        await session.RefreshAsync();
        await scenario.ExpectAsync("edited");
        Assert.Contains(warnings, warning => warning.Contains("missing (missing-module)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Real_profile_and_home_watches_reapply_precedence_and_recover_from_malformed_input()
    {
        await using var scenario = await Scenario.CreateAsync();
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver, enableHmr: true);
        await scenario.ExpectAsync("initial");
        Assert.NotNull(session.Hmr);
        await session.Context.RunAsync(ctx =>
        {
            Assert.Same(session.Hmr, ctx.Get("hmr"));
            Assert.IsType<ProfileLaunch>(ctx.Get("profileContext"));
            return Task.CompletedTask;
        });
        await scenario.WriteAsync(session, scenario.ProfilePatch, "- id: p\n  config: profile\n");
        await scenario.ExpectAsync("profile");
        await scenario.WriteAsync(session, scenario.HomePatch, "- id: p\n  config: home\n");
        await scenario.ExpectAsync("home");
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += failure => error.TrySetResult(failure);
        await scenario.WriteAsync(session, scenario.HomePatch, "invalid: [");
        await error.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.Context.RunAsync(ctx => { Assert.Equal("home", ctx.Get("value")); return Task.CompletedTask; });
        await scenario.WriteAsync(session, scenario.HomePatch, "[]\n");
        await scenario.ExpectAsync("profile");
        // Include re-reads must use the successful current profile overlays.
        await scenario.WriteAsync(session, scenario.Config, "- id: p\n  name: counter\n  config: changed-file\n");
        await session.RefreshAsync();
        await session.Context.RunAsync(ctx => { Assert.Equal("profile", ctx.Get("value")); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Include_watch_keeps_old_tree_after_invalid_structure_and_recovers()
    {
        await using var scenario = await Scenario.CreateAsync();
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver, enableHmr: true);
        await scenario.ExpectAsync("initial");
        var error = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Error += failure => error.TrySetResult(failure);
        await scenario.WriteAsync(session, scenario.Config, "not: an-entry-array\n");
        await error.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await session.Context.RunAsync(ctx => { Assert.Equal("initial", ctx.Get("value")); return Task.CompletedTask; });
        await scenario.WriteAsync(session, scenario.Config, "- id: p\n  name: counter\n  config: recovered\n");
        await scenario.ExpectAsync("recovered");
    }

    [Fact]
    public async Task Bundle_list_change_reconciles_available_modules_without_restart()
    {
        await using var scenario = await Scenario.CreateAsync();
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver, enableHmr: true);
        await scenario.ExpectAsync("initial");
        var bundle = Path.Combine(scenario.Directory, "new-bundle");
        System.IO.Directory.CreateDirectory(bundle);
        await File.WriteAllTextAsync(Path.Combine(bundle, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":\"cordis.patch.yml\"}}}");
        await File.WriteAllTextAsync(Path.Combine(bundle, "cordis.patch.yml"), "- id: p\n  config: new-bundle\n");
        scenario.Mappings["new"] = bundle;
        await scenario.WriteAsync(session, Path.Combine(scenario.Launch.Profile.Directory, "package.json"), "{\"dsh\":{\"profile\":{\"bundles\":[\"new\"]}}}");
        await scenario.ExpectAsync("new-bundle");
        Assert.False(session.RequiresRestart);
        await session.Context.RunAsync(ctx => { Assert.Equal("new-bundle", ctx.Get("value")); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Deployment_refresh_accepts_additions_and_requests_restart_before_remapping_live_code()
    {
        await using var scenario = await Scenario.CreateAsync();
        var package = Path.Combine(scenario.Directory, "package");
        var extra = Path.Combine(scenario.Directory, "extra");
        System.IO.Directory.CreateDirectory(package);
        System.IO.Directory.CreateDirectory(extra);
        var entry = new DeploymentEntry("counter", package, "1", Path.Combine(package, "package.json"), DeploymentPackageScope.Installation);
        var generation = new DeploymentGeneration(Path.Combine(scenario.Launch.Home, "profiles"), scenario.Launch.Profile.Directory, [entry]);
        var packages = new DeploymentPackageResolver(generation, native: (_, _) => package);
        var modules = new DeploymentModuleResolver(packages, scenario.Resolver).Register(package, ".", "counter");
        var next = generation;
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, modules,
            refreshDeployment: () => Task.FromResult(next));
        await scenario.ExpectAsync("initial");
        next = new(generation.ProfilesDirectory, generation.ProfileDirectory,
            [entry, new("extra", extra, "1", Path.Combine(extra, "package.json"), DeploymentPackageScope.Profile)]);
        await scenario.WriteAsync(session, scenario.ProfilePatch, "- id: p\n  config: additive\n");
        await session.RefreshAsync();
        await scenario.ExpectAsync("additive");
        Assert.Same(next, packages.Generation);
        var accepted = next;
        next = new(generation.ProfilesDirectory, generation.ProfileDirectory, [entry with { Directory = extra }]);
        await scenario.WriteAsync(session, scenario.ProfilePatch, "- id: p\n  config: blocked-remap\n");
        await session.RefreshAsync();
        Assert.True(session.RequiresRestart);
        Assert.Same(accepted, packages.Generation);
        await session.Context.RunAsync(ctx => { Assert.Equal("additive", ctx.Get("value")); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Nested_include_files_are_watched_and_unchanged_manual_refresh_does_not_restart()
    {
        await using var scenario = await Scenario.CreateAsync();
        var nested = Path.Combine(scenario.Directory, "nested.yml");
        await File.WriteAllTextAsync(nested, "- id: p\n  name: counter\n  config: nested\n");
        await File.WriteAllTextAsync(scenario.Config, "- id: nested\n  name: cordis:include\n  config:\n    path: nested.yml\n");
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver, enableHmr: true);
        await scenario.ExpectAsync("nested");
        await session.RefreshAsync();
        await session.RefreshAsync();
        Assert.False(scenario.HasApply);
        await scenario.WriteAsync(session, nested, "- id: p\n  name: counter\n  config: nested-updated\n");
        await scenario.ExpectAsync("nested-updated");
    }

    [Fact]
    public async Task Hmr_disabled_has_no_automatic_watch_but_manual_refresh_works()
    {
        await using var scenario = await Scenario.CreateAsync();
        await using var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver);
        await scenario.ExpectAsync("initial");
        Assert.Null(session.Hmr);
        await session.Context.RunAsync(ctx => { Assert.Null(ctx.Get("hmr")); return Task.CompletedTask; });
        await scenario.WriteAsync(session, scenario.ProfilePatch, "- id: p\n  config: manual\n");
        await session.Context.RunAsync(ctx => { Assert.Equal("initial", ctx.Get("value")); return Task.CompletedTask; });
        await session.RefreshAsync();
        await scenario.ExpectAsync("manual");
        await session.Context.RunAsync(ctx => { Assert.Equal("manual", ctx.Get("value")); return Task.CompletedTask; });
    }

    [Fact]
    public async Task Disposal_from_own_reload_queue_does_not_wait_on_itself()
    {
        await using var scenario = await Scenario.CreateAsync();
        var session = await ProfileSession.StartAsync(scenario.Config, scenario.Launch, scenario.Resolver, enableHmr: true);
        await session.Hmr!.RunExclusiveAsync(async () => await session.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RefreshAsync());
    }

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly Channel<string> values = Channel.CreateUnbounded<string>();
        public required string Directory { get; init; }
        public required string Config { get; init; }
        public required ProfileLaunch Launch { get; init; }
        public required Dictionary<string, string> Mappings { get; init; }
        public StaticModuleResolver Resolver { get; } = new();
        public string ProfilePatch => Path.Combine(Launch.Profile.Directory, "cordis.patch.yml");
        public string HomePatch => Path.Combine(Launch.Home, "cordis.patch.yml");
        public bool HasApply => values.Reader.TryPeek(out _);
        public static async Task<Scenario> CreateAsync()
        {
            var directory = System.IO.Directory.CreateTempSubdirectory("cordis-profile-session-").FullName;
            var home = Path.Combine(directory, "home");
            var profile = Path.Combine(home, "profiles", "test");
            Profiles.Initialize(profile, []);
            var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
            var loaded = await Profiles.LoadAsync(profile, mappings);
            var config = Path.Combine(directory, "cordis.yml");
            await File.WriteAllTextAsync(config, "- id: p\n  name: counter\n  config: initial\n");
            var scenario = new Scenario { Directory = directory, Config = config, Mappings = mappings, Launch = new(loaded, home, [], mappings) };
            scenario.Resolver.Register("counter", new Plugin<string>
            {
                Apply = (ctx, config) =>
            {
                ctx.Provide("value", config);
                scenario.values.Writer.TryWrite(config);
            }
            });
            return scenario;
        }
        public async Task ExpectAsync(string expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await values.Reader.ReadAsync(timeout.Token) != expected) { }
        }
        public Task WriteAsync(ProfileSession session, string path, string contents) => session.Hmr is { } hmr
            ? hmr.RunExclusiveAsync(() => File.WriteAllTextAsync(path, contents))
            : File.WriteAllTextAsync(path, contents);
        public ValueTask DisposeAsync() { System.IO.Directory.Delete(Directory, true); return ValueTask.CompletedTask; }
    }
}
