using Cordis.Composition;

/// <summary>One launcher commit; plugins borrow only the subscription contract.</summary>
internal sealed class ApplicationReadiness : IApplicationReady
{
    private readonly object gate = new();
    private readonly List<Action> listeners = [];
    private bool ready;

    public IDisposable OnReady(Action listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        lock (gate)
        {
            if (!ready)
            {
                listeners.Add(listener);
                return new Registration(this, listener);
            }
        }

        listener();
        return new Registration(null, listener);
    }

    internal void Commit()
    {
        Action[] pending;
        lock (gate)
        {
            if (ready)
                return;
            ready = true;
            pending = listeners.ToArray();
            listeners.Clear();
        }

        foreach (var listener in pending)
            listener();
    }

    private sealed class Registration(ApplicationReadiness? owner, Action listener) : IDisposable
    {
        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null)
                return;
            lock (current.gate)
                current.listeners.Remove(listener);
        }
    }
}
