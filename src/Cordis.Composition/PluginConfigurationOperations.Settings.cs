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

public sealed partial class PluginConfigurationOperations
{
    private sealed record SettingsSecret(IReadOnlyList<string> Path, bool Set);

    private static SettingsCapture CaptureSettings(
        Entry entry,
        SettingsPolicy policy,
        IReadOnlyDictionary<string, object?>? bindings = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var secrets = new List<SettingsSecret>();
        var diagnostics = new List<string>();
        var selected = SelectSettingsFields(entry, policy, diagnostics, bindings);
        foreach (var field in selected)
        {
            var redaction = new SettingsRedactionState();
            var redacted = RedactSettings(field.Descriptor, field.Value, [field.Name], secrets, state: redaction);
            if (redaction.Incomplete)
                diagnostics.Add(field.Name + ": unresolved or recursive redaction paths were omitted");
            if (!ReferenceEquals(redacted, Undefined.Value))
                values.Add(field.Name, redacted);
        }

        var descriptor = SelectedSettingsDescriptor(selected, entry.Fiber!.ConfigDescription!);
        object? value = values;
        if (Secret(descriptor))
        {
            secrets.Clear();
            value = RedactSettings(descriptor, values, [], secrets);
        }

        return new(
            descriptor,
            value,
            secrets.AsReadOnly(),
            diagnostics.AsReadOnly());
    }

    private static IReadOnlyList<SelectedSettingsField> SelectSettingsFields(
        Entry entry,
        SettingsPolicy policy,
        List<string>? diagnostics = null,
        IReadOnlyDictionary<string, object?>? bindings = null)
    {
        var selected = new List<SelectedSettingsField>();
        if (Annotation(entry.Fiber!.ConfigDescription!, "hidden"))
            return selected.AsReadOnly();
        foreach (var name in policy.Fields)
        {
            if (!policy.Allows(name))
            {
                diagnostics?.Add(name + ": hidden");
                continue;
            }

            if (!TrySettingsValue(entry, name, out var kind, out var value, bindings))
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

    private static ConfigDescriptor SelectedSettingsDescriptor(
        IReadOnlyList<SelectedSettingsField> selected,
        ConfigDescriptor complete)
    {
        var result = ConfigDescriptor.Object(selected.Select(field => (field.Name, field.Descriptor)).ToArray());
        return complete.IsVolatile ? PreserveObjectMetadata(result, complete) : result.Default(new EntryOptions());
    }

    private static ConfigDescriptor PreserveObjectMetadata(ConfigDescriptor result, ConfigDescriptor original)
    {
        result = result.WithAnnotations(original.Annotations);
        if (original.IsOptional)
            result = result.Optional();
        if (original.HasDefault)
            result = result.Default(original.DefaultValue);
        if (original.IsVolatile)
            result = result.Volatile();
        return result;
    }

    private sealed record SelectedSettingsField(string Name, string Kind, object? Value, ConfigDescriptor Descriptor);

    private sealed record SettingsCapture(
        ConfigDescriptor Descriptor,
        object? Value,
        IReadOnlyList<SettingsSecret> Secrets,
        IReadOnlyList<string> Diagnostics);

    private static bool TrySettingsValue(
        Entry entry,
        string name,
        out string kind,
        out object? value,
        IReadOnlyDictionary<string, object?>? bindings = null)
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
                if ((bindings ?? fiber.ConfigurationValues).TryGetValue(
                        DisplayPath(path.Take(count).ToArray()),
                        out var bound) &&
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
                var result = ConfigDescriptor.Object(members);
                if (!live)
                    return result.Default(new EntryOptions());
                return PreserveObjectMetadata(result, node);
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
        var visiting = new HashSet<(ConfigDescriptor, int)>();

        bool Allows(ConfigDescriptor? node, int depth, bool live)
        {
            if (node is null)
                return live;
            if (Annotation(node, "hidden"))
                return false;
            live |= node.IsVolatile;
            if (depth == path.Count)
                return live && !HasHidden(node);
            if (!visiting.Add((node, depth)))
                return live;
            try
            {
                if (node.Kind is "union" or "intersect")
                    return live && node.Children.All(child => Allows(child, depth, true));
                if (node.Kind is "lazy" or "transform" or "getter")
                    return Allows(node.Inner, depth, live);
                return Allows(ChildDescriptor(node, path[depth]), depth + 1, live);
            }
            finally
            {
                visiting.Remove((node, depth));
            }
        }

        return Allows(entry.Fiber!.ConfigDescription, 0, false);
    }

    private static bool HasHidden(ConfigDescriptor node)
    {
        var seen = new HashSet<ConfigDescriptor>(ReferenceEqualityComparer.Instance);

        bool Visit(ConfigDescriptor item) => seen.Add(item) && (Annotation(item, "hidden") ||
            item.Properties.Values.Any(Visit) || item.Inner is not null && Visit(item.Inner) ||
            item.Children.Any(Visit));

        return Visit(node);
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
