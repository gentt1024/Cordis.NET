using Cordis;
namespace Cordis.Composition;

/// <summary>
/// Represents the js expression component.
/// </summary>
/// <param name="Source">The source value.</param>
public sealed record JsExpression(string Source);
/// <summary>
/// Evaluates expressions embedded in Cordis configuration.
/// </summary>
public interface IExpressionEvaluator
{
    /// <summary>
    /// Evaluates an expression against the supplied Cordis context.
    /// </summary>
    object? Evaluate(string expression, Context context);
}
/// <summary>Marks a plugin whose raw config contains other entries' configurations.</summary>
public interface ITreeCarrierPlugin : IPlugin { }
/// <summary>
/// Represents the tree carrier plugin component.
/// </summary>
/// <param name="plugin">The plugin value.</param>
public sealed class TreeCarrierPlugin(IPlugin plugin) : ITreeCarrierPlugin
{
    /// <summary>
    /// Gets the identity value.
    /// </summary>
    public object Identity => plugin.Identity;
    /// <summary>
    /// Gets the name value.
    /// </summary>
    public string? Name => plugin.Name;
    /// <summary>
    /// Gets the dependencies value.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Dependencies => plugin.Dependencies;
    /// <summary>
    /// Resolves config.
    /// </summary>
    public object? ResolveConfig(object? configuration) => plugin.ResolveConfig(configuration);
    /// <summary>
    /// Applies async.
    /// </summary>
    public Task ApplyAsync(Context context, object? configuration) => plugin.ApplyAsync(context, configuration);
}
/// <summary>
/// Represents the i module resolver component.
/// </summary>
public interface IModuleResolver
{
    /// <summary>
    /// Resolves async.
    /// </summary>
    ValueTask<IPlugin> ResolveAsync(string specifier, Uri baseUri, CancellationToken cancellationToken = default);
}
/// <summary>
/// Represents the static module resolver component.
/// </summary>
public sealed class StaticModuleResolver : IModuleResolver
{
    private readonly Dictionary<string, IPlugin> modules = new(StringComparer.Ordinal);
    /// <summary>
    /// Performs the register operation.
    /// </summary>
    public StaticModuleResolver Register(string specifier, IPlugin plugin) { modules[specifier] = plugin; return this; }
    /// <summary>
    /// Resolves async.
    /// </summary>
    public ValueTask<IPlugin> ResolveAsync(string specifier, Uri baseUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (modules.TryGetValue(specifier, out var plugin) || modules.TryGetValue(new Uri(baseUri, specifier).AbsoluteUri, out plugin)) return ValueTask.FromResult(plugin);
        throw new FileNotFoundException($"Cannot resolve plugin module '{specifier}' from {baseUri}.");
    }
}

/// <summary>Raw entry fields. Missing fields remain distinct from explicit null.</summary>
public class EntryOptions : Dictionary<string, object?>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EntryOptions"/> type.
    /// </summary>
    public EntryOptions() : base(StringComparer.Ordinal) { }
    /// <summary>
    /// Initializes a new instance of the <see cref="EntryOptions"/> type.
    /// </summary>
    public EntryOptions(IEnumerable<KeyValuePair<string, object?>> values) : base(values, StringComparer.Ordinal) { }
    /// <summary>
    /// Gets the id value.
    /// </summary>
    public string Id { get => GetValueOrDefault("id") as string ?? ""; set => this["id"] = value; }
    /// <summary>
    /// Gets the name value.
    /// </summary>
    public string Name { get => GetValueOrDefault("name") as string ?? ""; set => this["name"] = value; }
    /// <summary>
    /// Gets the config value.
    /// </summary>
    public object? Config { get => GetValueOrDefault("config"); set => this["config"] = value; }
    internal object? RawConfig => TryGetValue("config", out var value) ? value : Undefined.Value;
    /// <summary>
    /// Gets the disabled value.
    /// </summary>
    public object? Disabled { get => GetValueOrDefault("disabled"); set => this["disabled"] = value; }
    /// <summary>
    /// Gets the group value.
    /// </summary>
    public bool Group { get => Data.Truthy(GetValueOrDefault("group")); set => this["group"] = value; }
    private object? GetValueOrDefault(string key) => TryGetValue(key, out var value) ? value : null;
}

/// <summary>
/// Represents the data component.
/// </summary>
public static class Data
{
    /// <summary>
    /// Performs the deep equals operation.
    /// </summary>
    public static bool DeepEquals(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is IDictionary<string, object?> a && right is IDictionary<string, object?> b)
            return a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var value) && DeepEquals(pair.Value, value));
        if (left is IEnumerable<object?> x && right is IEnumerable<object?> y)
        {
            using var first = x.GetEnumerator(); using var second = y.GetEnumerator();
            while (first.MoveNext()) if (!second.MoveNext() || !DeepEquals(first.Current, second.Current)) return false;
            return !second.MoveNext();
        }
        return Equals(left, right);
    }
    /// <summary>
    /// Performs the truthy operation.
    /// </summary>
    public static bool Truthy(object? value) => value switch { null or Undefined => false, bool b => b, string s => s.Length > 0, int i => i != 0, long l => l != 0, double d => d != 0 && !double.IsNaN(d), _ => true };
    /// <summary>
    /// Performs the clone operation.
    /// </summary>
    public static object? Clone(object? value) => Clone(value, new Dictionary<object, object>(ReferenceEqualityComparer.Instance));
    private static object? Clone(object? value, Dictionary<object, object> seen)
    {
        if (value is null) return null;
        if (seen.TryGetValue(value, out var existing)) return existing;
        if (value is IDictionary<string, object?> map)
        {
            var result = new EntryOptions(); seen[value] = result;
            foreach (var pair in map) result[pair.Key] = Clone(pair.Value, seen);
            return result;
        }
        if (value is IEnumerable<object?> list)
        {
            var result = new List<object?>(); seen[value] = result;
            foreach (var item in list) result.Add(Clone(item, seen));
            return result;
        }
        return value;
    }
    /// <summary>
    /// Performs the entries operation.
    /// </summary>
    public static List<EntryOptions> Entries(object? value) => value is List<EntryOptions> entries ? entries : value is IEnumerable<object?> list
        ? list.Select(item => item as EntryOptions ?? (item is IDictionary<string, object?> map ? new EntryOptions(map) : throw new FormatException("Entry must be a mapping."))).ToList()
        : throw new FormatException("Config file must be a top-level array of entries.");
    /// <summary>
    /// Performs the interpolate operation.
    /// </summary>
    public static object? Interpolate(object? value, Context context, IExpressionEvaluator? evaluator) => value switch
    {
        JsExpression expression => (evaluator ?? throw new NotSupportedException("A JavaScript evaluator is required for !!js expressions.")).Evaluate(expression.Source, context),
        IDictionary<string, object?> map when map.TryGetValue("__jsExpr", out var source) => (evaluator ?? throw new NotSupportedException("A JavaScript evaluator is required for !!js expressions.")).Evaluate((string)source!, context),
        IDictionary<string, object?> map => new EntryOptions(map.Select(kv => KeyValuePair.Create(kv.Key, Interpolate(kv.Value, context, evaluator)))),
        IEnumerable<object?> list => list.Select(item => Interpolate(item, context, evaluator)).ToList(),
        _ => value
    };
}

/// <summary>
/// Represents the entry patches component.
/// </summary>
public static class EntryPatches
{
    private static string Quote(string text) => ConfigurationFile.Write(text, true).Trim();
    /// <summary>Applies the pinned Include algorithm, including inserted-node aliasing.</summary>
    public static List<EntryOptions> Apply(IReadOnlyList<EntryOptions> data, IReadOnlyList<EntryOptions>? patches, Action<string>? warn = null)
    {
        if (patches is null || patches.Count == 0) return [.. data];
        var result = Data.Entries(Data.Clone(data));
        var index = new Dictionary<string, EntryOptions>(StringComparer.Ordinal);
        void Index(IEnumerable<EntryOptions> rows)
        {
            foreach (var row in rows) { if (row.Id.Length > 0) index[row.Id] = row; if (row.Group && row.Config is IEnumerable<object?>) Index(Data.Entries(row.Config)); }
        }
        Index(result);
        foreach (var patch in patches)
        {
            if (patch.TryGetValue("insert", out var insertion) && Data.Truthy(insertion))
            {
                var inserted = Data.Entries(insertion);
                if (patch.Id.Length == 0) result.AddRange(inserted);
                else
                {
                    if (!index.TryGetValue(patch.Id, out var target)) { warn?.Invoke($"patch insert: entry {Quote(patch.Id)} not found"); continue; }
                    if (!target.Group) { warn?.Invoke($"patch insert: entry {Quote(patch.Id)} is not a group"); continue; }
                    var children = target.Config is IEnumerable<object?> ? Data.Entries(target.Config) : [];
                    children.AddRange(inserted); target.Config = children;
                }
                Index(inserted); continue;
            }
            if (patch.Id.Length == 0) { warn?.Invoke("patch: id is required for non-insert patches"); continue; }
            if (!index.TryGetValue(patch.Id, out var existing)) { warn?.Invoke($"patch: entry {Quote(patch.Id)} not found"); continue; }
            if (patch.Name.Length > 0 && patch.Name != existing.Name) { warn?.Invoke($"patch: name mismatch for {Quote(patch.Id)} (expected {Quote(existing.Name)}, got {Quote(patch.Name)}), skipping"); continue; }
            foreach (var pair in patch) if (pair.Key is not ("id" or "name" or "insert")) existing[pair.Key] = pair.Value;
        }
        return result;
    }
}
