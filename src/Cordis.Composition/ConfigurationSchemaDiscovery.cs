namespace Cordis.Composition;

/// <summary>A declaration discovered from a literal entry without activating it.</summary>
/// <param name="EntryId">Stable source position when the entry has no explicit id.</param>
/// <param name="Name">Module specifier.</param>
/// <param name="Source">Configuration file containing the entry.</param>
/// <param name="Disabled">Declared disabled state; disabled entries still expose declarations.</param>
/// <param name="Schemastery">Independent form declaration, or null when unavailable.</param>
/// <param name="JsonSchema">Independent JSON Schema declaration, or null when unavailable.</param>
/// <param name="Diagnostic">Import, capture, or static carrier limitation.</param>
public sealed record DiscoveredConfigurationSchema(
    string EntryId,
    string Name,
    string Source,
    bool Disabled,
    ConfigurationSchemaExport? Schemastery,
    ConfigurationSchemaExport? JsonSchema,
    string? Diagnostic);

/// <summary>Read-only declaration inventory, including source failures without discarding surviving siblings.</summary>
/// <param name="Entries">Declarations in source traversal order.</param>
/// <param name="Diagnostics">Source and carrier discovery failures.</param>
public sealed record ConfigurationSchemaCatalog(
    IReadOnlyList<DiscoveredConfigurationSchema> Entries,
    IReadOnlyList<string> Diagnostics);

/// <summary>Discover explicitly captured plugin declarations without a Context, Loader, Apply or ResolveConfig invocation.</summary>
/// <remarks>Module resolution and optional capture are trusted author code and may execute during import.
/// Raw expressions and unresolved lazy builders remain inert. Native group/include traversal reads sources but never creates them.
/// Custom tree carriers need their own static discovery adapter; their configuration is not guessed.</remarks>
public static class ConfigurationSchemaDiscovery
{
    /// <summary>Read one entry-list file and ordered patches, then discover native carriers and plugin declarations.</summary>
    public static async Task<ConfigurationSchemaCatalog> DiscoverAsync(
        string filename,
        IModuleResolver resolver,
        IReadOnlyList<ConfigurationLayer>? layers = null,
        CancellationToken cancellationToken = default)
    {
        var collector = new Collector(resolver, null, cancellationToken);
        await collector.ReadAsync(filename, layers);
        return new(collector.Entries.AsReadOnly(), collector.Diagnostics.AsReadOnly());
    }

    /// <summary>Describe a composed entry-list and its root patch-list using JSON Schema draft 2020-12.</summary>
    /// <remarks>Layers are applied in the supplied order. Use the same prepared module resolver as the Host.
    /// A root read or composition failure throws. Declaration and include failures preserve the document with
    /// positioned diagnostics under x-cordis and Complete set to false.</remarks>
    public static Task<ConfigurationSchemaExport> DiscoverDocumentAsync(
        string profile,
        string filename,
        IModuleResolver resolver,
        IReadOnlyList<ConfigurationLayer>? layers = null,
        CancellationToken cancellationToken = default) =>
        DocumentAsync(profile, filename, resolver, layers, [], cancellationToken);

    /// <summary>Refresh a launch's bundle, profile, home and selected overlays before discovering its document.</summary>
    /// <remarks>Skipped bundle selections appear as errors in the partial document. Profile preparation errors
    /// propagate to the caller. Discovery does not mount plugins or invoke the launch's apply callback.</remarks>
    public static async Task<ConfigurationSchemaExport> DiscoverDocumentAsync(
        ProfileLaunch launch,
        string filename,
        IModuleResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launch);
        cancellationToken.ThrowIfCancellationRequested();
        var refresh = await ProfileComposition.RefreshAsync(launch);
        return await DiscoverDocumentAsync(refresh, launch.Profile.Name, filename, resolver, cancellationToken);
    }

    /// <summary>Describe the layers and skipped selections from a previously prepared profile refresh.</summary>
    /// <remarks>The caller can use this same refresh to select module artifacts. The root file and included
    /// entry files are read during discovery. Profile selection and patch files are not refreshed.</remarks>
    public static Task<ConfigurationSchemaExport> DiscoverDocumentAsync(
        ProfileRefresh refresh,
        string profile,
        string filename,
        IModuleResolver resolver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        cancellationToken.ThrowIfCancellationRequested();
        return DocumentAsync(profile, filename, resolver, refresh.Layers, refresh.SkippedBundles, cancellationToken);
    }

    private static async Task<ConfigurationSchemaExport> DocumentAsync(
        string profile,
        string filename,
        IModuleResolver resolver,
        IReadOnlyList<ConfigurationLayer>? layers,
        IReadOnlyList<SkippedBundle> skipped,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        var document = new ConfigurationDocumentBuilder();
        foreach (var bundle in skipped)
            document.Report("error", null, $"Selected profile bundle '{bundle.Name}' could not be loaded.");
        await new Collector(resolver, document, cancellationToken).ReadAsync(filename, layers);
        return document.Build(profile);
    }

    private sealed class Collector(
        IModuleResolver resolver,
        ConfigurationDocumentBuilder? document,
        CancellationToken cancellationToken)
    {
        public List<DiscoveredConfigurationSchema> Entries
        {
            get;
        } = [];

        public List<string> Diagnostics
        {
            get;
        } = [];

        private readonly Dictionary<string, string> targets = new(StringComparer.Ordinal);

        private readonly HashSet<string> files = new(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        private readonly HashSet<object> groups = new(ReferenceEqualityComparer.Instance);

        public async Task ReadAsync(string filename, IReadOnlyList<ConfigurationLayer>? layers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filename);
            ArgumentNullException.ThrowIfNull(resolver);
            await ReadFileAsync(
                Path.GetFullPath(filename),
                null,
                layers is null ? null : ProfileComposition.Flatten(layers),
                "",
                null);
        }

        private async Task ReadFileAsync(
            string file,
            object? initial,
            List<EntryOptions>? patches,
            string ids,
            string? ownerPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var canonical = DeploymentPackageResolver.Canonical(file);
                if (Path.GetExtension(file) is not (".yml" or ".yaml" or ".json"))
                    throw new NotSupportedException("Unsupported configuration extension.");
                if (!files.Add(canonical))
                    throw new FormatException("include cycle");
                try
                {
                    object? raw;
                    try
                    {
                        var content = await File.ReadAllTextAsync(file, cancellationToken);
                        try
                        {
                            raw = ConfigurationFile.Parse(content, Path.GetExtension(file) == ".json");
                        }
                        catch (Exception error) when (ownerPath is not null && error is not OperationCanceledException)
                        {
                            // Parser messages may quote configuration values.
                            throw new FormatException("Invalid configuration file: " + file, error);
                        }
                    }
                    catch (Exception error) when (initial is not null &&
                                                  error is FileNotFoundException or DirectoryNotFoundException)
                    {
                        raw = initial;
                    }

                    IEnumerable<object?> rows = document is null ? Data.Entries(raw) : List(raw);
                    if (patches is { Count: > 0 })
                    {
                        try
                        {
                            if (document is not null)
                            {
                                rows = PrepareIndexedRows(rows);
                                patches = patches
                                    .Select(patch =>
                                    {
                                        if (patch.TryGetValue("insert", out var insert) &&
                                            insert is IEnumerable<object?> inserted)
                                            return new EntryOptions(patch)
                                            {
                                                ["insert"] = PrepareIndexedRows(inserted)
                                            };
                                        return patch;
                                    })
                                    .ToList();
                            }

                            rows = EntryPatches.Apply(
                                Data.Entries(rows),
                                patches,
                                warning =>
                                {
                                    Diagnostics.Add(file + ": " + warning);
                                    document?.Report("warning", ownerPath, warning);
                                });
                        }
                        catch (FormatException error) when (ownerPath is not null)
                        {
                            throw new FormatException(
                                "Include patches could not be composed; child declarations are unavailable.",
                                error);
                        }
                    }

                    if (ownerPath is null && document is not null)
                        IndexTargets(rows, "", new(ReferenceEqualityComparer.Instance));
                    await WalkAsync(rows, file, ids, ownerPath is null ? "" : ownerPath + "/include");
                }
                finally
                {
                    files.Remove(canonical);
                }
            }
            catch (Exception error) when (error is not OperationCanceledException &&
                                          (ownerPath is not null || document is null))
            {
                Diagnostics.Add(file + ": " + error.Message);
                document?.Report("error", ownerPath, error.Message);
            }
        }

        private static IEnumerable<object?> List(object? value) =>
            value as IEnumerable<object?> ?? throw new FormatException("Expected a literal entry list.");

        // JavaScript's indexer can read an absent id from a non-null scalar. Keep that slot
        // as an invalid, anonymous row for positioned discovery. Null still fails indexing.
        // Insertions are enumerated only when the shared patch algorithm accepts their target.
        private static IEnumerable<object?> PrepareIndexedRows(IEnumerable<object?> rows) =>
            PrepareIndexedRows(rows, new(ReferenceEqualityComparer.Instance));

        private static IEnumerable<object?> PrepareIndexedRows(IEnumerable<object?> rows, HashSet<object> ancestors)
        {
            if (!ancestors.Add(rows))
                throw new FormatException("Recursive YAML alias in group patch index.");
            try
            {
                foreach (var value in rows)
                {
                    if (value is EntryOptions row)
                    {
                        yield return row.Group && row.Config is IEnumerable<object?> children
                            ? new EntryOptions(row)
                            {
                                Config = PrepareIndexedRows(children, ancestors).ToList()
                            }
                            : row;
                    }
                    else
                        yield return value is null ? null : new EntryOptions();
                }
            }
            finally
            {
                ancestors.Remove(rows);
            }
        }

        private static string Position(string prefix, int index) =>
            prefix + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Patch indexing follows group metadata even for an ordinary plugin whose Config is opaque to discovery.
        private void IndexTargets(IEnumerable<object?> rows, string prefix, HashSet<object> ancestors)
        {
            if (!ancestors.Add(rows))
                throw new FormatException("Recursive YAML alias in group patch index.");
            var index = 0;
            foreach (var value in rows)
            {
                var position = Position(prefix, index++);
                if (value is not EntryOptions row)
                    continue;
                if (row.Id.Length > 0)
                    targets[row.Id] = position;
                if (row.Group && row.Config is IEnumerable<object?> children)
                    IndexTargets(children, position + "/config", ancestors);
            }

            ancestors.Remove(rows);
        }

        private async Task WalkAsync(IEnumerable<object?> rows, string file, string ids, string prefix)
        {
            if (!groups.Add(rows))
                throw new FormatException("Recursive YAML alias in group configuration.");
            try
            {
                var index = 0;
                var baseUri = new Uri(new Uri(file), ".");
                foreach (var value in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var position = Position(prefix, index);
                    var row = value as EntryOptions;
                    var literalId = row?.GetValueOrDefault("id") as string;
                    var name = row?.GetValueOrDefault("name") as string;
                    var entryId = ids + (string.IsNullOrEmpty(literalId)
                        ? "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]"
                        : literalId);
                    index++;
                    var target = literalId is not null && targets.GetValueOrDefault(literalId) == position;
                    var tree = name switch
                    {
                        "cordis:group" => "group",
                        "cordis:include" => "include",
                        _ => null
                    };
                    try
                    {
                        if (row is null)
                            throw new FormatException("Each entry must be a mapping with a literal plugin name.");
                        if (document is not null)
                        {
                            if (name is null)
                                throw new FormatException("Each entry must be a mapping with a literal plugin name.");
                            ValidateMetadata(row);
                        }

                        name ??= "";
                    }
                    catch (FormatException error)
                    {
                        document?.Add(position, literalId, name, null, null, "error", false);
                        EntryError(row, entryId, name, file, position, error.Message);
                        continue;
                    }

                    if (tree is not null)
                    {
                        document?.Add(position, literalId, name, null, tree, "absent", target);
                        if (document is not null && !row.ContainsKey("config") && !row.Group &&
                            (row.Disabled is true || Expression(row.Disabled)))
                            continue;
                        try
                        {
                            if (Expression(row.Config))
                                throw new FormatException("Expression-backed carrier cannot be discovered statically.");
                            if (tree == "group")
                                await WalkAsync(List(row.Config), file, entryId + ":", position + "/config");
                            else
                                await IncludeAsync(row.Config, baseUri, entryId + ":", position);
                        }
                        catch (Exception error) when (error is not OperationCanceledException)
                        {
                            EntryError(row, entryId, name, file, position, error.Message);
                        }

                        continue;
                    }

                    try
                    {
                        if (document is not null && name.StartsWith("cordis:", StringComparison.Ordinal))
                            throw new NotSupportedException($"Unknown Cordis builtin '{name}'.");
                        if (document is not null && resolver is IConfigurationSchemaProjectionResolver projections)
                        {
                            try
                            {
                                var projection = await projections.TryResolveSchemaAsync(
                                    name,
                                    baseUri,
                                    cancellationToken);
                                if (projection is not null)
                                {
                                    document.AddProjection(position, literalId, name, target, projection);
                                    continue;
                                }
                            }
                            catch (NotSupportedException error)
                            {
                                document.Add(position, literalId, name, null, null, "unsupported", target);
                                document.Report("error", position, error.Message);
                                continue;
                            }
                        }

                        var plugin = (await resolver.ResolveAsync(name, baseUri, cancellationToken)).Plugin;
                        var captured = (plugin as IConfigurationPlugin)?.CaptureConfiguration();
                        var unsupported = plugin is ITreeCarrierPlugin;
                        var diagnostic = unsupported
                            ? "Custom tree carrier has no native static traversal contract."
                            : null;
                        if (document is not null)
                        {
                            document.Add(
                                position,
                                literalId,
                                name,
                                captured?.Descriptor,
                                null,
                                unsupported ? "unsupported" : captured is null ? "absent" : "schema",
                                target,
                                plugin);
                            if (diagnostic is not null)
                                document.Report("error", position, diagnostic);
                        }
                        else
                            Entries.Add(
                                new(
                                    entryId,
                                    name,
                                    file,
                                    Data.Truthy(row.Disabled),
                                    captured is null
                                        ? null
                                        : ConfigurationSchemaExporter.ToSchemastery(captured.Descriptor),
                                    captured is null
                                        ? null
                                        : ConfigurationSchemaExporter.ToJsonSchema(captured.Descriptor),
                                    diagnostic ?? (captured is null
                                        ? "Plugin has no captured configuration declaration."
                                        : null)));
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        document?.Add(position, literalId, name, null, null, "error", target);
                        EntryError(row, entryId, name, file, position, error.Message);
                    }
                }
            }
            finally
            {
                groups.Remove(rows);
            }
        }

        private void EntryError(
            EntryOptions? row,
            string id,
            string? name,
            string file,
            string position,
            string message)
        {
            if (document is null)
                Entries.Add(new(id, name ?? "", file, Data.Truthy(row?.Disabled), null, null, message));
            else
                document.Report("error", position, message);
        }

        private static void ValidateMetadata(EntryOptions row)
        {
            foreach (var key in new[] { "id", "name" })
                if (row.TryGetValue(key, out var value) && value is not string)
                    throw new FormatException(key + " must be a literal string.");
            if (row.TryGetValue("group", out var group) && group is not (bool or null))
                throw new FormatException("group must be a literal boolean or null.");
        }

        private static bool Expression(object? value) =>
            value is IReadOnlyDictionary<string, object?> map && map.ContainsKey("__jsExpr");

        private async Task IncludeAsync(object? config, Uri baseUri, string ids, string position)
        {
            string path;
            object? initial;
            List<EntryOptions>? patches;
            if (document is null)
                config = IncludeOptions.From(config);
            if (config is IncludeOptions options)
                (path, initial, patches) = (options.Path, options.Initial, options.Patches);
            else if (config is IDictionary<string, object?> map && map.GetValueOrDefault("path") is string literal)
            {
                path = literal;
                initial = map.GetValueOrDefault("initial");
                patches = map.TryGetValue("patches", out var value) ? Data.Entries(value) : null;
            }
            else
                throw new FormatException("Include config requires a literal path.");

            if (document is not null && patches is not null)
                foreach (var patch in patches)
                {
                    if (Expression(patch))
                        throw new FormatException("Include patches must be literal mappings.");
                    ValidateMetadata(patch);
                    if (patch.TryGetValue("insert", out var insert))
                        _ = List(insert);
                }

            var target = new Uri(baseUri, path);
            if (!target.IsFile)
                throw new NotSupportedException("Static include discovery requires a local file.");
            await ReadFileAsync(target.LocalPath, initial, patches, ids, position);
        }
    }
}
