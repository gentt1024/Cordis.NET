using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>Shared call semantics for generated Remote clients borrowing a Connection.</summary>
public abstract class TypertClient : IAsyncDisposable
{
    private readonly TypertConnection connection;
    private readonly TypertCallLifetime lifetime = new();

    /// <summary>Create a contribution borrowing the application's shared Connection.</summary>
    protected TypertClient(TypertConnection connection) =>
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));

    /// <summary>Prepare a call in its caller domain, then encode and execute it through the shared Connection.</summary>
    protected Task<RemoteResult<T>> InvokeAsync<TArgs, T>(
        string endpoint,
        Func<Task<TArgs>> prepare,
        JsonTypeInfo<TArgs> argumentsType,
        JsonTypeInfo<T> resultType,
        bool nullableResult,
        CancellationToken cancellationToken) => InvokeCoreAsync(endpoint, prepare, argumentsType, raw =>
        {
            if (raw is not { } value)
                throw new JsonException("Remote result is missing its declared value.");
            if (!nullableResult && value.ValueKind == JsonValueKind.Null)
                throw new JsonException("Non-null Remote result cannot be null.");
            return value.Deserialize(resultType)!;
        }, cancellationToken);

    /// <summary>Invoke a method whose successful result has no value field.</summary>
    protected async Task<RemoteResult> InvokeAsync<TArgs>(
        string endpoint,
        Func<Task<TArgs>> prepare,
        JsonTypeInfo<TArgs> argumentsType,
        CancellationToken cancellationToken)
    {
        var result = await InvokeCoreAsync(endpoint, prepare, argumentsType, value =>
        {
            if (value.HasValue)
                throw new JsonException("A void Remote result must omit its value.");
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return result.Ok ? RemoteResult.Success() : RemoteResult.Failure(result.Error);
    }

    private async Task<RemoteResult<T>> InvokeCoreAsync<TArgs, T>(
        string endpoint,
        Func<Task<TArgs>> prepare,
        JsonTypeInfo<TArgs> argumentsType,
        Func<JsonElement?, T> decode,
        CancellationToken cancellationToken)
    {
        using var call = lifetime.Enter();
        if (call is null)
            return Withdrawn<T>(endpoint);
        var arguments = await prepare().ConfigureAwait(false);
        if (!connection.Active)
            throw new InvalidOperationException("Remote " + endpoint + " has no active Connection.");
        using var signal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Token);
        try
        {
            var encoded = JsonSerializer.SerializeToElement(arguments, argumentsType);
            var result = await connection.CallAsync(endpoint, encoded, signal.Token).ConfigureAwait(false);
            if (!lifetime.Active)
                return Withdrawn<T>(endpoint);
            signal.Token.ThrowIfCancellationRequested();
            if (!result.Ok)
            {
                var error = result.Error!;
                return RemoteResult<T>.Failure(new(error.Code, error.Message, error.Details));
            }

            return RemoteResult<T>.Success(decode(result.Value));
        }
        catch (Exception error)
        {
            return RemoteResult<T>.Failure(new(
                signal.IsCancellationRequested ? "gateway/cancelled" : "gateway/internal",
                "Remote " + endpoint + " failed: " + error.Message,
                innerException: error));
        }
    }

    private static RemoteResult<T> Withdrawn<T>(string endpoint) =>
        RemoteResult<T>.Failure(new("gateway/internal", "Remote method " + endpoint + " is no longer mounted."));

    /// <summary>Prepare a logical stream and start its carrier before returning the single-consumer handle.</summary>
    protected async Task<RemoteStreamHandle<T>> OpenStreamAsync<TArgs, T>(
        string endpoint,
        Func<Task<TArgs>> prepare,
        JsonTypeInfo<TArgs> argumentsType,
        JsonTypeInfo<T> resultType,
        bool nullableResult,
        CancellationToken cancellationToken)
    {
        var call = lifetime.Enter() ?? throw new InvalidOperationException("The Remote contribution was withdrawn.");
        StreamOwner? owner = null;
        try
        {
            var arguments = await prepare().ConfigureAwait(false);
            if (!connection.Active)
                throw new InvalidOperationException("Remote " + endpoint + " has no active Connection.");
            var signal = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Token);
            owner = new StreamOwner(call, signal);
            var stream = connection.Open(endpoint, () => JsonSerializer.SerializeToElement(arguments, argumentsType), signal.Token, owner);
            return new(stream, value =>
            {
                if (value.ValueKind == JsonValueKind.Undefined)
                    throw new JsonException("Remote stream item is missing its declared value.");
                if (!nullableResult && value.ValueKind == JsonValueKind.Null)
                    throw new JsonException("Non-null Remote stream item cannot be null.");
                return value.Deserialize(resultType)!;
            }, () => lifetime.Active);
        }
        catch
        {
            if (owner is null)
                call.Dispose();
            else
                owner.Dispose();
            throw;
        }
    }

    /// <summary>Prepare an eager stream with the declared input codec and independent input/output capabilities.</summary>
    protected async Task<RemoteStreamHandle<TOutput, TInput>> OpenDuplexAsync<TArgs, TOutput, TInput>(
        string endpoint, Func<Task<TArgs>> prepare, JsonTypeInfo<TArgs> argumentsType,
        JsonTypeInfo<TOutput> resultType, bool nullableResult, TypertCodec input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = input.Schema;
        return new(await OpenStreamAsync(endpoint, prepare, argumentsType, resultType, nullableResult, cancellationToken).ConfigureAwait(false), input);
    }

    private sealed class StreamOwner(IDisposable call, CancellationTokenSource signal) : IDisposable
    {
        public void Dispose()
        {
            signal.Dispose();
            call.Dispose();
        }
    }

    /// <summary>Withdraw this contribution and drain its calls. Other clients and borrowed resources remain owned by their providers.</summary>
    public ValueTask DisposeAsync() => lifetime.DisposeAsync();
}
