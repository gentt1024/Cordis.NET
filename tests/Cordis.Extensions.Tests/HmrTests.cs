using Cordis.Extensions;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class HmrTests
{
    [Fact]
    public async Task SerializesMutationsRejectsNestingAndRecoversAfterFailure()
    {
        await using var hmr = new HmrCoordinator();
        var entered = Signal(); var release = Signal();
        List<int> order = [];
        var first = hmr.RunExclusiveAsync(async () => { order.Add(1); entered.SetResult(); await release.Task; throw new InvalidOperationException("partial package failure"); });
        await entered.Task;
        var second = hmr.RunExclusiveAsync(async () =>
        {
            order.Add(2);
            await Assert.ThrowsAsync<InvalidOperationException>(() => hmr.RunExclusiveAsync(() => Task.CompletedTask));
        });
        Assert.Equal([1], order);
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await second;
        Assert.Equal([1, 2], order);
    }

    [Fact]
    public async Task DisposalFromOwnTransactionDoesNotWaitOnItself()
    {
        var hmr = new HmrCoordinator();
        await hmr.RunExclusiveAsync(async () => await hmr.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => hmr.RunExclusiveAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task RoutesModulesFrameworkAndUnrelatedNotifications()
    {
        await using var hmr = new HmrCoordinator();
        int modules = 0, restarts = 0;
        List<string> unrelated = [];
        hmr.RegisterModule("plugin.dll", () => { modules++; return Task.CompletedTask; });
        hmr.RegisterFramework("Cordis.Core.dll");
        hmr.RestartHost = () => { restarts++; return Task.CompletedTask; };
        hmr.Changed += unrelated.Add;
        await hmr.NotifyChangedAsync("plugin.dll");
        await hmr.NotifyChangedAsync("Cordis.Core.dll");
        await hmr.NotifyChangedAsync("package.json.lock");
        Assert.Equal(1, modules); Assert.Equal(1, restarts);
        Assert.Equal(Path.GetFullPath("package.json.lock"), Assert.Single(unrelated));
    }

    [Fact]
    public async Task ModuleFailurePreservesQueueAndApplicationReadinessGatesReloads()
    {
        await using var hmr = new HmrCoordinator();
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hmr.ApplicationReady = ready.Task;
        int calls = 0;
        hmr.RegisterModule("plugin.dll", () => { calls++; throw new InvalidOperationException("bad replacement"); });
        var reload = hmr.NotifyChangedAsync("plugin.dll");
        Assert.Equal(0, calls);
        ready.SetResult(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reload);
        await hmr.RunExclusiveAsync(() => Task.CompletedTask);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExactWatcherObservesMissingParentsSerializesRefreshAndCloses()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-watch-");
        try
        {
            await using var hmr = new HmrCoordinator();
            var refreshed = Signal();
            var entered = Signal(); var release = Signal();
            var file = Path.Combine(directory.FullName, "missing", "cordis.patch.yml");
            int count = 0;
            var watch = hmr.WatchConfig(file, () => { Interlocked.Increment(ref count); refreshed.TrySetResult(); return Task.CompletedTask; });
            Assert.Throws<InvalidOperationException>(() => hmr.WatchConfig(file, () => Task.CompletedTask));
            var mutation = hmr.RunExclusiveAsync(async () => { entered.SetResult(); await release.Task; });
            await entered.Task;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "[]");
            Assert.Equal(0, count);
            release.SetResult();
            await mutation;
            await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await hmr.RunExclusiveAsync(async () => await watch.DisposeAsync());
            await using var second = hmr.WatchConfig(file, () => Task.CompletedTask, false);
            Assert.True(count >= 1);
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task WatcherFailureIsReportedAndLaterFileChangesStillWork()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-watch-error-");
        try
        {
            await using var hmr = new HmrCoordinator();
            var file = Path.Combine(directory.FullName, "config.yml");
            await File.WriteAllTextAsync(file, "[]");
            var failure = Signal(); var success = Signal();
            bool fail = true;
            hmr.Error += _ => failure.TrySetResult();
            await using var watch = hmr.WatchConfig(file, () =>
            {
                if (fail) throw new IOException("refresh failed");
                success.TrySetResult(); return Task.CompletedTask;
            });
            await failure.Task.WaitAsync(TimeSpan.FromSeconds(10));
            fail = false;
            await File.WriteAllTextAsync(file, "- id: changed");
            await success.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { directory.Delete(true); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
