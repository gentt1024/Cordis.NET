using YamlDotNet.Core;
using YamlDotNet.Core.Events;
namespace Cordis.Composition;

/// <summary>
/// Represents the profile dependency component.
/// </summary>
/// <param name="Name">The name value.</param>
/// <param name="Version">The version value.</param>
/// <param name="Bundle">The bundle value.</param>
/// <param name="Enabled">The enabled value.</param>
public sealed record ProfileDependency(string Name, string Version, bool Bundle, bool Enabled);
/// <summary>
/// Represents the profile inventory component.
/// </summary>
/// <param name="Manifest">The manifest value.</param>
/// <param name="Dependencies">The dependencies value.</param>
public sealed record ProfileInventory(PackageManifest Manifest, IReadOnlyList<ProfileDependency> Dependencies);
/// <summary>
/// Represents the profile reconciliation component.
/// </summary>
/// <param name="Inventory">The inventory value.</param>
/// <param name="AddedPlainDependencies">The added plain dependencies value.</param>
public sealed record ProfileReconciliation(ProfileInventory Inventory, IReadOnlyList<string> AddedPlainDependencies);

/// <summary>
/// Represents the profile maintenance component.
/// </summary>
public static class ProfileMaintenance
{
    /// <summary>
    /// Performs the inventory operation.
    /// </summary>
    public static ProfileInventory Inventory(string directory, IReadOnlyDictionary<string, string> installedPackages, IReadOnlyDictionary<string, string> installationBundles)
    {
        var manifest = PackageManifest.Read(Path.Combine(directory, "package.json"));
        PackageManifest? Optional(string name, IReadOnlyDictionary<string, string> map) { try { return map.TryGetValue(name, out var path) ? PackageManifest.Read(Path.Combine(path, "package.json")) : null; } catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException) { return null; } }
        var dependencies = (manifest.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ?? new EntryOptions()).Select(pair =>
        {
            var installed = Optional(pair.Key, installedPackages); var bundle = Optional(pair.Key, installationBundles) ?? installed;
            return new ProfileDependency(pair.Key, installed?.Raw.GetValueOrDefault("version") as string ?? (string)pair.Value!, bundle?.BundlePatch is not null, manifest.Bundles.Contains(pair.Key));
        }).ToArray();
        return new(manifest, dependencies);
    }
    /// <summary>
    /// Performs the write bundles operation.
    /// </summary>
    public static PackageManifest WriteBundles(string directory, PackageManifest manifest, IReadOnlyList<string> bundles)
    {
        var raw = (EntryOptions)Data.Clone(manifest.Raw)!;
        if (raw.GetValueOrDefault("dsh") is not EntryOptions dsh) raw["dsh"] = dsh = new();
        if (dsh.GetValueOrDefault("profile") is not EntryOptions profile) dsh["profile"] = profile = new();
        profile["bundles"] = bundles.ToArray(); var updated = new PackageManifest(raw); updated.Write(Path.Combine(directory, "package.json")); return updated;
    }
    /// <summary>
    /// Reconciles the requested value.
    /// </summary>
    public static ProfileReconciliation Reconcile(string directory, ProfileInventory before, bool preserveDisabled, IReadOnlyDictionary<string, string> installedPackages, IReadOnlyDictionary<string, string> installationBundles)
    {
        var after = Inventory(directory, installedPackages, installationBundles); var beforeNames = before.Dependencies.Select(d => d.Name).ToHashSet(StringComparer.Ordinal); var afterNames = after.Dependencies.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var bundleNames = after.Dependencies.Where(d => d.Bundle).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var disabled = before.Dependencies.Where(d => preserveDisabled && d.Bundle && !d.Enabled).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var bundles = after.Manifest.Bundles.Where(name => !(beforeNames.Contains(name) || afterNames.Contains(name)) || bundleNames.Contains(name)).ToList();
        foreach (var dependency in after.Dependencies) if (dependency.Bundle && !disabled.Contains(dependency.Name) && !bundles.Contains(dependency.Name)) bundles.Add(dependency.Name);
        var manifest = bundles.SequenceEqual(after.Manifest.Bundles) ? after.Manifest : WriteBundles(directory, after.Manifest, bundles);
        return new(new(manifest, after.Dependencies.Select(d => d with { Enabled = bundles.Contains(d.Name) }).ToArray()), after.Dependencies.Where(d => !d.Bundle && !beforeNames.Contains(d.Name)).Select(d => d.Name).ToArray());
    }
    /// <summary>The caller owns profile shutdown and exclusion of concurrent writes.</summary>
    public static string? Sanitize(string directory, IReadOnlyList<string> bundles, TimeProvider? timeProvider = null, Action<string, string>? move = null)
    {
        var manifestPath = Path.Combine(directory, "package.json"); var manifest = File.Exists(manifestPath) ? PackageManifest.Read(manifestPath) : null;
        var patch = Path.Combine(directory, "cordis.patch.yml"); var backupBase = patch + ".bak-" + (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds(); string? backup = backupBase; var ordinal = 0;
        while (File.Exists(backup)) backup = backupBase + "-" + ++ordinal;
        try { (move ?? File.Move)(patch, backup); } catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { backup = null; }
        if (manifest is not null) WriteBundles(directory, manifest, bundles); return backup;
    }
    /// <summary>Change only the last applicable override, preserving comments and raw expression syntax.</summary>
    public static async Task<bool> WriteEnabledAsync(string filename, string id, string name, bool enabled)
    {
        string text;
        try { text = await File.ReadAllTextAsync(filename); } catch (FileNotFoundException) { text = "[]\n"; }
        var entries = ConfigurationFile.ParseEntries(text);
        var target = entries.FindLastIndex(row => row.Id == id && !row.ContainsKey("insert") && (row.Name.Length == 0 || row.Name == name));
        if (target >= 0 && entries[target].Disabled is bool disabled && disabled == !enabled) return false;
        var parser = new Parser(new StringReader(text)); parser.Consume<StreamStart>(); parser.Consume<DocumentStart>(); var root = ReadNode(parser);
        var scalar = !enabled ? "true" : "false";
        if (target >= 0)
        {
            var node = root.Children[target];
            if (node.Fields.TryGetValue("disabled", out var field)) text = text[..field.Start] + scalar + text[field.End..];
            else if (node.Flow) text = text[..node.End] + ", disabled: " + scalar + text[node.End..];
            else
            {
                var insertion = new string(' ', node.Column) + "disabled: " + scalar + "\n";
                text = text[..node.End] + (node.End > 0 && text[node.End - 1] != '\n' ? "\n" : "") + insertion + text[node.End..];
            }
        }
        else
        {
            var added = new EntryOptions { Id = id, Disabled = !enabled };
            if (root.Flow)
            {
                var inline = ConfigurationFile.Write(added, true).Trim();
                text = text[..root.End] + (root.Children.Count == 0 ? "" : ", ") + inline + text[root.End..];
            }
            else text = text.TrimEnd() + "\n" + ConfigurationFile.Write(new[] { added });
        }
        _ = ConfigurationFile.ParseEntries(text);
        var temporary = filename + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, text);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, filename, true); return true;
    }
    private sealed class SyntaxNode(int start, int column)
    {
        public int Start { get; } = start;
        public int Column { get; } = column;
        public int End { get; set; }
        public bool Flow { get; set; }
        public List<SyntaxNode> Children { get; } = [];
        public Dictionary<string, SyntaxNode> Fields { get; } = new(StringComparer.Ordinal);
    }
    private static SyntaxNode ReadNode(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out var scalar)) return new(checked((int)scalar.Start.Index), checked((int)scalar.Start.Column)) { End = checked((int)scalar.End.Index) };
        if (parser.TryConsume<AnchorAlias>(out var alias)) return new(checked((int)alias.Start.Index), checked((int)alias.Start.Column)) { End = checked((int)alias.End.Index) };
        if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            var node = new SyntaxNode(checked((int)sequence.Start.Index), checked((int)sequence.Start.Column)) { Flow = sequence.Style == SequenceStyle.Flow };
            while (!parser.Accept<SequenceEnd>(out _)) node.Children.Add(ReadNode(parser)); node.End = checked((int)parser.Consume<SequenceEnd>().Start.Index); return node;
        }
        var mapping = parser.Consume<MappingStart>(); var result = new SyntaxNode(checked((int)mapping.Start.Index), checked((int)mapping.Start.Column)) { Flow = mapping.Style == MappingStyle.Flow };
        while (!parser.Accept<MappingEnd>(out _)) { var key = parser.Consume<Scalar>(); result.Fields[key.Value] = ReadNode(parser); }
        result.End = checked((int)parser.Consume<MappingEnd>().Start.Index); return result;
    }
}
