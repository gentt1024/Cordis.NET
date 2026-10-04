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
                var entry = EditableEntry(entryId, layers);
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
        => EditFieldCoreAsync(entryId, path, value, expectedRevision, liveOnly, cancellationToken);

    private async Task<ConfigurationEditResult> EditFieldCoreAsync(string entryId, IReadOnlyList<string> path,
        object? value, string expectedRevision, bool liveOnly, CancellationToken cancellationToken, SettingsPolicy? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0 || path.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("A fixed object-key path is required.", nameof(path));
        var keys = path.ToArray();
        // Validate the data boundary before cloning; no caller-owned mutable input enters the transaction.
        _ = RawFingerprint(value);
        var incoming = DetachData(value);
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
                await include.Context.RunAsync(context =>
                {
                    entry = EditableEntry(entryId, refresh.Layers);
                    revision = Revision(entry, refresh.Layers);
                    if (revision != expectedRevision) throw new Refusal("conflict");
                    if (settings is not null && (!settings.Allows(keys[0]) || !TrySettingsValue(entry, keys[0], out _, out _)))
                        throw new Refusal("field-not-offered");
                    next = RawObject(entry.Options.Config);
                    SetField(next, keys, incoming);
                    // A successful SET must persist the same data, including null, expressions and non-finite numbers.
                    // Undefined has runtime meaning but cannot round-trip through this source format.
                    if (RawFingerprint(next) != RawFingerprint(ConfigurationFile.Parse(ConfigurationFile.WriteFlow(next))))
                        throw new Refusal("non-persistable-configuration");
                    if (liveOnly)
                    {
                        var fiber = entry.Fiber!;
                        var bound = Enumerable.Range(0, keys.Length + 1).Any(count =>
                            fiber.ConfigurationValues.ContainsKey(DisplayPath(keys.Take(count).ToArray())));
                        if (!bound || !fiber.TryPrepareConfigurationUpdate(next, out _)) throw new Refusal("not-live-update");
                    }
                    else entry.Fiber!.ValidateConfiguration(next);
                    return Task.CompletedTask;
                });

                var before = await ReadPatchSourceAsync();
                var inheritedLayers = refresh.Layers.Where(layer => layer.Source != Path.Combine(launch.Home, "cordis.patch.yml")
                    && !launch.Overlays.Contains(layer)).Select(layer => layer.Source == PatchPath
                        ? layer with { Patches = WithoutConfigurationOverride(layer.Patches, entry.Options.Id) } : layer).ToArray();
                // Inheritance starts from source data, never the already-patched live tree.
                var baseRows = ConfigurationFile.ParseEntries(await File.ReadAllTextAsync(include.Filename), Path.GetExtension(include.Filename) == ".json");
                var inheritedRows = EntryPatches.Apply(baseRows, ProfileComposition.Flatten(inheritedLayers));
                var inherited = Flatten(inheritedRows).Single(row => row.Id == entry.Options.Id).Config;
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

    private void NotifyConfigurationChanged()
    {
        if (Changed is not { } changed) return;
        foreach (Action<string> observer in changed.GetInvocationList())
        {
            try { observer("configuration"); }
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

    private Entry EditableEntry(string id, IReadOnlyList<ConfigurationLayer> layers)
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

    private static void SetField(EntryOptions root, string[] path, object? value)
    {
        IDictionary<string, object?> current = root;
        for (var index = 0; index < path.Length - 1; index++)
        {
            if (current.ContainsKey("__jsExpr")) throw new Refusal("opaque-configuration");
            if (!current.TryGetValue(path[index], out var child)) current[path[index]] = child = new EntryOptions();
            if (child is not IDictionary<string, object?> map || map.ContainsKey("__jsExpr")) throw new Refusal("opaque-configuration");
            current = map;
        }
        if (current.ContainsKey("__jsExpr")) throw new Refusal("opaque-configuration");
        current[path[^1]] = value;
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
            await include.Context.RunAsync(_ => { revision = Revision(EditableEntry(entryId, layers), layers); return Task.CompletedTask; });
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
