namespace Cordis.Composition;

/// <summary>An identity-stable boolean permission source.</summary>
public sealed class TypertAccessSource
{
    private readonly TypertAccessState state;
    private readonly TypertAccessResource resource;
    private readonly string action;

    internal TypertAccessSource(TypertAccessState state, TypertAccessResource resource, string action)
    {
        this.state = state;
        this.resource = resource;
        this.action = action;
    }

    /// <summary>Read the latest permission answer.</summary>
    public bool Value => state.Answer(resource, action);

    /// <summary>Observe snapshot changes; dispose to remove the listener.</summary>
    public IDisposable Subscribe(Action listener) => state.Subscribe(listener);
}

/// <summary>The latest Client access snapshot and stable permission sources.</summary>
public sealed class TypertAccessState
{
    private readonly object gate = new();
    private readonly Dictionary<(TypertAccessResource, string), TypertAccessSource> sources = [];
    private readonly HashSet<Action> listeners = [];
    private Dictionary<TypertAccessResource, HashSet<string>>? grants;
    private bool owner;

    /// <summary>Create closed state, or apply the supplied page baseline.</summary>
    public TypertAccessState(TypertAccessFrame.Baseline? initial = null)
    {
        if (initial is not null)
            Replace(initial);
    }

    /// <summary>Whether the current baseline identifies the owner.</summary>
    public bool Owner
    {
        get
        {
            lock (gate)
                return owner;
        }
    }

    /// <summary>Get a stable source for an action on the global resource.</summary>
    public TypertAccessSource Global(string action) => Cached(TypertAccessResource.Global, action);

    /// <summary>Get a stable source for an action on one named resource.</summary>
    public TypertAccessSource Resource(string kind, string id, string action) =>
        Cached(TypertAccessResource.Create(kind, id), action);

    /// <summary>Atomically replace the snapshot from an opening frame.</summary>
    public void Replace(TypertAccessFrame.Baseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        Dictionary<TypertAccessResource, HashSet<string>>? next = null;
        if (baseline is TypertAccessFrame.Baseline.Granted granted)
        {
            next = [];
            foreach (var grant in granted.Grants)
                next[grant.Resource] = new(grant.Actions, StringComparer.Ordinal);
        }

        Publish(() =>
        {
            owner = baseline is TypertAccessFrame.Baseline.Owner;
            grants = next;
            return true;
        });
    }

    /// <summary>Apply one grant increment; deltas have no effect before a non-owner baseline.</summary>
    public void Update(TypertAccessFrame frame)
    {
        if (frame is not (TypertAccessFrame.Upsert or TypertAccessFrame.Remove))
            throw new ArgumentException("An access update must be an upsert or remove frame.", nameof(frame));
        Publish(() =>
        {
            if (grants is null)
                return false;
            if (frame is TypertAccessFrame.Upsert upsert)
                grants[upsert.Grant.Resource] = new(upsert.Grant.Actions, StringComparer.Ordinal);
            else if (frame is TypertAccessFrame.Remove remove)
                grants.Remove(remove.Resource);
            return true;
        });
    }

    /// <summary>Close non-owner permissions after a terminal stream failure; owner answers remain true.</summary>
    public void Unavailable() => Publish(() =>
    {
        if (owner)
            return false;
        grants = null;
        return true;
    });

    internal bool Answer(TypertAccessResource resource, string action)
    {
        lock (gate)
            return owner || grants is not null && grants.TryGetValue(resource, out var actions) &&
                actions.Contains(action);
    }

    internal IDisposable Subscribe(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate)
            listeners.Add(listener);
        return new Subscription(this, listener);
    }

    private TypertAccessSource Cached(TypertAccessResource resource, string action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (gate)
        {
            var key = (resource, action);
            if (!sources.TryGetValue(key, out var source))
                sources.Add(key, source = new(this, resource, action));
            return source;
        }
    }

    private void Publish(Func<bool> change)
    {
        Action[] selected;
        lock (gate)
        {
            if (!change())
                return;
            selected = listeners.ToArray();
        }

        foreach (var listener in selected)
            listener();
    }

    private sealed class Subscription(TypertAccessState state, Action listener) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                lock (state.gate)
                    state.listeners.Remove(listener);
        }
    }
}
