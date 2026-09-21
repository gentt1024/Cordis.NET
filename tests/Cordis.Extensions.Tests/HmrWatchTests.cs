using System.Threading.Channels;
using Cordis.Composition;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class HmrWatchTests
{
    [Fact]
    public async Task Include_refresh_waits_for_mutation_and_exact_path_is_not_reloaded_twice()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-include-");
        try
        {
            var filename = Path.Combine(directory.FullName, "plugins.yml");
            await File.WriteAllTextAsync(filename, "- id: p\n  name: probe\n  config: before\n");
            await using var context = new Context(); Loader? loader = null;
            await context.RunAsync(ctx => { loader = new Loader(ctx, new StaticModuleResolver().Register("probe", new Plugin<string> { Apply = (_, _) => { } })); return Task.CompletedTask; });
            var include = await ApplicationBoot.MountAsync(loader!, filename);
            ControlledWatcher? native = null;
            await using var hmr = new HmrCoordinator(path => native = new(path));
            var order = new List<string>(); var refreshed = Signal();
            await using var watch = hmr.WatchConfig(filename, async () => { await include.RefreshAsync(); order.Add("refresh"); refreshed.TrySetResult(); }, false);
            native!.EnableRaisingEvents = false;
            Assert.Throws<InvalidOperationException>(() => hmr.WatchConfig(filename, () => Task.CompletedTask));
            hmr.RegisterModule(filename, () => throw new InvalidOperationException("exact path dispatched as module"));
            var entered = Signal(); var release = Signal();
            var mutation = hmr.RunExclusiveAsync(async () => { entered.SetResult(); await release.Task; order.Add("write"); });
            await entered.Task;
            await File.WriteAllTextAsync(filename, "- id: p\n  name: probe\n  config: after\n");
            native.Change("plugins.yml"); var notification = hmr.NotifyChangedAsync(filename);
            Assert.Empty(order); release.SetResult(); await mutation; await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5)); await notification;
            Assert.Equal(["write", "refresh"], order); Assert.Equal("after", include.Store["p"].Fiber!.Config);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Root_lookup_stops_once_and_preserves_inaccessible_and_missing_path_errors()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-stat-");
        try
        {
            var root = Path.GetPathRoot(directory.FullName)!;
            var missing = new DirectoryNotFoundException("root missing"); var denied = new UnauthorizedAccessException("denied");
            var target = Path.Combine(directory.FullName, "denied.yml"); int rootReads = 0;
            await using var hmr = new HmrCoordinator(readAttributes: path =>
            {
                if (path == root) { rootReads++; throw missing; }
                if (path == target) throw denied;
                return File.GetAttributes(path);
            });
            Assert.Same(denied, Assert.Throws<UnauthorizedAccessException>(() => hmr.WatchConfig(target, () => Task.CompletedTask)));
            Assert.Same(missing, Assert.Throws<DirectoryNotFoundException>(() => hmr.WatchConfig(Path.Combine(root, "plugins.yml"), () => Task.CompletedTask)));
            Assert.Equal(1, rootReads);
            Assert.Same(missing, Assert.Throws<DirectoryNotFoundException>(() => hmr.WatchConfig(root, () => Task.CompletedTask)));
            Assert.Equal(2, rootReads);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Watcher_activation_failure_closes_resource_and_releases_registration(bool moduleRoot)
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-activate-");
        try
        {
            var watched = Path.Combine(directory.FullName, "watch"); Directory.CreateDirectory(watched);
            ControlledWatcher? native = null; bool fail = true;
            var failure = new IOException("watch activation failed");
            await using var hmr = new HmrCoordinator(path =>
            {
                native = new(path);
                return native;
            }, readAttributes: path =>
            {
                // Model failure after resource construction, without relying on whether the
                // host OS rejects a renamed directory in EnableRaisingEvents.
                if (fail && native is not null && path == watched) throw failure;
                return File.GetAttributes(path);
            });
            var filename = Path.Combine(watched, "plugins.yml");
            void Register() { if (moduleRoot) hmr.WatchModules(watched); else hmr.WatchConfig(filename, () => Task.CompletedTask); }
            Assert.Same(failure, Assert.Throws<IOException>(Register));
            Assert.Equal(1, native!.Disposals);
            fail = false;
            Register();
            var error = new IOException("runtime failure"); object? warning = null; hmr.Warning += value => warning = value;
            native!.Fail(error); Assert.Same(error, warning);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Watcher_created_during_owner_teardown_is_closed_before_activation(bool moduleRoot)
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-create-dispose-");
        try
        {
            HmrCoordinator? hmr = null; ControlledWatcher? native = null;
            hmr = new HmrCoordinator(path =>
            {
                native = new(path);
                hmr!.DisposeAsync().AsTask().GetAwaiter().GetResult();
                return native;
            });
            Assert.Throws<ObjectDisposedException>(() =>
            {
                if (moduleRoot) hmr.WatchModules(directory.FullName);
                else hmr.WatchConfig(Path.Combine(directory.FullName, "config.yml"), () => Task.CompletedTask);
            });
            Assert.Equal(1, native!.Disposals);
            await hmr.DisposeAsync();
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Configuration_bindings_follow_a_provider_reconfigured_inside_its_own_queue()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-rebind-");
        try
        {
            await using var root = new Context();
            var watchers = new List<ControlledWatcher>();
            var filename = Path.Combine(directory.FullName, "profile.yml");
            var refreshed = Signal(); int calls = 0;
            await root.RunAsync(async ctx =>
            {
                var provider = ctx.Plugin(new Plugin<int>
                {
                    Apply = (owner, _) =>
                {
                    var hmr = new HmrCoordinator(path => { var watcher = new ControlledWatcher(path); watchers.Add(watcher); return watcher; });
                    owner.Effect(() => hmr); owner.Provide("hmr", hmr);
                }
                }, 0);
                await provider.WaitAsync();
                var binding = ctx.Inject(["hmr"], owner => owner.Effect(() => owner.Get<HmrCoordinator>("hmr")!.WatchConfig(filename,
                    () => { calls++; refreshed.TrySetResult(); return Task.CompletedTask; })));
                await binding.WaitAsync();
                var previous = ctx.Get<HmrCoordinator>("hmr")!;
                await previous.RunExclusiveAsync(async () =>
                {
                    await ctx.RunAsync(_ => { provider.Update(1); return Task.CompletedTask; });
                    await provider.WaitAsync(); await binding.WaitAsync();
                }).WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotSame(previous, ctx.Get<HmrCoordinator>("hmr"));
                Assert.Equal(2, watchers.Count);
                watchers[^1].Change("profile.yml");
            });
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, calls);
        }
        finally { directory.Delete(true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filesystem_aliases_canonicalize_module_roots_and_exact_watch_identity(bool relative)
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-alias-");
        var alias = directory.FullName + "-alias";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(directory.FullName, "junction.ps1");
                File.WriteAllText(script, "param([string]$Link,[string]$Target)\nNew-Item -ItemType Junction -Path $Link -Target $Target | Out-Null");
                var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, alias, directory.FullName }) start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode);
            }
            else Directory.CreateSymbolicLink(alias, directory.FullName);
            await using var hmr = new HmrCoordinator { Debounce = TimeSpan.Zero };
            await using var config = hmr.WatchConfig(Path.Combine(alias, "plugins.yml"), () => Task.CompletedTask);
            Assert.Throws<InvalidOperationException>(() => hmr.WatchConfig(Path.Combine(directory.FullName, "plugins.yml"), () => Task.CompletedTask));
            var observed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var filename = Path.Combine(directory.FullName, "module.dll");
            hmr.Changed += path => { if (Path.GetFileName(path) == "module.dll") observed.TrySetResult(path); };
            using var modules = hmr.WatchModules(relative ? Path.GetRelativePath(Environment.CurrentDirectory, alias) : alias);
            await File.WriteAllTextAsync(filename, "changed");
            Assert.Equal(filename, await observed.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Disposal_cancels_module_and_config_work_waiting_for_startup_readiness()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-cancel-");
        try
        {
            var hmr = new HmrCoordinator { ApplicationReady = new TaskCompletionSource<bool>().Task };
            int modules = 0, refreshes = 0;
            hmr.RegisterModule("pending.dll", () => { modules++; return Task.CompletedTask; });
            var pending = hmr.NotifyChangedAsync("pending.dll");
            var path = Path.Combine(directory.FullName, "pending.yml");
            await File.WriteAllTextAsync(path, "[]");
            _ = hmr.WatchConfig(path, () => { refreshes++; return Task.CompletedTask; });
            await hmr.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await pending;
            Assert.Equal(0, modules); Assert.Equal(0, refreshes);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Watch_disposal_drains_already_dirty_refresh_without_overlap()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-drain-");
        try
        {
            ControlledWatcher? native = null;
            await using var hmr = new HmrCoordinator(path => native = new(path));
            var started = Signal(); var release = Signal();
            int calls = 0, active = 0, maximum = 0;
            var filename = Path.Combine(directory.FullName, "config.yml");
            var watch = hmr.WatchConfig(filename, async () =>
            {
                maximum = Math.Max(maximum, ++active);
                if (++calls == 1) { started.SetResult(); await release.Task; }
                active--;
            });
            native!.Change("unrelated.yml"); Assert.Equal(0, calls);
            native.Change("config.yml"); await started.Task;
            native.Change("config.yml"); native.Change("config.yml");
            var disposal = watch.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
            release.SetResult(); await disposal;
            Assert.Equal(1, maximum); Assert.Equal(2, calls);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Watch_start_failure_releases_registration_and_runtime_errors_preserve_identity()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-start-");
        try
        {
            var failure = new IOException("watcher unavailable"); bool fail = true;
            ControlledWatcher? native = null;
            await using var hmr = new HmrCoordinator(path => fail ? throw failure : native = new(path));
            var filename = Path.Combine(directory.FullName, "plugins.yml");
            Assert.Same(failure, Assert.Throws<IOException>(() => hmr.WatchConfig(filename, () => Task.CompletedTask)));
            fail = false;
            await using var watch = hmr.WatchConfig(filename, () => Task.CompletedTask);
            Exception? reported = null; hmr.Error += error => reported = error;
            native!.Fail(failure); Assert.Same(failure, reported);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Rejects_regular_file_parent_and_allows_registration_after_it_is_corrected()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-parent-");
        try
        {
            await using var hmr = new HmrCoordinator();
            var parent = Path.Combine(directory.FullName, "file"); File.WriteAllText(parent, "");
            var filename = Path.Combine(parent, "plugins.yml");
            Assert.Contains("not a directory", Assert.Throws<IOException>(() => hmr.WatchConfig(filename, () => Task.CompletedTask)).Message);
            File.Delete(parent); Directory.CreateDirectory(parent);
            await using var watch = hmr.WatchConfig(filename, () => Task.CompletedTask);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Exact_watch_observes_add_change_unlink_and_registration_inside_transaction()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-native-");
        try
        {
            await using var hmr = new HmrCoordinator();
            var path = Path.Combine(directory.FullName, "plugins.yml");
            var seen = Channel.CreateUnbounded<string>();
            IAsyncDisposable? watch = null;
            await hmr.RunExclusiveAsync(() =>
            {
                watch = hmr.WatchConfig(path, async () =>
                {
                    string value;
                    try { value = await File.ReadAllTextAsync(path); } catch (FileNotFoundException) { value = "missing"; }
                    seen.Writer.TryWrite(value);
                });
                return Task.CompletedTask;
            });
            await hmr.RunExclusiveAsync(() => File.WriteAllTextAsync(path, "one")); await Expect("one");
            await hmr.RunExclusiveAsync(() => File.WriteAllTextAsync(path, "two")); await Expect("two");
            await hmr.RunExclusiveAsync(() => { File.Delete(path); return Task.CompletedTask; }); await Expect("missing");
            await watch!.DisposeAsync();
            async Task Expect(string expected)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (await seen.Reader.ReadAsync(timeout.Token) != expected) { }
            }
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Configuration_events_after_the_previous_refresh_are_not_throttled_away()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-consecutive-");
        try
        {
            ControlledWatcher? native = null;
            await using var hmr = new HmrCoordinator(path => native = new(path));
            var values = new List<string>(); var completed = Signal(); string current = "enabled";
            await using var watch = hmr.WatchConfig(Path.Combine(directory.FullName, "config.yml"), () =>
            {
                values.Add(current);
                if (current == "enabled") { current = "disabled"; native!.Change("config.yml"); }
                else completed.SetResult();
                return Task.CompletedTask;
            });
            native!.Change("config.yml"); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(["enabled", "disabled"], values);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task Exact_paths_are_not_also_dispatched_as_module_notifications()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-exact-");
        try
        {
            await using var hmr = new HmrCoordinator(); int modules = 0, changes = 0;
            var path = Path.Combine(directory.FullName, "nested.yml");
            hmr.RegisterModule(path, () => { modules++; return Task.CompletedTask; });
            hmr.Changed += _ => changes++;
            await using var watch = hmr.WatchConfig(path, () => Task.CompletedTask);
            await hmr.NotifyChangedAsync(path);
            Assert.Equal(0, modules); Assert.Equal(0, changes);
        }
        finally { directory.Delete(true); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class ControlledWatcher(string directory) : FileSystemWatcher(directory)
    {
        public int Disposals { get; private set; }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
        public void Change(string name) => OnChanged(new(WatcherChangeTypes.Changed, Path, name));
        public void Fail(Exception error) => OnError(new(error));
    }
}
