using Cordis;

namespace Cordis.Composition;

/// <summary>
/// Represents the plugin configuration info component.
/// </summary>
/// <param name="EntryId">The entry id value.</param>
/// <param name="ModuleName">The module name value.</param>
/// <param name="Enabled">The enabled value.</param>
/// <param name="PatchId">The patch id value.</param>
/// <param name="ReadOnlyReason">The read only reason value.</param>
/// <param name="State">The state value.</param>
public sealed record PluginConfigurationInfo(string EntryId, string ModuleName, bool Enabled, string? PatchId, string? ReadOnlyReason, FiberState? State);
/// <summary>
/// Represents the bundle configuration row component.
/// </summary>
/// <param name="RowId">The row id value.</param>
/// <param name="ModuleName">The module name value.</param>
/// <param name="EntryId">The entry id value.</param>
public sealed record BundleConfigurationRow(string RowId, string ModuleName, string? EntryId);
/// <summary>
/// Represents the bundle configuration info component.
/// </summary>
/// <param name="Name">The name value.</param>
/// <param name="Version">The version value.</param>
/// <param name="Description">The description value.</param>
/// <param name="Enabled">The enabled value.</param>
/// <param name="Installed">The installed value.</param>
/// <param name="Optional">The optional value.</param>
/// <param name="Removable">The removable value.</param>
/// <param name="ReadOnlyReason">The read only reason value.</param>
/// <param name="Error">The error value.</param>
/// <param name="Rows">The rows value.</param>
/// <param name="Overrides">The overrides value.</param>
public sealed record BundleConfigurationInfo(string Name, string? Version, string? Description, bool Enabled, bool Installed, bool Optional, bool Removable, string? ReadOnlyReason, string? Error, IReadOnlyList<BundleConfigurationRow> Rows, IReadOnlyList<string> Overrides);
/// <summary>
/// Represents the configuration change component.
/// </summary>
/// <param name="Changed">The changed value.</param>
/// <param name="Application">The application value.</param>
/// <param name="Target">The target value.</param>
/// <param name="Enabled">The enabled value.</param>
/// <param name="Error">The error value.</param>
/// <param name="Diagnostic">The diagnostic value.</param>
/// <param name="Warnings">The warnings value.</param>
public sealed record ConfigurationChange(bool Changed, string Application, string Target, bool Enabled, string? Error = null, string? Diagnostic = null, IReadOnlyList<EntryDiagnostic>? Warnings = null);
/// <summary>Persistent profile configuration operations. Package installation and remote UI transport belong to the host.</summary>
public sealed class PluginConfigurationOperations(ProfileLaunch launch, Include include, string? ownerEntryId = null)
{
    private readonly SemaphoreSlim mutation = new(1, 1);
    private string ManifestPath => Path.Combine(launch.Profile.Directory, "package.json");
    private string PatchPath => launch.Profile.UserLayer.Source;
    /// <summary>Set to the active host HMR coordinator's RunExclusiveAsync. Null means startup-only configuration.</summary>
    public Func<Func<Task>, Task>? RunExclusiveAsync { get; set; }
    /// <summary>Modules needed to retain the management path. Hosts may extend this set for their own control plane.</summary>
    public ISet<string> ProtectedModules { get; } = new HashSet<string>(["cordis:manager", "cordis:include", "cordis:loader", "cordis:timer", "@deepseek-ai/dsh-plugin-manager", "@deepseek-ai/cordis-plugin-loader", "@deepseek-ai/cordis-plugin-include", "@deepseek-ai/dsh-api-gateway", "@deepseek-ai/dsh-host-webserver", "@deepseek-ai/dsh-client-modules", "@deepseek-ai/dsh-client-ui-settings-plugin-inventory", "@deepseek-ai/dsh-client-ui-plugin-manager", "@deepseek-ai/dsh-host-plugin-inventory", "@deepseek-ai/dsh-typert-registry", "@deepseek-ai/dsh-api-remotes", "@deepseek-ai/cordis-plugin-timer", "@deepseek-ai/dsh-client-connection", "@deepseek-ai/dsh-host-frontend-static", "@deepseek-ai/dsh-tools", "@deepseek-ai/dsh-hmr"], StringComparer.Ordinal);
    /// <summary>
    /// Gets the optional bundles value.
    /// </summary>
    public ISet<string> OptionalBundles { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the changed value.
    /// </summary>
    public event Action<string>? Changed;
    /// <summary>Reconcile a completed external deployment without reactivating retained dependencies. The host owns deployment success and exclusion of concurrent writes.</summary>
    public static async Task<ProfileReconciliation> ReconcileDeployedPackagesAsync(string directory, PackageManifest before, IReadOnlyDictionary<string, string> installedPackages, IReadOnlyDictionary<string, string> installationBundles)
    {
        var manifestPath = Path.Combine(directory, "package.json");
        var after = PackageManifest.Read(manifestPath);
        var oldDependencies = (before.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ?? new EntryOptions()).Keys.ToHashSet(StringComparer.Ordinal);
        var dependencies = (after.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ?? new EntryOptions()).Keys.ToArray();
        (PackageManifest Manifest, string Directory) Resolve(string name)
        {
            if (!installationBundles.TryGetValue(name, out var packageDirectory) && !installedPackages.TryGetValue(name, out packageDirectory))
                throw new FileNotFoundException($"Cannot resolve deployed package '{name}'.");
            return (PackageManifest.Read(Path.Combine(packageDirectory, "package.json")), packageDirectory);
        }

        var selected = after.Bundles.Where(name => !oldDependencies.Contains(name) && !dependencies.Contains(name) || dependencies.Contains(name) && Resolve(name).Manifest.BundlePatch is not null).ToList();
        var plain = new List<string>();
        foreach (var name in dependencies)
        {
            if (oldDependencies.Contains(name))
                continue;
            var package = Resolve(name);
            if (package.Manifest.BundlePatch is not { } patch)
            {
                plain.Add(name);
                continue;
            }

            await Profiles.ReadPatchesAsync(Path.Combine(package.Directory, patch));
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
    /// Performs the list plugins async operation.
    /// </summary>
    public async Task<IReadOnlyList<PluginConfigurationInfo>> ListPluginsAsync()
    {
        var profile = await Profiles.LoadAsync(launch.Profile.Directory, launch.InstallationBundles, launch.LocalBundles, userLayer: false);
        var profileLayers = profile.Bundles.Select(bundle => new ConfigurationLayer(bundle.PatchPath, bundle.Patches)).Append(new ConfigurationLayer(PatchPath, await Profiles.ReadPatchesAsync(PatchPath, true)));
        var rows = Flatten(Profiles.Compose(profileLayers)).ToArray();
        var result = new List<PluginConfigurationInfo>();
        await include.Context.RunAsync(_ =>
        {
            foreach (var entry in include.Loader.Entries())
            {
                var candidates = rows.Where(row => row.Id == entry.Options.Id).ToArray();
                var reason = ProtectedModules.Contains(entry.Options.Name) || entry.Id == ownerEntryId ? "management-required" : candidates.Length != 1 || candidates[0].Name != entry.Options.Name || entry.Parent.Tree != include ? "unaddressable" : null;
                result.Add(new(entry.Id, entry.Options.Name, !entry.Disabled, reason is null ? candidates[0].Id : null, reason, entry.Fiber?.State));
            }

            return Task.CompletedTask;
        });
        return result;
    }

    /// <summary>
    /// Performs the list bundles async operation.
    /// </summary>
    public async Task<IReadOnlyList<BundleConfigurationInfo>> ListBundlesAsync()
    {
        var manifest = PackageManifest.Read(ManifestPath);
        var dependencies = manifest.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ?? new EntryOptions();
        var names = manifest.Bundles.Concat(dependencies.Keys).Concat(launch.InstallationBundles.Keys).Distinct(StringComparer.Ordinal);
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
                        result.Add(new(name, null, null, true, installed, OptionalBundles.Contains(name), removable, null, "not-bundle", [], []));
                    continue;
                }

                var (metadata, patches) = info.Value;
                var declared = Flatten(Profiles.Compose([new("bundle", patches.Where(p => p.ContainsKey("insert")).ToList())])).Where(p => p.Id.Length > 0 && p.Name.Length > 0).ToArray();
                var ids = declared.Select(row => row.Id).ToHashSet(StringComparer.Ordinal);
                var reason = ProtectsManager(patches) ? "management-required" : null;
                result.Add(new(name, metadata.Raw.GetValueOrDefault("version") as string, metadata.Raw.GetValueOrDefault("description") as string, enabled, installed, OptionalBundles.Contains(name), removable && reason is null, reason, null, declared.Select(row => new BundleConfigurationRow(row.Id, row.Name, live.GetValueOrDefault(row.Id))).ToArray(), patches.Where(p => !p.ContainsKey("insert") && p.Id.Length > 0 && !ids.Contains(p.Id)).Select(p => p.Id).Distinct(StringComparer.Ordinal).ToArray()));
            }
            catch (Exception error)
            {
                if (enabled || installed)
                    result.Add(new(name, null, null, enabled, installed, OptionalBundles.Contains(name), removable, null, error.Message, [], []));
            }
        }

        return result;
    }

    /// <summary>
    /// Sets plugin enabled async.
    /// </summary>
    public Task<ConfigurationChange> SetPluginEnabledAsync(string entryId, bool enabled, CancellationToken cancellationToken = default) => ChangeAsync(entryId, enabled, "plugin", async () =>
    {
        var row = (await ListPluginsAsync()).FirstOrDefault(row => row.EntryId == entryId) ?? throw new Refusal("unknown-plugin");
        if (row.ReadOnlyReason is not null)
            throw new Refusal(row.ReadOnlyReason);
        await ProfileMaintenance.WriteEnabledAsync(PatchPath, row.PatchId!, row.ModuleName, enabled);
        var warnings = await ReloadAsync(enabled ? new HashSet<string>([row.PatchId!], StringComparer.Ordinal) : null);
        var current = (await ListPluginsAsync()).FirstOrDefault(item => item.EntryId == entryId);
        return (RunExclusiveAsync is not null && current?.Enabled != enabled ? "overridden" : null, warnings);
    }, cancellationToken);
    /// <summary>
    /// Sets bundle enabled async.
    /// </summary>
    public Task<ConfigurationChange> SetBundleEnabledAsync(string name, bool enabled, CancellationToken cancellationToken = default) => ChangeAsync(name, enabled, "bundle", async () =>
    {
        var manifest = PackageManifest.Read(ManifestPath);
        var previous = manifest.Bundles;
        var info = await ReadBundleAsync(name);
        if ((enabled || !previous.Contains(name)) && info is null)
            throw new Refusal("not-bundle");
        if (!enabled && previous.Contains(name) && info is not null && ProtectsManager(info.Value.Patches))
            throw new Refusal("management-required");
        var next = enabled ? previous.Concat(previous.Contains(name) ? [] : new[] { name }).ToArray() : previous.Where(item => item != name).ToArray();
        if (!previous.SequenceEqual(next))
            await WriteManifestAsync(manifest, next);
        var required = enabled && info is not null ? Flatten(Profiles.Compose([new("bundle", info.Value.Patches)])).Select(row => row.Id).ToHashSet(StringComparer.Ordinal) : null;
        return (null, await ReloadAsync(required));
    }, cancellationToken);
    /// <summary>Read the effective raw configuration without modifying files or evaluating expressions.</summary>
    public async Task<string> PreviewAsync(bool json = false) => Profiles.Preview((await ProfileComposition.RefreshAsync(launch)).Layers, json);
    private async Task<IReadOnlyList<EntryDiagnostic>> ReloadAsync(IReadOnlySet<string>? required)
    {
        if (RunExclusiveAsync is null)
            return [];
        var refresh = await ProfileComposition.RefreshAsync(launch);
        return await ApplicationBoot.ReconcileAsync(include, ProfileComposition.Flatten(refresh.Layers), required);
    }

    private async Task<ConfigurationChange> ChangeAsync(string target, bool enabled, string reason, Func<Task<(string? Application, IReadOnlyList<EntryDiagnostic> Warnings)>> operation, CancellationToken cancellationToken)
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
            Changed?.Invoke(reason);
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
                return new FileStream(ManifestPath + ".cordis-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
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

    private async Task WriteManifestAsync(PackageManifest manifest, IReadOnlyList<string> bundles)
    {
        var raw = (EntryOptions)Data.Clone(manifest.Raw)!;
        if (raw.GetValueOrDefault("dsh") is not EntryOptions dsh)
            raw["dsh"] = dsh = new();
        if (dsh.GetValueOrDefault("profile") is not EntryOptions profile)
            dsh["profile"] = profile = new();
        profile["bundles"] = bundles;
        var temporary = ManifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, ConfigurationFile.Write(raw, true));
            File.Move(temporary, ManifestPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private async Task<(PackageManifest Manifest, List<EntryOptions> Patches)?> ReadBundleAsync(string name)
    {
        if (!launch.InstallationBundles.TryGetValue(name, out var directory) && !(launch.LocalBundles?.TryGetValue(name, out directory) ?? false))
            throw new FileNotFoundException($"Cannot resolve bundle '{name}'.");
        var manifest = PackageManifest.Read(Path.Combine(directory!, "package.json"));
        return manifest.BundlePatch is { } patch ? (manifest, await Profiles.ReadPatchesAsync(Path.Combine(directory!, patch))) : null;
    }

    private bool ProtectsManager(List<EntryOptions> patches) => Flatten(Profiles.Compose([new("bundle", patches)])).Any(row => ProtectedModules.Contains(row.Name) || include.Owner?.Id + ":" + row.Id == ownerEntryId);
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
        public string Code { get; } = code;
    }
}
