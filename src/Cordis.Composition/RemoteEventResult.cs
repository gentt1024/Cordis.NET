using System.Text.Json;

namespace Cordis.Composition;

/// <summary>A typed waterfall result, retaining the continuation marker separately from every business value.</summary>
public readonly struct RemoteEventResult<T>
{
    private readonly T? value;

    private RemoteEventResult(T value)
    {
        this.value = value;
        HasValue = true;
    }

    /// <summary>Whether this listener answered, including a null or RemoteOptional.Undefined answer.</summary>
    public bool HasValue
    {
        get;
    }

    /// <summary>The listener's answer.</summary>
    public T Value =>
        HasValue ? value! : throw new InvalidOperationException("The waterfall continued without an answer.");

    /// <summary>Return a business value without changing its presence or null semantics.</summary>
    public static implicit operator RemoteEventResult<T>(T value) => new(value);
}

/// <summary>Typed listener plumbing emitted from the common event declaration.</summary>
public static class TypertEventListeners
{
    /// <summary>Register a typed scoped listener using the current reverse Context adapter.</summary>
    public static EffectHandle On<TRequest, TResult>(
        TypertRemoteEventsClient client,
        Context owner,
        string name,
        TypertCodec requestCodec,
        TypertCodec resultCodec,
        Func<Context, TRequest, Func<Task<RemoteEventResult<TResult>>>, CancellationToken,
            Task<RemoteEventResult<TResult>>> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return client.On(owner, name, (evt, args) => Invoke(evt, (TypertRemoteEventRequest)args[0]!));

        async Task<object?> Invoke(EventContext evt, TypertRemoteEventRequest request)
        {
            var result = await listener(
                request.Agent,
                (TRequest)requestCodec.DecodeOutput(request.Data)!,
                Next,
                request.CancellationToken);
            return result.HasValue ? resultCodec.EncodeInput(result.Value) : TypertRemoteEventsClient.NextMarker;

            async Task<RemoteEventResult<TResult>> Next()
            {
                var raw = await TypertRemoteEventsClient.AwaitAsync(evt.Next());
                return ReferenceEquals(raw, TypertRemoteEventsClient.NextMarker)
                    ? default(RemoteEventResult<TResult>)
                    : (TResult)resultCodec.DecodeOutput(raw is JsonElement json ? json : default)!;
            }
        }
    }
}
