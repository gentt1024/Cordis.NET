using System.Text;
namespace Cordis.Composition;

/// <summary>
/// Represents the profile launch component.
/// </summary>
/// <param name="Profile">The profile value.</param>
/// <param name="Home">The home value.</param>
/// <param name="Overlays">The overlays value.</param>
/// <param name="InstallationBundles">The installation bundles value.</param>
/// <param name="LocalBundles">The local bundles value.</param>
/// <param name="TelemetryDisabledEnv">The telemetry disabled env value.</param>
public sealed record ProfileLaunch(Profile Profile, string Home, IReadOnlyList<ConfigurationLayer> Overlays, IReadOnlyDictionary<string, string> InstallationBundles, IReadOnlyDictionary<string, string>? LocalBundles = null, string? TelemetryDisabledEnv = null);
/// <summary>
/// Represents the profile refresh component.
/// </summary>
/// <param name="Layers">The layers value.</param>
/// <param name="CurrentBundles">The current bundles value.</param>
public sealed record ProfileRefresh(IReadOnlyList<ConfigurationLayer> Layers, IReadOnlyList<string> CurrentBundles);

/// <summary>
/// Represents the profile composition component.
/// </summary>
public static class ProfileComposition
{
    /// <summary>Re-read bundle/profile/home sources while retaining the launch-time overlay values.</summary>
    public static async Task<ProfileRefresh> RefreshAsync(ProfileLaunch launch)
    {
        var current = await Profiles.LoadAsync(launch.Profile.Directory, launch.InstallationBundles, launch.LocalBundles, userLayer: false);
        var homePath = Path.Combine(launch.Home, "cordis.patch.yml");
        var layers = current.Bundles.Select(bundle => new ConfigurationLayer(bundle.PatchPath, bundle.Patches))
            .Append(new ConfigurationLayer(launch.Profile.UserLayer.Source, await Profiles.ReadPatchesAsync(launch.Profile.UserLayer.Source, true)))
            .Append(new ConfigurationLayer(homePath, await Profiles.ReadPatchesAsync(homePath, true))).Concat(launch.Overlays).ToList();
        // DSH's privacy opt-out is literal: even "0" and "false" disable telemetry.
        if (!string.IsNullOrEmpty(launch.TelemetryDisabledEnv) && Profiles.Compose(layers).Any(row => row.Id == "session-telemetry-otel"))
            layers.Add(new("DSH_TELEMETRY_DISABLED", [new() { Id = "session-telemetry-otel", Disabled = true }]));
        var names = current.Bundles.Select(b => b.Name).ToArray();
        return new(layers, names);
    }
    /// <summary>
    /// Performs the flatten operation.
    /// </summary>
    public static List<EntryOptions> Flatten(IEnumerable<ConfigurationLayer> layers) => Data.Entries(Data.Clone(layers.SelectMany(layer => layer.Patches).ToList()));
    /// <summary>Render each contiguous run with the source and layers that changed it, using one flattened patch application per snapshot.</summary>
    public static async Task<string> PreviewAsync(string basePath, IReadOnlyList<ConfigurationLayer> layers, Action<string>? warn = null, string diagnosticName = "cordis")
    {
        warn ??= Console.Error.WriteLine;
        string content;
        try { content = await File.ReadAllTextAsync(basePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new IOException($"{diagnosticName}: failed to read config {basePath}: {error.Message}", error); }
        List<EntryOptions> original;
        try { original = ConfigurationFile.ParseEntries(content, Path.GetExtension(basePath) == ".json"); }
        catch (Exception error) when (error is FormatException or YamlDotNet.Core.YamlException or System.Text.Json.JsonException) { throw new FormatException($"{diagnosticName}: failed to parse config {basePath}: {error.Message}", error); }
        var previous = original;
        var previousWarnings = 0;
        var origins = original.Select(_ => (Source: Path.GetFileName(basePath), Patches: new List<string>())).ToList();
        for (var count = 1; count <= layers.Count; count++)
        {
            var warnings = new List<string>();
            var current = EntryPatches.Apply(original, Flatten(layers.Take(count)), warnings.Add);
            foreach (var warning in warnings.Skip(previousWarnings)) warn($"{diagnosticName}: [{layers[count - 1].Source}] {warning}");
            for (var index = 0; index < current.Count; index++)
            {
                if (index >= previous.Count) origins.Add((layers[count - 1].Source, []));
                else if (ConfigurationFile.Write(previous[index], true) != ConfigurationFile.Write(current[index], true)) origins[index].Patches.Add(layers[count - 1].Source);
            }
            previous = current; previousWarnings = warnings.Count;
        }
        var output = new StringBuilder(); var group = new List<EntryOptions>(); string? label = null;
        void Flush() { if (group.Count == 0) return; output.Append("# == ").AppendLine(label).Append(ConfigurationFile.Write(group)); group.Clear(); }
        for (var index = 0; index < previous.Count; index++)
        {
            var origin = origins[index]; var next = origin.Patches.Count == 0 ? origin.Source : origin.Source + ", patched by " + string.Join(", ", origin.Patches);
            if (next != label) { Flush(); label = next; }
            group.Add(previous[index]);
        }
        Flush(); return output.Length == 0 ? "[]\n" : output.ToString();
    }
}
