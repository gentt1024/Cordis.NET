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
    public static string ReadPatch()
    {
        using var stream = typeof(GreetingModule).Assembly.GetManifestResourceStream("Cordis.Example.Greeting.cordis.patch.yml")
            ?? throw new InvalidOperationException("Packaged composition resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
