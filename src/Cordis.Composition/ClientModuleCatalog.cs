using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>One captured browser plugin and its declared package dependencies.</summary>
public sealed record ClientModuleEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("rev")] string Revision,
    [property: JsonPropertyName("inject")] IReadOnlyList<string> Inject,
    [property: JsonPropertyName("external")] IReadOnlyList<string> External,
    [property: JsonPropertyName("immediately")] bool Immediately);

/// <summary>An initial script resource. A resource registers factories before plugin activation.</summary>
public sealed record ClientModuleBatch(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("rev")] string Revision,
    [property: JsonPropertyName("entries")] IReadOnlyList<string> Entries);

/// <summary>A complete roster compatible with the fixed DSH browser module consumer.</summary>
/// <remarks>Replacing a graph retracts absent plugins. Its revision binds artifacts and dependency declarations, not application configuration.</remarks>
public sealed record ClientModuleGraph(
    [property: JsonPropertyName("rev")] string Revision,
    [property: JsonPropertyName("entries")] IReadOnlyList<ClientModuleEntry> Entries,
    [property: JsonPropertyName("batches")] IReadOnlyList<ClientModuleBatch> Batches);

/// <summary>Captures a selected browser roster through existing deployment routing.</summary>
/// <remarks>
/// Each export must be a precompiled lazy factory registration consumed by the TypeScript client module package.
/// This catalog neither builds code nor owns plugin lifetimes. The host replaces a completed catalog and publishes
/// its full graph; old revision URLs may serve their original captured bytes or return unavailable, never newer bytes.
/// One-resource batches are a .NET delivery adaptation. Arbitrary npm resolution and package-local chunks are not supplied.
/// </remarks>
public sealed class ClientModuleCatalog
{
    private static readonly HashSet<string> PlatformModules = new(StringComparer.Ordinal)
    {
        "@cordis-net/client-modules", "@cordis-net/client-modules/slots", "@deepseek-ai/cordis", "@deepseek-ai/cosmokit",
    };
    private readonly IReadOnlyDictionary<string, ClientArtifact> artifacts;

    private ClientModuleCatalog(ClientModuleGraph graph, IReadOnlyDictionary<string, ClientArtifact> artifacts)
        => (Graph, this.artifacts) = (graph, artifacts);

    /// <summary>The captured full graph. Serialize this object directly; wire names are explicit.</summary>
    public ClientModuleGraph Graph { get; }

    /// <summary>Find bytes belonging to this catalog and revision; a retired or unknown identity returns null.</summary>
    public ClientArtifact? FindArtifact(string packageName, string revision)
        => artifacts.TryGetValue(packageName, out var artifact) && artifact.Revision == revision ? artifact : null;

    /// <summary>Capture selected packages, validate their declarations, and order their module requests.</summary>
    /// <remarks>
    /// The caller selects active package names using its existing Loader. Every declared non-platform dependency
    /// must be present when withdrawUnavailableDependencies is false. When true, consumers of unavailable
    /// dependencies are recursively withdrawn from this delivery graph without changing profile selection.
    /// Import cycles fail before publication. Capture failure leaves the caller's
    /// previous catalog intact. A host must serialize selection and deployment changes through its existing owner.
    /// </remarks>
    public static async Task<ClientModuleCatalog> CaptureAsync(DeploymentPackageResolver packages,
        IEnumerable<string> packageNames, Uri parent, string artifactBasePath = "/client/artifacts",
        CancellationToken cancellationToken = default, bool withdrawUnavailableDependencies = false)
    {
        ArgumentNullException.ThrowIfNull(packages);
        ArgumentNullException.ThrowIfNull(packageNames);
        ArgumentNullException.ThrowIfNull(parent);
        if (!artifactBasePath.StartsWith('/') || artifactBasePath.StartsWith("//", StringComparison.Ordinal)
            || artifactBasePath.Contains('?') || artifactBasePath.Contains('#') || artifactBasePath.Contains('\\'))
            throw new ArgumentException("An absolute same-origin artifact path is required.", nameof(artifactBasePath));
        artifactBasePath = artifactBasePath.TrimEnd('/');
        var captured = new Dictionary<string, ClientArtifact>(StringComparer.Ordinal);
        var rows = new Dictionary<string, ClientModuleEntry>(StringComparer.Ordinal);
        foreach (var name in packageNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsPackageName(name) || PlatformModules.Contains(name))
                throw new ArgumentException("A client row must name a non-platform package.", nameof(packageNames));
            if (rows.ContainsKey(name)) throw new FormatException("Duplicate client package: " + name);
            var package = packages.PackageOf(name, parent) ?? throw new FileNotFoundException("No deployed client package: " + name);
            var manifest = package.Manifest;
            if (!manifest.TryGetProperty("dsh", out var dsh) || dsh.ValueKind != JsonValueKind.Object
                || !dsh.TryGetProperty("client", out var declaration) || declaration.ValueKind != JsonValueKind.Object)
                throw new FormatException("The package must declare dsh.client: " + name);
            var inject = Strings(declaration, "inject", name);
            var external = Strings(declaration, "external", name);
            var immediately = false;
            if (declaration.TryGetProperty("immediately", out var immediate))
            {
                if (immediate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatException("dsh.client.immediately must be a boolean: " + name);
                immediately = immediate.GetBoolean();
            }
            var artifact = await ClientArtifact.CaptureAsync(packages, name, parent, cancellationToken);
            if (artifact.PackageName != name) throw new FormatException("Client package identity differs from its selected name: " + name);
            var url = artifactBasePath + "/" + Uri.EscapeDataString(name) + "?rev=" + artifact.Revision;
            captured.Add(name, artifact);
            rows.Add(name, new(name, url, artifact.Revision, inject, external, immediately));
        }
        // A host may project selected packages to their closed client roster. Selection still belongs
        // to the profile; this only prevents a consumer retaining a withdrawn client dependency.
        if (withdrawUnavailableDependencies)
        {
            var removed = true;
            while (removed)
            {
                removed = false;
                foreach (var row in rows.Values.ToArray())
                    if (row.External.Any(request => !PlatformModules.Contains(request) && !rows.ContainsKey(StripClient(request)))
                        || row.Inject.Any(dependency => !rows.ContainsKey(dependency)))
                    {
                        rows.Remove(row.Id);
                        captured.Remove(row.Id);
                        removed = true;
                    }
            }
        }
        foreach (var row in rows.Values)
        {
            foreach (var request in row.External)
            {
                var dependency = StripClient(request);
                if (!PlatformModules.Contains(request) && !rows.ContainsKey(dependency))
                    throw new FormatException($"Client module '{row.Id}' requests unavailable module '{request}'.");
            }
            foreach (var dependency in row.Inject)
                if (!rows.ContainsKey(dependency)) throw new FormatException($"Client module '{row.Id}' injects unavailable package '{dependency}'.");
        }
        var ordered = new List<ClientModuleEntry>();
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var open = new List<string>();
        void Visit(ClientModuleEntry row)
        {
            if (placed.Contains(row.Id)) return;
            var cycle = open.IndexOf(row.Id);
            if (cycle >= 0) throw new FormatException("Client module graph cycle: " + string.Join(" -> ", open.Skip(cycle).Append(row.Id)));
            open.Add(row.Id);
            foreach (var request in row.External)
                if (rows.TryGetValue(StripClient(request), out var dependency)) Visit(dependency);
            open.RemoveAt(open.Count - 1);
            placed.Add(row.Id);
            ordered.Add(row);
        }
        foreach (var row in rows.Values) Visit(row);
        var entries = ordered.AsReadOnly();
        var batches = Array.AsReadOnly(ordered.Select(row => new ClientModuleBatch("application", row.Url, row.Revision,
            Array.AsReadOnly(new[] { row.Id }))).ToArray());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void HashText(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(Encoding.UTF8.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(bytes);
        }
        foreach (var row in entries)
        {
            HashText(row.Id); HashText(row.Url); HashText(row.Revision); HashText(row.Immediately ? "true" : "false");
            HashText(row.Inject.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in row.Inject) HashText(value);
            HashText(row.External.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in row.External) HashText(value);
        }
        var revision = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return new(new(revision, entries, batches), captured);
    }

    private static IReadOnlyList<string> Strings(JsonElement declaration, string field, string name)
    {
        if (!declaration.TryGetProperty(field, out var values)) return Array.Empty<string>();
        if (values.ValueKind != JsonValueKind.Array || values.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String))
            throw new FormatException($"dsh.client.{field} must be a string array: {name}");
        var result = values.EnumerateArray().Select(value => value.GetString()!).ToArray();
        if (result.Any(value => !(field == "external" && PlatformModules.Contains(value)) && !IsPackageName(StripClient(value))))
            throw new FormatException($"dsh.client.{field} must contain package names or /client requests: {name}");
        if (field == "inject" && result.Any(value => value != StripClient(value)))
            throw new FormatException("dsh.client.inject must contain package names: " + name);
        return Array.AsReadOnly(result);
    }

    private static string StripClient(string request) => request.EndsWith("/client", StringComparison.Ordinal) ? request[..^7] : request;
    private static bool IsPackageName(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var parts = value.Split('/');
        return (value.StartsWith('@') ? parts.Length == 2 && parts[0].Length > 1 : parts.Length == 1)
            && parts.All(part => part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character is '@' or '-' or '_' or '.'))
            && parts.All(part => part is not ("." or ".."));
    }
}

