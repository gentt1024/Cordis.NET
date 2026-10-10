using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cordis.Composition;

namespace Cordis.Clr;

/// <summary>NuGet acquisition and SDK preparation for explicitly declared CLR plugins in one profile.</summary>
/// <remarks>The resolver is borrowed and must outlive the session. Sources are host-selected local feeds or NuGet v3
/// service indexes. Builds require explicit approval because package targets can execute code. This is not a sandbox.</remarks>
[RequiresDynamicCode(
    "NuGet plugin deployment uses the ordinary CLR; Native AOT applications must republish static plugins.")]
[RequiresUnreferencedCode("External plugin entry types are explicitly named in package metadata.")]
public sealed class DotnetPluginToolchain : IProfilePackageToolchain, IDisposable
{
    private readonly string profileDirectory;
    private readonly string packagesDirectory;
    private readonly string workDirectory;
    private readonly ClrModuleResolver resolver;
    private readonly string dotnet;

    private readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    private readonly Dictionary<string, string> bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] sources;

    /// <summary>Initialize explicit routes for installed packages recorded in the profile, without loading their code.</summary>
    /// <remarks>An empty source whitelist permits installed applications to run offline; acquisition still requires an explicitly selected source.</remarks>
    public DotnetPluginToolchain(
        string profileDirectory,
        ClrModuleResolver resolver,
        IEnumerable<string> sources,
        string dotnetCommand = "dotnet")
    {
        this.profileDirectory = Path.GetFullPath(profileDirectory);
        packagesDirectory = Path.Combine(this.profileDirectory, ".cordis", "packages");
        workDirectory = Path.Combine(this.profileDirectory, ".cordis", "work");
        this.resolver = resolver;
        dotnet = dotnetCommand;
        this.sources = sources.Select(NormalizeSource).Distinct(StringComparer.Ordinal).ToArray();
        Bundles = new ReadOnlyDictionary<string, string>(bundles);
        if (PackageManifest
                .Read(Path.Combine(this.profileDirectory, "package.json"))
                .Raw.GetValueOrDefault("dependencies") is IDictionary<string, object?> dependencies)
            foreach (var (name, version) in dependencies)
            {
                // Profiles may also contain npm or host-owned dependencies. Only this adapter's
                // exact NuGet identities can name one of its managed deployment directories.
                if (version is not string exactVersion || !ValidIdentity(name, exactVersion))
                    continue;
                var directory = PackageDirectory(name, exactVersion);
                if (!Directory.Exists(directory))
                    continue;
                RequireOwned(directory, this.profileDirectory);
                Register(name, directory);
            }
    }

    /// <summary>Supply this live read-only map as ProfileLaunch.LocalBundles. Retired routes remain reserved until restart.</summary>
    public IReadOnlyDictionary<string, string> Bundles
    {
        get;
    }

    /// <summary>The host-approved sources. Inspect/prepare never fall back to machine-wide NuGet configuration.</summary>
    public IReadOnlyList<string> Sources => Array.AsReadOnly(sources);

    /// <summary>NuGet identities ignore case; retain an existing profile's spelling for its manifest and bundle selection.</summary>
    public string ResolvePackageName(string name)
    {
        ValidateIdentity(name, "0.0.0");
        var dependencies =
            PackageManifest.Read(Path.Combine(profileDirectory, "package.json")).Raw.GetValueOrDefault("dependencies")
                as IDictionary<string, object?>;
        return bundles
            .Keys.Concat(dependencies?.Keys ?? [])
            .FirstOrDefault(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>Map a NuGet package name to its stable compatibility key, preserving exact version text elsewhere.</summary>
    /// <remarks>Assign explicitly to ProfileLaunch.CompatibilityPackageName for a NuGet host. Scoped npm names
    /// and other non-NuGet names are unchanged; the default DSH policy never uses this adapter implicitly.</remarks>
    public static string NormalizeCompatibilityPackageName(string name) =>
        ValidIdentity(name, "0.0.0") ? name.ToLowerInvariant() : name;

    /// <summary>Read metadata for this adapter's registered NuGet modules before importing their code.</summary>
    /// <remarks>Supply as ProfileLaunch.ManifestLocator when enabling DSH admission. It reads deployed manifests,
    /// including their original package-name spelling; it does not load assemblies or rewrite metadata.</remarks>
    public PackageManifest? LocateManifest(string specifier, Uri parent)
    {
        if (specifier.StartsWith("nuget:", StringComparison.Ordinal))
        {
            var name = specifier["nuget:".Length..].Split('/')[0];
            if (bundles.TryGetValue(name, out var directory))
                return PackageManifest.Read(Path.Combine(directory, "package.json"));
        }

        return DshProfilePolicy.LocateManifest(specifier, parent);
    }

    /// <inheritdoc />
    public async Task<PackageInspection> InspectAsync(
        PackageRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(request.Name, request.Version);
        var bytes = await AcquireAsync(request, cancellationToken);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        var metadata = ReadMetadata(archive, request);
        var manifest = new EntryOptions
        {
            ["name"] = request.Name,
            ["version"] = request.Version
        };
        if (metadata.TryGetValue("peerDependencies", out var peers))
            manifest["peerDependencies"] = peers;
        return new(
            request,
            Convert.ToHexString(SHA256.HashData(bytes)),
            metadata.GetValueOrDefault("description") as string ?? "",
            true)
        {
            ManifestJson = ConfigurationFile.Write(manifest, true),
        };
    }

    /// <summary>Query available exact versions from the selected feed without executing package targets.</summary>
    public async Task<IReadOnlyList<string>> VersionsAsync(
        string name,
        string source,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(name, "0.0.0");
        source = SelectedSource(source);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            var versions = new List<string>();
            foreach (var file in Directory.EnumerateFiles(source, "*.nupkg"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var archive = ZipFile.OpenRead(file);
                var identity = ReadIdentity(archive);
                if (string.Equals(identity.Name, name, StringComparison.OrdinalIgnoreCase))
                    versions.Add(identity.Version);
            }

            return versions.AsReadOnly();
        }

        var address = await PackageBaseAddressAsync(uri, cancellationToken);
        using var document = JsonDocument.Parse(
            await http.GetByteArrayAsync(new Uri(address, name.ToLowerInvariant() + "/index.json"), cancellationToken));
        return document
            .RootElement.GetProperty("versions")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<PreparedPackage> PrepareAsync(
        PackageInspection inspection,
        bool buildApproved,
        Action<string> output,
        CancellationToken cancellationToken = default)
    {
        if (!buildApproved)
            throw new InvalidOperationException("Package SDK preparation requires host build-execution approval.");
        var request = inspection.Request;
        ValidateIdentity(request.Name, request.Version);
        if (retired.Contains(request.Name))
            throw new DeploymentRestartRequiredException(
                "A retired package identity requires a fresh resolver before reinstalling.");
        var bytes = await AcquireAsync(request, cancellationToken);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != inspection.ContentHash)
            throw new InvalidOperationException("Package contents changed after inspection.");
        using var archive = new ZipArchive(new MemoryStream(bytes));
        var metadata = ReadMetadata(archive, request);
        var work = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Every process writes its own staging tree. A crashed owner cannot leave a child writing a later operation's output.
            var feed = Directory.CreateDirectory(Path.Combine(work, "feed")).FullName;
            await File.WriteAllBytesAsync(
                Path.Combine(feed, request.Name + "." + request.Version + ".nupkg"),
                bytes,
                cancellationToken);
            var project = new XDocument(
                new XElement(
                    "Project",
                    new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                    new XElement(
                        "PropertyGroup",
                        new XElement("TargetFramework", "net10.0"),
                        new XElement("CopyLocalLockFileAssemblies", "true"),
                        new XElement("ManagePackageVersionsCentrally", "false"),
                        new XElement("ImportDirectoryBuildProps", "false"),
                        new XElement("ImportDirectoryBuildTargets", "false"),
                        new XElement("RestorePackagesPath", Path.Combine(work, "cache"))),
                    new XElement(
                        "ItemGroup",
                        new XElement(
                            "PackageReference",
                            new XAttribute("Include", request.Name),
                            new XAttribute("Version", request.Version)))));
            var projectPath = Path.Combine(work, "PluginDeployment.csproj");
            await File.WriteAllTextAsync(projectPath, project.ToString(), cancellationToken);
            var configuration = new XDocument(
                new XElement(
                    "configuration",
                    new XElement(
                        "packageSources",
                        new XElement("clear"),
                        new[] { feed }
                            .Concat(sources)
                            .Select((value, index) => new XElement(
                                "add",
                                new XAttribute("key", "source" + index),
                                new XAttribute("value", value)))),
                    new XElement(
                        "packageSourceMapping",
                        new XElement("clear"),
                        new XElement(
                            "packageSource",
                            new XAttribute("key", "source0"),
                            new XElement("package", new XAttribute("pattern", request.Name))),
                        sources.Select((_, index) => new XElement(
                            "packageSource",
                            new XAttribute("key", "source" + (index + 1)),
                            new XElement("package", new XAttribute("pattern", "*")))))));
            await File.WriteAllTextAsync(
                Path.Combine(work, "NuGet.Config"),
                configuration.ToString(),
                cancellationToken);
            var outputDirectory = Path.Combine(work, "bundle");
            await DotnetPackageProcess.RunAsync(
                dotnet,
                [
                    "publish",
                    projectPath,
                    "-c",
                    "Release",
                    "-o",
                    outputDirectory,
                    "--disable-build-servers",
                    "--configfile",
                    Path.Combine(work, "NuGet.Config"),
                    "-p:ImportDirectoryBuildProps=false",
                    "-p:ImportDirectoryBuildTargets=false",
                    "-p:ManagePackageVersionsCentrally=false",
                    "-p:UseSharedCompilation=false"
                ],
                work,
                Path.Combine(profileDirectory, ".cordis", "package-run.json"),
                output,
                TimeSpan.FromMinutes(10),
                cancellationToken);
            var restoredArchive = Path.Combine(
                work,
                "cache",
                request.Name.ToLowerInvariant(),
                request.Version.ToLowerInvariant(),
                request.Name.ToLowerInvariant() + "." + request.Version.ToLowerInvariant() + ".nupkg");
            if (Convert.ToHexString(
                    SHA256.HashData(await File.ReadAllBytesAsync(restoredArchive, cancellationToken))) !=
                inspection.ContentHash)
                throw new InvalidOperationException("The restored root package differs from the inspected archive.");
            var assembly = RequiredString(metadata, "assembly");
            _ = ReadExports(metadata);
            if (Path.GetFileName(assembly) != assembly ||
                !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(outputDirectory, assembly)))
                throw new FormatException("The declared plugin assembly must be a published DLL filename.");
            var patches = metadata.GetValueOrDefault("patches") is IEnumerable<object?> rows
                ? Data.Entries(rows)
                : new List<EntryOptions>
                {
                    new()
                    {
                        ["insert"] = new List<object?>
                        {
                            new EntryOptions
                            {
                                Id = request.Name.ToLowerInvariant(),
                                Name = ModuleName(request.Name),
                                Config = new EntryOptions()
                            }
                        }
                    }
                };
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "cordis.patch.yml"),
                ConfigurationFile.Write(patches),
                cancellationToken);
            var manifest = new EntryOptions
            {
                ["name"] = request.Name,
                ["version"] = request.Version,
                ["description"] = inspection.Description,
                ["dsh"] = new EntryOptions
                {
                    ["bundle"] = new EntryOptions
                    {
                        ["patch"] = "cordis.patch.yml"
                    }
                },
            };
            if (metadata.TryGetValue("peerDependencies", out var peers))
                manifest["peerDependencies"] = peers;
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "package.json"),
                ConfigurationFile.Write(manifest, true),
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "cordis.plugin.json"),
                ConfigurationFile.Write(metadata, true),
                cancellationToken);
            // ADR starts from the declared plugin DLL, not the SDK's preparation project.
            File.Copy(
                Path.Combine(outputDirectory, "PluginDeployment.deps.json"),
                Path.Combine(outputDirectory, Path.ChangeExtension(assembly, ".deps.json")),
                overwrite: true);
            return new(request.Name, request.Version, outputDirectory)
            {
                PublicationDirectory = PackageDirectory(request.Name, request.Version)
            };
        }
        catch (Exception error) when (error is not PackageToolException)
        {
            throw new PackageToolException(error.Message, work, cancelled: error is OperationCanceledException);
        }
    }

    /// <inheritdoc />
    public Task PublishAsync(PreparedPackage package, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(package.Name, package.Version);
        RequireOwned(package.Directory, workDirectory);
        var destination = PackageDirectory(package.Name, package.Version);
        var receipt = BundleFiles.ReceiptPath(destination);
        RequireOwned(destination, packagesDirectory);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new PackageToolException(
                "A deployment directory already exists; inspect its residual state before retrying.",
                destination);
        if (File.Exists(receipt) || Directory.Exists(receipt))
            throw new PackageToolException(
                "A deployment receipt already exists; inspect its residual state before retrying.",
                receipt);
        var published = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(package.Directory, destination);
            published = true;
            BundleFiles.Record(destination);
            // Existing routes stay bound to their running version until a fresh resolver reads the saved dependency.
            if (!bundles.ContainsKey(package.Name))
                Register(package.Name, destination);
            else
                BundleFiles.Verify(destination);
        }
        catch (Exception error)
        {
            // Files may already have moved even when route registration fails. Preserve the real
            // location so callers can report a partially published deployment without retrying it.
            throw new PackageToolException(
                error.Message,
                published ? destination : package.Directory,
                published: published);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!bundles.TryGetValue(name, out var directory))
            throw new KeyNotFoundException("No profile-owned CLR deployment is registered.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwned(directory, packagesDirectory);
        var metadata = (EntryOptions)ConfigurationFile.Parse(
            File.ReadAllText(Path.Combine(directory, "cordis.plugin.json")),
            true)!;
        foreach (var (subpath, _) in ReadExports(metadata))
        {
            // Once withdrawal begins, finish every export even if the caller cancels.
            await resolver.RemoveAsync(ModuleName(name, subpath), CancellationToken.None);
        }

        retired.Add(name);
        // Keep both artifact and route: other processes, lazy loads and Workers may still use the files.
    }

    private void Register(string name, string directory)
    {
        BundleFiles.Verify(directory);
        var identity = PackageManifest.Read(Path.Combine(directory, "package.json"));
        if (!string.Equals(
                identity.Raw.GetValueOrDefault("name") as string,
                name,
                StringComparison.OrdinalIgnoreCase) ||
            identity.Raw.GetValueOrDefault("version") is not string version ||
            !string.Equals(
                Path.GetFullPath(directory),
                PackageDirectory(name, version),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The deployed package identity differs from its version directory.");
        var metadata = (EntryOptions)ConfigurationFile.Parse(
            File.ReadAllText(Path.Combine(directory, "cordis.plugin.json")),
            true)!;
        foreach (var (subpath, entryType) in ReadExports(metadata))
            resolver.Register(
                ModuleName(name, subpath),
                new(directory, RequiredString(metadata, "assembly"), entryType));
        bundles.Add(name, directory);
    }

    /// <summary>Physically delete one unreferenced version after all hosts, Workers and other file consumers have stopped.</summary>
    /// <remarks>This is an explicit offline deployment operation, not an ALC cleanup hook. It refuses an active
    /// profile writer under the existing lock protocol. The caller excludes other consumers and uncoordinated writers.
    /// A still-recorded version is refused. Failure may
    /// leave files and reports the retained path; no GC, automatic scan or whole-filesystem rollback occurs.</remarks>
    public static Task DeleteRetainedArtifactAsync(
        string profileDirectory,
        string name,
        string version,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateIdentity(name, version);
        var profile = Path.GetFullPath(profileDirectory);
        var directory = Path.Combine(
            profile,
            ".cordis",
            "packages",
            name.ToLowerInvariant(),
            version.ToLowerInvariant());
        RequireOwned(directory, profile);
        var receipt = BundleFiles.ReceiptPath(directory);
        RequireOwned(receipt, profile);
        var manifestPath = Path.Combine(profile, "package.json");
        using var writer = new FileStream(
            manifestPath + ".cordis-lock",
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            1,
            FileOptions.DeleteOnClose);
        var manifest = PackageManifest.Read(manifestPath).Raw;
        if (manifest.TryGetValue("dependencies", out var declaration))
        {
            if (declaration is not IDictionary<string, object?> dependencies)
                throw new FormatException("Profile dependencies must be an object when declared.");
            foreach (var (dependencyName, dependencyValue) in dependencies)
            {
                if (!string.Equals(dependencyName, name, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (dependencyValue is not string dependencyVersion)
                    throw new FormatException("The package dependency must declare a version string.");
                if (string.Equals(dependencyVersion, version, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The profile still references this package version.");
            }
        }

        try
        {
            if (File.Exists(directory))
                throw new IOException("The retained artifact path is not a directory.");
            if (Directory.Exists(directory))
            {
                BundleFiles.ValidateTree(directory);
                Directory.Delete(directory, recursive: true);
            }

            File.Delete(receipt);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new PackageToolException(
                error.Message,
                Directory.Exists(directory) || File.Exists(directory) ? directory : receipt);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetRetainedDirectories(string name)
    {
        ValidateIdentity(name, "0.0.0");
        var directory = Path.Combine(packagesDirectory, name.ToLowerInvariant());
        RequireOwned(directory, profileDirectory);
        return Directory.Exists(directory)
            ? Directory.GetDirectories(directory).Order(StringComparer.Ordinal).ToArray()
            : [];
    }

    private async Task<byte[]> AcquireAsync(PackageRequest request, CancellationToken token)
    {
        var source = SelectedSource(request.Source);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.IsFile)
        {
            foreach (var file in Directory.EnumerateFiles(source, "*.nupkg"))
            {
                token.ThrowIfCancellationRequested();
                using var archive = ZipFile.OpenRead(file);
                var identity = ReadIdentity(archive);
                if (string.Equals(identity.Name, request.Name, StringComparison.OrdinalIgnoreCase) &&
                    identity.Version == request.Version)
                    return await File.ReadAllBytesAsync(file, token);
            }

            throw new FileNotFoundException(
                $"Package {request.Name}@{request.Version} is absent from the selected feed.");
        }

        var address = await PackageBaseAddressAsync(uri, token);
        var name = request.Name.ToLowerInvariant();
        var version = request.Version.ToLowerInvariant();
        return await http.GetByteArrayAsync(new Uri(address, $"{name}/{version}/{name}.{version}.nupkg"), token);
    }

    private async Task<Uri> PackageBaseAddressAsync(Uri source, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await http.GetByteArrayAsync(source, token));
        var resource = document
            .RootElement.GetProperty("resources")
            .EnumerateArray()
            .First(value => value.GetProperty("@type").GetString() == "PackageBaseAddress/3.0.0");
        var address = new Uri(resource.GetProperty("@id").GetString()!);
        if (address.Scheme is not ("https" or "http"))
            throw new FormatException("NuGet resources must use HTTP(S).");
        return address;
    }

    private string SelectedSource(string source)
    {
        source = NormalizeSource(source);
        if (!sources.Contains(source, StringComparer.Ordinal))
            throw new ArgumentException("The source is not in the host-selected source list.", nameof(source));
        return source;
    }

    private static string NormalizeSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile
            ? uri.Scheme is "https" or "http"
                ? uri.AbsoluteUri
                : throw new ArgumentException("A feed must be a directory or HTTP(S) NuGet v3 index.")
            : Path.GetFullPath(source);

    private static (string Name, string Version) ReadIdentity(ZipArchive archive)
    {
        var entry = archive.Entries.Single(item =>
            item.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        var metadata = XDocument.Load(stream).Root!.Elements().Single(element => element.Name.LocalName == "metadata");
        return (metadata.Elements().Single(element => element.Name.LocalName == "id").Value,
            metadata.Elements().Single(element => element.Name.LocalName == "version").Value);
    }

    private static EntryOptions ReadMetadata(ZipArchive archive, PackageRequest request)
    {
        var identity = ReadIdentity(archive);
        if (!string.Equals(identity.Name, request.Name, StringComparison.OrdinalIgnoreCase) ||
            identity.Version != request.Version)
            throw new FormatException("The package identity differs from the requested exact version.");
        var entry = archive.GetEntry("cordis.plugin.json") ??
            throw new FormatException("The NuGet package declares no cordis.plugin.json.");
        using var reader = new StreamReader(entry.Open());
        var metadata = ConfigurationFile.Parse(reader.ReadToEnd(), true) as EntryOptions ??
            throw new FormatException("Plugin metadata must be an object.");
        _ = RequiredString(metadata, "assembly");
        _ = ReadExports(metadata);
        return metadata;
    }

    private static string RequiredString(EntryOptions metadata, string key) =>
        metadata.GetValueOrDefault(key) is string value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new FormatException($"Plugin metadata requires {key}.");

    private static IReadOnlyList<(string Subpath, string EntryType)> ReadExports(EntryOptions metadata)
    {
        var result = new List<(string Subpath, string EntryType)>
        {
            (".", RequiredString(metadata, "entryType"))
        };
        if (!metadata.TryGetValue("exports", out var exports))
            return result;
        if (exports is not IDictionary<string, object?> entries)
            throw new FormatException("Plugin exports must map explicit './subpath' names to CLR entry type names.");
        foreach (var (subpath, entryType) in entries)
        {
            if (!subpath.StartsWith("./", StringComparison.Ordinal) ||
                subpath[2..].Split('/').Any(segment => string.IsNullOrWhiteSpace(segment) || segment is "." or "..") ||
                subpath.IndexOfAny(['\\', '?', '#', ':']) >= 0 ||
                entryType is not string type || string.IsNullOrWhiteSpace(type))
                throw new FormatException(
                    "Plugin exports require explicit './subpath' names and nonempty CLR entry type names.");
            result.Add((subpath, type));
        }

        return result;
    }

    private static string ModuleName(string name, string subpath = ".") =>
        "nuget:" + name.ToLowerInvariant() + (subpath == "." ? "" : subpath[1..]);

    private string PackageDirectory(string name, string version)
    {
        ValidateIdentity(name, version);
        return Path.Combine(packagesDirectory, name.ToLowerInvariant(), version.ToLowerInvariant());
    }

    private static void ValidateIdentity(string name, string version)
    {
        if (!ValidIdentity(name, version))
            throw new ArgumentException("An exact NuGet package ID and three-part version are required.");
    }

    private static bool ValidIdentity(string name, string version) =>
        Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant) && Regex.IsMatch(
            version,
            @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$",
            RegexOptions.CultureInvariant);

    private static void RequireOwned(string directory, string owner)
    {
        var path = Path.GetFullPath(directory);
        var relative = Path.GetRelativePath(Path.GetFullPath(owner), path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new InvalidOperationException("A deployment operation escaped its owned directory.");
        for (var current = new DirectoryInfo(path);current is not null;current = current.Parent)
        {
            if (current.LinkTarget is not null)
                throw new InvalidOperationException("Deployment directories may not traverse symbolic links.");
            if (current.FullName == Path.GetFullPath(owner))
                break;
        }
    }

    /// <summary>Close source connections. The borrowed CLR resolver and session must be disposed by their owner.</summary>
    public void Dispose() => http.Dispose();
}
