using Cordis.Extensions;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class TimerTests
{
    [Fact]
    public async Task ExternalCancellationSourceDoesNotRetainAnUnenumeratedDisposedStream()
    {
        using var cancellation = new CancellationTokenSource();
        var stream = await CreateDisposedStream(cancellation.Token);
        for (var attempt = 0; attempt < 8 && stream.IsAlive; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Yield();
        }
        Assert.False(stream.IsAlive);
        GC.KeepAlive(cancellation);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CreateDisposedStream(CancellationToken cancellation)
    {
        await using var root = new Context();
        WeakReference weak = null!;
        await root.RunAsync(async ctx =>
        {
            var stream = new TimerService(ctx, new ManualClock()).IntervalAsync(TimeSpan.FromSeconds(1), cancellation);
            weak = new WeakReference(stream);
            await ctx.Fiber.DisposeAsync();
        });
        return weak;
    }

    [Fact]
    public async Task LeadingThrottleRequiresOwnerDomain()
    {
        await using var root = new Context(); ScheduledAction<int> action = null!; int calls = 0;
        await root.RunAsync(ctx => { action = new TimerService(ctx).Throttle<int>(_ => calls++, TimeSpan.FromSeconds(1)); return Task.CompletedTask; });
        Assert.Throws<InvalidOperationException>(() => action.Invoke(1)); Assert.Equal(0, calls);
        await root.RunAsync(_ => { action.Invoke(1); return Task.CompletedTask; }); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CallbackErrorsHaveDefaultLoggingAndOptionalObserver()
    {
        await using var root = new Context(); var clock = new ManualClock();
        await root.RunAsync(ctx =>
        {
            var timer = new TimerService(ctx, clock);
            timer.Timeout(() => throw new InvalidOperationException("default error"), TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Contains(ctx.Logger.Buffer, message => message.Arguments.OfType<Exception>().Any(e => e.Message == "default error"));
            Exception? observed = null; timer.UnhandledError += error => observed = error;
            timer.Timeout(() => throw new InvalidOperationException("observed"), TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal("observed", observed?.Message);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task IteratorStartsImmediatelyDoesNotBufferAndReturnSettlesPendingNext()
    {
        await using var root = new Context();
        var clock = new ManualClock();
        await root.RunAsync(async ctx =>
        {
            var stream = new TimerService(ctx, clock).IntervalAsync(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(2));
            await using var iterator = stream.GetAsyncEnumerator();
            var next = iterator.MoveNextAsync().AsTask();
            Assert.False(next.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(await next); Assert.Equal(1, iterator.Current);
            clock.Advance(TimeSpan.FromSeconds(2));
            next = iterator.MoveNextAsync().AsTask(); Assert.False(next.IsCompleted);
            await iterator.DisposeAsync(); Assert.False(await next);
            Assert.False(await iterator.MoveNextAsync());
        });
    }

    [Fact]
    public async Task UnenumeratedIntervalIsOwnedAndPendingNextRejectsOnOwnerStop()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var timer = new TimerService(ctx, new ManualClock());
            var unenumerated = timer.IntervalAsync(TimeSpan.FromSeconds(1));
            await using var waiting = timer.IntervalAsync(TimeSpan.FromSeconds(1)).GetAsyncEnumerator();
            var next = waiting.MoveNextAsync().AsTask();
            await ctx.Fiber.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => next);
            await using var late = unenumerated.GetAsyncEnumerator();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => late.MoveNextAsync().AsTask());
        });
    }

    [Fact]
    public async Task TimerIteratorCancellationTerminatesOutstandingRequest()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            using var cancel = new CancellationTokenSource();
            await using var iterator = new TimerService(ctx, new ManualClock()).IntervalAsync(TimeSpan.FromSeconds(1), cancel.Token).GetAsyncEnumerator();
            var next = iterator.MoveNextAsync().AsTask(); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        });
    }

    [Fact]
    public async Task QueuedCallbacksDoNotEscapeOwnerDisposal()
    {
        await using var root = new Context();
        var clock = new ManualClock();
        await root.RunAsync(async ctx =>
        {
            int count = 0;
            var timer = new TimerService(ctx, clock);
            timer.Timeout(() => count++, TimeSpan.FromSeconds(1));
            timer.Interval(() => count++, TimeSpan.FromSeconds(1));
            var debounce = timer.Debounce<int>(_ => count++, TimeSpan.FromSeconds(1));
            debounce.Invoke(1);
            // A foreign timer thread queues work while the synchronous domain turn remains busy.
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(null);
                clock.Advance(TimeSpan.FromSeconds(1));
            });
            thread.Start(); thread.Join();
            await ctx.Fiber.DisposeAsync();
            await Task.Yield();
            Assert.Equal(0, count);
        });
    }

    [Fact]
    public async Task TimeoutRunsInOwnerDomainAndIsCanceledWithFiber()
    {
        await using var root = new Context();
        var clock = new ManualClock();
        await root.RunAsync(async ctx =>
        {
            int count = 0;
            var timer = new TimerService(ctx, clock);
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            timer.Timeout(() => { ctx.Get<object>("absent"); count++; complete.SetResult(); }, TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            await complete.Task;
            timer.Timeout(() => count++, TimeSpan.FromSeconds(1));
            await ctx.Fiber.DisposeAsync();
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal(1, count);
        });
    }

    [Fact]
    public async Task AwaitedTimeoutRejectsWhenOwnerDisposes()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var timer = new TimerService(ctx, new ManualClock());
            var wait = timer.TimeoutAsync(TimeSpan.FromSeconds(1));
            await ctx.Fiber.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => wait);
        });
    }

    [Fact]
    public async Task DebounceKeepsLastArgumentsAndDisposalPreventsTrailingCall()
    {
        await using var root = new Context();
        var clock = new ManualClock();
        await root.RunAsync(async ctx =>
        {
            List<int> values = [];
            var timer = new TimerService(ctx, clock);
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var action = timer.Debounce<int>(x => { values.Add(x); complete.TrySetResult(); }, TimeSpan.FromSeconds(1));
            action.Invoke(1); action.Invoke(2);
            clock.Advance(TimeSpan.FromSeconds(1));
            await complete.Task;
            action.Invoke(3); await action.DisposeAsync();
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal([2], values);
        });
    }

    [Fact]
    public async Task ThrottleNoTrailingStillAllowsLaterLeadingCalls()
    {
        await using var root = new Context();
        var clock = new ManualClock();
        await root.RunAsync(async ctx =>
        {
            List<int> values = [];
            var action = new TimerService(ctx, clock).Throttle<int>(values.Add, TimeSpan.FromSeconds(1), true);
            action.Invoke(1); action.Invoke(2);
            clock.Advance(TimeSpan.FromSeconds(1)); action.Invoke(3);
            await action.DisposeAsync();
            clock.Advance(TimeSpan.FromSeconds(1)); action.Invoke(4);
            Assert.Equal([1, 3, 4], values); // Upstream disposal suppresses trailing only.
        });
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period); _timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _now += duration;
            foreach (var timer in _timers.ToArray()) timer.Fire(_now);
        }
        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private DateTimeOffset _due = clock._now + dueTime;
            private TimeSpan _period = period;
            private bool _disposed;
            public bool Change(TimeSpan due, TimeSpan next) { _due = clock._now + due; _period = next; return !_disposed; }
            public void Fire(DateTimeOffset now)
            {
                if (_disposed || now < _due) return;
                if (_period == global::System.Threading.Timeout.InfiniteTimeSpan) _disposed = true; else _due = now + _period;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
