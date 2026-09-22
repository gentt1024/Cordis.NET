using System.Reflection;

namespace Cordis.Composition;

/// <summary>Reads explicitly named embedded patch resources from the supplied deployed assembly.</summary>
/// <remarks>No assemblies, streams or parsed rows are cached. Reading does not register modules or activate entries.</remarks>
public static class PatchResources
{
    /// <summary>Reads a fresh copy of an embedded resource and closes its stream before returning.</summary>
    /// <param name="assembly">The assembly containing the resource; no source directory or package cache is consulted.</param>
    /// <param name="resourceName">The exact manifest resource name, normally fixed by EmbeddedResource LogicalName.</param>
    /// <exception cref="FileNotFoundException">The named assembly resource is absent.</exception>
    public static string ReadText(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded patch resource '{resourceName}' was not found in assembly '{assembly.FullName}'.", resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Reads fresh patch rows through the existing YAML/JSON parser, ready for EntryPatches.Apply or ApplicationBoot.</summary>
    /// <param name="assembly">The assembly containing the resource.</param>
    /// <param name="resourceName">The exact manifest resource name.</param>
    /// <param name="json">Use JSON parsing instead of the default Loader YAML dialect.</param>
    /// <remarks>Rows remain raw patch data, including !!js expressions. No module-name rebasing, evaluation or application is performed.</remarks>
    /// <exception cref="FileNotFoundException">The named resource is absent.</exception>
    /// <exception cref="FormatException">Resource parsing failed; the original parser exception is retained as the inner exception.</exception>
    public static List<EntryOptions> Read(Assembly assembly, string resourceName, bool json = false)
    {
        var text = ReadText(assembly, resourceName);
        try { return ConfigurationFile.ParseEntries(text, json); }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or YamlDotNet.Core.YamlException)
        {
            throw new FormatException($"Cannot parse embedded patch resource '{resourceName}' in assembly '{assembly.FullName}'.", error);
        }
    }
}
