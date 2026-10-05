using System.Security.Cryptography;
using System.Text.Json;

namespace Cordis.Composition;

/// <summary>A captured precompiled web entry selected through the existing deployment package resolver.</summary>
/// <remarks>This class captures bytes only. The consumer defines their format, such as independent ESM or factory registration. It does not resolve browser imports, build code or own a module graph.</remarks>
public sealed class ClientArtifact
{
    private readonly byte[] content;
    private ClientArtifact(string packageName, byte[] content)
    {
        PackageName = packageName;
        this.content = content;
        Revision = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }
    /// <summary>The package selected by deployment routing.</summary>
    public string PackageName { get; }
    /// <summary>Content identity; a URL using it must serve these captured bytes or return unavailable.</summary>
    public string Revision { get; }
    /// <summary>A strong HTTP validator for these bytes.</summary>
    public string ETag => "\"" + Revision + "\"";
    /// <summary>The exact captured byte length.</summary>
    public long Length => content.LongLength;
    /// <summary>Open a read-only view of the captured generation; the caller owns the stream.</summary>
    public Stream OpenRead() => new MemoryStream(content, false);

    /// <summary>Capture dsh.client.platform=web and exports['./client'] from a selected package.</summary>
    /// <remarks>Only a string target or a single default target is accepted. Paths must remain inside the canonical package, including through links.</remarks>
    public static async Task<ClientArtifact> CaptureAsync(DeploymentPackageResolver packages, string packageName, Uri parent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);
        var package = packages.PackageOf(packageName, parent) ?? throw new FileNotFoundException("No deployed client package: " + packageName);
        var manifest = package.Manifest;
        if (!manifest.TryGetProperty("dsh", out var dsh) || !dsh.TryGetProperty("client", out var client)
            || !client.TryGetProperty("platform", out var platform) || platform.GetString() != "web"
            || !manifest.TryGetProperty("exports", out var exports) || !exports.TryGetProperty("./client", out var entry))
            throw new FormatException("The package must declare a web client export.");
        if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("default", out var fallback)) entry = fallback;
        var target = entry.ValueKind == JsonValueKind.String ? entry.GetString() : null;
        if (target is null || !target.StartsWith("./", StringComparison.Ordinal) || target.Contains('\\')
            || target.Contains('?') || target.Contains('#')) throw new FormatException("A package-relative precompiled client target is required.");
        var root = DeploymentPackageResolver.Canonical(package.Directory);
        var lexical = Path.GetFullPath(Path.Combine(root, target));
        var canonical = DeploymentPackageResolver.Canonical(lexical);
        if (!Within(lexical, root) || !Within(canonical, root)) throw new UnauthorizedAccessException("Client artifacts cannot escape their selected package.");
        if (Path.GetExtension(canonical) is not (".js" or ".mjs")) throw new FormatException("The client entry must be precompiled JavaScript.");
        return new(package.Name, await File.ReadAllBytesAsync(canonical, cancellationToken));
    }

    private static bool Within(string path, string root) => path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
