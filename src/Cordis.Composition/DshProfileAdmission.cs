using Cordis;

namespace Cordis.Composition;

public static partial class DshProfilePolicy
{
    /// <summary>Prepares detached effective DSH profile rows before importing modules. Missing manifests preserve Loader's own errors.</summary>
    public static List<EntryOptions> PrepareEntries(string profileDirectory, DshRuntimeIdentity runtime,
        IEnumerable<EntryOptions> entries, Uri baseUri, Func<string, Uri, PackageManifest?> manifestOf, Action<string>? warning = null)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(manifestOf);
        warning ??= Console.Error.WriteLine;
        var rows = Data.Entries(Data.Clone(entries.ToList()));
        var admission = CreateAdmission(profileDirectory, runtime, warning);
        var includes = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        bool Check(List<EntryOptions> candidates, Uri parent)
        {
            var blocked = false;
            foreach (var row in candidates)
            {
                if (row.Disabled is true && !row.Group) continue;
                string? reason = null;
                try
                {
                    if (!row.Name.StartsWith("cordis:", StringComparison.Ordinal) && manifestOf(row.Name, parent) is { } manifest) admission(manifest);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
                { reason = error.Message; }
                if (reason is not null) { Deny(row, reason); blocked = true; continue; }
                if ((row.Group || row.Name is "cordis:group" or "@deepseek-ai/cordis-plugin-group") && row.Config is IEnumerable<object?> children)
                    if (Check(Data.Entries(children), parent)) blocked = true;
                if (row.Name is not ("cordis:include" or "@deepseek-ai/cordis-plugin-include")) continue;
                reason = IncludedConflict(row, parent);
                if (reason is not null) { Deny(row, reason); blocked = true; }
            }
            return blocked;
        }
        void Deny(EntryOptions row, string reason)
        {
            row.Disabled = true;
            if (row.Group) row.Group = false;
            warning?.Invoke($"Disabling profile plugin '{(row.Id.Length == 0 ? row.Name : row.Id)}': {reason}");
        }
        string? IncludedConflict(EntryOptions row, Uri parent)
        {
            IncludeOptions config;
            try { config = IncludeOptions.From(row.Config); }
            catch (FormatException) { return null; }
            if (Path.GetExtension(config.Path) is not (".yml" or ".yaml" or ".json")) return null;
            var requested = new Uri(parent, config.Path);
            if (!requested.IsFile) return null;
            var filename = File.Exists(requested.LocalPath) ? DeploymentPackageResolver.Canonical(requested.LocalPath) : Path.GetFullPath(requested.LocalPath);
            if (!includes.Add(filename)) return null;
            try
            {
                List<EntryOptions> included;
                try { included = ConfigurationFile.ParseEntries(File.ReadAllText(filename), Path.GetExtension(filename) == ".json"); }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                { if (config.Initial is null) return null; included = Data.Entries(Data.Clone(config.Initial)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
                { return null; }
                var effective = EntryPatches.Apply(included, config.Patches, warning);
                return Check(effective, new Uri(filename)) ? $"Its included file {filename} reaches an incompatible plugin; that file is never rewritten." : null;
            }
            finally { includes.Remove(filename); }
        }
        Check(rows, baseUri);
        return rows;
    }

    /// <summary>Prepares a complete patch composition over an empty profile root. Nonempty roots must use effective-entry preparation.</summary>
    public static List<EntryOptions> PreparePatches(string profileDirectory, DshRuntimeIdentity runtime, List<EntryOptions> patches,
        Uri baseUri, Func<string, Uri, PackageManifest?> manifestOf, Action<string>? warning = null)
    {
        var rows = PrepareEntries(profileDirectory, runtime, EntryPatches.Apply([], patches, warning), baseUri, manifestOf, warning);
        return rows.Count == 0 ? [] : [new EntryOptions { ["insert"] = rows }];
    }

    /// <summary>Finds deployed or file-relative manifest metadata without executing a plugin. Missing resources have no admission opinion.</summary>
    public static PackageManifest? LocateManifest(string specifier, Uri parent, DeploymentPackageResolver? packages = null)
    {
        if (specifier.StartsWith("cordis:", StringComparison.Ordinal)) return null;
        if (packages?.PackageOf(specifier, parent) is { } package) return PackageManifest.Read(package.ManifestPath);
        if (!Path.IsPathRooted(specifier) && !specifier.StartsWith(".", StringComparison.Ordinal) && !specifier.StartsWith("file:", StringComparison.Ordinal)) return null;
        var resource = Path.IsPathRooted(specifier) ? new Uri(Path.GetFullPath(specifier)) : new Uri(parent, specifier);
        if (!resource.IsFile || !File.Exists(resource.LocalPath)) return null;
        var directory = Path.GetDirectoryName(DeploymentPackageResolver.Canonical(resource.LocalPath));
        while (directory is not null)
        {
            var path = Path.Combine(directory, "package.json");
            try { return PackageManifest.Read(path); }
            catch (FileNotFoundException) { directory = Path.GetDirectoryName(directory); }
        }
        return null;
    }

    /// <summary>Mounts a DSH profile with an explicit identity. Complete effective rows are prepared while retaining all base-root rows and patches.</summary>
    public static Task<Include> MountAsync(Loader loader, string configurationPath, string profileDirectory, DshRuntimeIdentity runtime,
        List<EntryOptions>? patches = null, DeploymentPackageResolver? packages = null, Action<string>? warning = null)
        => MountAsync(loader, configurationPath, profileDirectory, runtime, patches, packages, warning, null);

    /// <summary>Mounts with a host-owned metadata locator for explicit CLR or static module registrations, before executing their resolver.</summary>
    public static async Task<Include> MountAsync(Loader loader, string configurationPath, string profileDirectory, DshRuntimeIdentity runtime,
        List<EntryOptions>? patches, DeploymentPackageResolver? packages, Action<string>? warning, Func<string, Uri, PackageManifest?>? manifestOf)
    {
        var config = new IncludeOptions(new Uri(Path.GetFullPath(configurationPath)).AbsoluteUri, Patches: patches)
        {
            PrepareEntries = (rows, parent) => PrepareEntries(profileDirectory, runtime, rows, parent, manifestOf ?? ((name, uri) => LocateManifest(name, uri, packages)), warning)
        };
        await loader.CreateAsync(new EntryOptions { Id = "root", Name = "cordis:include", Config = config });
        await loader.WaitAsync();
        var entry = loader.Resolve("root");
        if (entry.Fiber?.State != FiberState.Active) throw new StartupException([new(entry.Id, entry.Options.Name, entry.Fiber?.State, entry.LastError, [], true)]);
        return (Include)entry.Subtree!;
    }
}
