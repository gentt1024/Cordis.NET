namespace Cordis.Composition;

/// <summary>Host-owned selection and redaction for the primitive, top-level live settings subset.</summary>
public sealed class SettingsPolicy
{
    /// <summary>Copy host policies; caller mutations cannot change an admitted request.</summary>
    public SettingsPolicy(IEnumerable<string> fields, IEnumerable<string>? hiddenFields = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = Array.AsReadOnly(fields.Distinct(StringComparer.Ordinal).ToArray());
        HiddenFields = Array.AsReadOnly((hiddenFields ?? []).Distinct(StringComparer.Ordinal).ToArray());
        foreach (var field in Fields.Concat(HiddenFields)) ArgumentException.ThrowIfNullOrWhiteSpace(field);
    }
    /// <summary>The explicitly offered top-level fields.</summary>
    public IReadOnlyList<string> Fields { get; }
    /// <summary>Fields whose values must never be returned or accepted by this settings endpoint.</summary>
    public IReadOnlyList<string> HiddenFields { get; }
    internal bool Allows(string field) => Fields.Contains(field, StringComparer.Ordinal) && !HiddenFields.Contains(field, StringComparer.Ordinal);
}

/// <summary>A selected published primitive value; no validator, default or ordinary raw value is exported.</summary>
/// <param name="Name">The fixed object key.</param>
/// <param name="Kind">The primitive description kind.</param>
/// <param name="Value">The latest committed live snapshot.</param>
/// <param name="Overridden">Whether the user source explicitly contains the key, independent of value equality.</param>
public sealed record SettingsField(string Name, string Kind, object? Value, bool Overridden);

/// <summary>A coherent live-only settings view taken inside the existing profile transaction.</summary>
/// <param name="EntryId">The live entry.</param>
/// <param name="Revision">The source and activation fence.</param>
/// <param name="Fields">Only host-selected, non-hidden primitive live fields.</param>
/// <param name="Diagnostics">Reasons other selected fields cannot be offered.</param>
public sealed record SettingsView(string EntryId, string Revision, IReadOnlyList<SettingsField> Fields, IReadOnlyList<string> Diagnostics);

public sealed partial class PluginConfigurationOperations
{
    /// <summary>Read selected primitive live values, redaction and override presence at one management checkpoint.</summary>
    public async Task<SettingsView> ReadSettingsAsync(string entryId, SettingsPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        SettingsView result = null!;
        await ConfigurationTransactionAsync(async () =>
        {
            await ReloadAsync(null);
            var layers = (await ProfileComposition.RefreshAsync(launch)).Layers;
            await include.Context.RunAsync(_ =>
            {
                var entry = EditableEntry(entryId, layers);
                var patches = layers.Single(layer => layer.Source == PatchPath).Patches;
                var overridden = patches.LastOrDefault(patch => patch.Id == entry.Options.Id && !Data.Truthy(patch.GetValueOrDefault("insert"))
                    && (patch.Name.Length == 0 || patch.Name == entry.Options.Name) && patch.ContainsKey("config"));
                // Inserted entries can originate in the user source too. This source-presence
                // view is independent of B's inheritance calculation and never uses effective values.
                var inserted = overridden is null ? Flatten(patches.Where(patch => Data.Truthy(patch.GetValueOrDefault("insert")))
                    .SelectMany(patch => Data.Entries(patch["insert"]))).SingleOrDefault(row => row.Id == entry.Options.Id) : null;
                var user = (overridden is null ? inserted?.Config : overridden.Config) as IReadOnlyDictionary<string, object?>;
                var fields = new List<SettingsField>();
                var diagnostics = new List<string>();
                foreach (var name in policy.Fields)
                {
                    if (!policy.Allows(name)) { diagnostics.Add(name + ": hidden"); continue; }
                    if (!TrySettingsValue(entry, name, out var kind, out var value))
                    { diagnostics.Add(name + ": not a supported primitive live field"); continue; }
                    fields.Add(new(name, kind, value, user?.ContainsKey(name) == true));
                }
                result = new(entryId, Revision(entry, layers), fields.AsReadOnly(), diagnostics.AsReadOnly());
                return Task.CompletedTask;
            });
        }, cancellationToken);
        return result;
    }

    /// <summary>Submit one offered live field through the same revision-fenced configuration operation.</summary>
    /// <remarks>Host authorization and secret policy are mandatory transport responsibilities. Reset and multi-field atomic edits are not this subset's contract.</remarks>
    public Task<ConfigurationEditResult> EditSettingsFieldAsync(string entryId, string field, object? value, string expectedRevision,
        SettingsPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Allows(field)) return Task.FromResult(new ConfigurationEditResult(entryId, false, false, null, "field-not-offered"));
        return EditFieldCoreAsync(entryId, [field], value, expectedRevision, true, cancellationToken, policy);
    }

    private static bool TrySettingsValue(Entry entry, string name, out string kind, out object? value)
    {
        kind = "";
        value = null;
        var fiber = entry.Fiber!;
        var description = fiber.ConfigDescription;
        if (description?.Kind != "object" || !description.Properties.TryGetValue(name, out var field)
            || field.Kind is not ("number" or "string" or "boolean")) return false;
        if (!fiber.ConfigurationValues.TryGetValue(name, out value)
            && !(fiber.ConfigurationValues.TryGetValue("", out var root) && root is IReadOnlyDictionary<string, object?> map
                && map.TryGetValue(name, out value))) return false;
        // A description is not a client-side validator. Refuse unsupported actual values explicitly.
        var supported = value is null || field.Kind switch
        {
            "string" => value is string,
            "boolean" => value is bool,
            "number" => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
                && double.IsFinite(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
            _ => false,
        };
        if (supported) kind = field.Kind;
        return supported;
    }
}
