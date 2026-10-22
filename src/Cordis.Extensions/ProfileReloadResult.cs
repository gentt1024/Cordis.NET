using Cordis.Composition;

namespace Cordis.Extensions;

/// <summary>A loaded module whose requested change requires a host restart.</summary>
/// <param name="Filename">The module source identity.</param>
/// <param name="Reason">host-module, unmanaged-plugin or module-comparison.</param>
public sealed record PendingProfileModule(string Filename, string Reason);

/// <summary>The observed application outcome of a profile refresh, separate from persisted configuration.</summary>
/// <param name="Warnings">Inactive entry diagnostics retained by the existing composition audit.</param>
/// <param name="Pending">Modules whose requested changes remain pending.</param>
/// <param name="RestartReason">A profile-wide restart reason, or null. Takes precedence over Pending.</param>
public sealed record ProfileReloadResult(
    IReadOnlyList<EntryDiagnostic> Warnings,
    IReadOnlyList<PendingProfileModule> Pending,
    string? RestartReason = null)
{
    /// <summary>Whether this refresh left a requested change pending until restart.</summary>
    public bool RequiresRestart => RestartReason is not null || Pending.Count != 0;
}
