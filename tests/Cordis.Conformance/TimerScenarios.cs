using Cordis.Extensions;

namespace Cordis.Conformance;

internal static class TimerScenarios
{
    internal static IReadOnlyList<(string Id, Func<Task<string[]>> Run)> Cases { get; } =
    [("C26-owned-timers-and-debounce", Timers), ("C27-unbuffered-eager-timer-iterator", Iterator)];

    private static async Task<string[]> Timers()
    {
        await using var root = new Context(); var clock = new Clock(); var trace = new List<string>();
        await root.RunAsync(async ctx =>
        {
            var timer = new TimerService(ctx, clock);
            timer.Timeout(() => trace.Add("timeout"), TimeSpan.FromMilliseconds(50));
            var pending = ObserveCancellation(timer.TimeoutAsync(TimeSpan.FromMilliseconds(100)));
            async Task ObserveCancellation(Task task)
            {
                try { await task; throw new InvalidOperationException("must cancel"); }
                catch (ObjectDisposedException) { trace.Add("cancelled"); }
            }
            var debounce = timer.Debounce<int>(value => trace.Add($"debounce:{value}"), TimeSpan.FromMilliseconds(10));
            debounce.Invoke(1); debounce.Invoke(2); clock.Advance(10); clock.Advance(40);
            await ctx.Fiber.DisposeAsync(); await pending;
            Scenarios.Equal("debounce:2|timeout|cancelled", string.Join('|', trace));
        });
        return [.. trace];
    }

    private static async Task<string[]> Iterator()
    {
        await using var root = new Context(); var clock = new Clock(); var trace = new List<string>();
        await root.RunAsync(async ctx =>
        {
            var timer = new TimerService(ctx, clock);
            await using var iterator = timer.IntervalAsync(TimeSpan.FromMilliseconds(10)).GetAsyncEnumerator();
            clock.Advance(20); var next = iterator.MoveNextAsync().AsTask();
            Scenarios.Equal(false, next.IsCompleted);
            clock.Advance(10); Scenarios.Equal(true, await next); trace.Add("tick");
            clock.Advance(20); var returning = iterator.MoveNextAsync().AsTask();
            await iterator.DisposeAsync(); Scenarios.Equal(false, await returning); trace.Add("return");
            var unenumerated = timer.IntervalAsync(TimeSpan.FromMilliseconds(10));
            await ctx.Fiber.DisposeAsync();
            await using var late = unenumerated.GetAsyncEnumerator();
            await Scenarios.ThrowsAsync<ObjectDisposedException>(() => late.MoveNextAsync().AsTask()); trace.Add("owned-before-next");
        });
        return [.. trace];
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, dueTime, period); timers.Add(timer); return timer;
        }
        internal void Advance(int milliseconds)
        {
            now = now.AddMilliseconds(milliseconds);
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
        {
            private DateTimeOffset deadline = clock.now + due;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            internal void Fire()
            {
                if (disposed || deadline > clock.now) return;
                if (period == Timeout.InfiniteTimeSpan) disposed = true; else deadline = clock.now + period;
                callback(state);
            }
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
