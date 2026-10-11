using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>A parsed management decision. Build execution and exact-version exemptions are separate permissions.</summary>
public sealed record PluginManagementPermission(
    string Operation,
    string? Target = null,
    PackageInspection? Package = null,
    bool Mutation = false);

/// <summary>One profile plugin's address, patch availability and observed runtime state.</summary>
/// <param name="EntryId">The mounted entry address.</param>
/// <param name="ModuleName">The entry's module identity.</param>
/// <param name="Enabled">Whether persistent configuration enables this entry.</param>
/// <param name="PatchId">The writable patch address, when available.</param>
/// <param name="ReadOnlyReason">Why the entry cannot be edited, or null.</param>
/// <param name="State">The observed Fiber state as text, when mounted.</param>
public sealed record PluginManagerPlugin(
    string EntryId,
    string ModuleName,
    bool Enabled,
    string? PatchId,
    string? ReadOnlyReason,
    string? State);

/// <summary>Persistence and runtime outcomes, with text-only entry diagnostics.</summary>
/// <param name="Changed">Whether persistent configuration changed.</param>
/// <param name="Application">The runtime application outcome.</param>
/// <param name="Target">The entry, bundle or exemption identity.</param>
/// <param name="Enabled">The requested enabled state.</param>
/// <param name="Error">A stable failure code, or null.</param>
/// <param name="Diagnostic">Failure details, or null.</param>
/// <param name="Warnings">Detached runtime diagnostics.</param>
public sealed record PluginManagerChange(
    bool Changed,
    string Application,
    string Target,
    bool Enabled,
    string? Error,
    string? Diagnostic,
    IReadOnlyList<PluginManagerDiagnostic> Warnings);

/// <summary>A text-only runtime diagnostic safe to retain after a plugin is withdrawn.</summary>
/// <param name="Id">The entry address.</param>
/// <param name="Module">The module identity.</param>
/// <param name="State">The observed Fiber state as text.</param>
/// <param name="Error">Detached error text, or null.</param>
/// <param name="Missing">Unresolved service dependencies.</param>
/// <param name="Required">Whether activation requires the entry.</param>
/// <param name="Phase">The phase associated with this diagnostic.</param>
public sealed record PluginManagerDiagnostic(
    string Id,
    string Module,
    string? State,
    string? Error,
    IReadOnlyList<string> Missing,
    bool Required,
    string? Phase);

/// <summary>An installation attempt identified by the caller, with approval bound to its inspected archive.</summary>
/// <param name="RequestId">A caller-selected identifier unique among active installations.</param>
/// <param name="InspectedHash">The archive content identity accepted by the caller.</param>
public sealed record PluginInstallOptions(string RequestId, string InspectedHash)
{
    /// <summary>Whether the installed bundle is selected. Installation does not imply successful activation.</summary>
    public bool Enabled
    {
        get;
        init;
    } = true;
}

/// <summary>The result of an explicit cancellation. Failed cleanup is reported as failed, never cancelled.</summary>
/// <param name="Status">The existing task owner's cancelled, failed, too-late or not-running outcome.</param>
public sealed record PluginInstallCancellation(string Status);

/// <summary>A completed operation's invalidation reason; consumers query current inventory.</summary>
/// <param name="Reason">The existing operations owner's invalidation reason.</param>
public sealed record PluginManagerChanged(string Reason);

/// <summary>The standard generated management contract over an existing profile operation owner.</summary>
/// <remarks>There is no second installation table or persistence queue. Disconnect stops observation only;
/// explicit cancel and ProfileSession shutdown own installation cancellation.</remarks>
[RemoteService("pluginManager", typeof(PluginManagerJson))]
public partial class PluginManager : Service
{
    private readonly PluginConfigurationOperations operations;
    private readonly IProfilePackageToolchain? packages;
    private readonly Func<Context, PluginManagementPermission, Task<bool>> authorize;
    private volatile bool closed;

    /// <summary>Mount generated management with the host's resource and build policy.</summary>
    /// <remarks>A missing dynamic toolchain still permits inventory and enablement. Package execution then refuses
    /// with an actionable diagnostic; static applications change code by republishing.</remarks>
    public PluginManager(
        Context context,
        PluginConfigurationOperations operations,
        Func<Context, PluginManagementPermission, Task<bool>> authorize,
        IProfilePackageToolchain? packages = null) : base(context, "pluginManager")
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(authorize);
        this.operations = operations;
        this.authorize = authorize;
        this.packages = packages;
        context.Effect(
            () =>
            {
                operations.Changed += Changed;
                operations.PackageProgressed += Progress;
                return () =>
                {
                    closed = true;
                    operations.Changed -= Changed;
                    operations.PackageProgressed -= Progress;
                };
            },
            "plugin-manager notifications");
    }

    private PluginManager(PluginManager provider, Context caller) : base(provider, caller)
    {
        operations = provider.operations;
        packages = provider.packages;
        authorize = provider.authorize;
    }

    /// <summary>Keep the caller's Context while sharing the existing operations and host policy.</summary>
    protected override Service CreateView(Context caller) => new PluginManager(this, caller);

    private Context Caller => TypertInvocation.Current?.Context ?? Context;

    private async Task PermitAsync(
        string operation,
        string? target = null,
        PackageInspection? package = null,
        bool mutation = false)
    {
        if (!await authorize(Caller, new(operation, target, package, mutation)))
            throw new RemoteError("plugin-manager/forbidden", "The host refused this management operation.");
    }

    /// <summary>Read plugins and their persistent patch addresses.</summary>
    [RemoteMethod("listPlugins")]
    public async Task<IReadOnlyList<PluginManagerPlugin>> ListPluginsAsync()
    {
        await PermitAsync("read");
        return (await operations.ListPluginsAsync())
            .Select(row => new PluginManagerPlugin(
                row.EntryId,
                row.ModuleName,
                row.Enabled,
                row.PatchId,
                row.ReadOnlyReason,
                row.State?.ToString().ToLowerInvariant()))
            .ToArray();
    }

    /// <summary>Read installed, selected and installation-provided bundles through the profile owner.</summary>
    [RemoteMethod("listBundles")]
    public async Task<IReadOnlyList<BundleConfigurationInfo>> ListBundlesAsync()
    {
        await PermitAsync("read");
        return await operations.ListBundlesAsync();
    }

    /// <summary>Read the sources selected by the host's platform toolchain.</summary>
    [RemoteMethod("sources")]
    public async Task<IReadOnlyList<string>> SourcesAsync()
    {
        await PermitAsync("read");
        return RequirePackages().Sources.ToArray();
    }

    /// <summary>Query exact versions at one host-selected source. Cancellation ends this lookup.</summary>
    [RemoteMethod("versions")]
    public async Task<IReadOnlyList<string>> VersionsAsync(
        string name,
        string source,
        CancellationToken cancellationToken = default)
    {
        await PermitAsync("inspect", name);
        return await RequirePackages().VersionsAsync(name, source, cancellationToken);
    }

    /// <summary>Inspect an exact platform package without executing its build targets.</summary>
    [RemoteMethod("inspect")]
    public async Task<PackageInspection> InspectAsync(
        PackageRequest request,
        CancellationToken cancellationToken = default)
    {
        await PermitAsync("inspect", request.Name);
        return await RequirePackages().InspectAsync(request, cancellationToken);
    }

    /// <summary>Save plugin enablement and apply the existing reload policy.</summary>
    [RemoteMethod("setPluginEnabled")]
    public async Task<PluginManagerChange> SetPluginEnabledAsync(string id, bool enabled)
    {
        await PermitAsync("manage", id, mutation: true);
        return Project(await operations.SetPluginEnabledAsync(id, enabled));
    }

    /// <summary>Change bundle selection without treating installed code as a new live version.</summary>
    [RemoteMethod("setBundleEnabled")]
    public async Task<PluginManagerChange> SetBundleEnabledAsync(string name, bool enabled)
    {
        await PermitAsync("manage", name, mutation: true);
        return Project(await operations.SetBundleEnabledAsync(name, enabled));
    }

    /// <summary>Approve one inspected archive and install using the existing request ID and task owner.</summary>
    /// <remarks>No transport cancellation signal is passed to the installation. The result separates durable
    /// installation, selection, application, restart and residual files. A lost response must not replay this call.</remarks>
    [RemoteMethod("installBundle")]
    public async Task<PackageChange> InstallBundleAsync(PackageRequest request, PluginInstallOptions options)
    {
        await PermitAsync("install", request.Name, mutation: true);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RequestId);
        var toolchain = RequirePackages();
        var caller = Caller;
        return await operations.InstallPackageAsync(
            toolchain,
            request with
            {
                ExpectedHash = options.InspectedHash
            },
            options.RequestId,
            async (inspection, token) =>
            {
                token.ThrowIfCancellationRequested();
                return await authorize(caller, new("build", inspection.Request.Name, inspection, Mutation: true));
            },
            enabled: options.Enabled);
    }

    /// <summary>Observe an installation active at lookup. Null establishes neither success nor cancellation.</summary>
    [RemoteMethod("waitForInstall")]
    public async Task<PackageChange?> WaitForInstallAsync(string requestId)
    {
        await PermitAsync("install", requestId);
        return await operations.WaitForInstallAsync(requestId);
    }

    /// <summary>Explicitly cancel preparation and wait for draining; application can already be too late.</summary>
    [RemoteMethod("cancelInstall")]
    public async Task<PluginInstallCancellation> CancelInstallAsync(string requestId)
    {
        await PermitAsync("install", requestId, mutation: true);
        return new(await operations.CancelInstallAsync(requestId));
    }

    /// <summary>Deselect and settle a profile-owned bundle before removing its dependency.</summary>
    [RemoteMethod("removeBundle")]
    public async Task<PackageChange> RemoveBundleAsync(string name)
    {
        await PermitAsync("manage", name, mutation: true);
        return await operations.RemovePackageAsync(RequirePackages(), name);
    }

    /// <summary>Read exact-version exemptions and retained file diagnostics.</summary>
    [RemoteMethod("listVersionExemptions")]
    public async Task<DshCompatibilityRead> ListVersionExemptionsAsync()
    {
        await PermitAsync("compatibility-read");
        return operations.ReadVersionCompatibility();
    }

    /// <summary>Authorize the canonical package identity before granting or revoking one exact runtime pair.</summary>
    [RemoteMethod("setVersionExemption")]
    public async Task<PluginManagerChange> SetVersionExemptionAsync(
        string packageVersion,
        string runtimeVersion,
        bool enabled,
        bool acceptRisk = false)
    {
        var identity = operations.ResolveVersionExemptionIdentity(packageVersion);
        await PermitAsync("compatibility-grant", identity, mutation: true);
        return Project(await operations.SetVersionExemptionAsync(identity, runtimeVersion, enabled, acceptRisk));
    }

    private IProfilePackageToolchain RequirePackages() => packages ?? throw new RemoteError(
        "plugin-manager/toolchain-absent",
        "This host has no dynamic package toolchain; static code changes require republishing.");

    private static PluginManagerChange Project(ConfigurationChange change) => new(
        change.Changed,
        change.Application,
        change.Target,
        change.Enabled,
        change.Error,
        change.Diagnostic,
        change
            .Warnings?.Select(diagnostic =>
            {
                var snapshot = diagnostic.ToSnapshot();
                return new PluginManagerDiagnostic(
                    snapshot.Id,
                    snapshot.Module,
                    snapshot.State?.ToString().ToLowerInvariant(),
                    snapshot.Error,
                    snapshot.Missing.ToArray(),
                    snapshot.Required,
                    snapshot.Phase);
            })
            .ToArray() ?? []);

    private void Changed(string reason) => _ = PublishAsync("plugin-manager/changed", new PluginManagerChanged(reason));

    private void Progress(PackageProgress progress) => _ = PublishAsync(
        progress.Output is null ? "plugin-manager/install-state" : "plugin-manager/install-log",
        progress);

    private async Task PublishAsync(string name, object value)
    {
        try
        {
            await Provider.RunAsync(context =>
            {
                if (!closed)
                    context.Emit(name, value);
                return Task.CompletedTask;
            });
        }
        catch (Exception error)
        {
            try
            {
                Provider.Logger.Warn(error.Message);
            }
            catch
            {
                /* A notification observer cannot alter an operation's result. */
            }
        }
    }
}

/// <summary>Static JSON metadata for the single standard PluginManager contract.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(IReadOnlyList<PluginManagerPlugin>))]
[JsonSerializable(typeof(IReadOnlyList<BundleConfigurationInfo>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(PackageRequest))]
[JsonSerializable(typeof(PackageInspection))]
[JsonSerializable(typeof(PackageChange))]
[JsonSerializable(typeof(PluginInstallOptions))]
[JsonSerializable(typeof(PluginInstallCancellation))]
[JsonSerializable(typeof(PluginManagerChange))]
[JsonSerializable(typeof(DshCompatibilityRead))]
[JsonSerializable(typeof(PluginManagerChanged))]
[JsonSerializable(typeof(PackageProgress))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
public partial class PluginManagerJson : JsonSerializerContext;
