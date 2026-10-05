namespace Cordis.Composition;

/// <summary>Both independent declaration exports read at one content/activation checkpoint.</summary>
/// <param name="EntryId">The live entry.</param>
/// <param name="Revision">The same fence used by configuration edits.</param>
/// <param name="Schemastery">The uid/refs declaration envelope.</param>
/// <param name="JsonSchema">The JSON Schema 2020-12 declaration projection.</param>
public sealed record ConfigurationSchemaView(string EntryId, string Revision, ConfigurationSchemaExport Schemastery, ConfigurationSchemaExport JsonSchema);

public sealed partial class PluginConfigurationOperations
{
    /// <summary>Synchronize profile changes and export the active entry's captured declaration.</summary>
    /// <remarks>Synchronization uses the normal lifecycle and can execute plugin code. Projection itself executes no validators or lazy builders.
    /// Defaults can contain sensitive data. Hosts must authorize this full configuration endpoint separately from redacted settings.</remarks>
    public Task<ConfigurationSchemaView> ReadConfigurationSchemasAsync(string entryId, CancellationToken cancellationToken = default)
        => ReadSchemasCoreAsync(entryId, null, cancellationToken);

    /// <summary>Export only offered live fields, with hidden fields and every declared default removed.</summary>
    /// <remarks>Secret fields remain write-only in the form. The same mandatory host policy used for settings reads controls export.</remarks>
    public Task<ConfigurationSchemaView> ReadSettingsSchemasAsync(string entryId, SettingsPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return ReadSchemasCoreAsync(entryId, policy, cancellationToken);
    }

    private async Task<ConfigurationSchemaView> ReadSchemasCoreAsync(string entryId, SettingsPolicy? policy, CancellationToken cancellationToken)
    {
        ConfigurationSchemaView result = null!;
        await ConfigurationTransactionAsync(async () =>
        {
            await ReloadAsync(null);
            var layers = (await ProfileComposition.RefreshAsync(launch)).Layers;
            await include.Context.RunAsync(context =>
            {
                var entry = EditableEntry(entryId);
                var descriptor = entry.Fiber!.ConfigDescription ?? throw new Refusal("no-configuration-description");
                if (policy is not null)
                {
                    descriptor = ConfigDescriptor.Object(policy.Fields.Where(name => policy.Allows(name)
                        && TrySettingsValue(entry, name, out _, out _)).Select(name => (name, SettingsDescriptor(descriptor.Properties[name], descriptor.IsVolatile)!)).ToArray());
                }
                result = new(entryId, Revision(entry, layers), ConfigurationSchemaExporter.ToSchemastery(descriptor, policy is not null, policy is not null),
                    ConfigurationSchemaExporter.ToJsonSchema(descriptor, policy is not null));
                return Task.CompletedTask;
            });
        }, cancellationToken);
        return result;
    }
}
