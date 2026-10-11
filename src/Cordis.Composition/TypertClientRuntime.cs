namespace Cordis.Composition;

/// <summary>The shared browser runtime shipped alongside generated Client modules.</summary>
public static class TypertClientRuntime
{
    /// <summary>Shared module. Save as cordis-remote.mjs beside generated modules.</summary>
    public static string Module
    {
        get;
    } = Read("cordis-remote.mjs");

    /// <summary>Shared TypeScript declarations. Save as cordis-remote.d.mts.</summary>
    public static string Declaration
    {
        get;
    } = Read("cordis-remote.d.mts");

    private static string Read(string name)
    {
        using var source =
            typeof(TypertClientRuntime).Assembly.GetManifestResourceStream("Cordis.Composition.Client." + name)
            ?? throw new InvalidOperationException("Missing shared Client runtime: " + name);
        using var reader = new StreamReader(source);
        return reader.ReadToEnd();
    }
}
