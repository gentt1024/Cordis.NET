namespace Cordis.Composition;

/// <summary>Host-owned selection and redaction for live settings fields and edits beneath them.</summary>
public sealed class SettingsPolicy
{
    /// <summary>Copy host policies; caller mutations cannot change an admitted request.</summary>
    public SettingsPolicy(IEnumerable<string> fields, IEnumerable<string>? hiddenFields = null)
    {
        ArgumentNullException.ThrowIfNull(fields);
        Fields = Array.AsReadOnly(fields.Distinct(StringComparer.Ordinal).ToArray());
        HiddenFields = Array.AsReadOnly((hiddenFields ?? []).Distinct(StringComparer.Ordinal).ToArray());
        foreach (var field in Fields.Concat(HiddenFields))
            ArgumentException.ThrowIfNullOrWhiteSpace(field);
    }

    /// <summary>The explicitly offered top-level fields.</summary>
    public IReadOnlyList<string> Fields
    {
        get;
    }

    /// <summary>Fields whose values must never be returned or accepted by this settings endpoint.</summary>
    public IReadOnlyList<string> HiddenFields
    {
        get;
    }

    internal bool Allows(string field) =>
        Fields.Contains(field, StringComparer.Ordinal) && !HiddenFields.Contains(field, StringComparer.Ordinal);
}

/// <summary>One concrete write-only secret position in the selected form.</summary>
/// <param name="Path">Object keys and concrete dictionary/array positions.</param>
/// <param name="Set">Whether the published value contains this secret.</param>
public sealed record SettingsSecret(IReadOnlyList<string> Path, bool Set);

/// <summary>A detached selected published data value; no validator, default or ordinary raw value is exported.</summary>
/// <param name="Name">The fixed object key.</param>
/// <param name="Kind">The declared data kind.</param>
/// <param name="Value">The latest committed live snapshot.</param>
/// <param name="Overridden">Whether the user source explicitly contains the key, independent of value equality.</param>
public sealed record SettingsField(string Name, string Kind, object? Value, bool Overridden);

/// <summary>A coherent live-only settings view taken inside the existing profile transaction.</summary>
/// <param name="EntryId">The live entry.</param>
/// <param name="Revision">The source and activation fence.</param>
/// <param name="Fields">Only host-selected, non-hidden data live fields.</param>
/// <param name="Diagnostics">Reasons other selected fields cannot be offered.</param>
public sealed record SettingsView(
    string EntryId,
    string Revision,
    IReadOnlyList<SettingsField> Fields,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>Secret presence without values, including nested concrete positions.</summary>
    public IReadOnlyList<SettingsSecret> Secrets
    {
        get;
        init;
    } = [];
}

public sealed partial class PluginConfigurationOperations
{
    /// <summary>Read selected plain live values, redaction and override presence at one management checkpoint.</summary>
    public async Task<SettingsView> ReadSettingsAsync(
        string entryId,
        SettingsPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        SettingsView result = null!;
        await ConfigurationTransactionAsync(
            async () =>
            {
                await ReloadAsync(null);
                var layers = (await ProfileComposition.RefreshAsync(launch)).Layers;
                await include.Context.RunAsync(_ =>
                {
                    var entry = EditableEntry(entryId);
                    var patches = layers.Single(layer => layer.Source == PatchPath).Patches;
                    var overridden = patches.LastOrDefault(patch =>
                        patch.Id == entry.Options.Id && !Data.Truthy(patch.GetValueOrDefault("insert")) &&
                        (patch.Name.Length == 0 || patch.Name == entry.Options.Name) && patch.ContainsKey("config"));
                    // Inserted entries can originate in the user source too. This source-presence
                    // view is independent of B's inheritance calculation and never uses effective values.
                    var inserted = overridden is null
                        ? Flatten(
                                patches
                                    .Where(patch => Data.Truthy(patch.GetValueOrDefault("insert")))
                                    .SelectMany(patch => Data.Entries(patch["insert"])))
                            .SingleOrDefault(row => row.Id == entry.Options.Id)
                        : null;
                    var user =
                        (overridden is null ? inserted?.Config : overridden.Config) as
                        IReadOnlyDictionary<string, object?>;
                    var captured = CaptureSettings(entry, policy, user);
                    result = new(entryId, Revision(entry, layers), captured.Fields, captured.Diagnostics)
                    {
                        Secrets = captured.Secrets
                    };
                    return Task.CompletedTask;
                });
            },
            cancellationToken);
        return result;
    }

    /// <summary>Submit one offered live field through the same revision-fenced configuration operation.</summary>
    /// <remarks>Host authorization and secret policy are mandatory transport responsibilities.</remarks>
    public Task<ConfigurationEditResult> EditSettingsFieldAsync(
        string entryId,
        string field,
        object? value,
        string expectedRevision,
        SettingsPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Allows(field))
            return Task.FromResult(new ConfigurationEditResult(entryId, false, false, null, "field-not-offered"));
        return MutateSettingsAsync(
            entryId,
            [new ConfigurationSet([field], value)],
            expectedRevision,
            policy,
            cancellationToken);
    }

    /// <summary>Apply ordered edits below offered live fields in one revision-fenced configuration commit.</summary>
    /// <remarks>Every operation must name an offered top-level field. Root edits cannot bypass selection or redaction.</remarks>
    public Task<ConfigurationEditResult> MutateSettingsAsync(
        string entryId,
        IReadOnlyList<ConfigurationPathOperation> operations,
        string expectedRevision,
        SettingsPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return MutateConfigurationCoreAsync(entryId, operations, expectedRevision, true, cancellationToken, policy);
    }

    private static SettingsCapture CaptureSettings(
        Entry entry,
        SettingsPolicy policy,
        IReadOnlyDictionary<string, object?>? user = null)
    {
        var fields = new List<SettingsField>();
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var secrets = new List<SettingsSecret>();
        var diagnostics = new List<string>();
        var selected = SelectSettingsFields(entry, policy, diagnostics);
        foreach (var field in selected)
        {
            var redaction = new SettingsRedactionState();
            var redacted = RedactSettings(field.Descriptor, field.Value, [field.Name], secrets, state: redaction);
            if (redaction.Incomplete)
                diagnostics.Add(field.Name + ": unresolved or recursive redaction paths were omitted");
            if (!ReferenceEquals(redacted, Undefined.Value))
                values.Add(field.Name, redacted);
            fields.Add(
                new(
                    field.Name,
                    field.Kind,
                    ReferenceEquals(redacted, Undefined.Value) ? null : redacted,
                    user?.ContainsKey(field.Name) == true));
        }

        return new(
            SelectedSettingsDescriptor(selected),
            fields.AsReadOnly(),
            values,
            secrets.AsReadOnly(),
            diagnostics.AsReadOnly());
    }

    private static IReadOnlyList<SelectedSettingsField> SelectSettingsFields(
        Entry entry,
        SettingsPolicy policy,
        List<string>? diagnostics = null)
    {
        var selected = new List<SelectedSettingsField>();
        foreach (var name in policy.Fields)
        {
            if (!policy.Allows(name))
            {
                diagnostics?.Add(name + ": hidden");
                continue;
            }

            if (!TrySettingsValue(entry, name, out var kind, out var value))
            {
                diagnostics?.Add(name + ": not a supported data live field");
                continue;
            }

            var description = entry.Fiber!.ConfigDescription!;
            selected.Add(
                new(name, kind, value, SettingsDescriptor(description.Properties[name], description.IsVolatile)!));
        }

        return selected.AsReadOnly();
    }

    private static ConfigDescriptor SelectedSettingsDescriptor(IReadOnlyList<SelectedSettingsField> selected) =>
        ConfigDescriptor.Object(selected.Select(field => (field.Name, field.Descriptor)).ToArray());

    private sealed record SelectedSettingsField(string Name, string Kind, object? Value, ConfigDescriptor Descriptor);

    private sealed record SettingsCapture(
        ConfigDescriptor Descriptor,
        IReadOnlyList<SettingsField> Fields,
        IReadOnlyDictionary<string, object?> Value,
        IReadOnlyList<SettingsSecret> Secrets,
        IReadOnlyList<string> Diagnostics);

    private static bool TrySettingsValue(Entry entry, string name, out string kind, out object? value)
    {
        kind = "";
        value = null;
        var fiber = entry.Fiber!;
        var description = fiber.ConfigDescription;
        if (description?.Kind != "object" || !description.Properties.TryGetValue(name, out var original))
            return false;
        var field = SettingsDescriptor(original, description.IsVolatile);
        if (field is null)
            return false;

        bool Read(ConfigDescriptor node, string[] path, out object? output)
        {
            for (var count = path.Length;count >= 0;count--)
                if (fiber.ConfigurationValues.TryGetValue(DisplayPath(path.Take(count).ToArray()), out var bound) &&
                    TryPath(bound, path.Skip(count).ToArray(), out output))
                    return true;
            if (node.Kind == "object")
            {
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (key, child) in node.Properties)
                    if (Read(child, [.. path, key], out var item))
                        map[key] = item;
                output = map;
                return map.Count > 0;
            }

            output = null;
            return false;
        }

        if (!Read(field, [name], out value))
            return false;
        // A description is not a client-side validator. Refuse unsupported actual values explicitly.
        var supported = value is null || field.Kind switch
        {
            "string" => value is string,
            "boolean" => value is bool,
            "number" => value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double
                or decimal && double.IsFinite(
                Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)),
            "object" or "dict" => value is IReadOnlyDictionary<string, object?> && SettingsData(value),
            "array" or "tuple" => value is IReadOnlyList<object?> && SettingsData(value),
            "union" or "intersect" or "any" => SettingsData(value),
            _ => false,
        };
        if (supported)
        {
            kind = field.Kind;
            value = DetachData(value);
        }

        return supported;
    }

    private static bool Annotation(ConfigDescriptor node, string key) =>
        node.Annotations.GetValueOrDefault(key) is true;

    private static bool Secret(ConfigDescriptor node) => node.Annotations.GetValueOrDefault("role") is "secret";

    // Only fixed object paths may introduce a live boundary. Beneath it dynamic values are plain data.
    private static ConfigDescriptor? SettingsDescriptor(
        ConfigDescriptor node,
        bool live = false,
        HashSet<ConfigDescriptor>? visiting = null)
    {
        if (Annotation(node, "hidden"))
            return null;
        live |= node.IsVolatile;
        if (node.Kind == "object")
        {
            visiting ??= new(ReferenceEqualityComparer.Instance);
            if (!visiting.Add(node))
                return live ? node : null;
            try
            {
                var members = node
                    .Properties.Select(pair => (pair.Key, Descriptor: SettingsDescriptor(pair.Value, live, visiting)))
                    .Where(pair => pair.Descriptor is not null)
                    .Select(pair => (pair.Key, pair.Descriptor!))
                    .ToArray();
                if (members.Length == 0 && !live)
                    return null;
                var result = ConfigDescriptor.Object(members).WithAnnotations(node.Annotations);
                if (node.IsOptional)
                    result = result.Optional();
                if (node.HasDefault)
                    result = result.Default(node.DefaultValue);
                if (node.IsVolatile)
                    result = result.Volatile();
                return result;
            }
            finally
            {
                visiting.Remove(node);
            }
        }

        return live ? node : null;
    }

    private sealed class SettingsRedactionState
    {
        internal HashSet<(ConfigDescriptor Node, object? Value)> Active
        {
            get;
        } = [];

        internal HashSet<(ConfigDescriptor Node, string Path)> ActiveLocations
        {
            get;
        } = [];

        internal bool Incomplete
        {
            get;
            set;
        }
    }

    private static object? RedactSettings(
        ConfigDescriptor node,
        object? value,
        string[] path,
        List<SettingsSecret> secrets,
        bool preserveUnknown = false,
        SettingsRedactionState? state = null)
    {
        state ??= new();
        if (Secret(node) || Annotation(node, "hidden"))
            return RedactSettingsCore(node, value, path, secrets, preserveUnknown, state);
        // Missing or null containers have no value descendants. Do not invent recursive children from their declaration.
        if ((value is null || ReferenceEquals(value, Undefined.Value)) &&
            node.Kind is not ("lazy" or "transform" or "getter" or "union" or "intersect"))
            return value;
        var location = (node, DisplayPath(path));
        if (state.Active.Contains((node, value)) || state.ActiveLocations.Contains(location))
        {
            state.Incomplete = true;
            return Undefined.Value;
        }

        state.Active.Add((node, value));
        state.ActiveLocations.Add(location);
        try
        {
            return RedactSettingsCore(node, value, path, secrets, preserveUnknown, state);
        }
        finally
        {
            state.Active.Remove((node, value));
            state.ActiveLocations.Remove(location);
        }
    }

    private static object? RedactSettingsCore(
        ConfigDescriptor node,
        object? value,
        string[] path,
        List<SettingsSecret> secrets,
        bool preserveUnknown,
        SettingsRedactionState state)
    {
        if (Secret(node))
        {
            var prior = secrets.FindIndex(secret => secret.Path.SequenceEqual(path, StringComparer.Ordinal));
            var present = !ReferenceEquals(value, Undefined.Value);
            if (prior < 0)
                secrets.Add(new(Array.AsReadOnly(path), present));
            else if (present)
                secrets[prior] = secrets[prior] with
                {
                    Set = true
                };
            return Undefined.Value;
        }

        if (Annotation(node, "hidden"))
            return Undefined.Value;
        if (node.Kind == "object")
        {
            var source = value as IReadOnlyDictionary<string, object?>;
            if (source is null)
            {
                state.Incomplete = true;
                return Undefined.Value;
            }

            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (preserveUnknown)
                foreach (var (key, item) in source)
                    if (!node.Properties.ContainsKey(key))
                        map[key] = item;
            foreach (var (key, child) in node.Properties)
            {
                var item = RedactSettings(
                    child,
                    source.GetValueOrDefault(key, Undefined.Value),
                    [.. path, key],
                    secrets,
                    preserveUnknown,
                    state);
                if (!ReferenceEquals(item, Undefined.Value))
                    map[key] = item;
            }

            return map;
        }

        if (node.Kind == "dict" && value is IReadOnlyDictionary<string, object?> dictionary && node.Inner is not null)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (key, child) in dictionary)
            {
                var item = RedactSettings(node.Inner, child, [.. path, key], secrets, state: state);
                if (!ReferenceEquals(item, Undefined.Value))
                    map[key] = item;
            }

            return map;
        }

        if (node.Kind is "array" or "tuple" && value is IReadOnlyList<object?> list)
            return list
                .Select((child, index) =>
                {
                    var descriptor = node.Kind == "array" ? node.Inner : node.Children.ElementAtOrDefault(index);
                    var item = descriptor is null
                        ? child
                        : RedactSettings(
                            descriptor,
                            child,
                            [.. path, index.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                            secrets,
                            state: state);
                    return ReferenceEquals(item, Undefined.Value) ? null : item;
                })
                .ToArray();
        if (node.Kind is "union" or "intersect")
        {
            // Gather presence from the original value before branches remove fields conservatively.
            foreach (var child in node.Children)
                _ = RedactSettings(child, value, path, secrets, true, state);
            foreach (var child in node.Children)
                value = RedactSettings(child, value, path, secrets, true, state);
            return value;
        }

        if (node.Kind is "transform" or "getter" or "lazy")
        {
            if (node.Inner is not null)
                return RedactSettings(node.Inner, value, path, secrets, state: state);
            if (value is null || ReferenceEquals(value, Undefined.Value))
                return value;
            state.Incomplete = true;
            return Undefined.Value;
        }

        if (node.Kind is "dict" or "array" or "tuple")
        {
            state.Incomplete = true;
            return Undefined.Value;
        }

        return value;
    }

    private static bool AllowsSettingsPath(Entry entry, SettingsPolicy policy, IReadOnlyList<string> path)
    {
        if (path.Count == 0 || !policy.Allows(path[0]))
            return false;
        var node = entry.Fiber!.ConfigDescription;
        var live = false;
        foreach (var key in path)
        {
            if (node is null || Annotation(node, "hidden") || Secret(node))
                return false;
            live |= node.IsVolatile;
            node = ChildDescriptor(node, key);
        }

        if (node is null || Annotation(node, "hidden"))
            return false;
        live |= node.IsVolatile;
        if (!live)
            return false;

        bool ContainsRestricted(ConfigDescriptor candidate, HashSet<ConfigDescriptor> seen) =>
            seen.Add(candidate) && (Annotation(candidate, "hidden") || Secret(candidate) ||
                candidate.Properties.Values.Any(child => ContainsRestricted(child, seen)) ||
                candidate.Inner is not null && ContainsRestricted(candidate.Inner, seen) ||
                candidate.Children.Any(child => ContainsRestricted(child, seen)));

        // Write-only leaves are editable. Replacing their ancestor would implicitly erase unreadable values.
        return Secret(node) || !ContainsRestricted(node, new(ReferenceEqualityComparer.Instance));
    }

    private static bool SettingsData(object? value)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        bool Visit(object? item)
        {
            if (item is null or string or bool)
                return true;
            if (item is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
                return double.IsFinite(Convert.ToDouble(item, System.Globalization.CultureInfo.InvariantCulture));
            if (item is Undefined || !seen.Add(item))
                return false;
            try
            {
                return item switch
                {
                    IReadOnlyDictionary<string, object?> map => !map.ContainsKey("__jsExpr") && map.Values.All(Visit),
                    IReadOnlyList<object?> list => list.All(Visit),
                    _ => false,
                };
            }
            finally
            {
                seen.Remove(item);
            }
        }

        return Visit(value);
    }
}
