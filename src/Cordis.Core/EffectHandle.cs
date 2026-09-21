namespace Cordis;
/// <summary>
/// One immediately-started effect. Public disposal is single-shot; the owning fiber still joins
/// cleanup started by an earlier caller. Ready observes setup, not cleanup or future readiness.
/// </summary>
public sealed class EffectHandle : IAsyncDisposable
{
    private readonly Fiber _owner;
    private readonly List<Func<Task>> _cleanups = [];
    private readonly List<EffectHandle> _children = [];
    private readonly TaskCompletionSource _setup = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource? _cleanup;
    private Task? _disposal;
    private bool _active = true;
    internal EffectHandle(Fiber owner, string label) => (_owner, Label) = (owner, label);
    /// <summary>
    /// Gets the label value.
    /// </summary>
    public string Label
    {
        get;
    }
    /// <summary>
    /// Gets the ready value.
    /// </summary>
    public Task Ready => _setup.Task;

    internal EffectMetadata Describe() => new(Label, Array.AsReadOnly(_children.Select(child => child.Describe()).ToArray()));
    internal void Start(Action setup)
    {
        try
        {
            setup();
            _setup.TrySetResult();
        }
        catch (Exception error)
        {
            _active = false;
            _setup.TrySetException(error);
            _ = _setup.Task.Exception; // The synchronous caller also receives this failure.
            _ = ObserveRollbackAsync(CleanupOnceAsync());
            throw;
        }
    }

    internal void StartAsync(Func<Task<IAsyncDisposable>> setup)
    {
        try
        {
            Task<IAsyncDisposable> pending = setup() ?? throw new ArgumentException("Effect setup returned a null Task.", nameof(setup));
            _ = FinishSetupAsync(pending);
        }
        catch (Exception error)
        {
            _active = false;
            _setup.TrySetException(error);
            _ = _setup.Task.Exception;
            _ = ObserveRollbackAsync(CleanupOnceAsync());
            throw;
        }
    }

    private async Task FinishSetupAsync(Task<IAsyncDisposable> pending)
    {
        try
        {
            IAsyncDisposable cleanup = await pending;
            Collect(cleanup);
            _setup.TrySetResult();
        }
        catch (Exception error)
        {
            _setup.TrySetException(error);
            _ = _setup.Task.Exception;
            await ObserveRollbackAsync(CleanupOnceAsync());
        }
    }

    internal void StartStream(Func<IAsyncEnumerable<IAsyncDisposable>> setup)
    {
        IAsyncEnumerable<IAsyncDisposable> stream;
        try
        {
            stream = setup();
        }
        catch (Exception error)
        {
            _active = false;
            _setup.TrySetException(error);
            _ = _setup.Task.Exception;
            _ = ObserveRollbackAsync(CleanupOnceAsync());
            throw;
        }

        _ = IterateAsync();
        async Task IterateAsync()
        {
            try
            {
                await Task.Yield();
                // The pinned runner stops requesting values on cancellation; it does not call
                // iterator.return(). Only yielded cleanup values belong to the effect.
                var iterator = stream.GetAsyncEnumerator();
                while (_active && await iterator.MoveNextAsync())
                    Collect(iterator.Current);
                _setup.TrySetResult();
            }
            catch (Exception error)
            {
                _setup.TrySetException(error);
                _ = _setup.Task.Exception;
                await ObserveRollbackAsync(CleanupOnceAsync());
            }
        }
    }

    internal void Collect(Action? cleanup)
    {
        if (cleanup is null)
            return;
        _cleanups.Add(() =>
        {
            cleanup();
            return Task.CompletedTask;
        });
    }

    internal void Collect(IDisposable? cleanup)
    {
        if (cleanup is not null)
            Collect(cleanup.Dispose);
    }

    internal void Collect(IAsyncDisposable? cleanup)
    {
        if (cleanup is null)
            return;
        if (cleanup is EffectHandle nested)
        {
            if (!ReferenceEquals(nested._owner, _owner))
                throw new ArgumentException("An effect can adopt only another effect of the same fiber.");
            _owner.RemoveEffect(nested);
            _children.Add(nested);
            _cleanups.Add(nested.JoinCoreAsync);
        }
        else
        {
            _cleanups.Add(() => cleanup.DisposeAsync().AsTask());
        }
    }

    /// <summary>
    /// The first call waits for setup and cleanup. Later public calls complete immediately,
    /// matching the baseline single-shot disposer rather than becoming another cleanup join.
    /// </summary>
    public ValueTask DisposeAsync() => new(_owner.Execution.RunAsync(DisposeCoreAsync));
    internal Task DisposeCoreAsync()
    {
        if (!_active)
            return Task.CompletedTask;
        _active = false;
        _disposal = DisposeAfterSetupAsync();
        return _disposal;
    }

    // Structural ownership is intentionally different from repeated public disposer calls.
    internal Task JoinCoreAsync()
    {
        Task first = DisposeCoreAsync();
        return _disposal ?? _cleanup?.Task ?? first;
    }

    private async Task DisposeAfterSetupAsync()
    {
        Exception? setupError = null;
        try
        {
            await _setup.Task;
        }
        catch (Exception error)
        {
            setupError = error;
        }

        await CleanupOnceAsync();
        if (setupError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(setupError).Throw();
    }

    private Task CleanupOnceAsync()
    {
        if (_cleanup is not null)
            return _cleanup.Task;
        _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = CompleteCleanupAsync();
        return _cleanup.Task;
    }

    private async Task CompleteCleanupAsync()
    {
        Func<Task>[] callbacks = _cleanups.AsEnumerable().Reverse().ToArray();
        _cleanups.Clear();
        try
        {
            // Baseline local effect groups short-circuit on a failed cleanup. The fiber's
            // separate sibling groups are nevertheless all attempted (Fiber.UnloadAsync).
            foreach (Func<Task> cleanup in callbacks)
                await cleanup();
            _cleanup!.TrySetResult();
        }
        catch (Exception error)
        {
            _cleanup!.TrySetException(error);
        }
        finally
        {
            _owner.RemoveEffect(this);
        }
    }

    private async Task ObserveRollbackAsync(Task cleanup)
    {
        try
        {
            await cleanup;
        }
        catch (Exception error)
        {
            _owner.Report(error);
        }
    }
}

/// <summary>A snapshot of a live effect group and its structurally adopted child groups.</summary>
public sealed record EffectMetadata(string Label, IReadOnlyList<EffectMetadata> Children);
