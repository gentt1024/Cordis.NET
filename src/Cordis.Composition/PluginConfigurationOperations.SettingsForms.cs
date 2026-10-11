using System.Globalization;
using System.Text.Json;

namespace Cordis.Composition;

public sealed partial class PluginConfigurationOperations
{
    internal async Task<SettingsDescribeSnapshot> ReadSettingsDescriptionAsync(SettingsForms owner)
    {
        SettingsDescribeSnapshot result = null!;
        await ConfigurationTransactionAsync(
            async () =>
            {
                await ReloadAsync(null);
                var inputs = await CaptureProfileAsync();
                var refresh = await ProfileComposition.RefreshAsync(
                    inputs.Launch,
                    ParseManifest(await inputs.ReadAsync(ManifestPath)),
                    inputs.ReadAsync);
                var baseRows = ConfigurationFile.ParseEntries(
                    await inputs.ReadAsync(include.Filename),
                    Path.GetExtension(include.Filename) == ".json");
                await include.Context.RunAsync(_ =>
                {
                    result = DescribeSettings(owner, refresh.Layers, baseRows);
                    return Task.CompletedTask;
                });
                await inputs.VerifyAsync();
            },
            default);
        return result;
    }

    private SettingsDescribeSnapshot DescribeSettings(
        SettingsForms owner,
        IReadOnlyList<ConfigurationLayer> layers,
        IReadOnlyList<EntryOptions> baseRows)
    {
        var namespaces = new List<SettingsNamespaceView>();
        var diagnostics = new List<string>();
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in include.Loader.Entries())
        {
            if (entry.Fiber?.State != FiberState.Active || entry.Fiber.ConfigDescription?.Kind != "object")
                continue;
            try
            {
                _ = EditableEntry(entry.Id);
            }
            catch (Refusal)
            {
                diagnostics.Add(entry.Options.Id + ": namespace is not uniquely addressable");
                continue;
            }

            var policy = owner.Policy(entry);
            var capture = CaptureSettings(entry, policy);
            if (capture.Descriptor.Properties.Count == 0)
                continue;
            if (ReferenceEquals(capture.Value, Undefined.Value))
            {
                diagnostics.Add(entry.Options.Id + ": a secret root has no JSON form value; namespace omitted");
                continue;
            }

            var schema = ConfigurationSchemaExporter.ToSettingsSchemastery(
                capture.Descriptor,
                (node, value) => RedactSettings(node, value, [], []));
            diagnostics.AddRange(
                capture.Diagnostics.Concat(schema.Diagnostics).Select(message => entry.Options.Id + ": " + message));
            var completeSchema = ConfigurationSchemaExporter.ToSchemastery(entry.Fiber.ConfigDescription);
            var fingerprint = entry.Fiber.Uid?.ToString(CultureInfo.InvariantCulture) + "\n" + completeSchema.Document +
                "\n" + RawFingerprint(entry.Options.Config ?? new EntryOptions());
            var revision = owner.Observe(entry, fingerprint);
            active.Add(entry.Id);
            var inherited = SettingsInheritance(entry, layers, baseRows);
            JsonElement resolvedBase;
            try
            {
                var interpolated = Data.Interpolate(inherited, entry.Fiber.Context, include.Loader.ExpressionEvaluator);
                var effective = entry.Fiber.ValidateConfiguration(interpolated);
                var bindings = entry.Fiber.ProjectConfigurationValues(effective);
                resolvedBase = SettingsJsonValue(CaptureSettings(entry, policy, bindings).Value);
            }
            catch
            {
                resolvedBase = RedactedForm(capture.Descriptor, inherited);
            }

            var user = SettingsUser(entry, layers);
            namespaces.Add(
                new(
                    entry.Options.Id,
                    owner.AutoGenerate(entry),
                    SettingsJsonValue(ConfigurationFile.Parse(schema.Document, true)),
                    SettingsJsonValue(capture.Value),
                    revision)
                {
                    Base = resolvedBase,
                    User = RedactedForm(capture.Descriptor, user),
                    Secrets = capture
                        .Secrets.Select(secret => new SettingsSecretView(secret.Path.ToArray(), secret.Set))
                        .ToArray()
                });
        }

        owner.ObserveUnavailable(active);
        return new(new(owner.Writable, owner.HasDocument, namespaces.AsReadOnly()), diagnostics.AsReadOnly());
    }

    private object? SettingsInheritance(
        Entry entry,
        IReadOnlyList<ConfigurationLayer> layers,
        IReadOnlyList<EntryOptions> baseRows)
    {
        var inherited = layers
            .Where(layer => layer.Source != Path.Combine(launch.Home, "cordis.patch.yml") &&
                !launch.Overlays.Contains(layer))
            .Select(layer => layer.Source == PatchPath
                ? layer with
                {
                    Patches = WithoutConfigurationOverride(layer.Patches, entry.Options.Id)
                }
                : layer)
            .ToArray();
        return Flatten(EntryPatches.Apply(baseRows, ProfileComposition.Flatten(inherited)))
            .Single(row => row.Id == entry.Options.Id)
            .Config;
    }

    private object? SettingsUser(Entry entry, IReadOnlyList<ConfigurationLayer> layers)
    {
        var patches = layers.Single(layer => layer.Source == PatchPath).Patches;
        var overridden = patches.LastOrDefault(patch =>
            patch.Id == entry.Options.Id && !Data.Truthy(patch.GetValueOrDefault("insert")) &&
            (patch.Name.Length == 0 || patch.Name == entry.Options.Name) && patch.ContainsKey("config"));
        return overridden?.Config ??
            Flatten(
                    patches
                        .Where(patch => Data.Truthy(patch.GetValueOrDefault("insert")))
                        .SelectMany(patch => Data.Entries(patch["insert"])))
                .SingleOrDefault(row => row.Id == entry.Options.Id)
                ?.Config ?? new EntryOptions();
    }

    private static JsonElement SettingsJsonValue(object? value)
    {
        if (!ConfigurationSchemaExporter.TryJson(value, out var encoded))
            throw new Refusal("non-json-settings-data");
        using var document = JsonDocument.Parse(encoded?.ToJsonString() ?? "null");
        return document.RootElement.Clone();
    }

    private static JsonElement RedactedForm(ConfigDescriptor form, object? raw) =>
        SettingsJsonValue(RedactSettings(form, ProjectForm(form, raw ?? new EntryOptions()), [], []));

    private static object? ProjectForm(ConfigDescriptor form, object? value)
    {
        if (form.Kind != "object" || value is not IReadOnlyDictionary<string, object?> map ||
            map.ContainsKey("__jsExpr"))
            return DetachData(value);
        return new EntryOptions(
            form
                .Properties.Where(pair => map.ContainsKey(pair.Key))
                .Select(pair => KeyValuePair.Create(pair.Key, ProjectForm(pair.Value, map[pair.Key]))));
    }

    private static EntryOptions MergeSettings(object? under, object? over)
    {
        var result = RawObject(under);
        foreach (var (key, value) in RawObject(over))
            result[key] = result.GetValueOrDefault(key) is IReadOnlyDictionary<string, object?> prior &&
                value is IReadOnlyDictionary<string, object?> next
                    ? MergeSettings(prior, next)
                    : DetachData(value);
        return result;
    }

    private static EntryOptions StripForm(
        ConfigDescriptor form,
        object? raw,
        ConfigDescriptor complete,
        bool live = false)
    {
        live |= complete.IsVolatile;
        var result = RawObject(raw);
        foreach (var (key, item) in result.ToArray())
        {
            if (!form.Properties.TryGetValue(key, out var child))
            {
                if (live && !complete.Properties.ContainsKey(key))
                    result.Remove(key);
                continue;
            }

            var original = complete.Properties.GetValueOrDefault(key) ?? child;
            if (child.Kind == "object" && item is IReadOnlyDictionary<string, object?> nested)
            {
                var remainder = StripForm(child, nested, original, live);
                if ((live || original.IsVolatile) && remainder.Count == 0)
                    result.Remove(key);
                else
                    result[key] = remainder;
            }
            else
                result.Remove(key);
        }

        return result;
    }

    private EntryOptions ApplySettingsChange(Entry entry, object? inherited, SettingsChange change)
    {
        var policy = change.Owner.Policy(entry);
        var selected = SelectedSettingsDescriptor(SelectSettingsFields(entry, policy), entry.Fiber!.ConfigDescription!);
        var current = RawObject(ProjectForm(selected, entry.Options.Config ?? new EntryOptions()));
        var basis = RawObject(ProjectForm(selected, inherited ?? new EntryOptions()));

        EntryOptions JsonObject(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new Refusal("settings-object-required");
            var input = RawObject(ConfigurationFile.Parse(value.GetRawText(), true));
            if (!SettingsData(input))
                throw new Refusal("non-json-settings-data");
            ValidateForm(input, selected, []);
            return input;
        }

        var next = change switch
        {
            SettingsUpdate update => MergeSettings(current, JsonObject(update.Patch)),
            SettingsReplace replace => MergeSettings(basis, JsonObject(replace.Section)),
            SettingsMutate mutate => MutateForm(mutate.Operations),
            _ => throw new InvalidOperationException("Unknown Settings operation.")
        };
        var hiddenVisits = new HashSet<(ConfigDescriptor, string, string)>();
        var candidate = MergeSettings(StripForm(selected, entry.Options.Config, entry.Fiber!.ConfigDescription!), next);
        VerifyHidden(entry.Fiber.ConfigDescription!, entry.Options.Config, candidate);
        return candidate;

        void VerifyHidden(ConfigDescriptor node, object? before, object? after)
        {
            if (!HasHidden(node))
                return;
            var beforeRaw = RawFingerprint(before);
            var afterRaw = RawFingerprint(after);
            if (beforeRaw == afterRaw)
                return;
            if (Annotation(node, "hidden"))
                throw new Refusal("field-not-offered");
            var observation = (node, beforeRaw, afterRaw);
            if (!hiddenVisits.Add(observation))
                throw new Refusal("unresolved-settings-policy");
            try
            {
                if (node.Kind == "object")
                {
                    foreach (var (key, child) in node.Properties)
                        VerifyHidden(child, Member(before, key), Member(after, key));
                }
                else if (node.Kind == "dict" && node.Inner is not null)
                {
                    var keys = (before as IReadOnlyDictionary<string, object?>)?.Keys ?? [];
                    var nextKeys = (after as IReadOnlyDictionary<string, object?>)?.Keys ?? [];
                    foreach (var key in keys.Concat(nextKeys).Distinct(StringComparer.Ordinal))
                        VerifyHidden(node.Inner, Member(before, key), Member(after, key));
                }
                else if (node.Kind is "array" or "tuple")
                {
                    var length = Math.Max(
                        (before as IReadOnlyList<object?>)?.Count ?? 0,
                        (after as IReadOnlyList<object?>)?.Count ?? 0);
                    for (var index = 0;index < length;index++)
                    {
                        var key = index.ToString(CultureInfo.InvariantCulture);
                        if (ChildDescriptor(node, key) is { } child)
                            VerifyHidden(child, Member(before, key), Member(after, key));
                    }
                }
                else if (node.Kind is "union" or "intersect")
                {
                    foreach (var child in node.Children)
                        VerifyHidden(child, before, after);
                }
                else if (node.Inner is not null)
                    VerifyHidden(node.Inner, before, after);
            }
            finally
            {
                hiddenVisits.Remove(observation);
            }
        }

        static object? Member(object? value, string key)
        {
            if (value is IReadOnlyDictionary<string, object?> map && map.TryGetValue(key, out var field))
                return field;
            if (value is IReadOnlyList<object?> list && int.TryParse(key, out var index) && index >= 0 &&
                index < list.Count)
                return list[index];
            return Undefined.Value;
        }

        EntryOptions MutateForm(IReadOnlyList<SettingsPathOp> ops)
        {
            var value = current;
            foreach (var op in ops)
            {
                if (op.Path.Any(key => key is null))
                    throw new Refusal("invalid-settings-path");
                if (op.Path.Count > 0 && !AllowsSettingsPath(entry, policy, op.Path))
                    throw new Refusal("field-not-offered");
                ConfigurationPathOperation edit = op.Op switch
                {
                    "set" when op.Value.ValueKind != JsonValueKind.Undefined => new ConfigurationSet(
                        op.Path,
                        ConfigurationFile.Parse(op.Value.GetRawText(), true)),
                    "unset" when op.Value.ValueKind == JsonValueKind.Undefined => new ConfigurationUnset(op.Path),
                    _ => throw new Refusal("invalid-settings-operation")
                };
                if (edit is ConfigurationSet set && !SettingsData(set.Value))
                    throw new Refusal("non-json-settings-data");
                if (edit is ConfigurationSet root && op.Path.Count == 0)
                {
                    if (root.Value is not IReadOnlyDictionary<string, object?>)
                        throw new Refusal("settings-object-required");
                    ValidateForm(RawObject(root.Value), selected, []);
                }

                value = ApplyOperation(
                    value,
                    basis,
                    edit,
                    entry.Fiber!.ConfigDescription!,
                    allowScalarContainers: true);
            }

            return value;
        }

        void ValidateForm(EntryOptions value, ConfigDescriptor node, string[] path)
        {
            foreach (var (key, item) in value)
            {
                var target = path.Append(key).ToArray();
                if (AllowsSettingsPath(entry, policy, target))
                    continue;
                var child = node.Properties.GetValueOrDefault(key);
                if (item is IReadOnlyDictionary<string, object?> nested && child is { Kind: "object" })
                    ValidateForm(RawObject(nested), child, target);
                else
                    throw new Refusal("field-not-offered");
            }
        }
    }

    internal async Task WriteSettingsAsync(SettingsChange change)
    {
        var result = await MutateConfigurationCoreAsync(change.Namespace, [], null, true, default, change);
        if (!result.Saved || !result.Applied)
            throw new InvalidOperationException(
                result.Diagnostic ?? result.Error ?? "Settings reconciliation did not complete.");
    }
}
