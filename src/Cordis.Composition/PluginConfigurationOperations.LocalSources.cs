using System.Security.Cryptography;
using System.Text;

namespace Cordis.Composition;

public sealed partial class PluginConfigurationOperations
{
    // An inspection belongs to this Host lifetime. The profile remains the only durable source inventory.
    private readonly byte[] sourceInspectionKey = RandomNumberGenerator.GetBytes(32);

    internal void RequireSourceToolchain(IProfileLocalSourceToolchain toolchain)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        if (!Path.IsPathFullyQualified(toolchain.ProfileDirectory) ||
            !ProfileInputs.SamePath(
                Path.TrimEndingDirectorySeparator(toolchain.ProfileDirectory),
                Path.TrimEndingDirectorySeparator(launch.Profile.Directory)) ||
            launch
                .RefreshPackageRoutesAsync?.GetInvocationList()
                .Any(callback =>
                    callback.Target is IProfileLocalSourceToolchain owner && !ReferenceEquals(owner, toolchain)) ==
            true)
            throw new InvalidOperationException("The local source toolchain does not own this profile.");
    }

    /// <summary>Inspect a Host-authorized local source and bind its exact identity to this profile revision and caller.</summary>
    public async Task<LocalSourceInspection> InspectLocalSourceAsync(
        IProfileLocalSourceToolchain toolchain,
        string subject,
        LocalSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireSourceToolchain(toolchain);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(request);
        LocalSourceInspection result = null!;
        await ConfigurationTransactionAsync(
            async () =>
            {
                var inputs = await CaptureProfileAsync();
                var facts = await toolchain.InspectLocalSourceAsync(subject, request, cancellationToken);
                await inputs.VerifyAsync();
                result = new(facts, SourceInspectionId(subject, inputs.Revision, facts));
            },
            cancellationToken);
        return result;
    }

    private string SourceInspectionId(string subject, string revision, LocalSourceFacts facts)
    {
        var binding = ConfigurationFile.Write(
            new object?[]
            {
                subject,
                ManifestPath,
                revision,
                facts.SourceRoot,
                facts.ProjectFile,
                facts.Configuration,
                facts.PackageId,
                facts.Version,
                facts.TargetFramework,
                facts.RuntimeIdentifier,
                facts.OutputDirectory,
                facts.OutputIdentity
            },
            true);
        return Convert.ToHexString(HMACSHA256.HashData(sourceInspectionKey, Encoding.UTF8.GetBytes(binding)));
    }

    /// <summary>Register an inspected source through the same profile writer, candidate admission and session as package installation.</summary>
    public Task<PackageChange> RegisterLocalSourceAsync(
        IProfileLocalSourceToolchain toolchain,
        string subject,
        LocalSourceRequest request,
        LocalSourceRegistration options,
        CancellationToken cancellationToken = default)
    {
        RequireSourceToolchain(toolchain);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.InspectionId);
        return StartInstall(
            subject,
            options.RequestId,
            cancellationToken,
            active => RegisterSourceCoreAsync(toolchain, subject, request, options, active));
    }

    /// <summary>Observe this caller's active source registration, including after reconnecting.</summary>
    public Task<PackageChange?> WaitForLocalSourceAsync(string subject, string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return WaitForInstallAsync(subject, requestId);
    }

    /// <summary>Cancel this caller's source preparation before the durable save begins.</summary>
    public Task<string> CancelLocalSourceAsync(string subject, string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return CancelInstallAsync(subject, requestId);
    }

    /// <summary>Read one managed source from the profile manifest without evaluating the project or inspecting its directory.</summary>
    /// <remarks>Authorize disclosure of the returned source before sending these Host-local paths to a client.</remarks>
    public async Task<RegisteredLocalSource?> ReadRegisteredLocalSourceAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RegisteredLocalSource? result = null;
        await ConfigurationTransactionAsync(
            async () =>
            {
                var manifest = ParseManifest(await File.ReadAllTextAsync(ManifestPath, cancellationToken));
                result = RegisteredSource(manifest, name);
            },
            cancellationToken);
        return result;
    }

    private static RegisteredLocalSource? RegisteredSource(PackageManifest manifest, string name)
    {
        if (!manifest.ManagedLocalSources.TryGetValue(name, out var managed))
            return null;
        var dependencies = manifest.Raw.GetValueOrDefault("dependencies") as EntryOptions;
        var storedName =
            dependencies?.Keys.SingleOrDefault(key => key.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (storedName is null || dependencies![storedName] is not string link ||
            !link.StartsWith("link:", StringComparison.Ordinal) || !Path.IsPathFullyQualified(link["link:".Length..]))
            throw new FormatException("A managed source requires a matching profile link.");
        return new(
            storedName,
            PackageManifest.LinkDirectory(link),
            managed.ProjectFile,
            managed.Configuration,
            manifest.Bundles.Contains(storedName));
    }

    private static async Task AuthorizeSourceAsync(
        IProfileLocalSourceToolchain toolchain,
        string subject,
        LocalSourceFacts facts,
        bool enable,
        CancellationToken token)
    {
        await toolchain.AuthorizeLocalSourceAsync(subject, LocalSourceStage.Register, facts, token);
        if (enable)
            await toolchain.AuthorizeLocalSourceAsync(subject, LocalSourceStage.Activate, facts, token);
        token.ThrowIfCancellationRequested();
    }

    private static async Task RecheckSourceAsync(
        IProfileLocalSourceToolchain toolchain,
        string subject,
        LocalSourceRequest request,
        LocalSourceFacts facts,
        bool enable,
        CancellationToken token)
    {
        await AuthorizeSourceAsync(toolchain, subject, facts, enable, token);
        if (await toolchain.InspectLocalSourceAsync(subject, request, token) != facts)
            throw new Refusal("inspection-changed");
    }

    private async Task<Bundle?> ReadSourceBundleAsync(LocalSourceFacts facts, ProfileInputs inputs)
    {
        if (facts.OutputDirectory is not { } directory)
            return null;
        var metadata = ParseManifest(await inputs.ReadAsync(Path.Combine(directory, "package.json")));
        if (!string.Equals(
                metadata.Raw.GetValueOrDefault("name") as string,
                facts.PackageId,
                StringComparison.OrdinalIgnoreCase) ||
            !Equals(metadata.Raw.GetValueOrDefault("version"), facts.Version) || !metadata.HasBundleDeclaration)
            throw new Refusal("invalid-source-output");
        if (launch.RuntimeIdentity is { } runtime)
            DshProfilePolicy.CreateAdmission(
                launch.Profile.Directory,
                runtime,
                launch.CompatibilityWarning,
                launch.CompatibilityPackageName)(metadata);
        return await Profiles.ReadBundleAsync(facts.PackageId, directory, metadata, inputs.ReadAsync);
    }

    /// <summary>Change a saved source selection after comparing its exact registered identity inside the profile transaction.</summary>
    public Task<ConfigurationChange> SetLocalSourceEnabledAsync(
        IProfileLocalSourceToolchain toolchain,
        string subject,
        RegisteredLocalSource source,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        RequireSourceToolchain(toolchain);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(source);
        return ChangeAsync(
            source.Name,
            enabled,
            "bundle",
            async () =>
            {
                var inputs = await CaptureProfileAsync();
                var manifest = ParseManifest(await inputs.ReadAsync(ManifestPath));
                var current = RegisteredSource(manifest, source.Name);
                if (current is null || current != source)
                    throw new Refusal("profile-conflict");
                LocalSourceFacts? facts = null;
                Bundle? bundle = null;
                Func<Task>? verifyPrepared = null;
                if (enabled)
                {
                    var request = new LocalSourceRequest(current.SourceRoot, current.Configuration);
                    facts = await toolchain.InspectLocalSourceAsync(subject, request, cancellationToken);
                    if (!ProfileInputs.SamePath(facts.SourceRoot, current.SourceRoot) ||
                        !ProfileInputs.SamePath(facts.ProjectFile, current.ProjectFile) ||
                        facts.PackageId != current.Name || facts.Configuration != current.Configuration)
                        throw new Refusal("inspection-changed");
                    if (facts.OutputDirectory is null)
                        throw new Refusal("source-needs-build");
                    await AuthorizeSourceAsync(toolchain, subject, facts, true, cancellationToken);
                    bundle = await ReadSourceBundleAsync(facts, inputs);
                    verifyPrepared = () => RecheckSourceAsync(
                        toolchain,
                        subject,
                        request,
                        facts,
                        true,
                        cancellationToken);
                }
                else if (current.Selected && (await ListBundlesAsync()).Single(row => row.Name == current.Name)
                         .ReadOnlyReason is { } reason)
                    throw new Refusal(reason);

                var next = enabled
                    ? manifest.Bundles.Append(current.Name).Distinct(StringComparer.Ordinal).ToArray()
                    : manifest.Bundles.Where(name => name != current.Name).ToArray();
                var candidate = await CreateCandidateAsync(inputs, WithBundles(manifest, next), source: facts);
                await AdmitCandidateAsync(inputs, candidate);
                if (current.Selected != enabled)
                {
                    await SaveCandidateAsync(inputs, candidate, verifyPrepared: verifyPrepared);
                    await inputs.VerifyAsync(savedManifest: candidate.ManifestJson);
                }
                else if (verifyPrepared is not null)
                    await verifyPrepared();

                if (enabled && (!HasCandidateReconciliation || launch.RefreshPackageRoutesAsync is null))
                    return ("restart-required", Array.Empty<EntryDiagnostic>());
                var required = bundle is null
                    ? null
                    : ComposedBundleRows(candidate, bundle.PatchPaths)
                        .Select(row => row.Id)
                        .ToHashSet(StringComparer.Ordinal);
                return (null, await ApplyCandidateAsync(candidate, required));
            },
            cancellationToken,
            notifyOnlyIfChanged: true);
    }

    private async Task<PackageChange> RegisterSourceCoreAsync(
        IProfileLocalSourceToolchain toolchain,
        string subject,
        LocalSourceRequest request,
        LocalSourceRegistration options,
        ActiveInstall active)
    {
        var token = active.Cancellation.Token;
        var stage = "inspect";
        var name = "";
        var installed = false;
        var selected = false;
        var saved = false;
        PackageChange result = null!;
        try
        {
            await ConfigurationTransactionAsync(
                async () =>
                {
                    var inputs = await CaptureProfileAsync();
                    var revision = inputs.Revision;
                    var facts = await toolchain.InspectLocalSourceAsync(subject, request, token);
                    if (SourceInspectionId(subject, revision, facts) != options.InspectionId)
                        throw new Refusal("inspection-changed");
                    name = facts.PackageId;
                    var manifest = ParseManifest(await inputs.ReadAsync(ManifestPath));
                    var before = ConfigurationFile.Write(manifest.Raw, true);
                    var dependencies = ChildObject(manifest.Raw, "dependencies", true)!;
                    var occupied = dependencies.Keys.SingleOrDefault(key => key.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase));
                    if (launch.InstallationBundles.Keys.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                        occupied is null && inputs.Launch.LocalBundles!.Keys.Contains(
                            name,
                            StringComparer.OrdinalIgnoreCase) ||
                        occupied is not null && (occupied != name || dependencies[occupied] is not string link ||
                            !link.StartsWith("link:", StringComparison.Ordinal) ||
                            !ProfileInputs.SamePath(PackageManifest.LinkDirectory(link), facts.SourceRoot)) ||
                        manifest.ManagedLocalSources.TryGetValue(name, out var previous) &&
                        !ProfileInputs.SamePath(previous.ProjectFile, facts.ProjectFile))
                        throw new Refusal("source-conflict");
                    installed = occupied is not null;
                    selected = manifest.Bundles.Contains(name);
                    var enable = options.Enabled || selected;
                    if (enable && facts.OutputDirectory is null)
                        throw new Refusal("source-needs-build");

                    await AuthorizeSourceAsync(toolchain, subject, facts, enable, token);
                    var bundle = await ReadSourceBundleAsync(facts, inputs);

                    stage = "admit";
                    dependencies[name] = "link:" + facts.SourceRoot;
                    PackageManifest.SetManagedLocalSource(
                        manifest.Raw,
                        name,
                        new(facts.ProjectFile, facts.Configuration));
                    SetRestoreGraph(manifest.Raw, name, null);
                    if (enable && !selected)
                        manifest = WithBundles(manifest, manifest.Bundles.Append(name).ToArray());
                    var candidate = await CreateCandidateAsync(inputs, manifest, source: facts);
                    await AdmitCandidateAsync(inputs, candidate);

                    stage = "save";

                    async Task BeginCommitAsync()
                    {
                        await RecheckSourceAsync(toolchain, subject, request, facts, enable, token);
                        lock (installsGate)
                        {
                            token.ThrowIfCancellationRequested();
                            active.Applying = true;
                        }
                    }

                    if (before != candidate.ManifestJson)
                    {
                        await SaveCandidateAsync(inputs, candidate, verifyPrepared: BeginCommitAsync);
                        saved = true;
                    }
                    else
                        await BeginCommitAsync();

                    installed = true;
                    selected = enable;
                    removedPackages.Remove(name);
                    await inputs.VerifyAsync(savedManifest: saved ? candidate.ManifestJson : null);
                    if (!enable)
                    {
                        result = new(options.RequestId, name, stage, true, false, "saved");
                        return;
                    }

                    stage = "apply";
                    if (RunExclusiveAsync is null || !HasCandidateReconciliation ||
                        launch.RefreshPackageRoutesAsync is null)
                    {
                        result = new(options.RequestId, name, stage, true, true, "restart-required")
                        {
                            RestartReason = RunExclusiveAsync is null ? "no-hmr" : "package-moved"
                        };
                        return;
                    }

                    var required = ComposedBundleRows(candidate, bundle!.PatchPaths)
                        .Select(row => row.Id)
                        .ToHashSet(StringComparer.Ordinal);
                    await ApplyCandidateAsync(candidate, required);
                    result = new(options.RequestId, name, stage, true, true, "applied");
                },
                token);
            return result;
        }
        catch (Exception error)
        {
            return new(
                options.RequestId,
                name,
                stage,
                installed,
                selected,
                error is OperationCanceledException ? "cancelled" :
                error is DeploymentRestartRequiredException ? "restart-required" : "failed",
                error is Refusal refusal ? refusal.Code :
                error is OperationCanceledException ? "cancelled" : "operation-error",
                error.Message)
            {
                RestartReason = (error as DeploymentRestartRequiredException)?.Reason
            };
        }
        finally
        {
            if (saved)
                NotifyConfigurationChanged();
        }
    }
}
