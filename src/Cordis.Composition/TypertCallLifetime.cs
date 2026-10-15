namespace Cordis.Composition;

// Each owner cancels and drains only the calls admitted through that owner.
internal sealed class TypertCallLifetime : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private readonly HashSet<Lease> calls = [];
    private Task? disposal;

    public bool Active
    {
        get
        {
            lock (gate)
                return disposal is null;
        }
    }

    public Lease? Enter()
    {
        lock (gate)
        {
            if (disposal is not null)
                return null;
            var lease = new Lease(this, cancellation.Token);
            calls.Add(lease);
            return lease;
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] pending;
        lock (gate)
        {
            if (disposal is not null)
                return new(disposal);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            disposal = completion.Task;
            pending = calls.Select(call => call.Completion).ToArray();
        }

        _ = DrainAsync(pending, completion);
        return new(completion.Task);
    }

    private async Task DrainAsync(Task[] pending, TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            cancellation.Cancel();
        }
        catch (Exception error)
        {
            failure = error;
        }

        await Task.WhenAll(pending).ConfigureAwait(false);
        cancellation.Dispose();
        if (failure is null)
            completion.SetResult();
        else
            completion.SetException(failure);
    }

    internal sealed class Lease(TypertCallLifetime owner, CancellationToken token) : IDisposable
    {
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken Token
        {
            get;
        } = token;

        public Task Completion => completion.Task;

        public void Dispose()
        {
            lock (owner.gate)
                owner.calls.Remove(this);
            completion.TrySetResult();
        }
    }
}
