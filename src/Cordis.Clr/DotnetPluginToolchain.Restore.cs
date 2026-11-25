using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Xml.Linq;
using Cordis.Composition;

namespace Cordis.Clr;

/// <summary>The dependency declaration and immutable output of one profile-owned NuGet restore graph.</summary>
public sealed record ProfileNuGetPackage(
    string Name,
    string Declaration,
    string Version,
    string Graph,
    string Directory,
    bool IsBundle);

public sealed partial class DotnetPluginToolchain
{
    /// <summary>Restore one root in isolation, inspect the resolved archive, and obtain approval before package build targets
    /// execute. The caller commits Declaration and Graph together in the profile. Preparation alone does not select a bundle.</summary>
    public async Task<ProfileNuGetPackage> PrepareProfilePackageAsync(
        string nameOrArchive,
        string? specification,
        string? lockedGraph,
        bool update,
        Func<PackageInspection, bool> approveBuild,
        Action<string> output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approveBuild);
        ArgumentNullException.ThrowIfNull(output);
        string? archive = null;
        var name = nameOrArchive;
        if (Path.IsPathFullyQualified(nameOrArchive) ||
            nameOrArchive.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            if (specification is not null || !nameOrArchive.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A local archive requires a .nupkg path without a version argument.");
            archive = Path.GetFullPath(nameOrArchive);
            using var zip = ZipFile.OpenRead(archive);
            (name, specification) = ReadIdentity(zip);
        }

        ValidateIdentity(name, "0.0.0");
        var requirement = RestoreRequirement(specification);
        string? savedLock = null;
        if (lockedGraph is not null)
        {
            ArgumentNullException.ThrowIfNull(specification);
            var retained = GraphDirectory(name, lockedGraph);
            savedLock = Path.Combine(retained, "restore", "packages.lock.json");
            if (!update && archive is null && Directory.Exists(Path.Combine(retained, "output", "bundle")))
                return ReadProfilePackage(name, specification, lockedGraph);
            if (File.Exists(savedLock))
                VerifyLock(savedLock, lockedGraph, requirement);
        }

        if (sources.Length == 0 && archive is null)
            throw new InvalidOperationException(
                "Acquiring a NuGet deployment requires an explicitly configured source.");
        var work = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        RequireOwned(work, profileDirectory);
        Directory.CreateDirectory(work);
        var lockPath = Path.Combine(work, "packages.lock.json");
        var completed = false;
        try
        {
            string? feed = null;
            string? expected = null;
            if (archive is not null)
            {
                feed = Directory.CreateDirectory(Path.Combine(work, "feed")).FullName;
                // Only the selected archive is admitted. Its siblings are not a package source.
                var copy = Path.Combine(feed, "root.nupkg");
                File.Copy(archive, copy);
                using (var zip = ZipFile.OpenRead(copy))
                    if (ReadIdentity(zip) != (name, specification))
                        throw new InvalidDataException("The local archive identity changed before restore.");
                using (var input = File.OpenRead(copy))
                    expected = Convert.ToHexString(SHA256.HashData(input));
                File.Move(copy, Path.Combine(feed, name + "." + specification + ".nupkg"));
            }

            await WriteNuGetConfigAsync(work, name, feed, cancellationToken);
            var project = await WriteRestoreProjectAsync(work, name, requirement, cancellationToken);
            var locked = !update && savedLock is not null && File.Exists(savedLock);
            if (locked)
                File.Copy(savedLock!, lockPath);
            await RestoreAsync(locked);
            var resolved = ReadRootLock(lockPath, name);
            if (specification is null)
            {
                // NuGet's stable floating selector chooses the version. Persist a genuinely exact lock for replay.
                specification = resolved.Version;
                requirement = RestoreRequirement(specification);
                await WriteRestoreProjectAsync(work, name, requirement, cancellationToken);
                await RestoreAsync(false);
                resolved = ReadRootLock(lockPath, name);
            }

            var graph = GraphIdentity(lockPath, requirement);
            if (lockedGraph is not null && !update && graph != lockedGraph)
                throw new InvalidDataException("Restore did not reproduce the profile's saved NuGet lock.");
            var destination = GraphDirectory(name, graph);
            var cache = Path.Combine(work, "cache", name.ToLowerInvariant(), resolved.Version.ToLowerInvariant());
            var nupkg = Path.Combine(
                cache,
                name.ToLowerInvariant() + "." + resolved.Version.ToLowerInvariant() + ".nupkg");
            using var stream = File.OpenRead(nupkg);
            using var provenance = JsonDocument.Parse(
                await File.ReadAllBytesAsync(Path.Combine(cache, ".nupkg.metadata"), cancellationToken));
            if (provenance.RootElement.GetProperty("contentHash").GetString() != resolved.ContentHash)
                throw new InvalidDataException("NuGet recorded different content hashes in its cache and lock.");
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (expected is not null && expected != hash)
                throw new InvalidDataException("NuGet restored different bytes from the selected local archive.");
            if (Directory.Exists(Path.Combine(destination, "output", "bundle")))
            {
                var retained = ReadProfilePackage(name, specification, graph);
                completed = true;
                return retained;
            }

            stream.Position = 0;
            using var package = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var identity = ReadIdentity(package);
            if (!name.Equals(identity.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The restored package ID differs from its dependency.");
            var origin = archive ?? SelectedSource(provenance.RootElement.GetProperty("source").GetString()!);
            var request = new PackageRequest(identity.Name, resolved.Version, origin);
            var metadata = package.GetEntry("cordis.plugin.json") is null
                ? null
                : ReadMetadata(
                    package,
                    request with
                    {
                        Version = identity.Version
                    });
            var inspection = CreateInspection(request, hash, metadata);
            if (!approveBuild(inspection))
                throw new InvalidOperationException(
                    $"Package SDK preparation for {name}@{resolved.Version} requires build-execution approval.");

            var staging = Directory.CreateDirectory(Path.Combine(work, "completed")).FullName;
            var restore = Directory.CreateDirectory(Path.Combine(staging, "restore")).FullName;
            File.Copy(lockPath, Path.Combine(restore, "packages.lock.json"));
            File.Copy(Path.Combine(work, "obj", "project.assets.json"), Path.Combine(restore, "project.assets.json"));
            BundleFiles.Record(restore);
            var bundle = Directory.CreateDirectory(Path.Combine(staging, "output", "bundle")).FullName;
            await RunSdkAsync(["publish", project, "--no-restore", "-c", "Release", "-o", bundle]);
            if (metadata is not null)
                await WritePluginBundleAsync(bundle, request, metadata, inspection.Description, cancellationToken);
            else
                await File.WriteAllTextAsync(
                    Path.Combine(bundle, "package.json"),
                    inspection.ManifestJson!,
                    cancellationToken);
            BundleFiles.Record(bundle);
            cancellationToken.ThrowIfCancellationRequested();
            PublishGraph(staging, destination, graph, requirement);

            var deployed = ReadProfilePackage(identity.Name, specification, graph);
            completed = true;
            return deployed;

            Task RestoreAsync(bool useLock) => RunSdkAsync(
            [
                "restore", project, "--configfile", Path.Combine(work, "NuGet.Config"),
                useLock ? "--locked-mode" : "--force-evaluate",
                "-p:ExcludeRestorePackageImports=true"
            ]);

            Task RunSdkAsync(string[] arguments) => DotnetPackageProcess.RunAsync(
                dotnet,
                [
                    .. arguments, "--disable-build-servers", "-p:ImportDirectoryBuildProps=false",
                    "-p:ImportDirectoryBuildTargets=false", "-p:ImportDirectoryPackagesProps=false",
                    "-p:ManagePackageVersionsCentrally=false",
                    "-p:UseSharedCompilation=false"
                ],
                work,
                Path.Combine(work, "package-run.json"),
                output,
                TimeSpan.FromMinutes(10),
                cancellationToken);
        }
        finally
        {
            if (completed)
                Directory.Delete(work, recursive: true);
        }
    }

    private static void PublishGraph(string staging, string destination, string graph, string requirement)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            Directory.Move(staging, destination);
            return;
        }
        catch (IOException) when (Directory.Exists(destination))
        {
            // Reuse a concurrent publication or restore the missing payload of a retained lock.
        }

        BundleFiles.Verify(Path.Combine(destination, "restore"));
        VerifyLock(Path.Combine(destination, "restore", "packages.lock.json"), graph, requirement);
        var output = Path.Combine(destination, "output");
        var prepared = Path.Combine(staging, "output");
        try
        {
            Directory.Move(prepared, output);
            return;
        }
        catch (IOException) when (Directory.Exists(output))
        {
            // An existing output's receipt binds even a repair to the formerly published files.
        }

        var bundle = Path.Combine(output, "bundle");
        var candidate = Path.Combine(prepared, "bundle");
        var receipt = BundleFiles.ReceiptPath(bundle);
        RequireOwned(receipt, destination);
        if (!File.ReadAllBytes(receipt).AsSpan().SequenceEqual(File.ReadAllBytes(BundleFiles.ReceiptPath(candidate))))
            throw new InvalidDataException("The restored bundle differs from the previously published files.");
        if (!Directory.Exists(bundle))
        {
            try
            {
                Directory.Move(candidate, bundle);
            }
            catch (IOException) when (Directory.Exists(bundle))
            {
                // ReadProfilePackage verifies every winner before exposing its path.
            }
        }
    }

    /// <summary>Read a retained verified deployment selected by an immutable lock in the profile.</summary>
    public ProfileNuGetPackage ReadProfilePackage(string name, string declaration, string graph)
    {
        var root = GraphDirectory(name, graph);
        var version = ReadLockedVersion(name, declaration, graph);
        var bundle = Path.Combine(root, "output", "bundle");
        RequireOwned(bundle, profileDirectory);
        BundleFiles.Verify(bundle);
        var manifest = PackageManifest.Read(Path.Combine(bundle, "package.json"));
        if (!string.Equals(
                manifest.Raw.GetValueOrDefault("name") as string,
                name,
                StringComparison.OrdinalIgnoreCase) ||
            manifest.Raw.GetValueOrDefault("version") as string != version)
            throw new InvalidDataException("The deployed bundle identity differs from the profile's NuGet lock.");
        return new(name, declaration, version, graph, bundle, manifest.HasBundleDeclaration);
    }

    /// <summary>Read the version resolved by the saved NuGet graph, even if its published runtime output is missing.</summary>
    public string ReadLockedVersion(string name, string declaration, string graph)
    {
        var restore = Path.Combine(GraphDirectory(name, graph), "restore");
        if (!Directory.Exists(restore))
            throw new DirectoryNotFoundException("The saved NuGet restore graph is missing for " + name);
        BundleFiles.Verify(restore);
        var path = Path.Combine(restore, "packages.lock.json");
        VerifyLock(path, graph, RestoreRequirement(declaration));
        return ReadRootLock(path, name).Version;
    }

    /// <summary>Ask the SDK for dependency chains in the retained restore graph. No Host or package imports are needed.</summary>
    public Task WhyAsync(
        string name,
        string declaration,
        string graph,
        string dependency,
        Action<string> output,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(dependency, "0.0.0");
        _ = ReadLockedVersion(name, declaration, graph);
        var root = GraphDirectory(name, graph);
        return DotnetPackageProcess.RunAsync(
            dotnet,
            ["nuget", "why", Path.Combine(root, "restore", "project.assets.json"), dependency],
            profileDirectory,
            Path.Combine(workDirectory, Guid.NewGuid().ToString("N") + ".json"),
            output,
            TimeSpan.FromMinutes(2),
            cancellationToken);
    }

    private string GraphDirectory(string name, string graph)
    {
        ValidateIdentity(name, "0.0.0");
        if (!IsGraph(graph))
            throw new ArgumentException("A SHA256 NuGet lock identity is required.", nameof(graph));
        var path = Path.Combine(profileDirectory, ".cordis", "nuget", name.ToLowerInvariant(), graph);
        RequireOwned(path, profileDirectory);
        return path;
    }

    private static bool IsGraph(string graph) => graph.Length == 64 && graph.All(char.IsAsciiHexDigitUpper);

    private static void VerifyLock(string path, string graph, string requirement)
    {
        if (GraphIdentity(path, requirement) != graph)
            throw new InvalidDataException(
                "The saved dependency and NuGet lock differ from the selected restore graph.");
    }

    private static string GraphIdentity(string path, string requirement)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(requirement + "\0"));
        hash.AppendData(File.ReadAllBytes(path));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static (string Version, string ContentHash) ReadRootLock(string path, string name)
    {
        using var file = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = file
            .RootElement.GetProperty("dependencies")
            .EnumerateObject()
            .Single()
            .Value
            .EnumerateObject()
            .Single(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (root.GetProperty("type").GetString() != "Direct")
            throw new InvalidDataException("The requested package is not the direct dependency of its restore graph.");
        return (root.GetProperty("resolved").GetString()!, root.GetProperty("contentHash").GetString()!);
    }

    private static string RestoreRequirement(string? specification)
    {
        if (specification is null)
            return "*";
        if (Regex.IsMatch(
                specification,
                @"\A[0-9]+(?:\.[0-9]+){0,3}(?:-[A-Za-z0-9.-]+)?(?:\+[A-Za-z0-9.-]+)?\z",
                RegexOptions.CultureInvariant))
            return "[" + specification + "]";
        if (Regex.IsMatch(
                specification,
                @"\A[\[(][A-Za-z0-9.,* +\-]+[\])]\z|\A[0-9*][A-Za-z0-9.*+\-]*\*[A-Za-z0-9.*+\-]*\z|\A\*\z",
                RegexOptions.CultureInvariant))
            return specification;
        throw new ArgumentException("Expected an exact NuGet version or a native NuGet range.", nameof(specification));
    }

    private static async Task<string> WriteRestoreProjectAsync(
        string work,
        string name,
        string requirement,
        CancellationToken token)
    {
        var project = new XDocument(
            new XElement(
                "Project",
                new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement(
                    "PropertyGroup",
                    new XElement("TargetFramework", "net10.0"),
                    new XElement("CopyLocalLockFileAssemblies", "true"),
                    new XElement("EnableDefaultItems", "false"),
                    new XElement("PathMap", "$(MSBuildProjectDirectory)=/_/deployment"),
                    new XElement("DebugType", "none"),
                    new XElement("RestorePackagesWithLockFile", "true"),
                    new XElement("RestorePackagesPath", Path.Combine(work, "cache"))),
                new XElement(
                    "ItemGroup",
                    new XElement(
                        "PackageReference",
                        new XAttribute("Include", name),
                        new XAttribute("Version", requirement)))));
        var path = Path.Combine(work, "PluginDeployment.csproj");
        await File.WriteAllTextAsync(path, project.ToString(), token);
        return path;
    }

    private async Task WriteNuGetConfigAsync(string work, string name, string? rootFeed, CancellationToken token)
    {
        var all = (rootFeed is null ? sources : new[] { rootFeed }.Concat(sources)).ToArray();
        var configuration = new XDocument(
            new XElement(
                "configuration",
                new XElement(
                    "packageSources",
                    new XElement("clear"),
                    all.Select((source, index) => new XElement(
                        "add",
                        new XAttribute("key", "source" + index),
                        new XAttribute("value", source)))),
                new XElement("fallbackPackageFolders", new XElement("clear")),
                new XElement(
                    "packageSourceMapping",
                    new XElement("clear"),
                    all.Select((_, index) => new XElement(
                        "packageSource",
                        new XAttribute("key", "source" + index),
                        new XElement(
                            "package",
                            new XAttribute("pattern", rootFeed is not null && index == 0 ? name : "*")))))));
        await File.WriteAllTextAsync(Path.Combine(work, "NuGet.Config"), configuration.ToString(), token);
    }
}
