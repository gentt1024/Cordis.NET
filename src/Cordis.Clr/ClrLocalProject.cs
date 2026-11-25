using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cordis.Clr;

/// <summary>One local source project selected by its manifest and standard MSBuild properties.</summary>
public sealed class ClrLocalProject
{
    private ClrLocalProject(string root, string project, JsonElement properties, bool bundle)
    {
        SourceRoot = root;
        ProjectFile = project;
        PackageId = properties.GetProperty("PackageId").GetString()!;
        Version = properties.GetProperty("Version").GetString()!;
        Configuration = properties.GetProperty("Configuration").GetString()!;
        TargetFramework = properties.GetProperty("TargetFramework").GetString()!;
        RuntimeIdentifier = properties.GetProperty("RuntimeIdentifier").GetString()!;
        IsBundle = bundle;
    }

    /// <summary>The absolute directory recorded by the logical link.</summary>
    public string SourceRoot
    {
        get;
    }

    /// <summary>The selected entry project.</summary>
    public string ProjectFile
    {
        get;
    }

    /// <summary>The evaluated NuGet package ID.</summary>
    public string PackageId
    {
        get;
    }

    /// <summary>The evaluated project version.</summary>
    public string Version
    {
        get;
    }

    /// <summary>The requested build configuration.</summary>
    public string Configuration
    {
        get;
    }

    /// <summary>The entry project's single target framework.</summary>
    public string TargetFramework
    {
        get;
    }

    /// <summary>The evaluated runtime identifier, or empty for portable builds.</summary>
    public string RuntimeIdentifier
    {
        get;
    }

    /// <summary>Whether the root declares a CLR plugin.</summary>
    public bool IsBundle
    {
        get;
    }

    /// <summary>Evaluate the local project without restoring packages, building, or creating a runtime output.</summary>
    public static async Task<ClrLocalProject> QueryAsync(
        string sourceRoot,
        string configuration = "Debug",
        string dotnet = "dotnet",
        CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var project = LocalBuildFiles.SelectProject(root, out var bundle);
        return await QuerySelectedAsync(root, project, bundle, configuration, dotnet, cancellationToken);
    }

    internal static async Task<ClrLocalProject> QuerySelectedAsync(
        string root,
        string project,
        bool bundle,
        string configuration,
        string dotnet,
        CancellationToken cancellationToken)
    {
        // MSBuild treats several punctuation characters as separators in command-line properties.
        if (!Regex.IsMatch(configuration, @"\A[A-Za-z0-9_][A-Za-z0-9_. -]*\z", RegexOptions.CultureInvariant))
            throw new ArgumentException("Invalid MSBuild configuration.", nameof(configuration));
        var temporary = Directory.CreateTempSubdirectory("cordis-project-").FullName;
        try
        {
            var result = Path.Combine(temporary, "properties.json");
            await DotnetPackageProcess.RunAsync(
                dotnet,
                [
                    "msbuild", project, "-noAutoResponse", "-nologo", "-p:Configuration=" + configuration,
                    "-getProperty:PackageId,Version,Configuration,TargetFramework,TargetFrameworks,RuntimeIdentifier",
                    "-getResultOutputFile:" + result
                ],
                Path.GetDirectoryName(project)!,
                Path.Combine(temporary, "query-process.json"),
                _ =>
                {
                },
                Timeout.InfiniteTimeSpan,
                cancellationToken);
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(result, cancellationToken));
            var properties = document.RootElement.GetProperty("Properties");
            if (string.IsNullOrWhiteSpace(properties.GetProperty("TargetFramework").GetString()))
                throw new InvalidDataException($"Local entry project '{project}' must select one TargetFramework.");
            return new(root, project, properties, bundle);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }

    /// <summary>Verify and return this configuration's last complete immutable output.</summary>
    public string ResolveOutput() => ReadOutput(allowMissing: false)!.Value.Directory;

    internal (string Directory, string Identity)? ReadOutput(bool allowMissing, bool verifyVersion = false)
    {
        if (!IsBundle)
            throw new InvalidOperationException($"'{SourceRoot}' declares no CLR plugin.");
        LocalBuildFiles.CheckPath(ProjectFile, SourceRoot);
        if (!File.Exists(ProjectFile) || !File.Exists(Path.Combine(SourceRoot, "cordis.plugin.json")))
            throw new FileNotFoundException(
                $"Local plugin source no longer exists: '{ProjectFile}' in '{SourceRoot}'.");
        var output = LocalBuildFiles.ReadOutput(
            SourceRoot,
            ProjectFile,
            Configuration,
            TargetFramework,
            RuntimeIdentifier,
            allowMissing);
        if (output is null)
            return null;
        using var manifest =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output.Value.Directory, "package.json")));
        if (!string.Equals(
                manifest.RootElement.GetProperty("name").GetString(),
                PackageId,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The local output's saved package name differs from the selected project.");
        if (verifyVersion && !string.Equals(
                manifest.RootElement.GetProperty("version").GetString(),
                Version,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The completed output has a different version. Rebuild the selected project.");
        return output;
    }
}
