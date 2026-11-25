using System.Security.Cryptography;
using System.Text.Json;

namespace Cordis.Clr;

internal static class LocalBuildFiles
{
    internal static string SelectProject(string root, out bool bundle)
    {
        var metadata = Path.Combine(root, "cordis.plugin.json");
        CheckPath(root, root);
        bundle = File.Exists(metadata);
        if (bundle)
        {
            CheckPath(metadata, root);
            using var declaration = JsonDocument.Parse(File.ReadAllBytes(metadata));
            var plugin = declaration.RootElement;
            if (string.IsNullOrWhiteSpace(plugin.GetProperty("entryType").GetString()) ||
                string.IsNullOrWhiteSpace(plugin.GetProperty("assembly").GetString()))
                throw new InvalidDataException($"Invalid CLR plugin declaration '{metadata}'.");
            if (plugin.TryGetProperty("project", out var field))
            {
                var path = Path.GetFullPath(Path.Combine(root, Relative(field.GetString()!)));
                CheckPath(path, root);
                if (!File.Exists(path) || !string.Equals(
                        Path.GetExtension(path),
                        ".csproj",
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Declared CLR project does not exist: {path}");
                return path;
            }
        }

        var projects = Directory.GetFiles(root, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projects.Length != 1)
            throw new InvalidDataException(
                $"Expected one entry .csproj in '{root}'. Specify the relative project in cordis.plugin.json.");
        CheckPath(projects[0], root);
        return projects[0];
    }

    internal static string Relative(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        if (Path.IsPathRooted(path) || parts.Any(p => p.Length == 0 || p.EndsWith('.') || p.EndsWith(' ') ||
                p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.Contains(':')))
            throw new InvalidDataException($"Expected a relative bundle path: '{path}'.");
        return Path.Combine(parts);
    }

    // The owner may itself live below a platform alias (for example /var on macOS).
    // Reject links from the nominated root down through the file, including the root itself.
    internal static void CheckPath(string path, string owner)
    {
        path = Path.GetFullPath(path);
        owner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(owner));
        var relative = Within(path, owner);
        var part = owner;
        RejectLink(part);
        if (relative == ".")
            return;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            part = Path.Combine(part, segment);
            RejectLink(part);
        }
    }

    internal static string Within(string path, string owner)
    {
        var relative = Path.GetRelativePath(owner, path);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new InvalidDataException($"'{path}' is outside '{owner}'.");
        return relative;
    }

    private static void RejectLink(string part)
    {
        var info = new FileInfo(part);
        if (info.LinkTarget != null || (File.Exists(part) || Directory.Exists(part)) &&
            (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Local CLR output paths must not traverse links: " + part);
    }

    internal static string Location(string root, string project, string configuration, string framework, string runtime)
    {
        foreach (var segment in new[] { configuration, framework, runtime }.Where(s => s.Length > 0))
            if (Relative(segment) != segment || Path.GetFileName(segment) != segment)
                throw new InvalidDataException($"Invalid build identity segment '{segment}'.");
        var location = Path.Combine(
            root,
            "obj",
            "cordis-local",
            Relative(Path.GetRelativePath(root, project)),
            configuration,
            framework,
            runtime.Length == 0 ? "portable" : "rid-" + runtime);
        CheckPath(location, root);
        return location;
    }

    internal static string Resolve(
        string root,
        string project,
        string configuration,
        string framework,
        string runtime) =>
        ReadOutput(root, project, configuration, framework, runtime, allowMissing: false)!.Value.Directory;

    internal static (string Directory, string Identity)? ReadOutput(
        string root,
        string project,
        string configuration,
        string framework,
        string runtime,
        bool allowMissing)
    {
        var location = Location(root, project, configuration, framework, runtime);
        var pointer = Path.Combine(location, "current");
        CheckPath(pointer, location);
        string identity;
        try
        {
            identity = File.ReadAllText(pointer).Trim();
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            if (allowMissing)
                return null;
            throw new FileNotFoundException(
                $"No completed CLR output for '{project}' ({configuration}, {framework}). Build this project first.",
                pointer);
        }

        if (identity.Length != 64 || !identity.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid local CLR completion identity.");
        var directory = Path.Combine(location, identity, "bundle");
        CheckPath(directory, location);
        VerifyIdentity(directory, identity);
        return (directory, identity);
    }

    internal static void VerifyIdentity(string directory, string identity)
    {
        var receipt = BundleFiles.ReceiptPath(directory);
        CheckPath(receipt, Path.GetDirectoryName(directory)!);
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(receipt))) != identity)
            throw new InvalidDataException("The local CLR completion identity differs from its receipt.");
        BundleFiles.Verify(directory);
    }
}
