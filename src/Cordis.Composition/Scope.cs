using System.Runtime.CompilerServices;

namespace Cordis.Composition;

/// <summary>A registration Context tagged with an opaque identity, owned by one child Fiber.</summary>
public sealed class Scope : IAsyncDisposable
{
    private sealed record Key(object? Value);

    private static readonly ConditionalWeakTable<Context, Key> Tags = new();
    private static readonly ConditionalWeakTable<object, Key> Carriers = new();
    private static readonly ConditionalWeakTable<object, Key> Parents = new();
    private static readonly object ParentLock = new();
    private readonly Fiber fiber;

    private Scope(Context owner, object key)
    {
        fiber = owner.Plugin(
            new Plugin<object?>
            {
                Name = "scope",
                Apply = (_, _) =>
                {
                }
            });
        Context = fiber.Context.Extend();
        Tags.Add(Context, new(key));
    }

    /// <summary>Register listeners, services and effects through this tagged Context.</summary>
    public Context Context
    {
        get;
    }

    /// <summary>Create within the owner's execution domain. A parent binds routing ancestry, not Fiber ownership.</summary>
    public static Scope Create(Context owner, object key, object? parent = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(key);
        if (parent is not null)
            BindParent(key, parent);
        return new(owner, key);
    }

    /// <summary>Read the nearest tag inherited through Context views.</summary>
    public static object? Of(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        for (Context? cursor = context;cursor is not null;cursor = cursor.ScopeParent)
            if (Tags.TryGetValue(cursor, out var key))
                return key.Value;
        return null;
    }

    /// <summary>Create an opaque routing receiver. Event arguments retain the actual subject.</summary>
    public static Scoped<T> Target<T>(T subject, object? key) where T : class
    {
        ArgumentNullException.ThrowIfNull(subject);
        Func<Context, bool>? filter = subject switch
        {
            Context context => context.Filter,
            Service service => service.Filter,
            IEventFilter receiver => receiver.Filter,
            _ => null
        };
        var carrier = new Scoped<T>(listener =>
        {
            if (filter is not null && !filter(listener))
                return false;
            var tag = Of(listener);
            if (tag is null)
                return true;
            lock (ParentLock)
                for (var cursor = key;cursor is not null;cursor = ParentOfCore(cursor))
                    if (ReferenceEquals(cursor, tag))
                        return true;
            return false;
        });
        Carriers.Add(carrier, new(key));
        return carrier;
    }

    /// <summary>Distinguish an unkeyed routing carrier from an ordinary receiver.</summary>
    public static bool IsCarrier(object? value) => value is not null && Carriers.TryGetValue(value, out _);

    /// <summary>Read a carrier's key. Null means an unkeyed carrier or a non-carrier.</summary>
    public static object? CarrierKeyOf(object? value) =>
        value is not null && Carriers.TryGetValue(value, out var key) ? key.Value : null;

    /// <summary>Bind a key's parent once. Only the returned binding may later change this link.</summary>
    public static ScopeParentBinding BindParent(object key, object parent)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(parent);
        lock (ParentLock)
        {
            if (Parents.TryGetValue(key, out _))
                throw new InvalidOperationException("The scope key already has a parent; use its original binding.");
            LinkParent(key, parent);
        }

        return new(key);
    }

    /// <summary>Read the immediate routing parent of an opaque key.</summary>
    public static object? ParentOf(object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (ParentLock)
            return ParentOfCore(key);
    }

    /// <summary>Snapshot the routing chain, nearest key first.</summary>
    public static IReadOnlyList<object> ChainOf(object? key)
    {
        var chain = new List<object>();
        lock (ParentLock)
            for (var cursor = key;cursor is not null;cursor = ParentOfCore(cursor))
                chain.Add(cursor);
        return chain.AsReadOnly();
    }

    private static object? ParentOfCore(object key) => Parents.TryGetValue(key, out var parent) ? parent.Value : null;

    private static void LinkParent(object key, object parent)
    {
        for (object? cursor = parent;cursor is not null;cursor = ParentOfCore(cursor))
            if (ReferenceEquals(cursor, key))
                throw new InvalidOperationException("The scope parent link would form a cycle.");
        Parents.Remove(key);
        Parents.Add(key, new(parent));
    }

    internal static void Rebind(object key, object parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        lock (ParentLock)
            LinkParent(key, parent);
    }

    /// <summary>Claim the child Fiber's ordinary single-shot disposer for ordered composite cleanup.</summary>
    public ValueTask DisposeRawAsync() => fiber.DisposeAsync();

    /// <summary>Await cleanup, including cleanup previously started through the raw disposer.</summary>
    public async ValueTask DisposeAsync()
    {
        await fiber.DisposeAsync();
        await fiber.WaitAsync();
    }

    internal bool Active => fiber.State == FiberState.Active;
}

/// <summary>An opaque routing receiver branded with its subject type.</summary>
public sealed class Scoped<T> : IEventFilter where T : class
{
    private readonly Func<Context, bool> filter;
    internal Scoped(Func<Context, bool> filter) => this.filter = filter;
    bool IEventFilter.Filter(Context listener) => filter(listener);
}

/// <summary>The original binder's authority to move one routing parent link.</summary>
public sealed class ScopeParentBinding
{
    private readonly object key;
    internal ScopeParentBinding(object key) => this.key = key;

    /// <summary>Move the link after the owner ensures nothing produced under the old parent is retained.</summary>
    public void Rebind(object parent) => Scope.Rebind(key, parent);
}
