namespace Cordis.Composition;

/// <summary>The replacement operation that could not be completed safely.</summary>
public enum PluginReplacementPhase
{
    /// <summary>The old generation did not retire safely; the candidate was not activated.</summary>
    Retirement,

    /// <summary>Candidate activation failed after old-generation retirement.</summary>
    Activation
}

/// <summary>Framework-owned recovery settlement; application business readiness needs its own verification.</summary>
public enum PluginRecoveryState
{
    /// <summary>Recovery was not safe to attempt; affected business admission must remain closed.</summary>
    NotAttempted,

    /// <summary>Candidate cleanup and original-plugin activation settled successfully.</summary>
    Succeeded,

    /// <summary>Original-plugin activation did not settle successfully.</summary>
    Failed
}

/// <summary>Failure details attached to the original thrown exception without replacing its identity.</summary>
/// <remarks>Retirement failure can leave a partially stopped generation. NotAttempted and Failed never
/// authorize resuming service. The application must keep affected admission closed and invalidate late
/// business work. No details on an arbitrary callback failure also means recovery is unconfirmed.</remarks>
public sealed class PluginReplacementFailure
{
    private const string Key = "Cordis.Composition.PluginReplacementFailure";

    internal PluginReplacementFailure(
        PluginReplacementPhase phase,
        PluginRecoveryState recovery,
        IEnumerable<Exception> cleanupErrors,
        IEnumerable<Exception> recoveryErrors)
    {
        Phase = phase;
        Recovery = recovery;
        CleanupErrors = Array.AsReadOnly(
            cleanupErrors.Distinct<Exception>(ReferenceEqualityComparer.Instance).ToArray());
        RecoveryErrors = Array.AsReadOnly(recoveryErrors.ToArray());
    }

    /// <summary>The failed lifecycle phase.</summary>
    public PluginReplacementPhase Phase
    {
        get;
    }

    /// <summary>The framework-owned recovery result.</summary>
    public PluginRecoveryState Recovery
    {
        get;
    }

    /// <summary>Cleanup failures that prevent confirming retirement.</summary>
    public IReadOnlyList<Exception> CleanupErrors
    {
        get;
    }

    /// <summary>Failures encountered while restoring the original plugin.</summary>
    public IReadOnlyList<Exception> RecoveryErrors
    {
        get;
    }

    /// <summary>Read the outcome of a Loader replacement failure. Null provides no recovery guarantee.</summary>
    public static PluginReplacementFailure? FromException(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            return error.Data[Key] as PluginReplacementFailure;
        }
        catch
        {
            return null;
        }
    }

    internal void Attach(Exception error)
    {
        try
        {
            error.Data[Key] = this;
        }
        catch
        {
            // A custom exception's metadata must not replace the original failure.
        }
    }
}
