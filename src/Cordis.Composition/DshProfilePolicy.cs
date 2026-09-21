using System.Collections.ObjectModel;

namespace Cordis.Composition;

/// <summary>The pinned DSH named-profile discovery and migration policy.</summary>
public static class DshProfilePolicy
{
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
    public static IReadOnlyList<string> OptionalBundles { get; } = Array.AsReadOnly(new[] { "@deepseek-ai/dsh-experimental-agent-team-profile", "@deepseek-ai/dsh-experimental-agent-team-web-profile" });

    /// <summary>Auto-initialize shipped names only, normalize the exact retired headless tuple, then load current files.</summary>
    public static async Task<Profile> LoadNamedAsync(string home, string name, IReadOnlyDictionary<string, string> installationBundles,
        IReadOnlyDictionary<string, string>? profileBundles = null, bool userLayer = true)
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
        return await Profiles.LoadAsync(directory, installationBundles, profileBundles, userLayer);
    }
}
