namespace Cordis.Composition;

/// <summary>The outcome of an authored Remote method with no business value.</summary>
public sealed class RemoteResult
{
    private readonly RemoteError? error;
    private RemoteResult(RemoteError? error) => this.error = error;

    /// <summary>Gets whether the call succeeded.</summary>
    public bool Ok => error is null;

    /// <summary>Gets the failed outcome.</summary>
    public RemoteError Error => error ?? throw new InvalidOperationException("A successful Remote call has no error.");

    /// <summary>Create a successful outcome without a value.</summary>
    public static RemoteResult Success() => new(null);

    /// <summary>Create a failed outcome.</summary>
    public static RemoteResult Failure(RemoteError error) =>
        new(error ?? throw new ArgumentNullException(nameof(error)));
}

/// <summary>The outcome of a Remote call. Local assembly faults remain exceptions.</summary>
public sealed class RemoteResult<T>
{
    private readonly T? value;
    private readonly RemoteError? error;

    private RemoteResult(bool ok, T? value, RemoteError? error)
    {
        Ok = ok;
        this.value = value;
        this.error = error;
    }

    /// <summary>Gets whether the call succeeded.</summary>
    public bool Ok
    {
        get;
    }

    /// <summary>Gets the successful business value.</summary>
    public T Value => Ok ? value! : throw new InvalidOperationException("A failed Remote call has no value.");

    /// <summary>Gets the call failure, preserving its remote code and details.</summary>
    public RemoteError Error => error ?? throw new InvalidOperationException("A successful Remote call has no error.");

    /// <summary>Create a successful outcome.</summary>
    public static RemoteResult<T> Success(T value) => new(true, value, null);

    /// <summary>Create a failed outcome.</summary>
    public static RemoteResult<T> Failure(RemoteError error) => new(
        false,
        default,
        error ?? throw new ArgumentNullException(nameof(error)));
}
