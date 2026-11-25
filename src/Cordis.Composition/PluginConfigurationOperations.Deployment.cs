namespace Cordis.Composition;

/// <summary>A profile-owned dependency change. A null declaration removes its selection and restore graph.</summary>
public sealed record ProfilePackageEdit(string Name, string? Declaration, bool Bundle, string? RestoreGraph = null);

/// <summary>Result of saving package declarations. Running application and notification are observed separately.</summary>
public sealed record ProfilePackageSave(
    PackageManifest Manifest,
    bool Changed,
    IReadOnlyList<string> AddedPlainDependencies);

public sealed partial class PluginConfigurationOperations
{
    /// <summary>
    /// Commit prepared package declarations and their ordered bundle selection against the originally read
    /// manifest text (null for an absent profile). New bundles are selected; retained dependencies keep their disabled state.
    /// </summary>
    /// <remarks>
    /// Prepare and approve immutable outputs outside this call. The same writer lock as online management
    /// protects the final comparison and manifest replacement. Use verifyPrepared to check that selected outputs
    /// still exist under this lock. The callback must not re-enter management or perform a restore.
    /// This operation neither launches a host nor acknowledges runtime activation.
    /// </remarks>
    public static async Task<ProfilePackageSave> CommitDeployedPackagesAsync(
        string directory,
        string? expectedManifestJson,
        IReadOnlyList<ProfilePackageEdit> edits,
        CancellationToken cancellationToken = default,
        Action? verifyPrepared = null)
    {
        directory = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(directory, "package.json");
        var next = expectedManifestJson is null
            ? Profiles.DefaultManifest(directory, [])
            : ParseManifest(expectedManifestJson);
        var previous = ConfigurationFile.Write(next.Raw, true);
        var dependencies = ChildObject(
            next.Raw,
            "dependencies",
            create: edits.Any(edit => edit.Declaration is not null));
        var previousNames = dependencies?.Keys.ToHashSet(StringComparer.Ordinal) ??
            new HashSet<string>(StringComparer.Ordinal);
        var touched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var edit in edits)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(edit.Name);
            if (!touched.Add(edit.Name))
                throw new ArgumentException($"Duplicate edit for '{edit.Name}'.", nameof(edits));
            if (edit.Declaration is null)
                dependencies?.Remove(edit.Name);
            else
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(edit.Declaration);
                if (edit.Declaration.StartsWith("link:", StringComparison.Ordinal))
                {
                    _ = PackageManifest.LinkDirectory(edit.Declaration);
                    if (edit.RestoreGraph is not null)
                        throw new ArgumentException(
                            "A linked source cannot also select a package restore graph.",
                            nameof(edits));
                }

                dependencies![edit.Name] = edit.Declaration;
            }

            if (edit.RestoreGraph is not null && edit.Declaration is null)
                throw new ArgumentException("A removed package cannot select a restore graph.", nameof(edits));
            SetRestoreGraph(next.Raw, edit.Name, edit.RestoreGraph);
        }

        var (selected, plain) = ReconcileSelection(
            next.Bundles,
            previousNames,
            edits.Select(edit => (edit.Name, edit.Declaration is not null, edit.Bundle)));
        if (!selected.SequenceEqual(next.Bundles))
            next = WithBundles(next, selected);
        var json = ConfigurationFile.Write(next.Raw, true);
        var changed = expectedManifestJson is null || json != previous;
        if (expectedManifestJson is null)
            Directory.CreateDirectory(directory);
        await using var writer = await AcquireFileLockAsync(manifestPath, cancellationToken);

        string? current = null;
        try
        {
            current = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Compare absence too. A first add must not overwrite a newly initialized profile.
        }

        if (current != expectedManifestJson)
            throw new InvalidOperationException("profile-conflict: the manifest changed during package preparation.");
        verifyPrepared?.Invoke();
        if (expectedManifestJson is null)
            Profiles.InitializePatch(directory);
        if (changed)
            await ReplaceManifestAsync(
                manifestPath,
                json,
                cancellationToken,
                overwrite: expectedManifestJson is not null);
        return new(next, changed, plain);
    }

    private static EntryOptions? ChildObject(EntryOptions parent, string key, bool create)
    {
        if (parent.TryGetValue(key, out var member))
            return member as EntryOptions ?? throw new FormatException($"Profile field '{key}' must be an object.");
        if (!create)
            return null;
        var child = new EntryOptions();
        parent[key] = child;
        return child;
    }

    private static void SetRestoreGraph(EntryOptions raw, string name, string? graph)
    {
        var create = graph is not null;
        if (ChildObject(raw, "dsh", create) is not { } dsh ||
            ChildObject(dsh, "profile", create) is not { } profile ||
            ChildObject(profile, "restore", create) is not { } restore)
            return;
        if (graph is null)
        {
            restore.Remove(name);
            if (restore.Count == 0)
                profile.Remove("restore");
        }
        else
            restore[name] = graph;
    }

    private static (List<string> Selected, List<string> Plain) ReconcileSelection(
        IReadOnlyList<string> selection,
        IReadOnlySet<string> previousDependencies,
        IEnumerable<(string Name, bool Present, bool Bundle)> edits)
    {
        var selected = selection.ToList();
        var plain = new List<string>();
        foreach (var edit in edits)
        {
            if (!edit.Present || !edit.Bundle)
                selected.RemoveAll(name => name == edit.Name);
            if (!edit.Present || previousDependencies.Contains(edit.Name))
                continue;
            if (!edit.Bundle)
                plain.Add(edit.Name);
            else if (!selected.Contains(edit.Name))
                selected.Add(edit.Name);
        }

        return (selected, plain);
    }

    private static async Task ReplaceManifestAsync(
        string path,
        string json,
        CancellationToken cancellationToken,
        bool overwrite = true)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
