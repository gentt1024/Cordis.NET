using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Cordis.Composition;

/// <summary>A detached raw view and a content/activation revision for optimistic editing.</summary>
/// <param name="EntryId">The live entry identity.</param>
/// <param name="Revision">The revision of participating sources and the live activation.</param>
/// <param name="Raw">The detached complete raw configuration.</param>
public sealed record ConfigurationSnapshot(string EntryId, string Revision, IReadOnlyDictionary<string, object?> Raw);

/// <summary>Persistence and live application are separate outcomes; recovery is cooperative.</summary>
/// <param name="EntryId">The requested entry.</param>
/// <param name="Saved">Whether the requested value remains durably saved.</param>
/// <param name="Applied">Whether reconciliation of the requested value completed.</param>
/// <param name="Revision">The resulting content/activation revision, if the entry remains available.</param>
/// <param name="Error">A stable refusal or failure code.</param>
/// <param name="Diagnostic">The primary failure description.</param>
/// <param name="RecoveryErrors">Source/runtime recovery failures, without replacing the primary failure.</param>
public sealed record ConfigurationEditResult(string EntryId, bool Saved, bool Applied, string? Revision,
    string? Error = null, string? Diagnostic = null, IReadOnlyList<string>? RecoveryErrors = null);

/// <summary>One ordered raw configuration edit; an empty path addresses the complete object.</summary>
/// <param name="Path">Object keys or canonical non-negative array indexes.</param>
public abstract record ConfigurationPathOperation(IReadOnlyList<string> Path);
/// <summary>Set a value, including an explicit null. Inputs are copied before waiting for management ownership.</summary>
/// <param name="Path">The value's path.</param>
/// <param name="Value">Detached, persistable source data.</param>
public sealed record ConfigurationSet(IReadOnlyList<string> Path, object? Value) : ConfigurationPathOperation(Path);
/// <summary>Restore an object field's inherited raw value, or remove an array element.</summary>
/// <param name="Path">The value's path; an empty path restores the complete inherited object.</param>
public sealed record ConfigurationUnset(IReadOnlyList<string> Path) : ConfigurationPathOperation(Path);

public sealed partial class PluginConfigurationOperations
{
    /// <summary>Refresh and read a detached data-object configuration inside the existing management queue.</summary>
    public async Task<ConfigurationSnapshot> ReadConfigurationAsync(string entryId, CancellationToken cancellationToken = default)
    {
        ConfigurationSnapshot result = null!;
        await ConfigurationTransactionAsync(async () =>
        {
            await ReloadAsync(null);
            var layers = (await ProfileComposition.RefreshAsync(launch)).Layers;
            await include.Context.RunAsync(_ =>
            {
                var entry = EditableEntry(entryId);
                result = new(entryId, Revision(entry, layers), RawObject(entry.Options.Config));
                return Task.CompletedTask;
            });
        }, cancellationToken);
        return result;
    }

    /// <summary>Edit one fixed object-key path in its profile user layer, after normal validation and overlay checks.</summary>
    /// <remarks>Requires a fresh revision. Live-only requests also require an existing live binding and compatible effective ordinary values.</remarks>
    public Task<ConfigurationEditResult> EditConfigurationFieldAsync(string entryId, IReadOnlyList<string> path,
        object? value, string expectedRevision, bool liveOnly = false, CancellationToken cancellationToken = default)
        => MutateConfigurationAsync(entryId, [new ConfigurationSet(path, value)], expectedRevision, liveOnly, cancellationToken);

    /// <summary>Apply ordered edits to one detached candidate, validate the final candidate, then save and reconcile once.</summary>
    /// <remarks>Intermediate candidates need not be valid. This is one source commit with cooperative recovery, not a transaction over the entire runtime tree.</remarks>
    public Task<ConfigurationEditResult> MutateConfigurationAsync(string entryId, IReadOnlyList<ConfigurationPathOperation> operations,
        string expectedRevision, bool liveOnly = false, CancellationToken cancellationToken = default)
        => MutateConfigurationCoreAsync(entryId, operations, expectedRevision, liveOnly, cancellationToken);

    private async Task<ConfigurationEditResult> MutateConfigurationCoreAsync(string entryId, IReadOnlyList<ConfigurationPathOperation> operations,
        string expectedRevision, bool liveOnly, CancellationToken cancellationToken, SettingsPolicy? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0) throw new ArgumentException("At least one edit is required.", nameof(operations));
        var edits = operations.Select(operation =>
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(operation.Path);
            if (operation.Path.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Path keys cannot be blank.", nameof(operations));
            var path = operation.Path.ToArray();
            if (operation is ConfigurationSet set)
            {
                _ = RawFingerprint(set.Value);
                return (ConfigurationPathOperation)new ConfigurationSet(path, DetachData(set.Value));
            }
            if (operation is ConfigurationUnset) return new ConfigurationUnset(path);
            throw new ArgumentException("Unknown configuration operation.", nameof(operations));
        }).ToArray();
        ConfigurationEditResult result = null!;
        await ConfigurationTransactionAsync(async () =>
        {
            string? revision = null;
            try
            {
                await ReloadAsync(null);
                var refresh = await ProfileComposition.RefreshAsync(launch);
                Entry entry = null!;
                EntryOptions next = null!;
                var before = await ReadPatchSourceAsync();
                var baseRows = ConfigurationFile.ParseEntries(await File.ReadAllTextAsync(include.Filename), Path.GetExtension(include.Filename) == ".json");
                object? inherited = null;
                await include.Context.RunAsync(context =>
                {
                    entry = EditableEntry(entryId);
                    revision = Revision(entry, refresh.Layers);
                    if (revision != expectedRevision) throw new Refusal("conflict");
                    var inheritedLayers = refresh.Layers.Where(layer => layer.Source != Path.Combine(launch.Home, "cordis.patch.yml")
                        && !launch.Overlays.Contains(layer)).Select(layer => layer.Source == PatchPath
                            ? layer with { Patches = WithoutConfigurationOverride(layer.Patches, entry.Options.Id) } : layer).ToArray();
                    inherited = Flatten(EntryPatches.Apply(baseRows, ProfileComposition.Flatten(inheritedLayers)))
                        .Single(row => row.Id == entry.Options.Id).Config;
                    next = RawObject(entry.Options.Config);
                    foreach (var edit in edits)
                    {
                        var keys = edit.Path;
                        if (settings is not null && !AllowsSettingsPath(entry, settings, keys))
                            throw new Refusal("field-not-offered");
                        if (settings is not null && edit is ConfigurationSet set && !SettingsData(set.Value))
                            throw new Refusal("non-json-settings-data");
                        next = ApplyOperation(next, inherited, edit, entry.Fiber!.ConfigDescription);
                    }
                    // A successful SET must persist the same data, including null, expressions and non-finite numbers.
                    // Undefined has runtime meaning but cannot round-trip through this source format.
                    if (RawFingerprint(next) != RawFingerprint(ConfigurationFile.Parse(ConfigurationFile.WriteFlow(next))))
                        throw new Refusal("non-persistable-configuration");
                    if (liveOnly)
                    {
                        var fiber = entry.Fiber!;
                        var bound = edits.All(edit => Enumerable.Range(0, edit.Path.Count + 1).Any(count =>
                            fiber.ConfigurationValues.ContainsKey(DisplayPath(edit.Path.Take(count).ToArray()))));
                        if (!bound || !fiber.TryPrepareConfigurationUpdate(next, out _)) throw new Refusal("not-live-update");
                    }
                    else entry.Fiber!.ValidateConfiguration(next);
                    return Task.CompletedTask;
                });

                var edited = ProfileMaintenance.WithConfiguration(before, entry.Options.Id, entry.Options.Name, next,
                    RawFingerprint(next) == RawFingerprint(inherited));
                var layers = refresh.Layers.Select(layer => layer.Source == PatchPath
                    ? layer with { Patches = ConfigurationFile.ParseEntries(edited) } : layer).ToArray();
                var effective = Flatten(EntryPatches.Apply(baseRows, ProfileComposition.Flatten(layers)))
                    .Single(row => row.Id == entry.Options.Id).Config;
                if (RawFingerprint(next) != RawFingerprint(effective)) throw new Refusal("overridden");
                cancellationToken.ThrowIfCancellationRequested();
                await ProfileMaintenance.WriteConfigurationSourceAsync(PatchPath, edited);
                try
                {
                    await ReloadAsync(new HashSet<string>([entry.Options.Id], StringComparer.Ordinal));
                }
                catch (Exception primary)
                {
                    var recovery = new List<string>();
                    var saved = true;
                    try
                    {
                        await ProfileMaintenance.WriteConfigurationSourceAsync(PatchPath, before);
                        saved = false;
                    }
                    catch (Exception error) { recovery.Add("source restore: " + error.Message); }
                    try { await ReloadAsync(null); }
                    catch (Exception error) { recovery.Add("runtime recovery: " + error.Message); }
                    result = new(entryId, saved, false, await CurrentRevisionAsync(entryId), "reconcile-failed", primary.Message, recovery.AsReadOnly());
                    return;
                }
                result = new(entryId, true, RunExclusiveAsync is not null, await CurrentRevisionAsync(entryId));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                result = new(entryId, false, false, revision, error is Refusal refusal ? refusal.Code :
                    error is ConfigurationValidationException ? "invalid-configuration" :
                    error is DeploymentRestartRequiredException ? "restart-required" : "operation-error", error.Message);
            }
            finally { NotifyConfigurationChanged(); }
        }, cancellationToken);
        return result;
    }

    private void NotifyConfigurationChanged(string reason = "configuration")
    {
        if (Changed is not { } changed) return;
        foreach (Action<string> observer in changed.GetInvocationList())
        {
            try { observer(reason); }
            catch (Exception error)
            {
                // A secondary observer cannot replace a durable result or a primary recovery failure.
                try { include.Context.Logger.Warn("Configuration observer failed: " + error.Message); }
                catch { /* Diagnostic sinks cannot change the already completed operation either. */ }
            }
        }
    }

    private async Task ConfigurationTransactionAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            if (RunExclusiveAsync is { } exclusive) await exclusive(action);
            else await action();
        }
        finally { mutation.Release(); }
    }

    private Entry EditableEntry(string id)
    {
        var entry = include.Loader.Entries().SingleOrDefault(row => row.Id == id) ?? throw new Refusal("unknown-plugin");
        var candidates = Flatten(include.Root.Data).Where(row => row.Id == entry.Options.Id).ToArray();
        if (entry.Parent.Tree != include || candidates.Length != 1
            || candidates[0].Name != entry.Options.Name) throw new Refusal("unaddressable");
        if (entry.Fiber?.State != FiberState.Active) throw new Refusal("inactive-plugin");
        return entry;
    }

    private static EntryOptions RawObject(object? raw)
    {
        if (raw is null || raw is Undefined) return new();
        if (raw is not IDictionary<string, object?> map || map.ContainsKey("__jsExpr")) throw new Refusal("opaque-configuration");
        _ = RawFingerprint(map);
        return (EntryOptions)DetachData(map)!;
    }

    private static object? DetachData(object? value) => value switch
    {
        JsExpression expression => expression,
        IReadOnlyDictionary<string, object?> map => new EntryOptions(map.Select(pair => KeyValuePair.Create(pair.Key, DetachData(pair.Value)))),
        IEnumerable<object?> list => list.Select(DetachData).ToList(),
        _ => value,
    };

    private static EntryOptions ApplyOperation(EntryOptions root, object? inherited, ConfigurationPathOperation operation, ConfigDescriptor? descriptor)
    {
        var path = operation.Path;
        if (path.Count == 0) return RawObject(operation is ConfigurationSet setRoot ? setRoot.Value : inherited);
        object? Edit(object? input, ConfigDescriptor? node, int depth)
        {
            var key = path[depth];
            var last = depth == path.Count - 1;
            if (input is Undefined && node?.HasDefault == true) input = DetachData(node.DefaultValue);
            if (input is IList<object?> list)
            {
                if (!int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index.ToString(CultureInfo.InvariantCulture) != key || index > list.Count
                    || index == list.Count && (!last || operation is ConfigurationUnset)) throw new Refusal("invalid-array-index");
                if (last && operation is ConfigurationUnset) list.RemoveAt(index);
                else
                {
                    var value = last ? ((ConfigurationSet)operation).Value : Edit(list[index], ChildDescriptor(node, key), depth + 1);
                    if (index == list.Count) list.Add(value); else list[index] = value;
                }
                return list;
            }
            if (input is null or Undefined) input = new EntryOptions();
            if (input is not IDictionary<string, object?> map || map.ContainsKey("__jsExpr")) throw new Refusal("opaque-configuration");
            if (!last)
                map[key] = Edit(map.TryGetValue(key, out var child) ? child : Undefined.Value, ChildDescriptor(node, key), depth + 1);
            else if (operation is ConfigurationSet set) map[key] = set.Value;
            else if (TryPath(inherited, path, out var fallback)) map[key] = DetachData(fallback);
            else map.Remove(key);
            return map;
        }
        return (EntryOptions)Edit(root, descriptor, 0)!;
    }

    private static ConfigDescriptor? ChildDescriptor(ConfigDescriptor? node, string key)
    {
        var seen = new HashSet<ConfigDescriptor>(ReferenceEqualityComparer.Instance);
        while (node?.Kind is "lazy" or "transform" && seen.Add(node)) node = node.Inner;
        if (node?.Kind == "object") return node.Properties.GetValueOrDefault(key);
        if (node?.Kind is "array" or "dict") return node.Inner;
        if (node?.Kind == "tuple" && int.TryParse(key, out var index) && index >= 0 && index < node.Children.Count) return node.Children[index];
        return null;
    }

    private static bool TryPath(object? value, IReadOnlyList<string> path, out object? result)
    {
        result = value;
        foreach (var key in path)
        {
            if (result is IReadOnlyDictionary<string, object?> map && !map.ContainsKey("__jsExpr") && map.TryGetValue(key, out result)) continue;
            if (result is IReadOnlyList<object?> list && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < list.Count) { result = list[index]; continue; }
            result = null;
            return false;
        }
        return true;
    }

    internal static string DisplayPath(IReadOnlyList<string> keys) => keys.Count switch
    {
        0 => "", 1 => keys[0],
        _ => "/" + string.Join("/", keys.Select(key => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal))),
    };

    // Preserve real inserts and unrelated patches as inheritance. Falsey insert values
    // are ordinary overrides, just as in EntryPatches.Apply.
    private static List<EntryOptions> WithoutConfigurationOverride(IEnumerable<EntryOptions> patches, string id)
        => patches.Select(patch =>
        {
            var copy = (EntryOptions)Data.Clone(patch)!;
            if (copy.Id == id && !Data.Truthy(copy.GetValueOrDefault("insert"))) copy.Remove("config");
            return copy;
        }).ToList();

    private string Revision(Entry entry, IReadOnlyList<ConfigurationLayer> layers) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        RawFingerprint(entry.Options) + "\n" + entry.Fiber!.Uid?.ToString(CultureInfo.InvariantCulture) + "\n"
        + string.Join("\n", layers.Select(layer => layer.Source + ":" + RawFingerprint(layer.Patches))))));

    private async Task<string?> CurrentRevisionAsync(string entryId)
    {
        try
        {
            var layers = (await ProfileComposition.RefreshAsync(launch)).Layers;
            string? revision = null;
            await include.Context.RunAsync(_ => { revision = Revision(EditableEntry(entryId), layers); return Task.CompletedTask; });
            return revision;
        }
        catch { return null; }
    }

    private async Task<string> ReadPatchSourceAsync()
    {
        try { return await File.ReadAllTextAsync(PatchPath); }
        catch (FileNotFoundException) { return "[]\n"; }
    }

    // Revision encoding preserves Undefined, null, expression maps and non-finite numbers.
    // It is not the persistence or browser wire format; no validator or expression is run.
    private static string RawFingerprint(object? value)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        var ancestors = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Write(object? item)
        {
            writer.WriteStartArray();
            switch (item)
            {
                case null: writer.WriteStringValue("null"); break;
                case Undefined: writer.WriteStringValue("undefined"); break;
                case string text: writer.WriteStringValue("string"); writer.WriteStringValue(text); break;
                case bool boolean: writer.WriteStringValue("boolean"); writer.WriteBooleanValue(boolean); break;
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    writer.WriteStringValue("number"); writer.WriteStringValue(Convert.ToDouble(item, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)); break;
                default:
                    if (!ancestors.Add(item)) throw new Refusal("cyclic-configuration");
                    try
                    {
                        if (item is IReadOnlyDictionary<string, object?> map)
                        {
                            writer.WriteStringValue("object");
                            foreach (var pair in map.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { writer.WriteStringValue(pair.Key); Write(pair.Value); }
                        }
                        else if (item is IEnumerable<object?> list)
                        {
                            writer.WriteStringValue("array");
                            foreach (var child in list) Write(child);
                        }
                        else throw new Refusal("unsupported-configuration-data");
                    }
                    finally { ancestors.Remove(item); }
                    break;
            }
            writer.WriteEndArray();
        }
        Write(value);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
