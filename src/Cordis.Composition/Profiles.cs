namespace Cordis.Composition;

/// <summary>
/// Represents the configuration layer component.
/// </summary>
/// <param name="Source">The source value.</param>
/// <param name="Patches">The patches value.</param>
public sealed record ConfigurationLayer(string Source, List<EntryOptions> Patches);
/// <summary>
/// Represents the bundle component.
/// </summary>
/// <param name="Name">The name value.</param>
/// <param name="Directory">The directory value.</param>
/// <param name="PatchPath">The patch path value.</param>
/// <param name="Patches">The patches value.</param>
public sealed record Bundle(string Name, string Directory, string PatchPath, List<EntryOptions> Patches);
/// <summary>
/// Represents the profile component.
/// </summary>
/// <param name="Name">The name value.</param>
/// <param name="Directory">The directory value.</param>
/// <param name="Bundles">The bundles value.</param>
/// <param name="UserLayer">The user layer value.</param>
public sealed record Profile(string Name, string Directory, IReadOnlyList<Bundle> Bundles, ConfigurationLayer UserLayer)
{
    /// <summary>
    /// Gets the layers value.
    /// </summary>
    public IEnumerable<ConfigurationLayer> Layers => Bundles.Select(b => new ConfigurationLayer(b.PatchPath, b.Patches)).Append(UserLayer);
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
    public string? BundlePatch => (Dsh?.GetValueOrDefault("bundle") as IDictionary<string, object?>)?.GetValueOrDefault("patch") as string;
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
    public static async Task<Profile> LoadAsync(string directory, IReadOnlyDictionary<string, string> installationBundles, IReadOnlyDictionary<string, string>? profileBundles = null, bool userLayer = true)
    {
        directory = Path.GetFullPath(directory); var manifest = PackageManifest.Read(Path.Combine(directory, "package.json")); var bundles = new List<Bundle>();
        foreach (var name in manifest.Bundles)
        {
            if (!installationBundles.TryGetValue(name, out var packageDirectory) && !(profileBundles?.TryGetValue(name, out packageDirectory) ?? false)) throw new FileNotFoundException($"Cannot resolve profile bundle '{name}'.");
            var bundleManifest = PackageManifest.Read(Path.Combine(packageDirectory!, "package.json"));
            var declaration = bundleManifest.BundlePatch ?? throw new FormatException($"Profile bundle '{name}' declares no dsh.bundle.patch.");
            var patch = Path.GetFullPath(Path.Combine(packageDirectory!, declaration)); bundles.Add(new(name, packageDirectory!, patch, await ReadPatchesAsync(patch)));
        }
        var userPath = Path.Combine(directory, "cordis.patch.yml");
        return new(Path.GetFileName(directory), directory, bundles, new(userPath, userLayer ? await ReadPatchesAsync(userPath, optional: true) : []));
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
        foreach (var patch in patches) if (patch.TryGetValue("insert", out var insertion) && insertion is not null) foreach (var entry in Data.Entries(insertion)) Anchor(entry);
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
