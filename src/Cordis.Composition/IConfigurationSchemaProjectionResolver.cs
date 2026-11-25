namespace Cordis.Composition;

/// <summary>Optional document-discovery capability of a module resolver for plugins with a foreign declaration format.</summary>
public interface IConfigurationSchemaProjectionResolver
{
    /// <summary>Return null to use ordinary module resolution. Return an owned declaration or throw for an owned module.</summary>
    /// <remarks>The owning resolver can run trusted import or projection code. Do not activate a plugin.
    /// Throw <see cref="NotSupportedException"/> for an unsupported declaration. Other failures are reported on the entry.</remarks>
    ValueTask<ResolvedConfigurationSchema?> TryResolveSchemaAsync(
        string specifier,
        Uri baseUri,
        CancellationToken cancellationToken = default);
}

/// <summary>A module's resolved identity and its standalone input schema for document discovery.</summary>
/// <param name="ModuleIdentity">Stable identity of the resolved export across imports in this discovery. Separate exports have separate identities.</param>
/// <param name="Schema">A self-contained JSON Schema draft 2020-12 export in the json-schema-2020-12 format,
/// or null if the owned module declares no Config. Report projection limitations through the export's Complete and Diagnostics.</param>
/// <param name="AcceptsMissing">Whether the input may be omitted. Null signals an unknown omission rule for a supplied schema.</param>
public sealed record ResolvedConfigurationSchema(
    string ModuleIdentity,
    ConfigurationSchemaExport? Schema,
    bool? AcceptsMissing);
