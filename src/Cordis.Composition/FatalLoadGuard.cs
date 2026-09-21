namespace Cordis.Composition;

/// <summary>
/// Host-supplied fatal asynchronous error boundary. .NET unobserved Task GC is not a JS rejection checkpoint;
/// hosts route fatal operations explicitly and keep ordinary observed activation failures on their fibers.
/// </summary>
public sealed class FatalLoadGuard(Action<string> writeError, Action<int> exit, Func<Task>? release = null,
    TimeProvider? timeProvider = null, string diagnosticName = "cordis") : IDisposable
{
    /// <summary>
    /// Gets the release timeout value.
    /// </summary>
    public static TimeSpan ReleaseTimeout { get; } = TimeSpan.FromSeconds(2);
    private int _exiting;
    private bool _disposed;
    private readonly Dictionary<Exception, int> _assembled = new(ReferenceEqualityComparer.Instance);
    private readonly object _gate = new();

    /// <summary>
    /// Performs the retain assembled operation.
    /// </summary>
    public IDisposable RetainAssembled(Exception error)
    {
        lock (_gate) _assembled[error] = _assembled.GetValueOrDefault(error) + 1;
        return new Registration(() => { lock (_gate) { if (_assembled[error] == 1) _assembled.Remove(error); else _assembled[error]--; } });
    }
    /// <summary>
    /// Reports async.
    /// </summary>
    public async Task ReportAsync(object? error)
    {
        lock (_gate) { if (_disposed || error is Exception exception && _assembled.ContainsKey(exception)) return; }
        if (Interlocked.Exchange(ref _exiting, 1) != 0) return;
        writeError($"{diagnosticName}: fatal load failure: {error}\n");
        if (release is not null)
        {
            try { await release().WaitAsync(ReleaseTimeout, timeProvider ?? TimeProvider.System).ConfigureAwait(false); }
            catch { /* The pending fatal exit owns the outcome even when release fails or stalls. */ }
        }
        exit(1);
    }
    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public void Dispose() { lock (_gate) _disposed = true; }
    private sealed class Registration(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
