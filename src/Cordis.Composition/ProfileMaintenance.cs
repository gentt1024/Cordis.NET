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
public static partial class ProfileMaintenance
{
    /// <summary>
    /// Performs the inventory operation.
    /// </summary>
    public static ProfileInventory Inventory(
        string directory,
        IReadOnlyDictionary<string, string> installedPackages,
        IReadOnlyDictionary<string, string> installationBundles)
    {
        var manifest = PackageManifest.Read(Path.Combine(directory, "package.json"));

        PackageManifest? Optional(string name, IReadOnlyDictionary<string, string> map)
        {
            try
            {
                return map.TryGetValue(name, out var path)
                    ? PackageManifest.Read(Path.Combine(path, "package.json"))
                    : null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException
                                              or System.Text.Json.JsonException)
            {
                return null;
            }
        }

        var dependencies =
            (manifest.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ?? new EntryOptions())
            .Select(pair =>
            {
                var installed = Optional(pair.Key, installedPackages);
                var bundle = Optional(pair.Key, installationBundles) ?? installed;
                return new ProfileDependency(
                    pair.Key,
                    installed?.Raw.GetValueOrDefault("version") as string ?? (string)pair.Value!,
                    bundle?.HasBundleDeclaration == true,
                    manifest.Bundles.Contains(pair.Key));
            })
            .ToArray();
        return new(manifest, dependencies);
    }

    /// <summary>
    /// Performs the write bundles operation.
    /// </summary>
    public static PackageManifest WriteBundles(
        string directory,
        PackageManifest manifest,
        IReadOnlyList<string> bundles)
    {
        var raw = (EntryOptions)Data.Clone(manifest.Raw)!;
        if (raw.GetValueOrDefault("dsh") is not EntryOptions dsh)
            raw["dsh"] = dsh = new();
        if (dsh.GetValueOrDefault("profile") is not EntryOptions profile)
            dsh["profile"] = profile = new();
        profile["bundles"] = bundles.ToArray();
        var updated = new PackageManifest(raw);
        updated.Write(Path.Combine(directory, "package.json"));
        return updated;
    }

    /// <summary>
    /// Reconciles the requested value.
    /// </summary>
    public static ProfileReconciliation Reconcile(
        string directory,
        ProfileInventory before,
        bool preserveDisabled,
        IReadOnlyDictionary<string, string> installedPackages,
        IReadOnlyDictionary<string, string> installationBundles)
    {
        var after = Inventory(directory, installedPackages, installationBundles);
        var beforeNames = before.Dependencies.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var afterNames = after.Dependencies.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var bundleNames = after.Dependencies.Where(d => d.Bundle).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var disabled = before
            .Dependencies.Where(d => preserveDisabled && d.Bundle && !d.Enabled)
            .Select(d => d.Name)
            .ToHashSet(StringComparer.Ordinal);
        var bundles = after
            .Manifest.Bundles.Where(name =>
                !(beforeNames.Contains(name) || afterNames.Contains(name)) || bundleNames.Contains(name))
            .ToList();
        foreach (var dependency in after.Dependencies)
            if (dependency.Bundle && !disabled.Contains(dependency.Name) && !bundles.Contains(dependency.Name))
                bundles.Add(dependency.Name);
        var manifest = bundles.SequenceEqual(after.Manifest.Bundles)
            ? after.Manifest
            : WriteBundles(directory, after.Manifest, bundles);
        return new(
            new(
                manifest,
                after
                    .Dependencies.Select(d => d with
                    {
                        Enabled = bundles.Contains(d.Name)
                    })
                    .ToArray()),
            after.Dependencies.Where(d => !d.Bundle && !beforeNames.Contains(d.Name)).Select(d => d.Name).ToArray());
    }

    /// <summary>The caller owns profile shutdown and exclusion of concurrent writes.</summary>
    public static string? Sanitize(
        string directory,
        IReadOnlyList<string> bundles,
        TimeProvider? timeProvider = null,
        Action<string, string>? move = null)
    {
        var manifestPath = Path.Combine(directory, "package.json");
        var manifest = File.Exists(manifestPath) ? PackageManifest.Read(manifestPath) : null;
        var patch = Path.Combine(directory, "cordis.patch.yml");
        var backupBase = patch + ".bak-" + (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        string? backup = backupBase;
        var ordinal = 0;
        while (File.Exists(backup))
            backup = backupBase + "-" + ++ordinal;
        try
        {
            (move ?? File.Move)(patch, backup);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            backup = null;
        }

        if (manifest is not null)
            WriteBundles(directory, manifest, bundles);
        return backup;
    }

    /// <summary>Change only the last applicable override, preserving comments and raw expression syntax.</summary>
    public static async Task<bool> WriteEnabledAsync(string filename, string id, string name, bool enabled)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(filename);
        }
        catch (FileNotFoundException)
        {
            text = "[]\n";
        }

        var edited = WithEnabled(text, id, name, enabled);
        if (edited == text)
            return false;
        var temporary = filename + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(temporary, edited);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, filename, true);
        return true;
    }

    internal static string WithEnabled(
        string text,
        string id,
        string name,
        bool enabled,
        Func<int, bool>? implied = null)
    {
        var entries = ConfigurationFile.ParseEntries(text);
        var parser = new Parser(new StringReader(text));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();
        var root = ReadNode(parser);
        var target = -1;
        for (var index = entries.Count - 1;index >= 0;index--)
        {
            var row = entries[index];
            if (!root.Children[index].Alias && row.Id == id && !row.ContainsKey("insert") &&
                (row.Name.Length == 0 || row.Name == name))
            {
                target = index;
                break;
            }
        }
        if (implied is not null && (target < 0 || root.Children[target].PlainSwitch) &&
            implied(target))
        {
            var pruned = target < 0 ? text : RemoveSwitch(text, root, target);
            _ = ConfigurationFile.ParseEntries(pruned);
            return pruned;
        }

        if (target >= 0 && entries[target].Disabled is bool disabled && disabled == !enabled)
            return text;
        var scalar = !enabled ? "true" : "false";
        if (target >= 0)
        {
            var node = root.Children[target];
            if (node.Fields.TryGetValue("disabled", out var field))
                text = text[..field.Start] + (field.Anchor is { } anchor ? "&" + anchor + " " : "") + scalar +
                    text[field.End..];
            else if (node.Flow)
                text = text[..node.End] + ", disabled: " + scalar + text[node.End..];
            else
            {
                var insertion = new string(' ', node.Column - 1) + "disabled: " + scalar + "\n";
                text = text[..node.End] + (node.End > 0 && text[node.End - 1] != '\n' ? "\n" : "") + insertion +
                    text[node.End..];
            }
        }
        else
        {
            var added = new EntryOptions
            {
                Id = id,
                Disabled = !enabled
            };
            if (root.Flow)
            {
                var inline = ConfigurationFile.Write(added, true).Trim();
                text = text[..root.End] + (root.Children.Count == 0 ? "" : ", ") + inline + text[root.End..];
            }
            else
                text = text.TrimEnd() + "\n" + ConfigurationFile.Write(new[] { added });
        }

        _ = ConfigurationFile.ParseEntries(text);
        return text;
    }

    private static string RemoveSwitch(string text, SyntaxNode root, int index)
    {
        var node = root.Children[index];
        if (root.Flow)
        {
            var comma = FlowSeparator(text, node.ContentEnd);
            if (comma >= 0)
                text = text.Remove(comma, 1);
            text = text.Remove(node.Start, node.ContentEnd - node.Start);
            if (comma < 0 && index > 0)
                text = text.Remove(FlowSeparator(text, root.Children[index - 1].ContentEnd), 1);
            return text;
        }

        var start = text.LastIndexOf('\n', node.Start) + 1;
        var end = EndOfLine(text, node.Flow ? node.ContentEnd : node.Fields.Values.Max(field => field.ContentEnd));
        var comments = string.Concat(text[start..end].Split('\n')
            .Where(line => line.TrimStart().StartsWith('#')).Select(line => line + "\n"));
        return text[..start] + comments + (root.Children.Count == 1 ? "[]\n" : "") + text[end..];
    }

    /// <summary>Grants or revokes an exact DSH compatibility exemption, independently of manifest and patch files.</summary>
    public static async Task SetVersionExemptionAsync(
        string directory,
        string packageVersion,
        string runtimeVersion,
        DshRuntimeIdentity runtime,
        bool enabled,
        bool acceptRisk = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        DshProfilePolicy.ValidatePackageVersion(packageVersion);
        DshProfilePolicy.ValidateExactVersion(runtimeVersion);
        if (enabled && (!acceptRisk || runtimeVersion != runtime.Version))
            throw new InvalidOperationException(
                "Granting an exemption requires explicit risk acknowledgment and the current exact DSH version.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, DshProfilePolicy.CompatibilityFilename);
        await using var fileLock = await AcquireCompatibilityLockAsync(path + ".lock", cancellationToken);
        var read = DshProfilePolicy.ReadCompatibility(directory);
        if (!read.Rewritable)
            throw new InvalidOperationException(
                "Cannot rewrite corrupt compatibility data: " + string.Join(" ", read.Warnings));
        var grants = read.Exemptions.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.Ordinal);
        if (enabled)
        {
            if (!grants.TryGetValue(packageVersion, out var versions))
                grants[packageVersion] = versions = [];
            if (!versions.Contains(runtimeVersion, StringComparer.Ordinal))
                versions.Add(runtimeVersion);
        }
        else if (grants.TryGetValue(packageVersion, out var versions))
        {
            versions.RemoveAll(version => version == runtimeVersion);
            if (versions.Count == 0)
                grants.Remove(packageVersion);
        }

        using var buffer = new MemoryStream();
        using (var json = new System.Text.Json.Utf8JsonWriter(
                   buffer,
                   new()
                   {
                       Indented = true,
                       Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                   }))
        {
            json.WriteStartObject();
            foreach (var (package, versions) in grants)
            {
                json.WriteStartArray(package);
                foreach (var version in versions)
                    json.WriteStringValue(version);
                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, buffer.ToArray(), cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static async Task<FileStream> AcquireCompatibilityLockAsync(
        string path,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 ||
                                            OperatingSystem.IsMacOS() && (error.HResult & 0xffff) == 35)
            {
                await Task.Delay(20, cancellationToken);
            }
        }
    }

    private sealed class SyntaxNode(int start, int column)
    {
        public string? Anchor
        {
            get; init;
        }
        public bool Alias
        {
            get; init;
        }
        public string? ScalarValue
        {
            get; init;
        }
        public bool PlainSwitch
        {
            get; set;
        }

        public int Start
        {
            get;
        } = start;

        public int Column
        {
            get;
        } = column;

        public int End
        {
            get;
            set;
        }

        public int ContentEnd
        {
            get;
            set;
        }

        public int EndColumn
        {
            get;
            set;
        }

        public int KeyStart
        {
            get;
            set;
        }

        public bool Container
        {
            get;
            set;
        }

        public bool Flow
        {
            get;
            set;
        }

        public List<SyntaxNode> Children
        {
            get;
        } = [];

        public Dictionary<string, SyntaxNode> Fields
        {
            get;
        } = new(StringComparer.Ordinal);
    }

    private static SyntaxNode ReadNode(IParser parser, Dictionary<string, SyntaxNode>? anchors = null)
    {
        anchors ??= new(StringComparer.Ordinal);
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            var node = new SyntaxNode(checked((int)scalar.Start.Index), checked((int)scalar.Start.Column))
            {
                End = checked((int)scalar.End.Index),
                ContentEnd = checked((int)scalar.End.Index),
                ScalarValue = scalar.Value,
                Anchor = scalar.Anchor.IsEmpty ? null : scalar.Anchor.Value
            };
            if (node.Anchor is { } anchor)
                anchors[anchor] = node;
            return node;
        }
        if (parser.TryConsume<AnchorAlias>(out var alias))
            return new(checked((int)alias.Start.Index), checked((int)alias.Start.Column))
            {
                End = checked((int)alias.End.Index),
                ContentEnd = checked((int)alias.End.Index),
                Alias = true,
                ScalarValue = anchors.GetValueOrDefault(alias.Value.Value)?.ScalarValue
            };
        if (parser.TryConsume<SequenceStart>(out var sequence))
        {
            var node = new SyntaxNode(checked((int)sequence.Start.Index), checked((int)sequence.Start.Column))
            {
                Flow = sequence.Style == SequenceStyle.Flow,
                Container = true
            };
            while (!parser.Accept<SequenceEnd>(out _))
                node.Children.Add(ReadNode(parser, anchors));
            var end = parser.Consume<SequenceEnd>();
            node.End = checked((int)end.Start.Index);
            node.ContentEnd = node.Flow ? checked(node.End + 1) : node.End;
            node.EndColumn = checked((int)end.Start.Column);
            return node;
        }

        var mapping = parser.Consume<MappingStart>();
        var result = new SyntaxNode(checked((int)mapping.Start.Index), checked((int)mapping.Start.Column))
        {
            Flow = mapping.Style == MappingStyle.Flow,
            Container = true,
            PlainSwitch = mapping.Anchor.IsEmpty
        };
        while (!parser.Accept<MappingEnd>(out _))
        {
            var key = ReadNode(parser, anchors);
            var value = ReadNode(parser, anchors);
            value.KeyStart = key.Start;
            result.Fields[key.ScalarValue!] = value;
            result.PlainSwitch &= !key.Alias && key.Anchor is null && key.ScalarValue is "id" or "disabled" &&
                !value.Alias && value.Anchor is null && value.ScalarValue is not null;
        }

        var ending = parser.Consume<MappingEnd>();
        result.End = checked((int)ending.Start.Index);
        result.ContentEnd = result.Flow ? checked(result.End + 1) : result.End;
        result.EndColumn = checked((int)ending.Start.Column);
        return result;
    }
}
