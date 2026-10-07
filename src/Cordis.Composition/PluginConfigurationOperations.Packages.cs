namespace Cordis.Composition;

public sealed partial class PluginConfigurationOperations
{
    /// <summary>Read exact DSH version grants and corruption diagnostics without rewriting the authorization file.</summary>
    public DshCompatibilityRead ReadVersionCompatibility() =>
        DshProfilePolicy.ReadCompatibility(launch.Profile.Directory);

    /// <summary>Resolve and validate a platform package/version for authorization and an exact grant operation.</summary>
    /// <remarks>Only the name is adapted. Pass this result unchanged to SetVersionExemptionAsync; that method never
    /// invokes the host mapping a second time. Versions and the persisted npm grant format remain exact.</remarks>
    public string ResolveVersionExemptionIdentity(string packageVersion)
    {
        var separator = packageVersion.LastIndexOf('@');
        if (separator <= 0)
            throw new FormatException("An exact package/version identity is required.");
        var name = packageVersion[..separator];
        var identity = (launch.CompatibilityPackageName?.Invoke(name) ?? name) + packageVersion[separator..];
        DshProfilePolicy.ValidatePackageVersion(identity);
        return identity;
    }

    /// <summary>Change a canonical DSH grant under existing profile coordination. Generic profiles have no DSH policy to grant.</summary>
    /// <remarks>Platform callers resolve the identity before authorization, then pass the same value here without remapping it.</remarks>
    public Task<ConfigurationChange> SetVersionExemptionAsync(
        string packageVersion,
        string runtimeVersion,
        bool enabled,
        bool acceptRisk = false,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(
            packageVersion,
            enabled,
            "compatibility",
            async () =>
            {
                var runtime = launch.RuntimeIdentity ??
                    throw new InvalidOperationException("This profile has no DSH runtime identity.");
                await ProfileMaintenance.SetVersionExemptionAsync(
                    launch.Profile.Directory,
                    packageVersion,
                    runtimeVersion,
                    runtime,
                    enabled,
                    acceptRisk,
                    cancellationToken);
                return (null, await ReloadAsync(null));
            },
            cancellationToken);

    private sealed class ActiveInstall(CancellationToken cancellationToken) : IDisposable
    {
        internal readonly CancellationTokenSource Cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        internal readonly TaskCompletionSource<PackageChange> Completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool Applying;
        public void Dispose() => Cancellation.Dispose();
    }

    private readonly object installsGate = new();
    private readonly Dictionary<string, ActiveInstall> installs = new(StringComparer.Ordinal);
    private bool packageOperationsClosed;

    /// <summary>Current operation progress. Subscribers receive no history and must query/wait after reconnecting.</summary>
    public event Action<PackageProgress>? PackageProgressed;

    /// <summary>Install a prepared platform package and select it through this profile's existing coordination.</summary>
    /// <remarks>The request ID must be unique while active. Cancellation is admitted until publication starts.
    /// Completed results are not retained. Hosts must authorize build execution independently from version exemptions.</remarks>
    public Task<PackageChange> InstallPackageAsync(
        IProfilePackageToolchain toolchain,
        PackageRequest request,
        string requestId,
        bool buildApproved,
        bool enabled = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var active = new ActiveInstall(cancellationToken);
        lock (installsGate)
        {
            if (packageOperationsClosed)
            {
                active.Dispose();
                throw new ObjectDisposedException(nameof(PluginConfigurationOperations));
            }

            if (!installs.TryAdd(requestId, active))
            {
                active.Dispose();
                throw new InvalidOperationException("An installation already owns this request ID.");
            }
        }

        _ = CompleteInstallAsync();
        return active.Completion.Task;

        async Task CompleteInstallAsync()
        {
            try
            {
                active.Completion.TrySetResult(
                    await InstallCoreAsync(toolchain, request, requestId, active, buildApproved, enabled));
            }
            catch (Exception error)
            {
                active.Completion.TrySetException(error);
            }
            finally
            {
                lock (installsGate)
                    installs.Remove(requestId);
                active.Dispose();
            }
        }
    }

    /// <summary>Close installation admission, cancel preparing tools and wait for all admitted installations.</summary>
    /// <remarks>Call outside the session queue before disposing the resolver. Applying operations settle normally.</remarks>
    public async Task StopPackageOperationsAsync()
    {
        ActiveInstall[] pending;
        lock (installsGate)
        {
            packageOperationsClosed = true;
            pending = installs.Values.ToArray();
            foreach (var active in pending)
                if (!active.Applying)
                    active.Cancellation.Cancel();
        }

        await Task.WhenAll(pending.Select(active => active.Completion.Task));
    }

    /// <summary>Wait for an operation active at lookup time. Null establishes neither success nor cancellation.</summary>
    public Task<PackageChange?> WaitForInstallAsync(string requestId)
    {
        lock (installsGate)
            return installs.TryGetValue(requestId, out var active)
                ? ObserveAsync(active.Completion.Task)
                : Task.FromResult<PackageChange?>(null);
        static async Task<PackageChange?> ObserveAsync(Task<PackageChange> task) => await task;
    }

    /// <summary>Cancel preparation and wait for process draining. Publication/activation returns too-late without cancelling.</summary>
    public async Task<string> CancelInstallAsync(string requestId)
    {
        Task<PackageChange> result;
        lock (installsGate)
        {
            if (!installs.TryGetValue(requestId, out var active))
                return "not-running";
            if (active.Applying)
                return "too-late";
            active.Cancellation.Cancel();
            result = active.Completion.Task;
        }

        var completed = await result;
        return completed.Application == "cancelled" ? "cancelled" : "failed";
    }

    private async Task<PackageChange> InstallCoreAsync(
        IProfilePackageToolchain toolchain,
        PackageRequest request,
        string requestId,
        ActiveInstall active,
        bool buildApproved,
        bool enabled)
    {
        var token = active.Cancellation.Token;
        var stage = "inspect";
        var installed = false;
        var selected = false;
        PreparedPackage? prepared = null;
        var acquired = false;
        try
        {
            await mutation.WaitAsync(token);
            acquired = true;
            await using var fileLock = await AcquireFileLockAsync(token);
            request = request with
            {
                Name = toolchain.ResolvePackageName(request.Name)
            };
            var inputs = await CaptureProfileAsync();
            var manifest = ParseManifest(await inputs.ReadAsync(ManifestPath));
            installed = HasPackageDirectory(request.Name);
            selected = manifest.Bundles.Contains(request.Name);
            if ((manifest.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?>)?.ContainsKey(
                    request.Name) == true || launch.InstallationBundles.ContainsKey(request.Name))
                throw new Refusal("already-installed");
            ReportPackageProgress(requestId, stage);
            var inspection = await toolchain.InspectAsync(request, token);
            if (request.ExpectedHash is { } expected && expected != inspection.ContentHash)
                throw new Refusal("inspection-changed");
            // Known metadata is admitted before any SDK execution, even for disabled installs.
            // Build approval and exact-version compatibility grants remain separate decisions.
            if (launch.RuntimeIdentity is { } inspectedRuntime && inspection.ManifestJson is { } manifestJson)
            {
                var inspectedManifest = ConfigurationFile.Parse(manifestJson, true) as EntryOptions ??
                    throw new FormatException("Inspected package metadata must be an object.");
                DshProfilePolicy.CreateAdmission(
                    launch.Profile.Directory,
                    inspectedRuntime,
                    launch.CompatibilityWarning,
                    launch.CompatibilityPackageName)(new(inspectedManifest));
            }

            stage = "prepare";
            ReportPackageProgress(requestId, stage);
            prepared = await toolchain.PrepareAsync(
                inspection,
                buildApproved,
                text => ReportPackageProgress(requestId, stage, text),
                token);
            inputs.PlanPublication(prepared);
            var metadata = PackageManifest.Read(Path.Combine(prepared.Directory, "package.json"));
            if (prepared.Name != request.Name || prepared.Version != request.Version || !metadata.HasBundleDeclaration)
                throw new Refusal("invalid-prepared-package");
            var bundle = await Profiles.ReadBundleAsync(prepared.Name, prepared.Directory, metadata);
            if (launch.RuntimeIdentity is { } runtime)
                DshProfilePolicy.CreateAdmission(
                    launch.Profile.Directory,
                    runtime,
                    launch.CompatibilityWarning,
                    launch.CompatibilityPackageName)(metadata);
            lock (installsGate)
            {
                token.ThrowIfCancellationRequested();
                active.Applying = true;
            }

            stage = "install";

            async Task Apply()
            {
                ReportPackageProgress(requestId, stage);
                var raw = (EntryOptions)Data.Clone(manifest.Raw)!;
                if (raw.GetValueOrDefault("dependencies") is not EntryOptions dependencies)
                    raw["dependencies"] = dependencies = new();
                dependencies[prepared.Name] = prepared.Version;
                var candidate = await CreateCandidateAsync(
                    inputs,
                    WithBundles(
                        new(raw),
                        enabled
                            ? manifest.Bundles.Append(prepared.Name).Distinct(StringComparer.Ordinal).ToArray()
                            : manifest.Bundles),
                    prepared);
                await AdmitCandidateAsync(inputs, candidate);
                await toolchain.PublishAsync(prepared);
                installed = true;
                await SaveCandidateAsync(inputs, candidate, published: true);
                selected = enabled;
                await inputs.VerifyAsync(published: true, savedManifest: candidate.ManifestJson);
                stage = "apply";
                ReportPackageProgress(requestId, stage);
                if (enabled)
                {
                    var required = Flatten(Profiles.Compose(bundle.PatchLayers))
                        .Select(row => row.Id)
                        .ToHashSet(StringComparer.Ordinal);
                    await ApplyCandidateAsync(candidate, required);
                }
            }

            if (RunExclusiveAsync is { } exclusive)
                await exclusive(Apply);
            else
                await Apply();
            return new(
                requestId,
                request.Name,
                stage,
                installed,
                selected,
                RunExclusiveAsync is null ? "restart-required" : "applied");
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException or PackageToolException { Cancelled: true };
            var residuals = new List<string>();
            if (launch.LocalBundles?.TryGetValue(request.Name, out var published) == true &&
                Directory.Exists(published))
            {
                installed = true;
                residuals.Add(published);
            }

            if (error is PackageToolException tool && Directory.Exists(tool.Directory))
            {
                if (!residuals.Contains(tool.Directory))
                    residuals.Add(tool.Directory);
                if (stage == "install" && tool.Published)
                    installed = true;
            }

            if (prepared is not null && Directory.Exists(prepared.Directory) && !residuals.Contains(prepared.Directory))
                residuals.Add(prepared.Directory);
            if (installed && prepared?.PublicationDirectory is { } destination && Directory.Exists(destination) &&
                !residuals.Contains(destination))
                residuals.Add(destination);
            return new(
                requestId,
                request.Name,
                stage,
                installed,
                selected,
                cancelled ? "cancelled" : error is DeploymentRestartRequiredException ? "restart-required" : "failed",
                error is Refusal refusal ? refusal.Code :
                cancelled ? "cancelled" :
                error is PackageToolException { TimedOut: true } ? "tool-timeout" : "operation-error",
                error.Message,
                residuals.AsReadOnly())
            {
                ToolExitCode = (error as PackageToolException)?.ExitCode
            };
        }
        finally
        {
            if (acquired)
                mutation.Release();
            NotifyConfigurationChanged();
        }
    }

    /// <summary>Deselect and settle the running tree before removing a profile-owned package.</summary>
    /// <remarks>A failure after deselection leaves that selection saved. Retained contributions prevent removal.
    /// Cancellation can leave a disabled package on disk; the result reports that residual instead of promising rollback.</remarks>
    public async Task<PackageChange> RemovePackageAsync(
        IProfilePackageToolchain toolchain,
        string name,
        CancellationToken cancellationToken = default)
    {
        var stage = "disable";
        var installed = false;
        var selected = false;
        var residuals = new List<string>();
        PackageChange result = null!;
        await ConfigurationTransactionAsync(
            async () =>
            {
                try
                {
                    name = toolchain.ResolvePackageName(name);
                    var inputs = await CaptureProfileAsync();
                    installed = HasPackageDirectory(name);
                    selected = ParseManifest(await inputs.ReadAsync(ManifestPath)).Bundles.Contains(name);
                    var info = (await ListBundlesAsync()).SingleOrDefault(row => row.Name == name);
                    if (info?.ReadOnlyReason is not null)
                        throw new Refusal(info.ReadOnlyReason);
                    if (info?.Removable != true)
                        throw new Refusal("not-removable");
                    selected = info.Enabled;
                    if (RunExclusiveAsync is null && info.Rows.Any(row => row.EntryId is not null))
                        throw new Refusal("stop-profile");
                    var candidate = await WriteManifestAsync(
                        inputs,
                        manifest => WithBundles(manifest, manifest.Bundles.Where(bundle => bundle != name).ToArray()));
                    selected = false;
                    await inputs.VerifyAsync(savedManifest: candidate.ManifestJson);
                    await ApplyCandidateAsync(candidate, null);
                    await include.Context.RunAsync(_ =>
                    {
                        if (include
                            .Loader.Entries()
                            .Any(entry => entry.Fiber is not null && info.Rows.Any(row =>
                                row.RowId == entry.Options.Id && row.ModuleName == entry.Options.Name)))
                            throw new Refusal("bundle-in-use");
                        return Task.CompletedTask;
                    });
                    stage = "remove";
                    ReportPackageProgress("remove:" + name, stage);
                    cancellationToken.ThrowIfCancellationRequested();
                    var removalInputs = await CaptureProfileAsync();
                    removalInputs.PlanRemoval(name);
                    await toolchain.RemoveAsync(name, cancellationToken);
                    installed = false;
                    await WriteManifestAsync(
                        removalInputs,
                        manifest =>
                        {
                            var raw = (EntryOptions)Data.Clone(manifest.Raw)!;
                            (raw.GetValueOrDefault("dependencies") as IDictionary<string, object?>)?.Remove(name);
                            return new(raw);
                        });
                    result = new(
                        "",
                        name,
                        stage,
                        false,
                        false,
                        RunExclusiveAsync is null ? "restart-required" : "applied");
                }
                catch (Exception error)
                {
                    if (launch.LocalBundles?.TryGetValue(name, out var directory) == true &&
                        Directory.Exists(directory))
                        residuals.Add(directory);
                    result = new(
                        "",
                        name,
                        stage,
                        installed,
                        selected,
                        error is OperationCanceledException ? "cancelled" : "failed",
                        error is Refusal refusal ? refusal.Code : "operation-error",
                        error.Message,
                        residuals.AsReadOnly());
                }
                finally
                {
                    NotifyConfigurationChanged();
                }
            },
            cancellationToken);
        return result;
    }

    private bool HasPackageDirectory(string name) =>
        (launch.LocalBundles?.TryGetValue(name, out var local) == true && Directory.Exists(local)) ||
        (launch.InstallationBundles.TryGetValue(name, out var installed) && Directory.Exists(installed));

    private void ReportPackageProgress(string requestId, string phase, string? output = null)
    {
        if (PackageProgressed is not { } observers)
            return;
        foreach (Action<PackageProgress> observer in observers.GetInvocationList())
        {
            try
            {
                observer(new(requestId, phase, output));
            }
            catch
            {
                /* Observers cannot turn an installed package into a reported tool failure. */
            }
        }
    }
}
