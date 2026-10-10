namespace Cordis.Composition;

/// <summary>An exact package identity and an explicitly selected source. Sources are interpreted by the platform toolchain.</summary>
public sealed record PackageRequest(string Name, string Version, string Source)
{
    /// <summary>Optional inspected archive hash to fence a host's approval against changed source contents.</summary>
    public string? ExpectedHash
    {
        get;
        init;
    }
}

/// <summary>Metadata inspected without importing plugin code or executing its build targets.</summary>
/// <remarks>ContentHash identifies the requested package archive. It does not describe or lock its transitive dependency closure.</remarks>
public sealed record PackageInspection(
    PackageRequest Request,
    string ContentHash,
    string Description,
    bool RequiresBuildApproval)
{
    /// <summary>Optional immutable manifest snapshot read from the archive identified by ContentHash, before executing tools.</summary>
    /// <remarks>Contains package identity and declared peers. Null means metadata cannot be known before preparation;
    /// the prepared manifest is still admitted separately. Serialization prevents callers from mutating captured peers.</remarks>
    public string? ManifestJson
    {
        get;
        init;
    }
}

/// <summary>A complete prepared directory. Preparing it does not select the bundle or activate code.</summary>
public sealed record PreparedPackage(string Name, string Version, string Directory)
{
    /// <summary>Planned final directory for the same prepared contents. Null means publication keeps Directory.</summary>
    /// <remarks>Wrappers preserve this plan. Publication may add only this package's mapping and must not alter its contents.</remarks>
    public string? PublicationDirectory
    {
        get;
        init;
    }
}

/// <summary>A bounded tool output chunk or a management phase. Observers must not control operation success.</summary>
public sealed record PackageProgress(string RequestId, string Phase, string? Output = null);

/// <summary>Separate durable package, selection and runtime outcomes; disk remnants are not an atomic rollback promise.</summary>
public sealed record PackageChange(
    string RequestId,
    string Target,
    string Stage,
    bool Installed,
    bool Selected,
    string Application,
    string? Error = null,
    string? Diagnostic = null,
    IReadOnlyList<string>? Residuals = null)
{
    /// <summary>The observed SDK exit status for a failed preparation, when a process was started and drained.</summary>
    public int? ToolExitCode
    {
        get;
        init;
    }
}

/// <summary>A platform tool failure with its observed process result and retained output path.</summary>
/// <remarks>Contains text and paths, not plugin objects. Cancellation does not imply that all downloaded files were removed.</remarks>
public sealed class PackageToolException(
    string message,
    string directory,
    int? exitCode = null,
    bool cancelled = false,
    bool timedOut = false,
    bool published = false) : Exception(message)
{
    /// <summary>The tool-owned output path requiring inspection or cleanup; it may be a directory or a receipt file.</summary>
    public string Directory
    {
        get;
    } = directory;

    /// <summary>The observed exit code, or null when the tool never reached a confirmed exit.</summary>
    public int? ExitCode
    {
        get;
    } = exitCode;

    /// <summary>Whether cancellation settled after the tool and its output were drained. Unconfirmed cleanup remains a failure.</summary>
    public bool Cancelled
    {
        get;
    } = cancelled;

    /// <summary>Whether the bounded process deadline expired.</summary>
    public bool TimedOut
    {
        get;
    } = timedOut;

    /// <summary>Whether the complete prepared files were moved to their deployment path before the failure.</summary>
    public bool Published
    {
        get;
    } = published;
}

/// <summary>Platform preparation and deployment, called under the profile's package/configuration lock.</summary>
/// <remarks>Publish and removal also run inside the session queue. The implementation owns its prepared directories and
/// resolver mappings; it must not re-enter configuration operations. The host owns the toolchain's lifetime.</remarks>
public interface IProfilePackageToolchain
{
    /// <summary>Resolve a platform package alias to its existing profile spelling without executing package code.</summary>
    /// <remarks>Called under profile coordination. Generic package names remain case-sensitive unless the adapter says otherwise.</remarks>
    string ResolvePackageName(string name) => name;

    /// <summary>The sources explicitly selected by the host; clients cannot add arbitrary fallback sources.</summary>
    IReadOnlyList<string> Sources
    {
        get;
    }

    /// <summary>Query exact versions without executing package build code.</summary>
    Task<IReadOnlyList<string>> VersionsAsync(
        string name,
        string source,
        CancellationToken cancellationToken = default);

    /// <summary>Read package metadata without executing package code. The returned hash binds subsequent preparation.</summary>
    Task<PackageInspection> InspectAsync(PackageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Prepare dependencies and complete output. Approval permits this build, including dependency targets.</summary>
    /// <remarks>The root archive must still match inspection. Dependency resolution follows the adapter's selected sources;
    /// this approval is neither a per-dependency hash grant nor a DSH compatibility exemption.</remarks>
    Task<PreparedPackage> PrepareAsync(
        PackageInspection inspection,
        bool buildApproved,
        Action<string> output,
        CancellationToken cancellationToken = default);

    /// <summary>Expose prepared metadata and explicit module mappings. Return only after all files are ready.</summary>
    Task PublishAsync(PreparedPackage package, CancellationToken cancellationToken = default);

    /// <summary>Release runtime mappings after the caller has stopped their fibers. File removal is platform-specific.</summary>
    /// <remarks>A toolchain may retain artifacts for other processes or Workers. Configuration removal reports
    /// retained directories separately; it must not infer physical deletion from fiber or ALC retirement.</remarks>
    Task RemoveAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Observe retained deployment directories for an identity, including earlier and pending versions.</summary>
    /// <remarks>This is neither a lease nor permission to delete. Adapters without retained files may return an empty list.</remarks>
    IReadOnlyList<string> GetRetainedDirectories(string name) => [];
}
