namespace Cordis.Composition;

/// <summary>A declaration discovered from a literal entry without activating it.</summary>
/// <param name="EntryId">Stable source position when the entry has no explicit id.</param>
/// <param name="Name">Module specifier.</param>
/// <param name="Source">Configuration file containing the entry.</param>
/// <param name="Disabled">Declared disabled state; disabled entries still expose declarations.</param>
/// <param name="Schemastery">Independent form declaration, or null when unavailable.</param>
/// <param name="JsonSchema">Independent JSON Schema declaration, or null when unavailable.</param>
/// <param name="Diagnostic">Import, capture, or static carrier limitation.</param>
public sealed record DiscoveredConfigurationSchema(string EntryId, string Name, string Source, bool Disabled,
    ConfigurationSchemaExport? Schemastery, ConfigurationSchemaExport? JsonSchema, string? Diagnostic);

/// <summary>Read-only declaration inventory, including source failures without discarding surviving siblings.</summary>
/// <param name="Entries">Declarations in source traversal order.</param>
/// <param name="Diagnostics">Source and carrier discovery failures.</param>
public sealed record ConfigurationSchemaCatalog(IReadOnlyList<DiscoveredConfigurationSchema> Entries, IReadOnlyList<string> Diagnostics);

/// <summary>Discover explicitly captured plugin declarations without a Context, Loader, Apply or ResolveConfig invocation.</summary>
/// <remarks>Module resolution and optional capture are trusted author code and may execute during import.
/// Raw expressions and unresolved lazy builders remain inert. Native group/include traversal reads sources but never creates them.
/// Custom tree carriers need their own static discovery adapter; their configuration is not guessed.</remarks>
public static class ConfigurationSchemaDiscovery
{
    /// <summary>Read one entry-list file and ordered patches, then discover native carriers and plugin declarations.</summary>
    public static async Task<ConfigurationSchemaCatalog> DiscoverAsync(string filename, IModuleResolver resolver,
        IReadOnlyList<ConfigurationLayer>? layers = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentNullException.ThrowIfNull(resolver);
        var entries = new List<DiscoveredConfigurationSchema>();
        var diagnostics = new List<string>();
        var activeFiles = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        async Task File(string path, List<EntryOptions>? initial, List<EntryOptions>? patches, string prefix)
        {
            cancellationToken.ThrowIfCancellationRequested();
            path = Path.GetFullPath(path);
            if (Path.GetExtension(path) is not (".yml" or ".yaml" or ".json"))
            { diagnostics.Add(path + ": unsupported configuration extension"); return; }
            if (!activeFiles.Add(path)) { diagnostics.Add(path + ": include cycle"); return; }
            try
            {
                List<EntryOptions> rows;
                try
                {
                    rows = ConfigurationFile.ParseEntries(await System.IO.File.ReadAllTextAsync(path, cancellationToken), Path.GetExtension(path) == ".json");
                }
                catch (FileNotFoundException) when (initial is not null) { rows = Data.Entries(Data.Clone(initial)); }
                rows = EntryPatches.Apply(rows, patches, warning => diagnostics.Add(path + ": " + warning));
                await Walk(rows, path, new Uri(new Uri(path), "."), prefix);
            }
            catch (Exception error) when (error is not OperationCanceledException) { diagnostics.Add(path + ": " + error.Message); }
            finally { activeFiles.Remove(path); }
        }
        async Task Walk(IReadOnlyList<EntryOptions> rows, string source, Uri baseUri, string prefix)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = rows[index];
                var id = prefix + (row.Id.Length > 0 ? row.Id : "[" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]");
                var name = row.Name;
                try
                {
                    if (name == "cordis:group")
                    {
                        if (row.Config is IReadOnlyDictionary<string, object?> marker && marker.ContainsKey("__jsExpr"))
                            throw new FormatException("Expression-backed group cannot be discovered statically.");
                        await Walk(Data.Entries(Data.Clone(row.Config)), source, baseUri, id + ":");
                        continue;
                    }
                    if (name == "cordis:include")
                    {
                        if (row.Config is IReadOnlyDictionary<string, object?> marker && marker.ContainsKey("__jsExpr"))
                            throw new FormatException("Expression-backed include cannot be discovered statically.");
                        var include = IncludeOptions.From(row.Config);
                        var target = new Uri(baseUri, include.Path);
                        if (!target.IsFile) throw new NotSupportedException("Static include discovery requires a local file.");
                        await File(target.LocalPath, include.Initial, include.Patches, id + ":");
                        continue;
                    }
                    var plugin = await resolver.ResolveAsync(name, baseUri, cancellationToken);
                    var captured = (plugin as IConfigurationPlugin)?.CaptureConfiguration();
                    var diagnostic = plugin is ITreeCarrierPlugin ? "Custom tree carrier has no native static traversal contract."
                        : captured is null ? "Plugin has no captured configuration declaration." : null;
                    entries.Add(new(id, name, source, Data.Truthy(row.Disabled), captured is null ? null : ConfigurationSchemaExporter.ToSchemastery(captured.Descriptor),
                        captured is null ? null : ConfigurationSchemaExporter.ToJsonSchema(captured.Descriptor), diagnostic));
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    entries.Add(new(id, name, source, Data.Truthy(row.Disabled), null, null, error.Message));
                }
            }
        }
        await File(filename, null, layers is null ? null : ProfileComposition.Flatten(layers), "");
        return new(entries.AsReadOnly(), diagnostics.AsReadOnly());
    }
}
