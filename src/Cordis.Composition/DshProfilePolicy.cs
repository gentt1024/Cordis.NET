using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cordis.Composition;

/// <summary>The pinned DSH named-profile discovery and migration policy.</summary>
public static partial class DshProfilePolicy
{
    /// <summary>The profile-local file containing explicitly acknowledged exact-version exemptions.</summary>
    public const string CompatibilityFilename = "compatibility.json";

    /// <summary>Checks all own peer values before selecting the DSH package family. Cordis peers are not DSH peers.</summary>
    public static DshPluginCompatibility? EvaluateCompatibility(PackageManifest manifest, DshRuntimeIdentity runtime,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? exactGrants = null)
        => EvaluateCompatibility(manifest, runtime, exactGrants, null);

    /// <summary>Evaluate exact grants with a host-selected package-name adapter; versions remain exact and case-sensitive.</summary>
    public static DshPluginCompatibility? EvaluateCompatibility(PackageManifest manifest, DshRuntimeIdentity runtime,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? exactGrants, Func<string, string>? compatibilityPackageName)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(runtime);
        if (!manifest.Raw.TryGetValue("peerDependencies", out var declared)) return null;
        if (declared is not IDictionary<string, object?> peers) throw new FormatException("peerDependencies must be an object.");
        foreach (var pair in peers)
            if (pair.Value is not string) throw new FormatException($"peerDependencies.{pair.Key} must be a string.");
        var mismatches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in peers)
        {
            if (name != "@deepseek-ai/dsh" && !name.StartsWith("@deepseek-ai/dsh-", StringComparison.Ordinal)) continue;
            var range = (string)value!;
            var requirement = range is "workspace:^" or "workspace:~" or "workspace:*" ? runtime.Version : range;
            if (!DshSemver.Satisfies(runtime.SemVersion, requirement)) mismatches[name] = range;
        }
        if (mismatches.Count == 0) return null;
        if (manifest.Raw.GetValueOrDefault("name") is not string packageName || packageName.Length == 0 ||
            manifest.Raw.GetValueOrDefault("version") is not string version || version.Length == 0)
            throw new FormatException("An incompatible plugin manifest must have a nonempty name and version.");
        var grantName = compatibilityPackageName?.Invoke(packageName) ?? packageName;
        var exempted = exactGrants?.TryGetValue(grantName + "@" + version, out var grants) == true && grants.Contains(runtime.Version, StringComparer.Ordinal);
        return new(packageName, version, runtime.Version, new ReadOnlyDictionary<string, string>(mismatches), exempted);
    }

    /// <summary>Reads valid sibling grants while retaining warnings and refusing to authorize rewriting corrupt data.</summary>
    public static DshCompatibilityRead ReadCompatibility(string directory)
    {
        var grants = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var path = Path.Combine(directory, CompatibilityFilename);
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Compatibility data must be an object.");
            foreach (var property in json.RootElement.EnumerateObject())
            {
                try
                {
                    ValidatePackageVersion(property.Name);
                    if (property.Value.ValueKind != JsonValueKind.Array) throw new FormatException("A grant must be an array of exact runtime versions.");
                    var versions = new List<string>();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) throw new FormatException("A grant runtime version must be a string.");
                        var version = item.GetString()!;
                        ValidateExactVersion(version);
                        versions.Add(version);
                    }
                    grants[property.Name] = versions.AsReadOnly();
                }
                catch (FormatException error) { warnings.Add($"Ignoring compatibility record '{property.Name}': {error.Message}"); }
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { warnings.Add($"Cannot read {path}: {error.Message}"); }
        return new(new ReadOnlyDictionary<string, IReadOnlyList<string>>(grants), warnings.AsReadOnly(), warnings.Count == 0);
    }

    internal static void ValidateExactVersion(string version)
    {
        if (!DshSemver.Version.TryParse(version, out var parsed) || parsed.Text != version)
            throw new FormatException($"'{version}' must be a canonical exact npm version.");
    }

    internal static void ValidatePackageVersion(string identity)
    {
        var separator = identity.LastIndexOf('@');
        if (separator <= 0 || !Regex.IsMatch(identity[..separator], @"^(?:@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant))
            throw new FormatException("A grant identity must be a lowercase npm package name followed by an exact version.");
        ValidateExactVersion(identity[(separator + 1)..]);
    }

    /// <summary>Creates an admission callback for DSH profiles and pre-import manifest checks, without changing generic loaders.</summary>
    public static Action<PackageManifest> CreateAdmission(string directory, DshRuntimeIdentity runtime, Action<string>? warning = null)
        => CreateAdmission(directory, runtime, warning, null);

    /// <summary>Create DSH admission using an explicit platform name adapter without changing generic or npm defaults.</summary>
    public static Action<PackageManifest> CreateAdmission(string directory, DshRuntimeIdentity runtime, Action<string>? warning,
        Func<string, string>? compatibilityPackageName)
    {
        warning ??= Console.Error.WriteLine;
        var read = ReadCompatibility(directory);
        foreach (var message in read.Warnings) warning?.Invoke(message);
        return manifest =>
        {
            var issue = EvaluateCompatibility(manifest, runtime, read.Exemptions, compatibilityPackageName);
            if (issue is null) return;
            var message = $"Plugin {issue.Name}@{issue.Version} requires incompatible DSH peers ({string.Join(", ", issue.Peers.Select(pair => pair.Key + ": " + pair.Value))}); running DSH {runtime.Version}.";
            if (!issue.Exempted) throw new InvalidOperationException(message + " Upgrade the plugin or explicitly acknowledge an exact-version exemption.");
            warning?.Invoke(message + " An exact-version exemption is active; runtime compatibility is not guaranteed.");
        };
    }
    /// <summary>
    /// Gets the templates value.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Templates { get; } =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["acp"] = Array.AsReadOnly(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-acp-app" }),
            ["web"] = Array.AsReadOnly(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" }),
            ["headless"] = Array.AsReadOnly(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-headless" }),
            ["sdk"] = Array.AsReadOnly(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-sdk-app" }),
            ["sdk-minimal"] = Array.AsReadOnly(new[] { "@deepseek-ai/dsh-sdk-minimal" }),
        });
    /// <summary>
    /// Gets the default bundles value.
    /// </summary>
    public static IReadOnlyList<string> DefaultBundles { get; } = Array.AsReadOnly(new[] { "@deepseek-ai/dsh-base" });
    /// <summary>
    /// Gets the optional bundles value.
    /// </summary>
    public static IReadOnlyList<string> OptionalBundles { get; } = Array.AsReadOnly(new[]
    {
        "@deepseek-ai/dsh-experimental-agent-team-profile",
        "@deepseek-ai/dsh-experimental-voice-input-bundle",
        "@deepseek-ai/dsh-experimental-auto-review",
        "@deepseek-ai/dsh-experimental-schedule-bundle",
    });

    /// <summary>Auto-initialize shipped names only, normalize the exact retired headless tuple, then load current files.</summary>
    public static async Task<Profile> LoadNamedAsync(string home, string name, IReadOnlyDictionary<string, string> installationBundles,
        IReadOnlyDictionary<string, string>? profileBundles = null, bool userLayer = true)
        => await LoadNamedCoreAsync(home, name, installationBundles, profileBundles, userLayer, null, null, null);

    /// <summary>Loads a named DSH profile with explicitly supplied runtime admission and exact-version grants.</summary>
    public static Task<Profile> LoadNamedAsync(string home, string name, IReadOnlyDictionary<string, string> installationBundles,
        IReadOnlyDictionary<string, string>? profileBundles, bool userLayer, DshRuntimeIdentity runtime, Action<string>? warning = null)
        => LoadNamedCoreAsync(home, name, installationBundles, profileBundles, userLayer, runtime, warning, null);

    /// <summary>Load a named DSH profile using a host-selected platform package-name adapter for exact grants.</summary>
    public static Task<Profile> LoadNamedAsync(string home, string name, IReadOnlyDictionary<string, string> installationBundles,
        IReadOnlyDictionary<string, string>? profileBundles, bool userLayer, DshRuntimeIdentity runtime, Action<string>? warning,
        Func<string, string>? compatibilityPackageName)
        => LoadNamedCoreAsync(home, name, installationBundles, profileBundles, userLayer, runtime, warning, compatibilityPackageName);

    private static async Task<Profile> LoadNamedCoreAsync(string home, string name, IReadOnlyDictionary<string, string> installationBundles,
        IReadOnlyDictionary<string, string>? profileBundles, bool userLayer, DshRuntimeIdentity? runtime, Action<string>? warning, Func<string, string>? compatibilityPackageName)
    {
        var directory = Profiles.ResolveDirectory(home, name);
        var path = Path.Combine(directory, "package.json");
        if (!File.Exists(path))
        {
            if (!Templates.TryGetValue(name, out var template)) throw new FileNotFoundException($"Profile '{name}' does not exist.", path);
            Profiles.Initialize(directory, template);
        }
        var manifest = PackageManifest.Read(path);
        if (name == "headless" && manifest.Bundles.SequenceEqual(new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "@deepseek-ai/dsh-headless" }, StringComparer.Ordinal))
        {
            var dsh = (IDictionary<string, object?>)manifest.Raw["dsh"]!;
            var profile = (IDictionary<string, object?>)dsh["profile"]!;
            profile["bundles"] = Templates[name];
            manifest.Write(path);
        }
        return await Profiles.LoadAsync(directory, installationBundles, profileBundles, userLayer,
            runtime is null ? null : CreateAdmission(directory, runtime, warning, compatibilityPackageName));
    }
}

/// <summary>The explicitly supplied running DSH version, independent of the Cordis.NET assembly version.</summary>
public sealed class DshRuntimeIdentity
{
    /// <summary>The original runtime identity spelling, including build metadata.</summary>
    public string Version { get; }
    internal DshSemver.Version SemVersion { get; }
    /// <summary>Validates an npm semantic version supplied by the DSH host.</summary>
    public DshRuntimeIdentity(string version)
    {
        if (!DshSemver.Version.TryParse(version, out var parsed)) throw new FormatException("The running DSH version must be an npm semantic version.");
        Version = version;
        SemVersion = parsed;
    }
}

/// <summary>An incompatible plugin and all of its mismatching DSH peers.</summary>
/// <param name="Name">The manifest package name.</param>
/// <param name="Version">The manifest package version.</param>
/// <param name="RuntimeVersion">The explicitly supplied DSH version.</param>
/// <param name="Peers">The original mismatching peer declarations.</param>
/// <param name="Exempted">Whether an exact grant exists for this plugin and runtime spelling.</param>
public sealed record DshPluginCompatibility(string Name, string Version, string RuntimeVersion, IReadOnlyDictionary<string, string> Peers, bool Exempted);

/// <summary>Valid grants and diagnostics from a tolerant read of profile compatibility data.</summary>
/// <param name="Exemptions">Only fully valid sibling records.</param>
/// <param name="Warnings">Read and validation failures.</param>
/// <param name="Rewritable">False if any record or the file itself is invalid.</param>
public sealed record DshCompatibilityRead(IReadOnlyDictionary<string, IReadOnlyList<string>> Exemptions, IReadOnlyList<string> Warnings, bool Rewritable);
