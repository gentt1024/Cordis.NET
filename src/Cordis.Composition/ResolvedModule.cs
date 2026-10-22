using Cordis;

namespace Cordis.Composition;

/// <summary>A resolved import in one runtime generation, independent of other imports sharing its plugin.</summary>
/// <remarks>Resolvers retain this reference for repeated imports of the same request in that generation.
/// Holding it retains the exported plugin; it is not an unload or file ownership handle.</remarks>
public sealed class ResolvedModule
{
    /// <summary>Create an import identity with an optional source file for native HMR diagnostics.</summary>
    public ResolvedModule(IPlugin plugin, string? filename = null)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        Plugin = plugin;
        Filename = filename;
    }

    /// <summary>The plugin exported by this import.</summary>
    public IPlugin Plugin
    {
        get;
    }

    /// <summary>The loaded source file, or null when the resolver has no file identity.</summary>
    public string? Filename
    {
        get;
    }
}

/// <summary>One loaded import and its prepared successor, retaining alias ownership during a split.</summary>
/// <param name="Previous">The actual loaded import, compared by reference.</param>
/// <param name="Replacement">The prepared successor import.</param>
public sealed record ModuleReplacement(ResolvedModule Previous, ResolvedModule Replacement);
