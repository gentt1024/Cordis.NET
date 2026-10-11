using System.Text.Json;

namespace Cordis.Composition;

internal sealed class TypertUplinkReader : IAsyncDisposable
{
    private readonly IAsyncEnumerator<JsonElement> source;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken invocation;
    private readonly Func<JsonElement, object?> decode;
    private readonly Action<Exception> reject;
    private readonly object gate = new();
    private Task<bool>? pending;
    private Task? disposal;
    private bool taken;
    private bool closed;

    internal TypertUplinkReader(IAsyncEnumerable<JsonElement> source, Func<JsonElement, object?> decode,
        CancellationToken invocation, Action<Exception> reject)
    {
        this.invocation = invocation;
        this.decode = decode;
        this.reject = reject;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(invocation);
        try
        {
            this.source = source.GetAsyncEnumerator(lifetime.Token);
        }
        catch { lifetime.Dispose(); throw; }
    }

    internal IAsyncEnumerable<T> Take<T>()
    {
        lock (gate)
        {
            if (taken)
                throw new InvalidOperationException("A Remote invocation has one uplink consumer.");
            taken = true;
        }
        return new TypedEnumerable<T>(this);
    }

    private async ValueTask<(bool Moved, object? Value)> ReadAsync(CancellationToken token)
    {
        invocation.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
        Task<bool> move;
        lock (gate)
        {
            if (closed)
                return (false, null);
            if (pending is not null)
                throw new InvalidOperationException("A Remote uplink has one pending read.");
            move = pending = source.MoveNextAsync().AsTask();
        }
        using var registration = token.Register(() =>
        {
            lock (gate)
                if (!closed)
                    lifetime.Cancel();
        });
        try
        {
            var moved = await move.ConfigureAwait(false);
            invocation.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            JsonElement current;
            lock (gate)
            {
                if (closed || !moved)
                    return (false, null);
                current = source.Current;
            }
            try
            {
                return (true, decode(current));
            }
            catch (Exception error)
            {
                reject(error);
                throw;
            }
        }
        catch (OperationCanceledException) when (closed && !invocation.IsCancellationRequested && !token.IsCancellationRequested)
        {
            return (false, null);
        }
        finally
        {
            lock (gate)
                pending = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            closed = true;
            return new(disposal ??= CloseAsync(pending));
        }
    }

    private async Task CloseAsync(Task<bool>? read)
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (read is not null)
            try
            {
                await read.ConfigureAwait(false);
            }
            catch (Exception) { }
        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
        finally { lifetime.Dispose(); }
    }

    private sealed class TypedEnumerable<T>(TypertUplinkReader owner) : IAsyncEnumerable<T>
    {
        private int consumed;
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref consumed, 1) != 0)
                throw new InvalidOperationException("A Remote uplink has one consumer.");
            return new TypedEnumerator<T>(owner, cancellationToken);
        }
    }

    private sealed class TypedEnumerator<T>(TypertUplinkReader owner, CancellationToken token) : IAsyncEnumerator<T>
    {
        public T Current { get; private set; } = default!;
        public async ValueTask<bool> MoveNextAsync()
        {
            var next = await owner.ReadAsync(token).ConfigureAwait(false);
            if (next.Moved)
                Current = (T)next.Value!;
            return next.Moved;
        }
        public ValueTask DisposeAsync() => owner.DisposeAsync();
    }
}
