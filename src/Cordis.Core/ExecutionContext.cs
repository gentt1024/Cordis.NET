using System.Collections.Concurrent;

namespace Cordis;
// A synchronization context, not a second scheduler for plugin lifecycle. One queued callback
// is one JS-like synchronous turn. Async methods capture this context; no thread is dedicated.
// This boundary prevents Task.Yield's pool continuation overtaking the caller's next statement.
internal sealed class CordisExecutionContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private int _draining;
    internal bool HasAccess => ReferenceEquals(Current, this);

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        _queue.Enqueue((d, state));
        Schedule();
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!HasAccess)
            throw new InvalidOperationException("Use Context.RunAsync to enter this Cordis execution context.");
        d(state);
    }

    private void Schedule()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            ThreadPool.UnsafeQueueUserWorkItem(static (CordisExecutionContext state) => state.Drain(), this, preferLocal: false);
    }

    private void Drain()
    {
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(this);
        try
        {
            while (_queue.TryDequeue(out var item))
                item.Callback(item.State);
        }
        finally
        {
            SetSynchronizationContext(previous);
            Volatile.Write(ref _draining, 0);
            if (!_queue.IsEmpty)
                Schedule();
        }
    }

    internal Task RunAsync(Func<Task> operation)
    {
        if (HasAccess)
        {
            try
            {
                return operation() ?? throw new InvalidOperationException("The operation returned a null Task.");
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Do not let the first scheduler caller's AsyncLocals leak into subsequent entries.
        // Await continuations carry their own ExecutionContext; initial host entries need one too.
        System.Threading.ExecutionContext? caller = System.Threading.ExecutionContext.Capture();
        Post(state =>
        {
            if (caller is null)
            {
                _ = CompleteAsync();
                return;
            }

            System.Threading.ExecutionContext.Run(caller, ignored =>
            {
                SynchronizationContext? previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    _ = CompleteAsync();
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }, null);
        }, null);
        return completion.Task;
        async Task CompleteAsync()
        {
            try
            {
                await (operation() ?? throw new InvalidOperationException("The operation returned a null Task."));
                completion.TrySetResult();
            }
            catch (OperationCanceledException error)
            {
                completion.TrySetCanceled(error.CancellationToken);
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        }
    }

    internal void VerifyAccess()
    {
        if (!HasAccess)
            throw new InvalidOperationException("Cordis operations must execute inside Context.RunAsync or a Cordis callback. " + "After ConfigureAwait(false), marshal back with Context.RunAsync.");
    }
}
