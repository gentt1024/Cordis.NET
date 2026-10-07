using System.Security.Cryptography;
using System.Text;

namespace Cordis.Composition;

public sealed partial class PluginConfigurationOperations
{
    /// <summary>Optional product admission before publication or selection persistence, inside existing profile coordination.</summary>
    /// <remarks>Throw to reject. Do not re-enter mutations. Product-owned inputs must remain stable in this window.
    /// Null preserves normal library consumption without a product policy.</remarks>
    public Func<ProfileCandidate, Task>? AdmitProfileAsync
    {
        get;
        set;
    }

    private Func<ProfileCandidate, IReadOnlySet<string>?, Task<IReadOnlyList<EntryDiagnostic>>>? candidateReconcile;
    private Func<IReadOnlySet<string>?, Task<IReadOnlyList<EntryDiagnostic>>>? candidateReconcileOwner;

    /// <summary>Session-owned application of the same approved composition, without re-entering its queue.</summary>
    /// <remarks>Assignment associates this callback with the current ReconcileAsync. Replacing that legacy callback
    /// requires explicitly assigning a candidate callback again before using product admission.</remarks>
    public Func<ProfileCandidate, IReadOnlySet<string>?, Task<IReadOnlyList<EntryDiagnostic>>>? ReconcileCandidateAsync
    {
        get => candidateReconcile;
        set
        {
            candidateReconcile = value;
            candidateReconcileOwner = ReconcileAsync;
        }
    }

    private bool HasCandidateReconciliation =>
        candidateReconcile is not null && candidateReconcileOwner == ReconcileAsync;

    /// <summary>Read a detached metadata editing baseline through the existing profile owner.</summary>
    public async Task<ProfileDocument> ReadProfileAsync(CancellationToken cancellationToken = default)
    {
        ProfileDocument result = null!;
        await ConfigurationTransactionAsync(
            async () =>
            {
                var inputs = await CaptureProfileAsync();
                result = new(await inputs.ReadAsync(ManifestPath), inputs.Revision);
            },
            cancellationToken);
        return result;
    }

    /// <summary>Save metadata against a captured revision. Dependencies and dsh policy/selection remain management-owned.</summary>
    /// <remarks>Waits for existing operations, then rejects a stale revision; never merges or replays a save.</remarks>
    public Task<ConfigurationChange> SaveProfileMetadataAsync(
        string manifestJson,
        string expectedRevision,
        CancellationToken cancellationToken = default) => ChangeAsync(
        "profile",
        false,
        "profile",
        async () =>
        {
            var inputs = await CaptureProfileAsync();
            if (inputs.Revision != expectedRevision)
                throw new Refusal("profile-conflict");
            var next = ParseManifest(manifestJson);
            var previous = ParseManifest(await inputs.ReadAsync(ManifestPath));
            foreach (var key in new[] { "dependencies", "dsh" })
                if (ConfigurationFile.Write(next.Raw.GetValueOrDefault(key), true) !=
                    ConfigurationFile.Write(previous.Raw.GetValueOrDefault(key), true))
                    throw new Refusal("managed-profile-field");
            var candidate = await CreateCandidateAsync(inputs, next);
            await AdmitCandidateAsync(inputs, candidate);
            await SaveCandidateAsync(inputs, candidate);
            return (null, Array.Empty<EntryDiagnostic>());
        },
        cancellationToken);

    private static PackageManifest ParseManifest(string text) =>
        new(
            ConfigurationFile.Parse(text, true) as EntryOptions ??
            throw new FormatException("Profile must be a JSON object."));

    private async Task<ProfileInputs> CaptureProfileAsync()
    {
        var inputs = new ProfileInputs(launch);
        var manifest = ParseManifest(await inputs.ReadAsync(ManifestPath));
        await inputs.ReadAsync(include.Filename);
        await inputs.ReadOptionalAsync(Path.Combine(launch.Profile.Directory, DshProfilePolicy.CompatibilityFilename));
        await ProfileComposition.RefreshAsync(inputs.Launch, manifest, inputs.ReadAsync);
        await inputs.VerifyAsync();
        return inputs;
    }

    private async Task<ProfileCandidate> CreateCandidateAsync(
        ProfileInputs inputs,
        PackageManifest manifest,
        PreparedPackage? prepared = null)
    {
        var proposal = inputs.Launch;
        if (prepared is not null)
        {
            var local = new Dictionary<string, string>(
                proposal.LocalBundles ?? new Dictionary<string, string>(),
                StringComparer.Ordinal)
            {
                [prepared.Name] = Path.GetFullPath(prepared.PublicationDirectory ?? prepared.Directory)
            };
            proposal = proposal with
            {
                LocalBundles = local
            };
        }

        var refresh = await ProfileComposition.RefreshAsync(proposal, manifest, inputs.ReadCandidateAsync);
        var candidate = new ProfileCandidate(
            inputs.Revision,
            ConfigurationFile.Write(manifest.Raw, true),
            include.Filename,
            await inputs.ReadAsync(include.Filename),
            refresh,
            prepared);
        await inputs.VerifyAsync();
        return candidate;
    }

    private async Task AdmitCandidateAsync(ProfileInputs inputs, ProfileCandidate candidate)
    {
        await inputs.VerifyAsync();
        if (AdmitProfileAsync is not null && ReconcileAsync is not null && !HasCandidateReconciliation &&
            RunExclusiveAsync is not null)
            throw new Refusal("candidate-reconciliation-required");
        if (AdmitProfileAsync is { } admit)
            await admit(candidate);
        await inputs.VerifyAsync();
    }

    private async Task SaveCandidateAsync(ProfileInputs inputs, ProfileCandidate candidate, bool published = false)
    {
        var temporary = ManifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, candidate.ManifestJson);
            await inputs.VerifyAsync(published);
            File.Move(temporary, ManifestPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private async Task<IReadOnlyList<EntryDiagnostic>> ApplyCandidateAsync(
        ProfileCandidate candidate,
        IReadOnlySet<string>? required)
    {
        if (RunExclusiveAsync is null)
            return [];
        if (HasCandidateReconciliation && ReconcileCandidateAsync is { } reconcile)
            return await reconcile(candidate, required);
        if (AdmitProfileAsync is null && ReconcileAsync is { } legacy)
            return await legacy(required);
        return await ApplicationBoot.ReconcileAsync(include, candidate, required);
    }

    private sealed class ProfileInputs(ProfileLaunch source)
    {
        private static StringComparer PathComparer => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private readonly Dictionary<string, byte[]?> files = new(PathComparer);
        private PreparedPackage? publication;
        private string? removal;
        private Dictionary<string, string>? preparedFiles;

        public ProfileLaunch Launch
        {
            get;
        } = source with
        {
            InstallationBundles = new Dictionary<string, string>(source.InstallationBundles, StringComparer.Ordinal),
            LocalBundles = new Dictionary<string, string>(
                source.LocalBundles ?? new Dictionary<string, string>(),
                StringComparer.Ordinal),
            Overlays = source
                .Overlays.Select(layer => layer with
                {
                    Patches = ProfileComposition.Flatten([layer])
                })
                .ToArray()
        };

        public string Revision
        {
            get
            {
                var values = files
                    .OrderBy(pair => pair.Key, PathComparer)
                    .Select(pair =>
                        new[]
                        {
                            pair.Key, pair.Value is null ? null : Convert.ToHexString(SHA256.HashData(pair.Value))
                        })
                    .ToArray();
                return Convert.ToHexString(
                    SHA256.HashData(
                        Encoding.UTF8.GetBytes(
                            ConfigurationFile.Write(
                                new object?[]
                                {
                                    values,
                                    Map(Launch.InstallationBundles),
                                    Map(Launch.LocalBundles),
                                    ProfileComposition.Flatten(Launch.Overlays)
                                },
                                true))));
            }
        }

        private static string Map(IReadOnlyDictionary<string, string>? map)
        {
            var sorted = (map ?? new Dictionary<string, string>())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => (object?)Path.GetFullPath(pair.Value));
            return ConfigurationFile.Write(sorted, true);
        }

        public async Task<byte[]?> ReadOptionalAsync(string path)
        {
            path = Path.GetFullPath(path);
            if (files.TryGetValue(path, out var bytes))
                return bytes;
            bytes = await ReadBytesAsync(path);
            files.Add(path, bytes);
            return bytes;
        }

        public async Task<string> ReadAsync(string path)
        {
            var bytes = await ReadOptionalAsync(path) ??
                throw new FileNotFoundException("Missing profile input.", path);
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
            return await reader.ReadToEndAsync();
        }

        public Task<string> ReadCandidateAsync(string path)
        {
            if (publication is { } package)
            {
                var target = Path.GetFullPath(package.PublicationDirectory ?? package.Directory);
                var relative = Path.GetRelativePath(target, Path.GetFullPath(path));
                if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(
                        ".." + Path.DirectorySeparatorChar,
                        StringComparison.Ordinal))
                    return ReadAsync(Path.Combine(package.Directory, relative));
            }

            return ReadAsync(path);
        }

        public void PlanPublication(PreparedPackage package)
        {
            publication = package;
            preparedFiles = Tree(package.Directory);
        }

        public void PlanRemoval(string name) => removal = name;

        public async Task VerifyAsync(bool published = false, string? savedManifest = null)
        {
            var expectedLocal = new Dictionary<string, string>(Launch.LocalBundles!, StringComparer.Ordinal);
            if (removal is { } removed && source.LocalBundles?.ContainsKey(removed) != true)
                expectedLocal.Remove(removed);
            if (published && publication is { } package && source.LocalBundles is not null)
                expectedLocal.Add(package.Name, Path.GetFullPath(package.PublicationDirectory ?? package.Directory));
            if (Map(source.InstallationBundles) != Map(Launch.InstallationBundles) ||
                Map(source.LocalBundles) != Map(expectedLocal) ||
                ConfigurationFile.Write(ProfileComposition.Flatten(source.Overlays), true) !=
                ConfigurationFile.Write(ProfileComposition.Flatten(Launch.Overlays), true))
                throw new Refusal("profile-conflict");
            foreach (var (path, expected) in files)
            {
                var actualPath = path;
                if (published && publication is { } moved)
                {
                    var relative = Path.GetRelativePath(Path.GetFullPath(moved.Directory), path);
                    if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(
                            ".." + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal))
                        actualPath = Path.Combine(moved.PublicationDirectory ?? moved.Directory, relative);
                }

                var actual = await ReadBytesAsync(actualPath);
                var expectedBytes = savedManifest is not null && PathComparer.Equals(
                    path,
                    Path.GetFullPath(Path.Combine(source.Profile.Directory, "package.json")))
                    ? Encoding.UTF8.GetBytes(savedManifest)
                    : expected;
                if (expectedBytes is null
                        ? actual is not null
                        : actual is null || !expectedBytes.AsSpan().SequenceEqual(actual))
                    throw new Refusal("profile-conflict");
            }

            if (publication is { } prepared)
            {
                var actual = Tree(published ? prepared.PublicationDirectory ?? prepared.Directory : prepared.Directory);
                if (actual.Count != preparedFiles!.Count || actual.Any(pair =>
                        !preparedFiles.TryGetValue(pair.Key, out var expected) || pair.Value != expected))
                    throw new Refusal("profile-conflict");
            }
        }

        private static async Task<byte[]?> ReadBytesAsync(string path)
        {
            try
            {
                return await File.ReadAllBytesAsync(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        private static Dictionary<string, string> Tree(string directory) => Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, PathComparer)
            .ToDictionary(
                path => Path.GetRelativePath(directory, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                PathComparer);
    }
}
