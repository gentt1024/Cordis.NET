namespace Cordis.Composition;

/// <summary>A synchronously resolved Client Context and its optional invocation-owned cleanup.</summary>
public sealed class TypertResolvedContext : IDisposable
{
    private Action? release;

    private TypertResolvedContext(Context value, Action? release)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        this.release = release;
    }

    /// <summary>The Context passed to the listener. Resolving it does not change its Fiber owner.</summary>
    public Context Value
    {
        get;
    }

    /// <summary>Borrow a Context without transferring cleanup responsibility.</summary>
    public static implicit operator TypertResolvedContext?(Context? value) =>
        value is null ? null : new(value, null);

    /// <summary>Transfer one synchronous, non-throwing cleanup to the invocation and reply owner.</summary>
    public static TypertResolvedContext Owned(Context value, Action release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return new(value, release);
    }

    /// <summary>Release an owned value at most once. Borrowed Contexts are untouched.</summary>
    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
}
