using System.Collections.Frozen;
using System.Collections.ObjectModel;
using System.Text.Json;
using Cordis;

namespace Cordis.Composition;

/// <summary>
/// Represents the deployment package scope component.
/// </summary>
public enum DeploymentPackageScope
{
    /// <summary>
    /// Gets the installation value.
    /// </summary>
    Installation,
    /// <summary>
    /// Gets the profile value.
    /// </summary>
    Profile
}

/// <summary>
/// Represents the deployment resolution behavior component.
/// </summary>
public enum DeploymentResolutionBehavior
{
    /// <summary>
    /// Gets the enforce value.
    /// </summary>
    Enforce,
    /// <summary>
    /// Gets the verify value.
    /// </summary>
    Verify
}

/// <summary>A selected package directory, with the edge that selected it.</summary>
public sealed record DeploymentEntry(string Name, string Directory, string? Version, string Declarer, DeploymentPackageScope Scope);
/// <summary>Immutable deployment routing data. Package acquisition remains the host's responsibility.</summary>
public sealed class DeploymentGeneration
{
    /// <summary>
    /// Gets the profiles directory value.
    /// </summary>
    public string ProfilesDirectory { get; }
    /// <summary>
    /// Gets the profile directory value.
    /// </summary>
    public string? ProfileDirectory { get; }
    /// <summary>
    /// Gets the entries value.
    /// </summary>
    public IReadOnlyList<DeploymentEntry> Entries { get; }
    /// <summary>
    /// Gets the local packages value.
    /// </summary>
    public IReadOnlyDictionary<string, string> LocalPackages { get; }
    /// <summary>
    /// Gets the local package names value.
    /// </summary>
    public IReadOnlyList<string> LocalPackageNames { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentGeneration"/> type.
    /// </summary>
    public DeploymentGeneration(string profilesDirectory, string? profileDirectory, IEnumerable<DeploymentEntry> entries, IReadOnlyDictionary<string, string>? localPackages = null)
    {
        ProfilesDirectory = Path.GetFullPath(profilesDirectory);
        ProfileDirectory = profileDirectory is null ? null : Path.GetFullPath(profileDirectory);
        Entries = Array.AsReadOnly(entries.Select(entry => entry with { Directory = Path.GetFullPath(entry.Directory), Declarer = Path.GetFullPath(entry.Declarer), }).ToArray());
        LocalPackageNames = Array.AsReadOnly((localPackages?.Keys ?? []).ToArray());
        LocalPackages = (localPackages ?? new Dictionary<string, string>()).ToFrozenDictionary(pair => pair.Key, pair => Path.GetFullPath(pair.Value), StringComparer.Ordinal);
    }

    /// <summary>
    /// Computes installation breadth-first closure, then each selected bundle's complete closure.
    /// The resolver consumes a declarer's absolute manifest path and a dependency name, and returns
    /// a deployed package directory. Missing deployed dependencies are skipped, not installed.
    /// </summary>
    public static DeploymentGeneration Create(string installationManifest, string profilesDirectory, Profile? profile, Func<string, string, string?> resolveDependency, IReadOnlyDictionary<string, string>? localPackages = null)
    {
        ArgumentNullException.ThrowIfNull(resolveDependency);
        installationManifest = Path.GetFullPath(installationManifest);
        var entries = new Dictionary<string, DeploymentEntry>(StringComparer.Ordinal);
        void Visit(string anchor, DeploymentPackageScope scope)
        {
            var root = ReadManifest(anchor);
            if (scope == DeploymentPackageScope.Profile && Text(root, "name") is null)
                return;
            if (Text(root, "name") is { } ownName && !entries.ContainsKey(ownName))
                entries[ownName] = new(ownName, Path.GetDirectoryName(anchor)!, Text(root, "version"), anchor, scope);
            var queue = new Queue<(string Anchor, JsonElement Manifest)>();
            queue.Enqueue((anchor, root));
            while (queue.TryDequeue(out var item))
            {
                foreach (var name in DependencyNames(item.Manifest))
                {
                    if (entries.ContainsKey(name))
                        continue;
                    var directory = resolveDependency(item.Anchor, name);
                    if (directory is null)
                        continue;
                    directory = Path.GetFullPath(directory);
                    var manifestPath = Path.Combine(directory, "package.json");
                    var manifest = ReadManifest(manifestPath);
                    entries[name] = new(name, directory, Text(manifest, "version"), item.Anchor, scope);
                    queue.Enqueue((manifestPath, manifest));
                }
            }
        }

        Visit(installationManifest, DeploymentPackageScope.Installation);
        var installationNames = entries.Keys.ToHashSet(StringComparer.Ordinal);
        if (profile is not null)
        {
            foreach (var bundle in profile.Bundles)
                if (!installationNames.Contains(bundle.Name))
                    Visit(Path.Combine(bundle.Directory, "package.json"), DeploymentPackageScope.Profile);
            foreach (var bundle in profile.Bundles)
                if (!installationNames.Contains(bundle.Name))
                    entries.Remove(bundle.Name);
        }

        var selectedLocals = new Dictionary<string, string>(StringComparer.Ordinal);
        if (profile is not null)
        {
            var manifestPath = Path.Combine(profile.Directory, "package.json");
            if (File.Exists(manifestPath))
                foreach (var name in DependencyNames(ReadManifest(manifestPath)))
                    if (localPackages?.TryGetValue(name, out var directory) == true && File.Exists(Path.Combine(directory, "package.json")))
                        selectedLocals[name] = directory;
        }

        return new(profilesDirectory, profile?.Directory, entries.Values, selectedLocals);
    }

    internal static JsonElement ReadManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Package manifest '{path}' must hold an object.");
        return document.RootElement.Clone();
    }

    internal static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static IEnumerable<string> DependencyNames(JsonElement manifest)
    {
        foreach (var key in new[]
        {
            "dependencies",
            "peerDependencies"
        }

        )
            if (manifest.TryGetProperty(key, out var dependencies) && dependencies.ValueKind == JsonValueKind.Object)
                foreach (var property in dependencies.EnumerateObject())
                    yield return property.Name;
    }
}

/// <summary>
/// Represents the deployment restart required exception component.
/// </summary>
/// <param name="message">The message value.</param>
public sealed class DeploymentRestartRequiredException(string message) : InvalidOperationException(message);
/// <summary>
/// Represents the deployment resolution mismatch exception component.
/// </summary>
/// <param name="message">The message value.</param>
public sealed class DeploymentResolutionMismatchException(string message) : InvalidOperationException(message);
/// <summary>
/// Represents the deployment package component.
/// </summary>
/// <param name="Name">The name value.</param>
/// <param name="Version">The version value.</param>
/// <param name="Directory">The directory value.</param>
/// <param name="ManifestPath">The manifest path value.</param>
/// <param name="Manifest">The manifest value.</param>
public sealed record DeploymentPackage(string Name, string? Version, string Directory, string ManifestPath, JsonElement Manifest);
/// <summary>
/// Routes explicit .NET deployment packages and their metadata through one generation. It never
/// hooks the CLR or emulates Node exports. Native, local and ancestor lookups are host-provided maps.
/// </summary>
public sealed class DeploymentPackageResolver
{
    private sealed class State(DeploymentGeneration? generation)
    {
        internal DeploymentGeneration? Generation { get; } = generation;
        internal FrozenDictionary<string, DeploymentEntry> Entries { get; } = (generation?.Entries ?? []).ToFrozenDictionary(entry => entry.Name, StringComparer.Ordinal);
        internal Dictionary<(string Directory, string Name), DeploymentPackage?> Packages { get; } = [];
    }

    private State current;
    private readonly object gate = new();
    private readonly Func<string, Uri, string?>? native;
    private readonly Func<string, Uri, string?>? local;
    private readonly Func<string, Uri, string?>? ancestor;
    private readonly DeploymentResolutionBehavior behavior;
    /// <summary>
    /// Gets the generation value.
    /// </summary>
    public DeploymentGeneration Generation
    {
        get
        {
            lock (gate)
                return current.Generation ?? throw new InvalidOperationException("Runtime deployment resolution is not installed.");
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentPackageResolver"/> type.
    /// </summary>
    public DeploymentPackageResolver(DeploymentGeneration generation, Func<string, Uri, string?>? native = null, Func<string, Uri, string?>? local = null, Func<string, Uri, string?>? ancestor = null, DeploymentResolutionBehavior behavior = DeploymentResolutionBehavior.Enforce) => (current, this.native, this.local, this.ancestor, this.behavior) = (new(generation), native, local, ancestor, behavior);
    private DeploymentPackageResolver(Func<string, Uri, string?> native) => (current, this.native) = (new(null), native);
    /// <summary>
    /// Performs the native operation.
    /// </summary>
    public static DeploymentPackageResolver Native(Func<string, Uri, string?> resolve) => new(resolve);
    /// <summary>
    /// Performs the replace operation.
    /// </summary>
    public void Replace(DeploymentGeneration generation)
    {
        var next = new State(generation);
        lock (gate)
        {
            var old = current.Generation ?? throw new InvalidOperationException("Runtime deployment resolution is not installed.");
            if (old.ProfilesDirectory != generation.ProfilesDirectory || old.ProfileDirectory != generation.ProfileDirectory)
                throw new DeploymentRestartRequiredException("A generation cannot change its profile scope.");
            foreach (var entry in current.Entries.Values)
            {
                if (!next.Entries.TryGetValue(entry.Name, out var replacement) || !SamePath(entry.Directory, replacement.Directory) || !SamePath(entry.Declarer, replacement.Declarer) || entry.Version != replacement.Version || entry.Scope != replacement.Scope)
                    throw new DeploymentRestartRequiredException($"Replacing package '{entry.Name}' requires a process restart.");
            }

            foreach (var name in old.LocalPackageNames)
                if (!generation.LocalPackages.ContainsKey(name))
                    throw new DeploymentRestartRequiredException($"Removing local package '{name}' requires a process restart.");
            foreach (var name in generation.LocalPackageNames)
                if (!old.LocalPackages.ContainsKey(name) && current.Entries.ContainsKey(name))
                    throw new DeploymentRestartRequiredException($"Overriding package '{name}' locally requires a process restart.");
            current = next;
        }
    }

    /// <summary>
    /// Performs the package directory operation.
    /// </summary>
    public string? PackageDirectory(string specifier, Uri parent)
    {
        var name = BarePackageName(specifier);
        if (name is null)
            return null;
        lock (gate)
            return ResolveChecked(current, name, parent);
    }

    /// <summary>
    /// Performs the package of operation.
    /// </summary>
    public DeploymentPackage? PackageOf(string specifier, Uri parent)
    {
        var name = BarePackageName(specifier);
        if (name is null)
            return null;
        lock (gate)
        {
            var state = current;
            var directory = ResolveChecked(state, name, parent);
            if (directory is null)
                return null;
            var key = (directory, name);
            if (state.Packages.TryGetValue(key, out var cached))
                return cached;
            var manifestPath = Path.Combine(directory, "package.json");
            if (!File.Exists(manifestPath))
                return state.Packages[key] = null;
            var manifest = DeploymentGeneration.ReadManifest(manifestPath);
            return state.Packages[key] = new(DeploymentGeneration.Text(manifest, "name") ?? name, DeploymentGeneration.Text(manifest, "version"), directory, manifestPath, manifest);
        }
    }

    private string? ResolveChecked(State state, string name, Uri parent)
    {
        var expected = Resolve(state, name, parent);
        var generation = state.Generation;
        bool scoped = parent.IsFile && generation is not null && (Within(parent.LocalPath, generation.ProfilesDirectory) || generation.ProfileDirectory is not null && Within(parent.LocalPath, generation.ProfileDirectory));
        if (behavior != DeploymentResolutionBehavior.Verify || !scoped)
            return expected;
        var actual = native?.Invoke(name, parent);
        if (actual is null && expected is null || actual is not null && expected is not null && SamePath(actual, expected))
            return expected;
        throw new DeploymentResolutionMismatchException($"Deployment resolution mismatch for '{name}' from '{parent}': deployment selected {actual ?? "nothing"}, generation selected {expected ?? "nothing"}.");
    }

    private string? Resolve(State state, string name, Uri parent)
    {
        var generation = state.Generation;
        if (generation is null)
            return native?.Invoke(name, parent);
        bool active = parent.IsFile && generation.ProfileDirectory is not null && Within(parent.LocalPath, generation.ProfileDirectory);
        bool scoped = parent.IsFile && (active || Within(parent.LocalPath, generation.ProfilesDirectory));
        if (!scoped)
            return native?.Invoke(name, parent);
        if (active && generation.LocalPackages.TryGetValue(name, out var installed))
            return installed;
        if (local?.Invoke(name, parent) is { } nearest)
            return nearest;
        if (state.Entries.TryGetValue(name, out var entry) && (entry.Scope == DeploymentPackageScope.Installation || active))
            return entry.Directory;
        // A caller using the canonical tree continues above that tree. A caller
        // using the deployment alias retains the alias's ancestor position.
        var profilesDirectory = generation.ProfilesDirectory;
        var canonicalProfiles = Canonical(profilesDirectory);
        if (LexicallyWithin(parent.LocalPath, canonicalProfiles) && !LexicallyWithin(parent.LocalPath, profilesDirectory))
            profilesDirectory = canonicalProfiles;
        var after = new Uri(Path.Combine(Path.GetDirectoryName(profilesDirectory)!, "package.json"));
        return ancestor?.Invoke(name, after);
    }

    /// <summary>
    /// Performs the bare package name operation.
    /// </summary>
    public static string? BarePackageName(string request)
    {
        if (request.Length == 0 || request[0] is '.' or '/' or '\\' or '#' || request.Contains(':'))
            return null;
        // Node builtins belong to the compatibility input domain, not to NuGet package names.
        if (NodeBuiltins.Contains(request))
            return null;
        int first = request.IndexOf('/');
        if (request[0] != '@')
            return first < 0 ? request : request[..first];
        if (first < 0)
            return null;
        int second = request.IndexOf('/', first + 1);
        return second < 0 ? request : request[..second];
    }

    private static readonly FrozenSet<string> NodeBuiltins = new[]
    {
        "assert",
        "assert/strict",
        "async_hooks",
        "buffer",
        "child_process",
        "cluster",
        "console",
        "constants",
        "crypto",
        "dgram",
        "diagnostics_channel",
        "dns",
        "dns/promises",
        "domain",
        "events",
        "fs",
        "fs/promises",
        "http",
        "http2",
        "https",
        "module",
        "net",
        "os",
        "path",
        "path/posix",
        "path/win32",
        "perf_hooks",
        "process",
        "punycode",
        "querystring",
        "readline",
        "readline/promises",
        "repl",
        "stream",
        "stream/consumers",
        "stream/promises",
        "stream/web",
        "string_decoder",
        "sys",
        "timers",
        "timers/promises",
        "tls",
        "trace_events",
        "tty",
        "url",
        "util",
        "util/types",
        "v8",
        "vm",
        "wasi",
        "worker_threads",
        "zlib"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static bool Within(string path, string directory)
    {
        return LexicallyWithin(Canonical(path), Canonical(directory));
    }

    private static bool LexicallyWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool SamePath(string left, string right) => string.Equals(Canonical(left), Canonical(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string Canonical(string path)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
            return path;
        var parent = Canonical(directory);
        var candidate = Path.Combine(parent, Path.GetFileName(path));
        FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
        try
        {
            return info.ResolveLinkTarget(true)?.FullName ?? candidate;
        }
        catch (IOException)
        {
            return candidate;
        }
    }
}

/// <summary>Explicit deployed module exports; does not reinterpret npm main/exports as CLR entrypoints.</summary>
public sealed class DeploymentModuleResolver(DeploymentPackageResolver packages, IModuleResolver modules) : IModuleResolver
{
    /// <summary>
    /// Gets the packages value.
    /// </summary>
    public DeploymentPackageResolver Packages => packages;

    private readonly Dictionary<(string Directory, string Subpath), string> mappings = [];
    /// <summary>
    /// Performs the register operation.
    /// </summary>
    public DeploymentModuleResolver Register(string packageDirectory, string subpath, string moduleSpecifier)
    {
        mappings[(Path.GetFullPath(packageDirectory), subpath)] = moduleSpecifier;
        return this;
    }

    /// <summary>
    /// Resolves async.
    /// </summary>
    public ValueTask<IPlugin> ResolveAsync(string specifier, Uri baseUri, CancellationToken cancellationToken = default)
    {
        var name = DeploymentPackageResolver.BarePackageName(specifier);
        if (name is null)
            return modules.ResolveAsync(specifier, baseUri, cancellationToken);
        var directory = packages.PackageDirectory(specifier, baseUri);
        if (directory is null)
            throw new FileNotFoundException($"Cannot resolve package '{name}' from '{baseUri}'.");
        var subpath = specifier.Length == name.Length ? "." : "." + specifier[name.Length..];
        if (!mappings.TryGetValue((Path.GetFullPath(directory), subpath), out var target))
            throw new FileNotFoundException($"Package '{name}' has no deployed CLR module '{subpath}' (importer '{baseUri}').");
        return modules.ResolveAsync(target, new Uri(Path.Combine(directory, "package.json")), cancellationToken);
    }
}
