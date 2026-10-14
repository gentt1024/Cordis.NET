using Cordis.Composition;

namespace Cordis.Clr;

/// <summary>Borrowed access to the actual prepared exports of one replacement.</summary>
/// <remarks>Only usable during the admission callback. Retaining returned CLR objects can keep their load
/// context alive after retirement. Copy strings and JSON when retaining an admission decision.</remarks>
public sealed class ClrReplacementCandidate
{
    private readonly object gate = new();
    private readonly IReadOnlyList<string> specifiers;
    private Func<string, IPlugin>? plugin;
    private Func<string, TypertContribution?>? contribution;

    internal ClrReplacementCandidate(
        IEnumerable<string> specifiers,
        Func<string, IPlugin> plugin,
        Func<string, TypertContribution?> contribution)
    {
        this.specifiers = Array.AsReadOnly(specifiers.ToArray());
        this.plugin = plugin;
        this.contribution = contribution;
    }

    /// <summary>Every registered specifier being replaced, including aliases and inactive exports.</summary>
    public IReadOnlyList<string> Specifiers
    {
        get
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(plugin is null, this);
                return specifiers;
            }
        }
    }

    /// <summary>Get the prepared plugin without activating it. Aliases return the same instance.</summary>
    public IPlugin GetPlugin(string specifier)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(plugin is null, this);
            return plugin(specifier);
        }
    }

    /// <summary>Get the same cached contribution that will be published after commit, or null for an export
    /// without IClrTypertModule. Factories and lazy schemas may execute author code and fail.</summary>
    public TypertContribution? GetContribution(string specifier)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(contribution is null, this);
            return contribution(specifier);
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            plugin = null;
            contribution = null;
        }
    }
}
