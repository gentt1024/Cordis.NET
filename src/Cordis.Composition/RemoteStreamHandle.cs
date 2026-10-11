using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cordis.Composition;

/// <summary>A failure of the physical carrier, distinct from a Remote error frame.</summary>
public sealed class RemoteStreamCarrierException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>One eagerly opened, single-consumer Remote downlink. Disposal cancels only this logical stream.</summary>
public sealed class RemoteStreamHandle<T> : IAsyncEnumerable<T>, IAsyncDisposable
{
    private readonly TypertStreamCall call;
    private readonly Func<JsonElement, T> decode;
    private readonly Func<bool> active;
    private int consumed;
    private int disposed;

    internal RemoteStreamHandle(TypertStreamCall call, Func<JsonElement, T> decode, Func<bool> active)
    {
        this.call = call;
        this.decode = decode;
        this.active = active;
    }

    /// <summary>Take the downlink's only consumer. Cancelling enumeration cancels this logical stream.</summary>
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref consumed, 1) != 0)
            throw new InvalidOperationException("A Remote stream has one consumer.");
        return new Enumerator(this, Iterate(cancellationToken).GetAsyncEnumerator(CancellationToken.None));
    }

    private async IAsyncEnumerable<T> Iterate([EnumeratorCancellation] CancellationToken token)
    {
        try
        {
            while (true)
            {
                JsonElement value;
                try
                {
                    var next = await call.ReadAsync(token).ConfigureAwait(false);
                    if (Volatile.Read(ref disposed) != 0 || !next.HasValue)
                        yield break;
                    value = next.Value;
                }
                catch (Exception) when (Volatile.Read(ref disposed) != 0)
                {
                    yield break;
                }

                if (!active())
                    throw new InvalidOperationException("The Remote contribution was withdrawn.");
                yield return decode(value);
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Cancel the stream and await its local pump and registration cleanup, without a remote acknowledgement.</summary>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return call.DisposeAsync();
    }

    internal void Send(TypertCodec codec, object? value)
    {
        CheckInput();
        call.CheckWritable();
        var encoded = codec.Encode(value);
        TypertCodec.ValidateUplinkItem(encoded);
        _ = codec.Decode(encoded);
        call.Send(encoded);
    }

    internal void End()
    {
        call.EndInput();
    }

    private void CheckInput()
    {
        if (Volatile.Read(ref disposed) != 0 || !active())
            throw new InvalidOperationException("The Remote stream is no longer writable.");
    }

    private sealed class Enumerator(RemoteStreamHandle<T> owner, IAsyncEnumerator<T> iterator) : IAsyncEnumerator<T>
    {
        public T Current => iterator.Current;
        public ValueTask<bool> MoveNextAsync() => iterator.MoveNextAsync();

        public async ValueTask DisposeAsync()
        {
            // Disposal must also close a handle whose iterator has never been started.
            await owner.DisposeAsync().ConfigureAwait(false);
            await iterator.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>A Remote stream with a declared typed uplink and a single downlink consumer.</summary>
public sealed class RemoteStreamHandle<TOutput, TInput> : IAsyncEnumerable<TOutput>, IAsyncDisposable
{
    private readonly RemoteStreamHandle<TOutput> output;
    private readonly TypertCodec input;

    internal RemoteStreamHandle(RemoteStreamHandle<TOutput> output, TypertCodec input)
    {
        this.output = output;
        this.input = input;
    }

    /// <summary>Validate and enqueue an item locally, without waiting for Host receipt or execution.</summary>
    public void Send(TInput value) => output.Send(input, value);

    /// <summary>Half-close the uplink after queued items. The downlink remains active.</summary>
    public void End() => output.End();

    /// <summary>Take the single downlink consumer.</summary>
    public IAsyncEnumerator<TOutput> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        output.GetAsyncEnumerator(cancellationToken);

    /// <summary>Cancel this logical stream and await its owned local work.</summary>
    public ValueTask DisposeAsync() => output.DisposeAsync();
}
