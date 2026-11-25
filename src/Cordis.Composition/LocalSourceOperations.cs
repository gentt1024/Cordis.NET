namespace Cordis.Composition;

/// <summary>A host-relative or absolute source directory and the requested MSBuild configuration.</summary>
/// <param name="Source">Source directory, relative to the host workspace or absolute within it.</param>
/// <param name="Configuration">Exact MSBuild configuration selected by the caller.</param>
public sealed record LocalSourceRequest(string Source, string Configuration);

/// <summary>Facts observed from the selected project. An absent output means an ordinary build is needed.</summary>
/// <param name="SourceRoot">Canonical absolute author directory.</param>
/// <param name="ProjectFile">Absolute entry project selected by the source declaration.</param>
/// <param name="Configuration">Exact MSBuild configuration selected by the caller.</param>
/// <param name="PackageId">NuGet package identity returned by the authorized evaluation.</param>
/// <param name="Version">Evaluated project version.</param>
/// <param name="TargetFramework">Single selected target framework.</param>
/// <param name="RuntimeIdentifier">Selected RID, or empty for a portable output.</param>
/// <param name="OutputDirectory">Selected immutable successful build directory, or null before the first build.</param>
/// <param name="OutputIdentity">Verified identity of the completed file set, or null if no output was selected.</param>
public sealed record LocalSourceFacts(
    string SourceRoot,
    string ProjectFile,
    string Configuration,
    string PackageId,
    string Version,
    string TargetFramework,
    string RuntimeIdentifier,
    string? OutputDirectory,
    string? OutputIdentity);

/// <summary>The opaque inspection ID binds these observations to the caller and profile revision.</summary>
/// <param name="Facts">Observed project and output facts.</param>
/// <param name="InspectionId">Opaque proof bound to this host, caller, profile revision, project and output.</param>
/// <param name="OutputStatus">Ready for activation checks, or needs-build when no complete output exists.</param>
public sealed record LocalSourceInspection(LocalSourceFacts Facts, string InspectionId, string OutputStatus)
{
    internal LocalSourceInspection(LocalSourceFacts facts, string inspectionId)
        : this(facts, inspectionId, facts.OutputIdentity is null ? "needs-build" : "ready")
    {
    }
}

/// <summary>Register one inspected source. Disabling selection allows a source to be saved before its first successful build.</summary>
/// <param name="RequestId">Identifier scoped to this caller while the registration is active.</param>
/// <param name="InspectionId">Opaque proof bound to this host, caller, profile revision, project and output.</param>
public sealed record LocalSourceRegistration(string RequestId, string InspectionId)
{
    /// <summary>Whether to select the bundle after registration.</summary>
    public bool Enabled
    {
        get;
        init;
    } = true;
}

/// <summary>The project and configuration to reauthorize when a managed source is reopened.</summary>
/// <param name="ProjectFile">Absolute entry project selected by the source declaration.</param>
/// <param name="Configuration">Exact MSBuild configuration selected by the caller.</param>
public sealed record ManagedLocalSource(string ProjectFile, string Configuration);

/// <summary>A saved logical link and its bundle selection. Running state is reported by the plugin inventory.</summary>
/// <param name="Name">Saved dependency name.</param>
/// <param name="SourceRoot">Canonical absolute author directory.</param>
/// <param name="ProjectFile">Absolute entry project selected by the source declaration.</param>
/// <param name="Configuration">Exact MSBuild configuration selected by the caller.</param>
/// <param name="Selected">Whether the saved manifest selects the bundle.</param>
public sealed record RegisteredLocalSource(
    string Name,
    string SourceRoot,
    string ProjectFile,
    string Configuration,
    bool Selected);

/// <summary>The first content read, MSBuild evaluation, durable registration and activation each need a host decision.</summary>
public enum LocalSourceStage
{
    /// <summary>Probe a source directory or disclose its saved location.</summary>
    Read,

    /// <summary>Evaluate the selected project with the host's MSBuild tool.</summary>
    Evaluate,

    /// <summary>Persist the checked source and its build configuration.</summary>
    Register,

    /// <summary>Use a verified output for the running profile.</summary>
    Activate
}

/// <summary>A host authorization request. Subject is null for the host's own reopen/refresh. Facts are available after evaluation.</summary>
/// <param name="Subject">Authenticated host principal, or null for a host-owned refresh or reopen.</param>
/// <param name="ProfileDirectory">Absolute directory of the governed profile.</param>
/// <param name="Stage">Action for which the host must make a decision.</param>
/// <param name="SourceRoot">Canonical absolute author directory.</param>
/// <param name="Configuration">Exact MSBuild configuration selected by the caller.</param>
/// <param name="ProjectFile">Exact project once selected; absent before the first read.</param>
/// <param name="Facts">Verified facts when authorizing registration or activation.</param>
public sealed record LocalSourceAccess(
    string? Subject,
    string ProfileDirectory,
    LocalSourceStage Stage,
    string SourceRoot,
    string Configuration,
    string? ProjectFile = null,
    LocalSourceFacts? Facts = null);

/// <summary>Resolves relative sources against Workspace. The host decides per phase which callers and project writers it trusts.</summary>
/// <param name="Workspace">Absolute trusted host workspace used for path resolution and containment.</param>
/// <param name="Authorize">Returns whether this caller or the host may perform the named stage.</param>
public sealed record LocalSourcePolicy(
    string Workspace,
    Func<LocalSourceAccess, CancellationToken, Task<bool>> Authorize);

/// <summary>Host-selected local project adapter. The package feed toolchain has a separate archive inspection contract.</summary>
public interface IProfileLocalSourceToolchain
{
    /// <summary>The absolute profile directory governed by this Host adapter.</summary>
    string ProfileDirectory
    {
        get;
    }

    /// <summary>Authorize content reads and project evaluation, then observe the requested configuration's complete output.</summary>
    Task<LocalSourceFacts> InspectLocalSourceAsync(
        string subject,
        LocalSourceRequest request,
        CancellationToken cancellationToken);

    /// <summary>Recheck the host's registration or activation decision for a particular inspected project and output.</summary>
    Task AuthorizeLocalSourceAsync(
        string subject,
        LocalSourceStage stage,
        LocalSourceFacts facts,
        CancellationToken cancellationToken);

    /// <summary>Authorize disclosure of a persisted source record without evaluating the project.</summary>
    Task AuthorizeLocalSourceReadAsync(
        string subject,
        RegisteredLocalSource source,
        CancellationToken cancellationToken);
}
