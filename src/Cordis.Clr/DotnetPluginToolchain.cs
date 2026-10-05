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
[RequiresDynamicCode("NuGet plugin deployment uses the ordinary CLR; Native AOT applications must republish static plugins.")]
[RequiresUnreferencedCode("External plugin entry types are explicitly named in package metadata.")]
public sealed class DotnetPluginToolchain : IProfilePackageToolchain, IDisposable
{
    private readonly string profileDirectory;
    private readonly string packagesDirectory;
    private readonly string workDirectory;
    private readonly ClrModuleResolver resolver;
    private readonly string dotnet;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly Dictionary<string, string> bundles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] sources;

    /// <summary>Initialize explicit routes for installed packages recorded in the profile, without loading their code.</summary>
    public DotnetPluginToolchain(string profileDirectory, ClrModuleResolver resolver, IEnumerable<string> sources, string dotnetCommand = "dotnet")
    {
        this.profileDirectory = Path.GetFullPath(profileDirectory);
        packagesDirectory = Path.Combine(this.profileDirectory, ".cordis", "packages");
        workDirectory = Path.Combine(this.profileDirectory, ".cordis", "work");
        this.resolver = resolver;
        dotnet = dotnetCommand;
        this.sources = sources.Select(NormalizeSource).Distinct(StringComparer.Ordinal).ToArray();
        if (this.sources.Length == 0) throw new ArgumentException("At least one explicitly selected NuGet source is required.", nameof(sources));
        Bundles = new ReadOnlyDictionary<string, string>(bundles);
        if (PackageManifest.Read(Path.Combine(this.profileDirectory, "package.json")).Raw.GetValueOrDefault("dependencies") is IDictionary<string, object?> dependencies)
            foreach (var (name, version) in dependencies)
            {
                // Profiles may also contain npm or host-owned dependencies. Only this adapter's
                // exact NuGet identities can name one of its managed deployment directories.
                if (version is not string exactVersion || !ValidIdentity(name, exactVersion)) continue;
                var directory = PackageDirectory(name, exactVersion);
                if (!File.Exists(Path.Combine(directory, "cordis.plugin.json"))) continue;
                Register(name, directory);
            }
    }

    /// <summary>Supply this live read-only map as ProfileLaunch.LocalBundles. Retired routes remain reserved until restart.</summary>
    public IReadOnlyDictionary<string, string> Bundles { get; }
    /// <summary>The host-approved sources. Inspect/prepare never fall back to machine-wide NuGet configuration.</summary>
    public IReadOnlyList<string> Sources => Array.AsReadOnly(sources);

    /// <summary>NuGet identities ignore case; retain an existing profile's spelling for its manifest and bundle selection.</summary>
    public string ResolvePackageName(string name)
    {
        ValidateIdentity(name, "0.0.0");
        var dependencies = PackageManifest.Read(Path.Combine(profileDirectory, "package.json")).Raw
            .GetValueOrDefault("dependencies") as IDictionary<string, object?>;
        return bundles.Keys.Concat(dependencies?.Keys ?? []).FirstOrDefault(
            existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)) ?? name;
    }

    /// <summary>Map a NuGet package name to its stable compatibility key, preserving exact version text elsewhere.</summary>
    /// <remarks>Assign explicitly to ProfileLaunch.CompatibilityPackageName for a NuGet host. Scoped npm names
    /// and other non-NuGet names are unchanged; the default DSH policy never uses this adapter implicitly.</remarks>
    public static string NormalizeCompatibilityPackageName(string name)
        => ValidIdentity(name, "0.0.0") ? name.ToLowerInvariant() : name;

    /// <summary>Read metadata for this adapter's registered NuGet modules before importing their code.</summary>
    /// <remarks>Supply as ProfileLaunch.ManifestLocator when enabling DSH admission. It reads deployed manifests,
    /// including their original package-name spelling; it does not load assemblies or rewrite metadata.</remarks>
    public PackageManifest? LocateManifest(string specifier, Uri parent)
    {
        if (specifier.StartsWith("nuget:", StringComparison.Ordinal)
            && bundles.TryGetValue(specifier["nuget:".Length..], out var directory))
            return PackageManifest.Read(Path.Combine(directory, "package.json"));
        return DshProfilePolicy.LocateManifest(specifier, parent);
    }

    /// <inheritdoc />
    public async Task<PackageInspection> InspectAsync(PackageRequest request, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(request.Name, request.Version);
        var bytes = await AcquireAsync(request, cancellationToken);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        var metadata = ReadMetadata(archive, request);
        var manifest = new EntryOptions { ["name"] = request.Name, ["version"] = request.Version };
        if (metadata.TryGetValue("peerDependencies", out var peers)) manifest["peerDependencies"] = peers;
        return new(request, Convert.ToHexString(SHA256.HashData(bytes)), metadata.GetValueOrDefault("description") as string ?? "", true)
        {
            ManifestJson = ConfigurationFile.Write(manifest, true),
        };
    }

    /// <summary>Query available exact versions from the selected feed without executing package targets.</summary>
    public async Task<IReadOnlyList<string>> VersionsAsync(string name, string source, CancellationToken cancellationToken = default)
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
                if (string.Equals(identity.Name, name, StringComparison.OrdinalIgnoreCase)) versions.Add(identity.Version);
            }
            return versions.AsReadOnly();
        }
        var address = await PackageBaseAddressAsync(uri, cancellationToken);
        using var document = JsonDocument.Parse(await http.GetByteArrayAsync(new Uri(address, name.ToLowerInvariant() + "/index.json"), cancellationToken));
        return document.RootElement.GetProperty("versions").EnumerateArray().Select(value => value.GetString()!).ToArray();
    }

    /// <inheritdoc />
    public async Task<PreparedPackage> PrepareAsync(PackageInspection inspection, bool buildApproved, Action<string> output,
        CancellationToken cancellationToken = default)
    {
        if (!buildApproved) throw new InvalidOperationException("Package SDK preparation requires host build-execution approval.");
        var request = inspection.Request;
        ValidateIdentity(request.Name, request.Version);
        if (retired.Contains(request.Name)) throw new DeploymentRestartRequiredException("A retired package identity requires a fresh resolver before reinstalling.");
        var bytes = await AcquireAsync(request, cancellationToken);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != inspection.ContentHash) throw new InvalidOperationException("Package contents changed after inspection.");
        using var archive = new ZipArchive(new MemoryStream(bytes));
        var metadata = ReadMetadata(archive, request);
        var work = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Every process writes its own staging tree. A crashed owner cannot leave a child writing a later operation's output.
            var feed = Directory.CreateDirectory(Path.Combine(work, "feed")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(feed, request.Name + "." + request.Version + ".nupkg"), bytes, cancellationToken);
            var project = new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("CopyLocalLockFileAssemblies", "true"),
                    new XElement("ManagePackageVersionsCentrally", "false"), new XElement("ImportDirectoryBuildProps", "false"),
                    new XElement("ImportDirectoryBuildTargets", "false"), new XElement("RestorePackagesPath", Path.Combine(work, "cache"))),
                new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", request.Name), new XAttribute("Version", request.Version)))));
            var projectPath = Path.Combine(work, "PluginDeployment.csproj");
            await File.WriteAllTextAsync(projectPath, project.ToString(), cancellationToken);
            var configuration = new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear"),
                new[] { feed }.Concat(sources).Select((value, index) => new XElement("add", new XAttribute("key", "source" + index), new XAttribute("value", value)))),
                new XElement("packageSourceMapping", new XElement("clear"),
                    new XElement("packageSource", new XAttribute("key", "source0"), new XElement("package", new XAttribute("pattern", request.Name))),
                    sources.Select((_, index) => new XElement("packageSource", new XAttribute("key", "source" + (index + 1)),
                        new XElement("package", new XAttribute("pattern", "*")))))));
            await File.WriteAllTextAsync(Path.Combine(work, "NuGet.Config"), configuration.ToString(), cancellationToken);
            var outputDirectory = Path.Combine(work, "bundle");
            await DotnetPackageProcess.RunAsync(dotnet, ["publish", projectPath, "-c", "Release", "-o", outputDirectory, "--disable-build-servers",
                "--configfile", Path.Combine(work, "NuGet.Config"), "-p:ImportDirectoryBuildProps=false", "-p:ImportDirectoryBuildTargets=false",
                "-p:ManagePackageVersionsCentrally=false", "-p:UseSharedCompilation=false"], work,
                Path.Combine(profileDirectory, ".cordis", "package-run.json"), output, TimeSpan.FromMinutes(10), cancellationToken);
            var restoredArchive = Path.Combine(work, "cache", request.Name.ToLowerInvariant(), request.Version.ToLowerInvariant(),
                request.Name.ToLowerInvariant() + "." + request.Version.ToLowerInvariant() + ".nupkg");
            if (Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(restoredArchive, cancellationToken))) != inspection.ContentHash)
                throw new InvalidOperationException("The restored root package differs from the inspected archive.");
            var assembly = RequiredString(metadata, "assembly");
            var entryType = RequiredString(metadata, "entryType");
            if (Path.GetFileName(assembly) != assembly || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(outputDirectory, assembly))) throw new FormatException("The declared plugin assembly must be a published DLL filename.");
            var patches = metadata.GetValueOrDefault("patches") is IEnumerable<object?> rows ? Data.Entries(rows)
                : new List<EntryOptions> { new() { ["insert"] = new List<object?>
                    { new EntryOptions { Id = request.Name.ToLowerInvariant(), Name = ModuleName(request.Name), Config = new EntryOptions() } } } };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "cordis.patch.yml"), ConfigurationFile.Write(patches), cancellationToken);
            var manifest = new EntryOptions
            {
                ["name"] = request.Name, ["version"] = request.Version,
                ["description"] = inspection.Description,
                ["dsh"] = new EntryOptions { ["bundle"] = new EntryOptions { ["patch"] = "cordis.patch.yml" } },
            };
            if (metadata.TryGetValue("peerDependencies", out var peers)) manifest["peerDependencies"] = peers;
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "package.json"), ConfigurationFile.Write(manifest, true), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "cordis.plugin.json"), ConfigurationFile.Write(metadata, true), cancellationToken);
            return new(request.Name, request.Version, outputDirectory);
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
        if (bundles.ContainsKey(package.Name)) throw new DeploymentRestartRequiredException("This process already routed the package identity; replace through CLR HMR or restart.");
        RequireOwned(package.Directory, workDirectory);
        var destination = PackageDirectory(package.Name, package.Version);
        if (Directory.Exists(destination)) throw new PackageToolException("A deployment directory already exists; inspect its residual state before retrying.", destination);
        var published = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(package.Directory, destination);
            published = true;
            Register(package.Name, destination);
        }
        catch (Exception error)
        {
            // Files may already have moved even when route registration fails. Preserve the real
            // location so callers can report a partially published deployment without retrying it.
            throw new PackageToolException(error.Message, published ? destination : package.Directory, published: published);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!bundles.TryGetValue(name, out var directory)) throw new KeyNotFoundException("No profile-owned CLR deployment is registered.");
        cancellationToken.ThrowIfCancellationRequested();
        RequireOwned(directory, packagesDirectory);
        await resolver.RemoveAsync(ModuleName(name), cancellationToken);
        retired.Add(name);
        Directory.Delete(directory, recursive: true);
        // Keep the route reserved for the life of this resolver. Removing it from ProfileSession's map would change its generation.
    }

    private void Register(string name, string directory)
    {
        var metadata = (EntryOptions)ConfigurationFile.Parse(File.ReadAllText(Path.Combine(directory, "cordis.plugin.json")), true)!;
        resolver.Register(ModuleName(name), new(directory, RequiredString(metadata, "assembly"), RequiredString(metadata, "entryType")));
        bundles.Add(name, directory);
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
                if (string.Equals(identity.Name, request.Name, StringComparison.OrdinalIgnoreCase) && identity.Version == request.Version)
                    return await File.ReadAllBytesAsync(file, token);
            }
            throw new FileNotFoundException($"Package {request.Name}@{request.Version} is absent from the selected feed.");
        }
        var address = await PackageBaseAddressAsync(uri, token);
        var name = request.Name.ToLowerInvariant();
        var version = request.Version.ToLowerInvariant();
        return await http.GetByteArrayAsync(new Uri(address, $"{name}/{version}/{name}.{version}.nupkg"), token);
    }

    private async Task<Uri> PackageBaseAddressAsync(Uri source, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await http.GetByteArrayAsync(source, token));
        var resource = document.RootElement.GetProperty("resources").EnumerateArray()
            .First(value => value.GetProperty("@type").GetString() == "PackageBaseAddress/3.0.0");
        var address = new Uri(resource.GetProperty("@id").GetString()!);
        if (address.Scheme is not ("https" or "http")) throw new FormatException("NuGet resources must use HTTP(S).");
        return address;
    }

    private string SelectedSource(string source)
    {
        source = NormalizeSource(source);
        if (!sources.Contains(source, StringComparer.Ordinal)) throw new ArgumentException("The source is not in the host-selected source list.", nameof(source));
        return source;
    }

    private static string NormalizeSource(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile
        ? uri.Scheme is "https" or "http" ? uri.AbsoluteUri : throw new ArgumentException("A feed must be a directory or HTTP(S) NuGet v3 index.")
        : Path.GetFullPath(source);

    private static (string Name, string Version) ReadIdentity(ZipArchive archive)
    {
        var entry = archive.Entries.Single(item => item.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        var metadata = XDocument.Load(stream).Root!.Elements().Single(element => element.Name.LocalName == "metadata");
        return (metadata.Elements().Single(element => element.Name.LocalName == "id").Value,
            metadata.Elements().Single(element => element.Name.LocalName == "version").Value);
    }

    private static EntryOptions ReadMetadata(ZipArchive archive, PackageRequest request)
    {
        var identity = ReadIdentity(archive);
        if (!string.Equals(identity.Name, request.Name, StringComparison.OrdinalIgnoreCase) || identity.Version != request.Version)
            throw new FormatException("The package identity differs from the requested exact version.");
        var entry = archive.GetEntry("cordis.plugin.json") ?? throw new FormatException("The NuGet package declares no cordis.plugin.json.");
        using var reader = new StreamReader(entry.Open());
        var metadata = ConfigurationFile.Parse(reader.ReadToEnd(), true) as EntryOptions ?? throw new FormatException("Plugin metadata must be an object.");
        _ = RequiredString(metadata, "assembly");
        _ = RequiredString(metadata, "entryType");
        return metadata;
    }

    private static string RequiredString(EntryOptions metadata, string key) => metadata.GetValueOrDefault(key) is string value && !string.IsNullOrWhiteSpace(value)
        ? value : throw new FormatException($"Plugin metadata requires {key}.");
    private static string ModuleName(string name) => "nuget:" + name.ToLowerInvariant();
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

    private static bool ValidIdentity(string name, string version) => Regex.IsMatch(name, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant)
        && Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant);

    private static void RequireOwned(string directory, string owner)
    {
        var path = Path.GetFullPath(directory);
        var relative = Path.GetRelativePath(Path.GetFullPath(owner), path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("A deployment operation escaped its owned directory.");
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null) throw new InvalidOperationException("Deployment directories may not traverse symbolic links.");
            if (current.FullName == Path.GetFullPath(owner)) break;
        }
    }

    /// <summary>Close source connections. The borrowed CLR resolver and session must be disposed by their owner.</summary>
    public void Dispose() => http.Dispose();
}
