namespace Cordis.Composition;

/// <summary>Committed PluginManager inventory change.</summary>
[RemoteEvent("plugin-manager/changed", typeof(PluginManagerJson))]
public delegate void PluginManagerChangedEvent(PluginManagerChanged change);

/// <summary>Installation task progress, owned by the existing installation operation.</summary>
[RemoteEvent("plugin-manager/install-state", typeof(PluginManagerJson))]
public delegate void PluginInstallStateEvent(PackageProgress progress);

/// <summary>Installation output from the existing installation operation.</summary>
[RemoteEvent("plugin-manager/install-log", typeof(PluginManagerJson))]
public delegate void PluginInstallLogEvent(PackageProgress progress);

/// <summary>Settings form version changed.</summary>
[RemoteEvent("settings/document-updated", typeof(SettingsJson))]
public delegate void SettingsDocumentUpdatedEvent(string ns, double revision);

/// <summary>The application configuration was reloaded.</summary>
[RemoteEvent("app-boot/config-reload", typeof(SettingsJson))]
public delegate void ConfigurationReloadEvent();

/// <summary>Client module inventory changed.</summary>
[RemoteEvent("client-modules/changed", typeof(SettingsJson))]
public delegate void ClientModulesChangedEvent();

/// <summary>Standard platform events selected from their generated declarations.</summary>
public static class StandardRemoteEvents
{
    /// <summary>Generate listener types and install Host forwarding from this same selection.</summary>
    public static IReadOnlyList<TypertRemoteEventDefinition> Definitions
    {
        get;
    } = Array.AsReadOnly<TypertRemoteEventDefinition>(
    [
        PluginManagerChangedEventTypert.Definition,
        PluginInstallStateEventTypert.Definition,
        PluginInstallLogEventTypert.Definition,
        SettingsDocumentUpdatedEventTypert.Definition,
        ConfigurationReloadEventTypert.Definition,
        ClientModulesChangedEventTypert.Definition
    ]);
}
