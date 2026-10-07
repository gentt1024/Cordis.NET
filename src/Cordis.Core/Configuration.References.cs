using System.Collections.ObjectModel;

namespace Cordis;

/// <summary>A readonly reference whose identity survives compatible configuration commits.</summary>
/// <typeparam name="T">The projected immutable value type.</typeparam>
public sealed class ConfigReference<T> : IConfigReference
{
    private readonly Func<T> _read;
    internal ConfigReference(Func<T> read) => _read = read;

    /// <summary>The value in the Fiber's latest atomically committed configuration state.</summary>
    public T Value => _read();
}

internal interface IConfigReference
{
}

internal abstract class ConfigBinding
{
    protected ConfigBinding(IReadOnlyList<string> path)
    {
        Keys = System.Array.AsReadOnly(path.ToArray());
        Path = DisplayPath(path);
    }

    internal IReadOnlyList<string> Keys
    {
        get;
    }

    internal string Path
    {
        get;
    }

    internal static string DisplayPath(IReadOnlyList<string> keys) =>
        keys.Count switch
        {
            0 => "",
            1 => keys[0],
            _ => "/" + string.Join(
                "/",
                keys.Select(key =>
                    key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)))
        };

    internal abstract Type ValueType
    {
        get;
    }

    internal abstract bool TryProject(object? config, out object? value);
    internal abstract object CreateReference(ConfigurationCell cell);
}

internal sealed class ConfigBinding<T>(IReadOnlyList<string> path, Func<object?, T> project) : ConfigBinding(path)
{
    internal override Type ValueType => typeof(T);

    internal override bool TryProject(object? config, out object? value)
    {
        if (!ConfigSnapshots.TryCreate(project(config), out value))
            throw new ConfigurationValidationException(
                [$"Volatile configuration '{Path}' must be an acyclic plain value."]);
        return value is T || value is null && default(T) is null;
    }

    internal override object CreateReference(ConfigurationCell cell)
    {
        var path = Path;
        return new ConfigReference<T>(() => (T)cell.State.Values[path]!);
    }
}

internal sealed record CapturedConfigSchema(
    ConfigDescriptor Descriptor,
    IReadOnlyList<ConfigBinding> Bindings,
    Func<object?, object?, bool>? OrdinaryEquality,
    Func<object?, object?>? Simplify,
    Func<object?, object?>? DescriptionData)
{
    internal bool TryProject(object? config, out IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var binding in Bindings)
        {
            if (!binding.TryProject(config, out var value))
            {
                values = result;
                return false;
            }

            result.Add(binding.Path, value);
        }

        values = new ReadOnlyDictionary<string, object?>(result);
        return true;
    }
}

// References own only detached field snapshots. The Fiber owns the effective config
// and plugin delegates, so retaining a scalar reference cannot keep their ALC alive.
internal sealed record ConfigurationState(IReadOnlyDictionary<string, object?> Values);

internal sealed class ConfigurationCell(ConfigurationState initial)
{
    private ConfigurationState _state = initial;

    internal ConfigurationState State
    {
        get => Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, value);
    }
}

/// <summary>A validated, detached candidate bound to one active Fiber and one committed state.</summary>
public sealed class ConfigurationUpdate
{
    private readonly Fiber _fiber;
    private readonly ConfigurationState _previous;
    private readonly ConfigurationState _next;
    private readonly ConfigurationCell _cell;
    private int _used;

    internal ConfigurationUpdate(
        Fiber fiber,
        ConfigurationCell cell,
        ConfigurationState previous,
        IReadOnlyDictionary<string, object?> projectedValues,
        object? effective,
        object? raw)
    {
        (_fiber, _cell, _previous, RawConfig) = (fiber, cell, previous, raw);
        Config = effective;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var changed = new List<string>();
        foreach (var (path, value) in projectedValues)
        {
            if (ConfigSnapshots.Equal(previous.Values[path], value))
                values.Add(path, previous.Values[path]);
            else
            {
                values.Add(path, value);
                changed.Add(path);
            }
        }

        _next = new(new ReadOnlyDictionary<string, object?>(values));
        ChangedPaths = System.Array.AsReadOnly(changed.ToArray());
    }

    /// <summary>The raw input supplied to the single validation attempt.</summary>
    public object? RawConfig
    {
        get;
    }

    /// <summary>The candidate's validated effective value.</summary>
    public object? Config
    {
        get;
    }

    /// <summary>Volatile field paths whose detached values differ.</summary>
    public IReadOnlyList<string> ChangedPaths
    {
        get;
    }

    /// <summary>Atomically publish references, retaining effective identity, without update hooks, saving, or reapplying.</summary>
    /// <returns>False when stale or no longer active. A candidate can be consumed only once.</returns>
    public bool Commit()
    {
        _fiber.Context.VerifyAccess();
        if (Interlocked.Exchange(ref _used, 1) != 0)
            throw new InvalidOperationException("This configuration candidate has already been consumed.");
        return _fiber.CommitConfiguration(_cell, _previous, _next);
    }
}
