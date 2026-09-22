using Cordis.Composition;

namespace Cordis.Example.Greeting;

/// <summary>
/// Represents the greeting module component.
/// </summary>
public static class GreetingModule
{
    /// <summary>
    /// Gets the plugin value.
    /// </summary>
    public static IPlugin Plugin { get; } = new Plugin<string>
    {
        Name = "greeting",
        Config = raw => raw is string text ? ConfigResult<string>.Success(text) : ConfigResult<string>.Failure("Greeting config must be a string."),
        Apply = (ctx, text) => ctx.Provide("greeting", text),
    };

    /// <summary>
    /// Reads patch.
    /// </summary>
    public static string ReadPatch() => PatchResources.ReadText(typeof(GreetingModule).Assembly, "Cordis.Example.Greeting.cordis.patch.yml");
}
