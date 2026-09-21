namespace Cordis;
/// <summary>Dependency declaration normalization; a dictionary value is intercept configuration.</summary>
public static class Inject
{
    /// <summary>
    /// Resolves the requested value.
    /// </summary>
    public static Dictionary<string, object?> Resolve(IEnumerable<string> names) => names.Distinct(StringComparer.Ordinal).ToDictionary(name => name, _ => (object?)null, StringComparer.Ordinal);
    /// <summary>
    /// Resolves the requested value.
    /// </summary>
    public static Dictionary<string, object?> Resolve(IReadOnlyDictionary<string, object?> declaration) => new(declaration, StringComparer.Ordinal);
}

/// <summary>Ordered reference collection with stable per-registration removal and reverse draining.</summary>
public sealed class DisposableList<T> : IEnumerable<T> where T : class
{
    private readonly SortedDictionary<long, T> _values = [];
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<T, Index> _keys = new();
    private sealed record Index(long Value);
    private long _sequence;
    /// <summary>
    /// Gets the count value.
    /// </summary>
    public int Count => _values.Count;

    /// <summary>
    /// Adds the requested value.
    /// </summary>
    public Func<bool> Add(T value)
    {
        long key = ++_sequence;
        _values.Add(key, value);
        _keys.Remove(value);
        _keys.Add(value, new Index(key));
        return () => _values.Remove(key);
    }

    /// <summary>
    /// Removes the requested value.
    /// </summary>
    public bool Remove(T value) => _keys.TryGetValue(value, out var key) && _values.Remove(key.Value);
    /// <summary>
    /// Performs the clear operation.
    /// </summary>
    public IReadOnlyList<T> Clear()
    {
        var values = _values.Values.Reverse().ToArray();
        _values.Clear();
        _keys.Clear();
        return values;
    }

    /// <summary>
    /// Gets enumerator.
    /// </summary>
    public IEnumerator<T> GetEnumerator() => _values.Values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
