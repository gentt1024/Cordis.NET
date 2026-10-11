namespace Cordis.Composition;

/// <summary>A Remote item that distinguishes absence from a present value, including null.</summary>
public readonly struct RemoteOptional<T>
{
    private readonly T? value;

    /// <summary>Create a present item. Null is a value when the declared T allows it.</summary>
    public RemoteOptional(T value)
    {
        this.value = value;
        HasValue = true;
    }

    /// <summary>The absent item, encoded without a value field.</summary>
    public static RemoteOptional<T> Undefined => default;

    /// <summary>Whether an item has a value. This remains true for an explicit null.</summary>
    public bool HasValue
    {
        get;
    }

    /// <summary>Get the present value; reading an absent item is a local usage error.</summary>
    public T Value => HasValue ? value! : throw new InvalidOperationException("The Remote item is undefined.");
}
