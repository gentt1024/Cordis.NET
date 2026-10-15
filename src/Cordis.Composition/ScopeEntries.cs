using System.Diagnostics.CodeAnalysis;

namespace Cordis.Composition;

/// <summary>Insertion-ordered borrowed values with caller-owned duplicate diagnostics.</summary>
/// <remarks>Views retain one table generation. Draining it detaches old views from later registrations.</remarks>
public sealed class NamedEntries<T>(Func<string, Exception> duplicateError)
{
    private readonly ScopeEntryTable<string, T> entries = new(StringComparer.Ordinal);

    private readonly Func<string, Exception> duplicate =
        duplicateError ?? throw new ArgumentNullException(nameof(duplicateError));

    /// <summary>Whether the table contains no registrations.</summary>
    public bool IsEmpty => entries.IsEmpty;

    /// <summary>Live names in insertion order within the current generation.</summary>
    public IEnumerable<string> Keys => entries.Entries().Select(entry => entry.Key);

    /// <summary>Live borrowed values in insertion order within the current generation.</summary>
    public IEnumerable<T> Values => entries.Entries().Select(entry => entry.Value);

    /// <summary>Live named entries in insertion order within the current generation.</summary>
    public IEnumerable<KeyValuePair<string, T>> Entries => entries.Entries();

    /// <summary>Insert a unique name and return an idempotent undo for this exact registration.</summary>
    public Action Insert(string name, T value) => entries.Insert(name, value, duplicate);

    /// <summary>Read a borrowed value, distinguishing absence from a stored null.</summary>
    public bool TryGetValue(string name, [MaybeNullWhen(false)] out T value) => entries.TryGetValue(name, out value);

    /// <summary>Whether the name has a registration.</summary>
    public bool ContainsKey(string name) => entries.ContainsKey(name);
}

/// <summary>Insertion-ordered borrowed values with independent registration identity.</summary>
public sealed class AnonymousEntries<T>
{
    private readonly ScopeEntryTable<object, T> entries = new(ReferenceEqualityComparer.Instance);

    /// <summary>Whether the table contains no registrations.</summary>
    public bool IsEmpty => entries.IsEmpty;

    /// <summary>Live borrowed values; equal values remain distinct registrations.</summary>
    public IEnumerable<T> Values => entries.Entries().Select(entry => entry.Value);

    /// <summary>Append one value and return an idempotent undo for this exact registration.</summary>
    public Action Append(T value) => entries.Insert(new object(), value, _ => new InvalidOperationException());
}

internal sealed class ScopeEntryTable<TKey, TValue>(IEqualityComparer<TKey> comparer) where TKey : notnull
{
    private sealed class Node
    {
        internal TKey Key = default!;
        internal TValue Value = default!;
        internal bool Active;
        internal Node? Previous;
        internal Node? Next;
    }

    private sealed class Generation(IEqualityComparer<TKey> comparer)
    {
        internal readonly Dictionary<TKey, Node> Index = new(comparer);
        internal Node Head = new();
        internal Node? Tail;
    }

    private readonly object gate = new();
    private Generation current = new(comparer);

    internal bool IsEmpty
    {
        get
        {
            lock (gate)
                return current.Index.Count == 0;
        }
    }

    internal bool ContainsKey(TKey key)
    {
        lock (gate)
            return current.Index.ContainsKey(key);
    }

    internal bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (gate)
        {
            if (current.Index.TryGetValue(key, out var node))
            {
                value = node.Value;
                return true;
            }

            value = default;
            return false;
        }
    }

    internal Action Insert(TKey key, TValue value, Func<TKey, Exception> duplicate)
    {
        lock (gate)
        {
            var generation = current;
            if (generation.Index.ContainsKey(key))
                throw duplicate(key);
            var node = generation.Tail ?? generation.Head;
            node.Key = key;
            node.Value = value;
            node.Active = true;
            node.Next = new()
            {
                Previous = node
            };
            generation.Tail = node.Next;
            generation.Index.Add(key, node);
            return () =>
            {
                lock (gate)
                {
                    if (!node.Active)
                        return;
                    generation.Index.Remove(key);
                    if (node.Previous is { } previous)
                        previous.Next = node.Next;
                    else
                        generation.Head = node.Next!;
                    node.Next!.Previous = node.Previous;
                    node.Previous = null;
                    node.Active = false;
                    node.Key = default!;
                    node.Value = default!;
                    if (generation.Index.Count == 0 && ReferenceEquals(current, generation))
                        current = new(comparer);
                }
            };
        }
    }

    internal IEnumerable<KeyValuePair<TKey, TValue>> Entries()
    {
        lock (gate)
            return Iterate(current);
    }

    private IEnumerable<KeyValuePair<TKey, TValue>> Iterate(Generation generation)
    {
        Node? cursor;
        lock (gate)
            cursor = generation.Head;
        while (true)
        {
            KeyValuePair<TKey, TValue> item;
            lock (gate)
            {
                while (cursor is { Active: false, Next: not null })
                    cursor = cursor.Next;
                if (cursor is null || !cursor.Active)
                    yield break;
                item = new(cursor.Key, cursor.Value);
                cursor = cursor.Next;
            }

            yield return item;
        }
    }
}
