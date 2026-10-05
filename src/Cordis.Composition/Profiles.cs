namespace Cordis.Composition;

/// <summary>
/// Represents the configuration layer component.
/// </summary>
/// <param name="Source">The source value.</param>
/// <param name="Patches">The patches value.</param>
public sealed record ConfigurationLayer(string Source, List<EntryOptions> Patches);
/// <summary>
/// Ordered file layers are the authoritative bundle data. Legacy properties are views over those layers.
/// </summary>
/// <param name="Name">The name value.</param>
/// <param name="Directory">The directory value.</param>
/// <param name="PatchPath">The patch path value.</param>
/// <param name="Patches">The patches value.</param>
public sealed record Bundle(string Name, string Directory, string PatchPath, List<EntryOptions> Patches)
{
    private IReadOnlyList<ConfigurationLayer> layers = Array.AsReadOnly(new[] { new ConfigurationLayer(PatchPath, Patches) });

    /// <summary>The primary file source, or an empty string for an empty declaration. Assignment relabels only the primary layer.</summary>
    public string PatchPath
    {
        get => layers.FirstOrDefault()?.Source ?? "";
        init
        {
            layers = Array.AsReadOnly(layers.Count == 0
                ? [new ConfigurationLayer(value, [])]
                : layers.Select((layer, index) => index == 0 ? layer with { Source = value } : layer).ToArray());
        }
    }

    /// <summary>All patch rows in file order. Single-file bundles retain their mutable list; multi-file bundles return a flattened copy.</summary>
    /// <remarks>Assignment replaces the entire bundle with one layer at the current primary source. Use PatchLayers to retain multiple file sources.</remarks>
    public List<EntryOptions> Patches
    {
        get => layers.Count == 1 ? layers[0].Patches : layers.SelectMany(layer => layer.Patches).ToList();
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            layers = Array.AsReadOnly(new[] { new ConfigurationLayer(PatchPath, value) });
        }
    }

    /// <summary>Declared patch sources in application order. Assignment relabels existing layers without changing their count.</summary>
    /// <exception cref="ArgumentException">The source count differs from the layer count. Assign PatchLayers to replace the declaration.</exception>
    public IReadOnlyList<string> PatchPaths
    {
        get => Array.AsReadOnly(layers.Select(layer => layer.Source).ToArray());
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Count != layers.Count) throw new ArgumentException("PatchPaths must label every existing layer; use PatchLayers to change the declaration.", nameof(PatchPaths));
            layers = Array.AsReadOnly(layers.Select((layer, index) => layer with { Source = value[index] }).ToArray());
        }
    }

    /// <summary>Individual file layers preserve patch provenance and file-relative inserted modules.</summary>
    public IReadOnlyList<ConfigurationLayer> PatchLayers
    {
        get => layers;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            layers = Array.AsReadOnly(value.ToArray());
        }
    }
}
/// <summary>A selected bundle that contributed no layer, with its load or admission failure.</summary>
public sealed record SkippedBundle(string Name, string Reason);
/// <summary>
/// Represents the profile component.
/// </summary>
/// <param name="Name">The name value.</param>
/// <param name="Directory">The directory value.</param>
/// <param name="Bundles">The bundles value.</param>
/// <param name="UserLayer">The user layer value.</param>
public sealed record Profile(string Name, string Directory, IReadOnlyList<Bundle> Bundles, ConfigurationLayer UserLayer)
{
    /// <summary>Manifest selection, including bundles that failed to load.</summary>
    public IReadOnlyList<string> SelectedBundles { get; init; } = Bundles.Select(bundle => bundle.Name).ToArray();
    /// <summary>Failures in manifest selection order. Loading does not print diagnostics.</summary>
    public IReadOnlyList<SkippedBundle> SkippedBundles { get; init; } = [];
    /// <summary>
    /// Gets the layers value.
    /// </summary>
    public IEnumerable<ConfigurationLayer> Layers => Bundles.SelectMany(b => b.PatchLayers).Append(UserLayer);
}

/// <summary>Reads DSH composition metadata without treating npm dependencies as service injection.</summary>
public sealed class PackageManifest
{
    /// <summary>
    /// Gets the raw value.
    /// </summary>
    public EntryOptions Raw { get; }
    /// <summary>
    /// Initializes a new instance of the <see cref="PackageManifest"/> type.
    /// </summary>
    public PackageManifest(EntryOptions raw) => Raw = raw;
    /// <summary>
    /// Reads the requested value.
    /// </summary>
    public static PackageManifest Read(string path) => new(ConfigurationFile.Parse(File.ReadAllText(path), true) as EntryOptions ?? throw new FormatException($"Manifest {path} must be a JSON object."));
    private IDictionary<string, object?>? Dsh => Raw.GetValueOrDefault("dsh") as IDictionary<string, object?>;
    /// <summary>
    /// Gets the bundle patch value.
    /// </summary>
    public string? BundlePatch => BundlePatchFiles.FirstOrDefault();
    /// <summary>Whether this package declares bundle metadata, independently of its patch count.</summary>
    public bool HasBundleDeclaration => Dsh?.GetValueOrDefault("bundle") is IDictionary<string, object?>;
    /// <summary>Ordered package-relative files. Invalid declarations fail rather than silently becoming plain packages.</summary>
    public IReadOnlyList<string> BundlePatchFiles
    {
        get
        {
            if (!HasBundleDeclaration) return [];
            var declared = ((IDictionary<string, object?>)Dsh!["bundle"]!).GetValueOrDefault("patch");
            if (declared is string path) return [path];
            if (declared is IEnumerable<object?> values)
                return values.Select(value => value as string ?? throw new FormatException("dsh.bundle.patch must be a file path or a list of file paths.")).ToArray();
            throw new FormatException("dsh.bundle.patch must be a file path or a list of file paths.");
        }
    }
    /// <summary>
    /// Gets the bundles value.
    /// </summary>
    public IReadOnlyList<string> Bundles
    {
        get
        {
            var declared = (Dsh?.GetValueOrDefault("profile") as IDictionary<string, object?>)?.GetValueOrDefault("bundles");
            return declared is null ? [] : declared is IEnumerable<object?> values
                ? values.Select(value => value as string ?? throw new FormatException("Profile bundles must be strings.")).ToArray()
                : throw new FormatException("Profile bundles must be an array.");
        }
    }
    /// <summary>
    /// Gets the client package dependencies value.
    /// </summary>
    public IReadOnlyList<string> ClientPackageDependencies => (Dsh?.GetValueOrDefault("client") as IDictionary<string, object?>)?.GetValueOrDefault("inject") is IEnumerable<object?> values ? values.Cast<string>().ToArray() : [];
    /// <summary>
    /// Performs the write operation.
    /// </summary>
    public void Write(string path) => File.WriteAllText(path, ConfigurationFile.Write(Raw, true));
}

/// <summary>
/// Represents the profiles component.
/// </summary>
public static class Profiles
{
    /// <summary>
    /// Resolves directory.
    /// </summary>
    public static string ResolveDirectory(string home, string name)
    {
        if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name is "." or ".." or "node_modules") throw new ArgumentException($"Invalid profile name '{name}'.", nameof(name));
        return Path.Combine(home, "profiles", name);
    }
    /// <summary>
    /// Performs the initialize operation.
    /// </summary>
    public static void Initialize(string directory, IReadOnlyList<string> bundles)
    {
        System.IO.Directory.CreateDirectory(directory);
        var manifest = Path.Combine(directory, "package.json");
        if (!File.Exists(manifest)) File.WriteAllText(manifest, ConfigurationFile.Write(new EntryOptions { ["name"] = "dsh-profile-" + Path.GetFileName(directory), ["private"] = true, ["dependencies"] = new EntryOptions(), ["dsh"] = new EntryOptions { ["profile"] = new EntryOptions { ["bundles"] = bundles } } }, true));
        var patch = Path.Combine(directory, "cordis.patch.yml"); if (!File.Exists(patch)) File.WriteAllText(patch, "[]\n");
    }
    /// <summary>Installation mappings have priority over profile mappings for bundle layers.</summary>
    public static Task<Profile> LoadAsync(string directory, IReadOnlyDictionary<string, string> installationBundles, IReadOnlyDictionary<string, string>? profileBundles = null, bool userLayer = true)
        => LoadAsync(directory, installationBundles, profileBundles, userLayer, null);
    /// <summary>Load with an explicit bundle admission policy. Generic loading supplies no version policy.</summary>
    public static async Task<Profile> LoadAsync(string directory, IReadOnlyDictionary<string, string> installationBundles, IReadOnlyDictionary<string, string>? profileBundles, bool userLayer, Action<PackageManifest>? admitBundle)
    {
        directory = Path.GetFullPath(directory); var manifest = PackageManifest.Read(Path.Combine(directory, "package.json")); var bundles = new List<Bundle>();
        var selected = manifest.Bundles; var skipped = new List<SkippedBundle>();
        foreach (var name in selected)
        {
            try
            {
                if (!installationBundles.TryGetValue(name, out var packageDirectory) && !(profileBundles?.TryGetValue(name, out packageDirectory) ?? false)) throw new FileNotFoundException($"Cannot resolve profile bundle '{name}'.");
                var bundleManifest = PackageManifest.Read(Path.Combine(packageDirectory!, "package.json"));
                if (!bundleManifest.HasBundleDeclaration) throw new FormatException($"Profile bundle '{name}' declares no dsh.bundle in its package.json.");
                admitBundle?.Invoke(bundleManifest);
                bundles.Add(await ReadBundleAsync(name, packageDirectory!, bundleManifest));
            }
            catch (Exception error) { skipped.Add(new(name, error.Message)); }
        }
        var userPath = Path.Combine(directory, "cordis.patch.yml");
        return new(Path.GetFileName(directory), directory, bundles, new(userPath, userLayer ? await ReadPatchesAsync(userPath, optional: true) : []))
        { SelectedBundles = selected, SkippedBundles = skipped };
    }
    internal static async Task<Bundle> ReadBundleAsync(string name, string directory, PackageManifest manifest)
    {
        var paths = manifest.BundlePatchFiles.Select(path => Path.GetFullPath(Path.Combine(directory, path))).ToArray();
        var layers = new List<ConfigurationLayer>();
        foreach (var path in paths) layers.Add(new(path, await ReadPatchesAsync(path)));
        // Publish only after every declared file succeeds; an earlier prefix never leaks.
        return new(name, directory, paths.FirstOrDefault() ?? "", []) { PatchLayers = layers };
    }
    /// <summary>Report each skipped selection once when the caller elects to report this load.</summary>
    public static void ReportSkippedBundles(Profile profile, Action<string> report, string diagnosticName = "cordis")
    {
        foreach (var skipped in profile.SkippedBundles)
            report($"{diagnosticName}: skipping profile bundle {ConfigurationFile.Write(skipped.Name, true).TrimEnd()}: {skipped.Reason}");
    }
    /// <summary>
    /// Reads patches async.
    /// </summary>
    public static async Task<List<EntryOptions>> ReadPatchesAsync(string path, bool optional = false)
    {
        List<EntryOptions> patches;
        try { patches = await ConfigurationFile.ReadEntriesAsync(path); }
        catch (FileNotFoundException) when (optional) { return []; }
        catch (DirectoryNotFoundException) when (optional) { return []; }
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        void Anchor(EntryOptions entry)
        {
            if (Path.IsPathRooted(entry.Name) || entry.Name.StartsWith("./", StringComparison.Ordinal) || entry.Name.StartsWith("../", StringComparison.Ordinal)) entry.Name = new Uri(Path.GetFullPath(entry.Name, baseDirectory)).AbsoluteUri;
            if (entry.Group && entry.Config is IEnumerable<object?>) foreach (var child in Data.Entries(entry.Config)) Anchor(child);
        }
        foreach (var patch in patches) if (patch.TryGetValue("insert", out var insertion) && Data.Truthy(insertion)) foreach (var entry in Data.Entries(insertion)) Anchor(entry);
        return patches;
    }
    /// <summary>
    /// Performs the compose operation.
    /// </summary>
    public static List<EntryOptions> Compose(IEnumerable<ConfigurationLayer> layers, Action<string>? warn = null)
    {
        var patches = Data.Entries(Data.Clone(layers.SelectMany(layer => layer.Patches).ToList()));
        return EntryPatches.Apply([], patches, warn);
    }
    /// <summary>
    /// Performs the preview operation.
    /// </summary>
    public static string Preview(IEnumerable<ConfigurationLayer> layers, bool json = false, Action<string>? warn = null) => ConfigurationFile.Write(Compose(layers, warn), json);
}
