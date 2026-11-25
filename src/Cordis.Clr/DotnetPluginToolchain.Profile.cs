using Cordis.Composition;

namespace Cordis.Clr;

public sealed partial class DotnetPluginToolchain
{
    /// <summary>Open the saved CLR routes for a new Host using the requested build configuration.</summary>
    /// <remarks>Installation-owned names take precedence. Local sources are queried without running Build.</remarks>
    public static async Task<DotnetPluginToolchain> OpenForRunAsync(
        string profileDirectory,
        ClrModuleResolver resolver,
        IEnumerable<string> sources,
        string configuration = "Debug",
        IReadOnlyDictionary<string, string>? installationBundles = null,
        string dotnetCommand = "dotnet",
        CancellationToken cancellationToken = default)
    {
        var toolchain = OpenOffline(profileDirectory, resolver, sources, dotnetCommand);
        try
        {
            toolchain.configuration = configuration;
            toolchain.installationBundles = installationBundles;
            await toolchain.RefreshRoutesAsync(
                PackageManifest.Read(Path.Combine(profileDirectory, "package.json")),
                cancellationToken);
            return toolchain;
        }
        catch
        {
            toolchain.Dispose();
            throw;
        }
    }

    private static void CheckProjectIdentity(string name, string? actual)
    {
        if (!string.Equals(name, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Saved dependency '{name}' resolved to package '{actual}'.");
    }

    private string configuration = "Debug";
    private IReadOnlyDictionary<string, string>? installationBundles;

    /// <summary>Refresh saved CLR routes inside the owning profile session's lifecycle queue.</summary>
    public Task RefreshProfileRoutesAsync(PackageManifest manifest) => RefreshRoutesAsync(manifest, default);

    private async Task RefreshRoutesAsync(PackageManifest manifest, CancellationToken token)
    {
        var routes = InstalledRoutes(manifest);
        foreach (var link in manifest.LinkedRoots)
        {
            if (installationBundles?.ContainsKey(link.Name) == true)
                continue;
            var selected = manifest.Bundles.Contains(link.Name);
            try
            {
                var project = await ClrLocalProject.QueryAsync(link.Directory, configuration, dotnet, token);
                CheckProjectIdentity(link.Name, project.PackageId);
                if (project.IsBundle || selected)
                    routes.Add(link.Name, (project.ResolveOutput(), true));
            }
            catch (IOException error) when (!selected && error is FileNotFoundException or DirectoryNotFoundException)
            {
                // A disabled source can be registered before it has a completed output.
            }
        }

        foreach (var (id, directory) in bundles)
        {
            if (!routes.TryGetValue(id, out var selected) ||
                (!string.Equals(
                        directory,
                        selected.Directory,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) &&
                    !IsDeferredOnlineRoute(id, directory, selected.Directory, manifest)))
                throw new DeploymentRestartRequiredException(
                    $"The saved source for '{id}' changed. Restart to load the selected output.");
        }

        // A removal can withdraw exports before its manifest save conflicts. Keep them withdrawn on retry.
        foreach (var name in withdrawn)
            if (!manifest.Bundles.Contains(name))
                routes.Remove(name);
        RegisterRoutes(routes);
    }

    private Dictionary<string, (string Directory, bool Verified)> InstalledRoutes(PackageManifest manifest)
    {
        var routes = new Dictionary<string, (string Directory, bool Verified)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in Dependencies(manifest))
        {
            if (installationBundles?.ContainsKey(name) == true || value is not string declaration)
                continue;
            if (manifest.RestoreGraphs.TryGetValue(name, out var graph))
            {
                try
                {
                    var package = ReadProfilePackage(name, declaration, graph);
                    if (package.IsBundle)
                        routes.Add(name, (package.Directory, true));
                }
                catch (IOException error) when (!manifest.Bundles.Contains(name) &&
                                                error is FileNotFoundException or DirectoryNotFoundException)
                {
                    // A stopped, unselected package can be repaired by an offline install.
                }
            }
            else if (ValidIdentity(name, declaration))
            {
                var directory = PackageDirectory(name, declaration);
                if (Directory.Exists(directory))
                {
                    RequireOwned(directory, profileDirectory);
                    routes.Add(name, (directory, false));
                }
            }
        }

        return routes;
    }

    private void RegisterSavedProfile(PackageManifest manifest) => RegisterRoutes(InstalledRoutes(manifest));

    private void RegisterRoutes(Dictionary<string, (string Directory, bool Verified)> routes)
    {
        foreach (var (name, route) in routes)
        {
            if (bundles.ContainsKey(name))
                continue;
            if (route.Verified)
                RegisterVerifiedBundle(name, route.Directory);
            else
                Register(name, route.Directory);
        }
    }

    private static IDictionary<string, object?> Dependencies(PackageManifest manifest) =>
        manifest.Raw.GetValueOrDefault("dependencies") switch
        {
            null => new EntryOptions(),
            IDictionary<string, object?> dependencies => dependencies,
            _ => throw new FormatException("Profile dependencies must be an object.")
        };

    /// <summary>Prepare explicit local roots, archives or NuGet specifications and save them together in the profile.</summary>
    public async Task<ProfilePackageSave> AddProfilePackagesAsync(
        IReadOnlyList<string> specifications,
        Func<PackageInspection, bool> approveBuild,
        Action<string> output,
        string configuration = "Debug",
        IReadOnlyDictionary<string, string>? installationBundles = null,
        CancellationToken cancellationToken = default)
    {
        if (specifications.Count == 0)
            throw new ArgumentException("Specify at least one package or local source.", nameof(specifications));
        var requests = specifications.Select(ParseSpecification).ToArray();
        var (text, manifest) = await ReadProfileAsync(cancellationToken, allowNew: true);
        var edits = new List<ProfilePackageEdit>();
        foreach (var (source, version, local) in requests)
        {
            ProfilePackageEdit edit;
            if (local && !source.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                var project = await ClrLocalProject.QueryAsync(source, configuration, dotnet, cancellationToken);
                ValidateIdentity(project.PackageId, "0.0.0");
                edit = new(project.PackageId, "link:" + project.SourceRoot, project.IsBundle);
            }
            else
            {
                var package = await PrepareProfilePackageAsync(
                    source,
                    version,
                    null,
                    false,
                    approveBuild,
                    output,
                    cancellationToken);
                edit = new(package.Name, package.Declaration, package.IsBundle, package.Graph);
            }

            var name = SavedName(manifest, edit.Name);
            if (edits.Any(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The same package was specified twice: " + name);
            edits.Add(
                edit with
                {
                    Name = name,
                    Bundle = EffectiveBundle(name, edit.Bundle, installationBundles)
                });
        }

        return await CommitPreparationsAsync(text, edits, cancellationToken);
    }

    /// <summary>Restore saved dependencies. An explicit update re-evaluates ranges using each root's own NuGet graph.</summary>
    public async Task<ProfilePackageSave> RestoreProfilePackagesAsync(
        bool update,
        string? name,
        Func<PackageInspection, bool> approveBuild,
        Action<string> output,
        string configuration = "Debug",
        IReadOnlyDictionary<string, string>? installationBundles = null,
        CancellationToken cancellationToken = default)
    {
        var (text, manifest) = await ReadProfileAsync(cancellationToken);
        var dependencies = Dependencies(manifest);
        var edits = new List<ProfilePackageEdit>();
        if (name is not null)
        {
            name = SavedName(manifest, name);
            if (!dependencies.ContainsKey(name))
                throw new ArgumentException("Unknown profile dependency: " + name);
        }

        foreach (var (id, value) in dependencies)
        {
            if (name is not null && id != name)
                continue;
            if (value is not string declaration)
                throw new FormatException("Expected a string dependency declaration for " + id);
            var graph = manifest.RestoreGraphs.GetValueOrDefault(id);
            bool bundle;
            if (declaration.StartsWith("link:", StringComparison.Ordinal))
            {
                var link = manifest.LinkedRoots.Single(root => root.Name == id);
                var project = await ClrLocalProject.QueryAsync(
                    link.Directory,
                    configuration,
                    dotnet,
                    cancellationToken);
                CheckProjectIdentity(id, project.PackageId);
                bundle = project.IsBundle;
            }
            else
            {
                var package = await PrepareProfilePackageAsync(
                    id,
                    declaration,
                    graph,
                    update,
                    approveBuild,
                    output,
                    cancellationToken);
                bundle = package.IsBundle;
                graph = package.Graph;
            }

            edits.Add(new(id, declaration, EffectiveBundle(id, bundle, installationBundles), graph));
        }

        return await CommitPreparationsAsync(text, edits, cancellationToken);
    }

    private Task<ProfilePackageSave> CommitPreparationsAsync(
        string? previous,
        IReadOnlyList<ProfilePackageEdit> edits,
        CancellationToken token) =>
        PluginConfigurationOperations.CommitDeployedPackagesAsync(
            profileDirectory,
            previous,
            edits,
            token,
            verifyPrepared: () =>
            {
                // Retained graphs can be deleted by another offline owner until the profile selects them.
                foreach (var edit in edits)
                    if (edit.RestoreGraph is { } graph)
                        ReadProfilePackage(edit.Name, edit.Declaration!, graph);
            });

    /// <summary>Remove a saved dependency and its bundle selection, retaining source and deployed files.</summary>
    public async Task<ProfilePackageSave> RemoveProfilePackageAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(name, "0.0.0");
        var (text, manifest) = await ReadProfileAsync(cancellationToken);
        name = SavedName(manifest, name);
        if (!Dependencies(manifest).ContainsKey(name))
            throw new ArgumentException("Unknown profile dependency: " + name);
        return await PluginConfigurationOperations.CommitDeployedPackagesAsync(
            profileDirectory,
            text,
            [new ProfilePackageEdit(name, null, false)],
            cancellationToken: cancellationToken);
    }

    private async Task<(string? Text, PackageManifest Manifest)> ReadProfileAsync(
        CancellationToken token,
        bool allowNew = false)
    {
        var path = Path.Combine(profileDirectory, "package.json");
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, token);
        }
        catch (IOException error) when (allowNew && error is FileNotFoundException or DirectoryNotFoundException)
        {
            return (null, new(new EntryOptions()));
        }

        var parsed = ConfigurationFile.Parse(text, true) as EntryOptions ??
            throw new FormatException("Expected a profile manifest object.");
        return (text, new(parsed));
    }

    private static string SavedName(PackageManifest manifest, string name) =>
        Dependencies(manifest).Keys.SingleOrDefault(id => id.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;

    private static bool EffectiveBundle(string id, bool declared, IReadOnlyDictionary<string, string>? installation) =>
        installation?.TryGetValue(id, out var directory) == true
            ? PackageManifest.Read(Path.Combine(directory, "package.json")).HasBundleDeclaration
            : declared;

    private static (string Source, string? Version, bool Local) ParseSpecification(string spec)
    {
        if (Path.IsPathFullyQualified(spec) || spec.StartsWith("./", StringComparison.Ordinal) ||
            spec.StartsWith("../", StringComparison.Ordinal) || spec.StartsWith(@".\", StringComparison.Ordinal) ||
            spec.StartsWith(@"..\", StringComparison.Ordinal) || spec is "." or "..")
            return (Path.GetFullPath(spec), null, true);
        var parts = spec.Split('@', 2);
        ValidateIdentity(parts[0], "0.0.0");
        if (parts.Length == 2 && string.IsNullOrWhiteSpace(parts[1]))
            throw new ArgumentException("A package version is missing: " + spec);
        return (parts[0], parts.Length == 2 ? parts[1] : null, false);
    }
}
