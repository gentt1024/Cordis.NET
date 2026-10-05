using Cordis;

namespace Cordis.Composition;
/// <summary>A launcher-owned immutable argument snapshot. Application parsers consume it without owning process streams.</summary>
public sealed class CommandLineArguments
{
    private readonly IReadOnlyList<string> arguments;
    /// <summary>Copy application-owned arguments without parsing or retaining the caller's collection.</summary>
    public CommandLineArguments(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        this.arguments = Array.AsReadOnly(arguments.ToArray());
    }

    /// <summary>Return the same read-only snapshot to every consumer.</summary>
    public IReadOnlyList<string> Get() => arguments;
    /// <summary>Register the copied argument values for the lifetime of the current plugin.</summary>
    public static EffectHandle Provide(Context context, IEnumerable<string> arguments, string serviceName = "cmdlineArgs") => context.Provide(serviceName, new CommandLineArguments(arguments));
}
