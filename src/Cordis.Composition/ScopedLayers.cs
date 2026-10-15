namespace Cordis.Composition;

/// <summary>One scope's aggregate contribution to a registry.</summary>
public interface IScopeLayer
{
    /// <summary>Whether every contribution table in the layer is empty.</summary>
    bool IsEmpty
    {
        get;
    }
}

/// <summary>Own global and exact-scope layers, deriving visibility and cleanup from each registration Context.</summary>
public sealed class ScopedLayers<TLayer> where TLayer : class, IScopeLayer
{
    private readonly object gate = new();
    private readonly Dictionary<object, TLayer> scoped = new(ReferenceEqualityComparer.Instance);
    private readonly Func<object?, TLayer> createLayer;
    private readonly Action onChange;

    /// <summary>Construct the global layer eagerly; reads never create scoped overlays.</summary>
    public ScopedLayers(Func<object?, TLayer> createLayer, Action onChange)
    {
        ArgumentNullException.ThrowIfNull(createLayer);
        ArgumentNullException.ThrowIfNull(onChange);
        this.createLayer = createLayer;
        this.onChange = onChange;
        Global = createLayer(null);
    }

    /// <summary>The registry's eagerly created global layer.</summary>
    public TLayer Global
    {
        get;
    }

    /// <summary>Read only the exact scope's existing overlay; this does not follow parents.</summary>
    public TLayer? Peek(object? scope)
    {
        lock (gate)
            return scope is null ? null : scoped.GetValueOrDefault(scope);
    }

    /// <summary>Snapshot existing parent-chain layers, farthest ancestor first and exact scope last.</summary>
    public IReadOnlyList<TLayer> ChainLayers(object? scope)
    {
        lock (gate)
            return Scope.ChainOf(scope).Reverse().Where(scoped.ContainsKey).Select(key => scoped[key]).ToArray();
    }

    /// <summary>Copy global named entries and parent-chain shadows, retaining insertion order.</summary>
    public OrderedDictionary<string, TValue> Merge<TValue>(object? scope, Func<TLayer, NamedEntries<TValue>> pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        lock (gate)
        {
            var merged = new OrderedDictionary<string, TValue>(StringComparer.Ordinal);
            foreach (var entry in pick(Global).Entries)
                merged.Add(entry.Key, entry.Value);
            foreach (var layer in ChainLayers(scope))
                foreach (var entry in pick(layer).Entries)
                    merged[entry.Key] = entry.Value;
            return merged;
        }
    }

    /// <summary>Attach an atomic synchronous mutation and undo to the supplied registration Context.</summary>
    /// <remarks>The existing Context effect collects undo before notification. Values remain borrowed.</remarks>
    public EffectHandle Effect(Context context, Func<TLayer, Action> action, string label, bool notify = true)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(action);
        var scope = Scope.Of(context);
        return context.Effect(Apply, label);

        IEnumerable<IAsyncDisposable> Apply()
        {
            TLayer layer;
            Action undo;
            lock (gate)
            {
                var created = false;
                if (scope is null)
                    layer = Global;
                else if (!scoped.TryGetValue(scope, out layer!))
                {
                    layer = createLayer(scope);
                    scoped.Add(scope, layer);
                    created = true;
                }

                try
                {
                    undo = action(layer);
                }
                catch
                {
                    if (scope is not null && created && layer.IsEmpty)
                        scoped.Remove(scope);
                    throw;
                }
            }

            yield return new Cleanup(() =>
            {
                lock (gate)
                {
                    undo();
                    if (scope is not null && layer.IsEmpty)
                        scoped.Remove(scope);
                    if (notify)
                        onChange();
                }
            });
            if (notify)
                onChange();
        }
    }

    private sealed class Cleanup(Action undo) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            undo();
            return ValueTask.CompletedTask;
        }
    }
}
