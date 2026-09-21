namespace Cordis.Extensions;

/// <summary>Fiber-owned timers. Callbacks return to the owning Cordis execution domain.</summary>
public sealed class TimerService(Context context, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Performs the timeout operation.
    /// </summary>
    public EffectHandle Timeout(Action callback, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ITimer? timer = null;
        EffectHandle? effect = null;
        bool active = true;
        effect = context.Effect(() =>
        {
            timer = _time.CreateTimer(_ => Dispatch(async () =>
            {
                if (!active || context.Fiber.State is FiberState.Unloading or FiberState.Disposed) return;
                await effect!.DisposeAsync();
                callback();
            }), null, delay, System.Threading.Timeout.InfiniteTimeSpan);
            return () => { active = false; timer.Dispose(); };
        });
        return effect;
    }

    /// <summary>
    /// Performs the timeout async operation.
    /// </summary>
    public async Task TimeoutAsync(TimeSpan delay)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var effect = context.Effect(() =>
        {
            var timer = _time.CreateTimer(_ => completion.TrySetResult(), null, delay, System.Threading.Timeout.InfiniteTimeSpan);
            return () =>
            {
                timer.Dispose();
                completion.TrySetException(new ObjectDisposedException(nameof(Context), "Context has been disposed"));
            };
        });
        try { await completion.Task; }
        finally { await effect.DisposeAsync(); }
    }

    /// <summary>
    /// Performs the interval operation.
    /// </summary>
    public EffectHandle Interval(Action callback, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        bool active = true;
        return context.Effect(() =>
        {
            var timer = _time.CreateTimer(_ => Dispatch(() =>
            {
                if (active && context.Fiber.State is not (FiberState.Unloading or FiberState.Disposed)) callback();
                return Task.CompletedTask;
            }), null, period, period);
            return () => { active = false; timer.Dispose(); };
        });
    }

    /// <summary>Starts an owned, unbuffered timer immediately; only a pending MoveNext receives a tick.</summary>
    public IAsyncEnumerable<long> IntervalAsync(TimeSpan period, CancellationToken cancellationToken = default) =>
        new TimerTickStream(context, _time, period, cancellationToken);

    /// <summary>
    /// Creates a debounced action for values of type <typeparamref name="T"/>.
    /// </summary>
    public ScheduledAction<T> Debounce<T>(Action<T> callback, TimeSpan delay) => new(context, _time, callback, delay, false, false);
    /// <summary>
    /// Creates a throttled action for values of type <typeparamref name="T"/>.
    /// </summary>
    public ScheduledAction<T> Throttle<T>(Action<T> callback, TimeSpan delay, bool noTrailing = false) => new(context, _time, callback, delay, true, noTrailing);

    private void Dispatch(Func<Task> callback)
    {
        _ = Invoke();
        async Task Invoke()
        {
            try
            {
                await context.RunAsync(async _ =>
                {
                    try { await callback(); }
                    catch (Exception error) { TimerErrors.Report(context, UnhandledError, error); }
                });
            }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Gets the unhandled error value.
    /// </summary>
    public event Action<Exception>? UnhandledError;
}

/// <summary>A cancellable debounced or throttled delegate owned by a fiber.</summary>
public sealed class ScheduledAction<T> : IAsyncDisposable
{
    private readonly Context _context;
    private readonly TimeProvider _time;
    private readonly Action<T> _callback;
    private readonly TimeSpan _delay;
    private readonly bool _throttle;
    private bool _noTrailing;
    private DateTimeOffset? _last;
    private ITimer? _timer;
    private readonly EffectHandle _effect;
    private long _generation;
    private readonly SynchronizationContext? _domain;

    internal ScheduledAction(Context context, TimeProvider time, Action<T> callback, TimeSpan delay, bool throttle, bool noTrailing)
    {
        _context = context; _time = time; _callback = callback; _delay = delay;
        _domain = SynchronizationContext.Current;
        _throttle = throttle; _noTrailing = noTrailing;
        _effect = context.Effect(() => () => { _generation++; _noTrailing = true; _timer?.Dispose(); });
    }

    /// <summary>
    /// Performs the invoke operation.
    /// </summary>
    public void Invoke(T argument)
    {
        if (!ReferenceEquals(SynchronizationContext.Current, _domain))
            throw new InvalidOperationException("Scheduled actions must be invoked inside Context.RunAsync or a Cordis callback.");
        _timer?.Dispose();
        var generation = ++_generation;
        var remaining = _last is null ? TimeSpan.Zero : _delay - (_time.GetUtcNow() - _last.Value);
        if (_throttle && remaining <= TimeSpan.Zero) { Execute(argument); return; }
        if (_noTrailing) return;
        _timer = _time.CreateTimer(_ => { _ = Run(argument, generation); }, null,
            _throttle ? remaining : _delay, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private async Task Run(T argument, long generation)
    {
        try
        {
            await _context.RunAsync(_ =>
            {
                if (generation == _generation && _context.Fiber.State is not (FiberState.Unloading or FiberState.Disposed))
                {
                    try { Execute(argument); }
                    catch (Exception error) { TimerErrors.Report(_context, UnhandledError, error); }
                }
                return Task.CompletedTask;
            });
        }
        catch (ObjectDisposedException) { }
    }

    private void Execute(T argument) { _last = _time.GetUtcNow(); _callback(argument); }
    /// <summary>
    /// Gets the unhandled error value.
    /// </summary>
    public event Action<Exception>? UnhandledError;
    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public ValueTask DisposeAsync() => _effect.DisposeAsync();
}

internal static class TimerErrors
{
    internal static void Report(Context context, Action<Exception>? handler, Exception error)
    {
        if (handler is null) { context.Logger.Error(error); return; }
        try { handler(error); }
        catch (Exception observerError) { context.Logger.Error(new AggregateException(error, observerError)); }
    }
}

internal sealed class TimerTickStream : IAsyncEnumerable<long>, IAsyncEnumerator<long>
{
    private readonly Context _context;
    private readonly EffectHandle _effect;
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _pending;
    private Exception? _error;
    private bool _done;
    private long _ticks;
    private CancellationTokenRegistration _cancellation;
    private CancellationTokenRegistration _enumerationCancellation;

    internal TimerTickStream(Context context, TimeProvider time, TimeSpan period, CancellationToken cancellation)
    {
        if (period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(period));
        _context = context;
        _effect = context.Effect(() =>
        {
            var timer = time.CreateTimer(_ =>
            {
                lock (_gate)
                {
                    if (_done || _pending is null || _pending.Task.IsCompleted) return;
                    Current = ++_ticks;
                    _pending.TrySetResult(true);
                }
            }, null, period, period);
            return () =>
            {
                timer.Dispose();
                // Unregister without waiting on a cancellation callback that may itself be stopping us.
                _cancellation.Unregister();
                _enumerationCancellation.Unregister();
                lock (_gate)
                {
                    if (_done) return;
                    _done = true;
                    _error = new ObjectDisposedException(nameof(Context));
                    _pending?.TrySetException(_error);
                }
            };
        });
        _cancellation = cancellation.Register(() => Cancel(cancellation));
        lock (_gate) if (_done) _cancellation.Unregister();
    }

    public long Current { get; private set; }
    public IAsyncEnumerator<long> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        _enumerationCancellation.Dispose();
        _enumerationCancellation = cancellationToken.Register(() => Cancel(cancellationToken));
        lock (_gate) if (_done) _enumerationCancellation.Unregister();
        return this;
    }
    public ValueTask<bool> MoveNextAsync()
    {
        lock (_gate)
        {
            if (_done) return _error is null ? ValueTask.FromResult(false) : ValueTask.FromException<bool>(_error);
            if (_pending is not null && !_pending.Task.IsCompleted) throw new InvalidOperationException("MoveNextAsync calls cannot overlap.");
            _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(_pending.Task);
        }
    }
    private void Cancel(CancellationToken token)
    {
        lock (_gate)
        {
            if (_done) return;
            _done = true; _error = new OperationCanceledException(token);
            _pending?.TrySetCanceled(token);
        }
        _ = StopEffectAsync();
    }
    private async Task StopEffectAsync()
    {
        try { await _context.RunAsync(async _ => await _effect.DisposeAsync()); }
        catch (ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (!_done) { _done = true; _pending?.TrySetResult(false); } }
        _cancellation.Dispose(); _enumerationCancellation.Dispose();
        await StopEffectAsync();
    }
}
