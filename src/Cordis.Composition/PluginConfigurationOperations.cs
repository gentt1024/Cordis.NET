using Cordis;

namespace Cordis.Composition;

/// <summary>
/// A profile entry's configured enablement, runtime state and management address.
/// </summary>
/// <param name="EntryId">The mounted entry's runtime address.</param>
/// <param name="ModuleName">The module selected by the entry.</param>
/// <param name="Enabled">Configured enablement; this does not guarantee activation.</param>
/// <param name="PatchId">The unambiguous profile patch address, or null when the entry cannot be managed.</param>
/// <param name="ReadOnlyReason">The reason mutations are refused, or null for a manageable entry.</param>
/// <param name="State">Current fiber state, or null when no fiber is mounted.</param>
public sealed record PluginConfigurationInfo(
    string EntryId,
    string ModuleName,
    bool Enabled,
    string? PatchId,
    string? ReadOnlyReason,
    FiberState? State);

/// <summary>
/// An inserted bundle row and its mounted entry, if one can be identified.
/// </summary>
/// <param name="RowId">The row's patch address.</param>
/// <param name="ModuleName">The module declared by the row.</param>
/// <param name="EntryId">The current mounted entry mapping, or null when unavailable.</param>
public sealed record BundleConfigurationRow(string RowId, string ModuleName, string? EntryId);

/// <summary>
/// Bundle selection, installation and management diagnostics for the current profile.
/// </summary>
/// <param name="Name">The bundle package name.</param>
/// <param name="Version">Declared package version, when available.</param>
/// <param name="Description">Package description, when available.</param>
/// <param name="Enabled">Whether the profile selects the bundle.</param>
/// <param name="Installed">Whether the profile declares the package dependency.</param>
/// <param name="Optional">Whether the host marks this bundle as optional in inventory.</param>
/// <param name="Removable">Whether this profile owns the dependency and permits its removal.</param>
/// <param name="ReadOnlyReason">The reason management is refused, or null.</param>
/// <param name="Error">Bundle loading failure text, or null.</param>
/// <param name="Rows">Inserted rows and their runtime mappings.</param>
/// <param name="Overrides">Patch addresses overridden by this bundle.</param>
public sealed record BundleConfigurationInfo(
    string Name,
    string? Version,
    string? Description,
    bool Enabled,
    bool Installed,
    bool Optional,
    bool Removable,
    string? ReadOnlyReason,
    string? Error,
    IReadOnlyList<BundleConfigurationRow> Rows,
    IReadOnlyList<string> Overrides);

/// <summary>
/// The persistence and application outcomes of one management operation.
/// </summary>
/// <param name="Changed">Whether profile manifest or patch bytes changed; this is separate from runtime application.</param>
/// <param name="Application">Application outcome: applied, restart-required, overridden or failed.</param>
/// <param name="Target">The affected entry, bundle or exact compatibility package/version identity.</param>
/// <param name="Enabled">The requested enablement state.</param>
/// <param name="Error">Stable failure code, if any.</param>
/// <param name="Diagnostic">Primary failure text, if available.</param>
/// <param name="Warnings">Entry diagnostics produced by auditing the operation.</param>
public sealed record ConfigurationChange(
    bool Changed,
    string Application,
    string Target,
    bool Enabled,
    string? Error = null,
    string? Diagnostic = null,
    IReadOnlyList<EntryDiagnostic>? Warnings = null);

/// <summary>Profile configuration and package coordination. Platform toolchains and transports reuse this owner above Core.</summary>
public sealed partial class PluginConfigurationOperations(
    ProfileLaunch launch,
    Include include,
    string? ownerEntryId = null)
{
    private readonly SemaphoreSlim mutation = new(1, 1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> managementBundles =
        new(StringComparer.Ordinal);

    private string ManifestPath => Path.Combine(launch.Profile.Directory, "package.json");
    private string PatchPath => launch.Profile.UserLayer.Source;

    /// <summary>Host lifecycle queue for mutations. ProfileSession supplies its queue even without automatic watching; null means startup-only configuration.</summary>
    public Func<Func<Task>, Task>? RunExclusiveAsync
    {
        get;
        set;
    }

    /// <summary>Optional session-owned reconciliation called inside the existing exclusive queue, without re-entering it.</summary>
    public Func<IReadOnlySet<string>?, Task<IReadOnlyList<EntryDiagnostic>>>? ReconcileAsync
    {
        get;
        set;
    }

    /// <summary>Modules needed to retain the management path. Hosts may extend this set for their own control plane.</summary>
    public ISet<string> ProtectedModules
    {
        get;
    } = new HashSet<string>(
        [
            "cordis:manager",
            "cordis:include",
            "cordis:loader",
            "cordis:timer",
            "@deepseek-ai/dsh-plugin-manager",
            "@deepseek-ai/cordis-plugin-loader",
            "@deepseek-ai/cordis-plugin-include",
            "@deepseek-ai/dsh-api-gateway",
            "@deepseek-ai/dsh-host-webserver",
            "@deepseek-ai/dsh-client-modules",
            "@deepseek-ai/dsh-client-ui-settings-plugin-inventory",
            "@deepseek-ai/dsh-client-ui-plugin-manager",
            "@deepseek-ai/dsh-host-plugin-inventory",
            "@deepseek-ai/dsh-typert-registry",
            "@deepseek-ai/dsh-api-remotes",
            "@deepseek-ai/cordis-plugin-timer",
            "@deepseek-ai/dsh-client-connection",
            "@deepseek-ai/dsh-host-frontend-static",
            "@deepseek-ai/dsh-tools",
            "@deepseek-ai/dsh-hmr"
        ],
        StringComparer.Ordinal);

    /// <summary>
    /// Host-selected optional bundle inventory labels. This set does not authorize mutations.
    /// </summary>
    public ISet<string> OptionalBundles
    {
        get;
    } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Reports completed configuration, plugin or bundle operations synchronously. Observer failures are logged and isolated from the operation result and other observers.
    /// </summary>
    /// <remarks>Some notification paths run inside mutation coordination; handlers must not synchronously re-enter management mutations.</remarks>
    public event Action<string>? Changed;

    /// <summary>Reconcile a completed external deployment without reactivating retained dependencies. The host owns deployment success and exclusion of concurrent writes.</summary>
    public static async Task<ProfileReconciliation> ReconcileDeployedPackagesAsync(
        string directory,
        PackageManifest before,
        IReadOnlyDictionary<string, string> installedPackages,
        IReadOnlyDictionary<string, string> installationBundles)
    {
        var manifestPath = Path.Combine(directory, "package.json");
        var after = PackageManifest.Read(manifestPath);
        var oldDependencies =
            (before.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ?? new EntryOptions()).Keys
            .ToHashSet(StringComparer.Ordinal);
        var dependencies =
            (after.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ?? new EntryOptions()).Keys
            .ToArray();

        (PackageManifest Manifest, string Directory) Resolve(string name)
        {
            if (!installationBundles.TryGetValue(name, out var packageDirectory) &&
                !installedPackages.TryGetValue(name, out packageDirectory))
                throw new FileNotFoundException($"Cannot resolve deployed package '{name}'.");
            return (PackageManifest.Read(Path.Combine(packageDirectory, "package.json")), packageDirectory);
        }

        var selected = after
            .Bundles.Where(name =>
                !oldDependencies.Contains(name) && !dependencies.Contains(name) ||
                dependencies.Contains(name) && Resolve(name).Manifest.HasBundleDeclaration)
            .ToList();
        var plain = new List<string>();
        foreach (var name in dependencies)
        {
            if (oldDependencies.Contains(name))
                continue;
            var package = Resolve(name);
            if (!package.Manifest.HasBundleDeclaration)
            {
                plain.Add(name);
                continue;
            }

            await Profiles.ReadBundleAsync(name, package.Directory, package.Manifest);
            if (!selected.Contains(name))
                selected.Add(name);
        }

        if (!selected.SequenceEqual(after.Bundles))
        {
            var raw = (EntryOptions)Data.Clone(after.Raw)!;
            if (raw.GetValueOrDefault("dsh") is not EntryOptions dsh)
                raw["dsh"] = dsh = new();
            if (dsh.GetValueOrDefault("profile") is not EntryOptions profile)
                dsh["profile"] = profile = new();
            profile["bundles"] = selected;
            var temporary = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, ConfigurationFile.Write(raw, true));
                File.Move(temporary, manifestPath, true);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        return new(ProfileMaintenance.Inventory(directory, installedPackages, installationBundles), plain);
    }

    /// <summary>
    /// List mounted entries with their current state and profile management addresses.
    /// </summary>
    /// <remarks>Reads current profile sources without refreshing the runtime or evaluating configuration expressions.
    /// Protected or ambiguous entries remain listed with a read-only reason. Profile manifest and user patch read or parse failures propagate.</remarks>
    public async Task<IReadOnlyList<PluginConfigurationInfo>> ListPluginsAsync()
    {
        var profile = await Profiles.LoadAsync(
            launch.Profile.Directory,
            launch.InstallationBundles,
            launch.LocalBundles,
            userLayer: false);
        var profileLayers = profile
            .Bundles.SelectMany(bundle => bundle.PatchLayers)
            .Append(new ConfigurationLayer(PatchPath, await Profiles.ReadPatchesAsync(PatchPath, true)));
        var rows = Flatten(Profiles.Compose(profileLayers)).ToArray();
        var result = new List<PluginConfigurationInfo>();
        await include.Context.RunAsync(_ =>
        {
            foreach (var entry in include.Loader.Entries())
            {
                var candidates = rows.Where(row => row.Id == entry.Options.Id).ToArray();
                var reason = ProtectedModules.Contains(entry.Options.Name) || entry.Id == ownerEntryId
                    ? "management-required"
                    : candidates.Length != 1 || candidates[0].Name != entry.Options.Name || entry.Parent.Tree != include
                        ? "unaddressable"
                        : null;
                result.Add(
                    new(
                        entry.Id,
                        entry.Options.Name,
                        !entry.Disabled,
                        reason is null ? candidates[0].Id : null,
                        reason,
                        entry.Fiber?.State));
            }

            return Task.CompletedTask;
        });
        return result;
    }

    /// <summary>
    /// List selected, profile-dependent and installation bundles with runtime mappings and management diagnostics.
    /// </summary>
    /// <remarks>Does not install packages or refresh the runtime. Individual bundle failures are returned as diagnostics;
    /// failures reading the profile manifest or obtaining runtime mappings propagate.</remarks>
    public async Task<IReadOnlyList<BundleConfigurationInfo>> ListBundlesAsync()
    {
        var manifest = PackageManifest.Read(ManifestPath);
        var dependencies = manifest.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ??
            new EntryOptions();
        var names = manifest
            .Bundles.Concat(dependencies.Keys)
            .Concat(launch.InstallationBundles.Keys)
            .Distinct(StringComparer.Ordinal);
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        await include.Context.RunAsync(_ =>
        {
            foreach (var entry in include.Loader.Entries())
                live[entry.Options.Id] = entry.Id;
            return Task.CompletedTask;
        });
        var result = new List<BundleConfigurationInfo>();
        foreach (var name in names)
        {
            var enabled = manifest.Bundles.Contains(name);
            var installed = dependencies.ContainsKey(name);
            var removable = installed && !launch.InstallationBundles.ContainsKey(name);
            try
            {
                var info = await ReadBundleAsync(name);
                if (info is null)
                {
                    if (enabled)
                        result.Add(
                            new(
                                name,
                                null,
                                null,
                                true,
                                installed,
                                OptionalBundles.Contains(name),
                                removable,
                                null,
                                "not-bundle",
                                [],
                                []));
                    continue;
                }

                var (metadata, patches) = info.Value;
                var declared =
                    Flatten(Profiles.Compose([new("bundle", patches.Where(p => p.ContainsKey("insert")).ToList())]))
                        .Where(p => p.Id.Length > 0 && p.Name.Length > 0)
                        .ToArray();
                var ids = declared.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                if (ProtectsManager(patches))
                    managementBundles.TryAdd(name, 0);
                var reason = managementBundles.ContainsKey(name) ? "management-required" : null;
                result.Add(
                    new(
                        name,
                        metadata.Raw.GetValueOrDefault("version") as string,
                        metadata.Raw.GetValueOrDefault("description") as string,
                        enabled,
                        installed,
                        OptionalBundles.Contains(name),
                        removable && reason is null,
                        reason,
                        null,
                        declared
                            .Select(row => new BundleConfigurationRow(row.Id, row.Name, live.GetValueOrDefault(row.Id)))
                            .ToArray(),
                        patches
                            .Where(p => !p.ContainsKey("insert") && p.Id.Length > 0 && !ids.Contains(p.Id))
                            .Select(p => p.Id)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray()));
            }
            catch (Exception error)
            {
                if (enabled || installed)
                    result.Add(
                        new(
                            name,
                            null,
                            null,
                            enabled,
                            installed,
                            OptionalBundles.Contains(name),
                            removable && !managementBundles.ContainsKey(name),
                            managementBundles.ContainsKey(name) ? "management-required" : null,
                            error.Message,
                            [],
                            []));
            }
        }

        return result;
    }

    /// <summary>
    /// Persist enablement for an addressable profile entry and reconcile through the host lifecycle queue.
    /// </summary>
    /// <remarks>Protected or unaddressable entries are refused. Persistence precedes reconciliation; a failed result may
    /// leave the requested patch saved. With no lifecycle queue, the change requires restart. Later overlays may override it.</remarks>
    public Task<ConfigurationChange> SetPluginEnabledAsync(
        string entryId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            entryId,
            enabled,
            "plugin",
            async () =>
            {
                var row = (await ListPluginsAsync()).FirstOrDefault(row => row.EntryId == entryId) ??
                    throw new Refusal("unknown-plugin");
                if (row.ReadOnlyReason is not null)
                    throw new Refusal(row.ReadOnlyReason);
                await ProfileMaintenance.WriteEnabledAsync(PatchPath, row.PatchId!, row.ModuleName, enabled);
                var warnings = await ReloadAsync(
                    enabled ? new HashSet<string>([row.PatchId!], StringComparer.Ordinal) : null);
                var current = (await ListPluginsAsync()).FirstOrDefault(item => item.EntryId == entryId);
                return (RunExclusiveAsync is not null && current?.Enabled != enabled ? "overridden" : null, warnings);
            },
            cancellationToken);

    /// <summary>
    /// Persist bundle selection and reconcile through the host lifecycle queue, retaining package dependencies.
    /// </summary>
    /// <remarks>Bundles required by the management path cannot be deselected. Persistence precedes reconciliation;
    /// failure does not automatically restore selection. With no lifecycle queue, the change requires restart.</remarks>
    public Task<ConfigurationChange> SetBundleEnabledAsync(
        string name,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            name,
            enabled,
            "bundle",
            async () =>
            {
                var inputs = await CaptureProfileAsync();
                var manifest = PackageManifest.Read(ManifestPath);
                var previous = manifest.Bundles;
                var info = await ReadBundleAsync(name);
                if ((enabled || !previous.Contains(name)) && info is null)
                    throw new Refusal("not-bundle");
                if (!enabled && previous.Contains(name) && info is not null && ProtectsManager(info.Value.Patches))
                    throw new Refusal("management-required");
                var next = enabled
                    ? previous.Concat(previous.Contains(name) ? [] : new[] { name }).ToArray()
                    : previous.Where(item => item != name).ToArray();
                var candidate = await CreateCandidateAsync(inputs, WithBundles(manifest, next));
                await AdmitCandidateAsync(inputs, candidate);
                if (!previous.SequenceEqual(next))
                {
                    await SaveCandidateAsync(inputs, candidate);
                    await inputs.VerifyAsync(savedManifest: candidate.ManifestJson);
                }

                var required = enabled && info is not null
                    ? Flatten(Profiles.Compose([new("bundle", info.Value.Patches)]))
                        .Select(row => row.Id)
                        .ToHashSet(StringComparer.Ordinal)
                    : null;
                return (null, await ApplyCandidateAsync(candidate, required));
            },
            cancellationToken);

    /// <summary>Read the effective raw configuration without modifying files or evaluating expressions.</summary>
    public async Task<string> PreviewAsync(bool json = false) =>
        Profiles.Preview((await ProfileComposition.RefreshAsync(launch)).Layers, json);

    private async Task<IReadOnlyList<EntryDiagnostic>> ReloadAsync(IReadOnlySet<string>? required)
    {
        if (RunExclusiveAsync is null)
            return [];
        if (ReconcileAsync is { } reconcile)
            return await reconcile(required);
        var refresh = await ProfileComposition.RefreshAsync(launch);
        return await ApplicationBoot.ReconcileAsync(include, ProfileComposition.Flatten(refresh.Layers), required);
    }

    private async Task<ConfigurationChange> ChangeAsync(
        string target,
        bool enabled,
        string reason,
        Func<Task<(string? Application, IReadOnlyList<EntryDiagnostic> Warnings)>> operation,
        CancellationToken cancellationToken)
    {
        await mutation.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(cancellationToken);
            var before = await DiskStateAsync();
            var application = RunExclusiveAsync is null ? "restart-required" : "applied";
            string? code = null, diagnostic = null;
            IReadOnlyList<EntryDiagnostic> warnings = [];
            try
            {
                async Task Apply()
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = await operation();
                    application = result.Application ?? application;
                    warnings = result.Warnings;
                }

                if (RunExclusiveAsync is { } exclusive)
                    await exclusive(Apply);
                else
                    await Apply();
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                application = "failed";
                code = error is Refusal refusal ? refusal.Code : "operation-error";
                diagnostic = error is Refusal ? null : error.Message;
            }

            var changed = before != await DiskStateAsync();
            NotifyConfigurationChanged(reason);
            return new(changed, application, target, enabled, code, diagnostic, warnings);
        }
        finally
        {
            mutation.Release();
        }
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    ManifestPath + ".cordis-lock",
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (started.Elapsed < TimeSpan.FromMinutes(2))
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private async Task<string> DiskStateAsync()
    {
        async Task<string> Read(string file)
        {
            try
            {
                return await File.ReadAllTextAsync(file);
            }
            catch (FileNotFoundException)
            {
                return "";
            }
        }

        return await Read(ManifestPath) + "\0" + await Read(PatchPath);
    }

    private async Task<ProfileCandidate> WriteManifestAsync(
        ProfileInputs inputs,
        Func<PackageManifest, PackageManifest> change)
    {
        var manifest = ParseManifest(await inputs.ReadAsync(ManifestPath));
        var candidate = await CreateCandidateAsync(inputs, change(manifest));
        await AdmitCandidateAsync(inputs, candidate);
        await SaveCandidateAsync(inputs, candidate);
        return candidate;
    }

    private static PackageManifest WithBundles(PackageManifest manifest, IReadOnlyList<string> bundles)
    {
        var raw = (EntryOptions)Data.Clone(manifest.Raw)!;
        if (raw.GetValueOrDefault("dsh") is not EntryOptions dsh)
            raw["dsh"] = dsh = new();
        if (dsh.GetValueOrDefault("profile") is not EntryOptions profile)
            dsh["profile"] = profile = new();
        profile["bundles"] = bundles;
        return new(raw);
    }

    private async Task<(PackageManifest Manifest, List<EntryOptions> Patches)?> ReadBundleAsync(string name)
    {
        if (!launch.InstallationBundles.TryGetValue(name, out var directory) &&
            !(launch.LocalBundles?.TryGetValue(name, out directory) ?? false))
            throw new FileNotFoundException($"Cannot resolve bundle '{name}'.");
        var manifest = PackageManifest.Read(Path.Combine(directory!, "package.json"));
        return manifest.HasBundleDeclaration
            ? (manifest, (await Profiles.ReadBundleAsync(name, directory!, manifest)).Patches)
            : null;
    }

    private bool ProtectsManager(List<EntryOptions> patches) =>
        Flatten(Profiles.Compose([new("bundle", patches)]))
            .Any(row => ProtectedModules.Contains(row.Name) || include.Owner?.Id + ":" + row.Id == ownerEntryId);

    private static IEnumerable<EntryOptions> Flatten(IEnumerable<EntryOptions> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
            if (row.Group && row.Config is IEnumerable<object?>)
                foreach (var child in Flatten(Data.Entries(row.Config)))
                    yield return child;
        }
    }

    private sealed class Refusal(string code) : Exception(code)
    {
        public string Code
        {
            get;
        } = code;
    }
}
