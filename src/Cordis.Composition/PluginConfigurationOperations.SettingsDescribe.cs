using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>Host-selected namespace identity, live entry, visibility and page policy.</summary>
/// <param name="Ns">The namespace exposed to the consumer.</param>
/// <param name="EntryId">The current Loader entry address.</param>
/// <param name="Policy">The offered and hidden live fields.</param>
/// <param name="AutoGenerate">Whether a consumer may generate a page.</param>
public sealed record SettingsNamespaceSelection(
    string Ns,
    string EntryId,
    SettingsPolicy Policy,
    bool AutoGenerate = true);

/// <summary>One declared secret's presence, without its value.</summary>
/// <param name="Path">Object keys and concrete collection positions.</param>
/// <param name="Set">Whether the live value contains the secret.</param>
public sealed record SettingsSecretView(
    [property: JsonPropertyName("path")] IReadOnlyList<string> Path,
    [property: JsonPropertyName("set")] bool Set);

/// <summary>One selected live namespace, using a native content and activation revision.</summary>
/// <param name="Ns">The host-selected namespace identity.</param>
/// <param name="AutoGenerate">Whether a consumer may generate a page.</param>
/// <param name="Schema">The redacted Schemastery declaration, with all defaults omitted.</param>
/// <param name="Value">The selected redacted live values.</param>
/// <param name="Revision">The native content and activation hash, not a monotonic counter.</param>
public sealed record SettingsNamespaceView(
    [property: JsonPropertyName("ns")] string Ns,
    [property: JsonPropertyName("autoGenerate")]
    bool AutoGenerate,
    [property: JsonPropertyName("schema")] JsonElement Schema,
    [property: JsonPropertyName("value")] JsonElement Value,
    [property: JsonPropertyName("revision")]
    string Revision)
{
    /// <summary>The native live-only provider does not reconstruct a composition base.</summary>
    [JsonPropertyName("base")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Base
    {
        get;
        init;
    }

    /// <summary>The native live-only provider does not reconstruct the raw user layer.</summary>
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? User
    {
        get;
        init;
    }

    /// <summary>The selected configuration is applied live.</summary>
    [JsonPropertyName("applies")]
    [JsonRequired]
    public string Applies
    {
        get;
        init;
    } = "live";

    /// <summary>Concrete declared secret positions and their configured state.</summary>
    [JsonPropertyName("secrets")]
    [JsonRequired]
    public IReadOnlyList<SettingsSecretView> Secrets
    {
        get;
        init;
    } = [];
}

/// <summary>The deployment facts and namespaces consumed by a settings page.</summary>
/// <param name="Writable">Explicit host authority for write controls.</param>
/// <param name="HasDocument">Explicit host authority for document presence.</param>
/// <param name="Namespaces">The selected currently available live namespaces.</param>
public sealed record SettingsDescribeValue(
    [property: JsonPropertyName("writable")]
    bool Writable,
    [property: JsonPropertyName("hasDocument")]
    bool HasDocument,
    [property: JsonPropertyName("namespaces")]
    IReadOnlyList<SettingsNamespaceView> Namespaces);

/// <summary>A native read retaining projection limitations separately from the fixed wire value.</summary>
/// <param name="Value">The exact settings describe response.</param>
/// <param name="Diagnostics">Namespace and field projection limitations without captured values.</param>
public sealed record SettingsDescribeSnapshot(SettingsDescribeValue Value, IReadOnlyList<string> Diagnostics);

public sealed partial class PluginConfigurationOperations
{
    /// <summary>Read every host-selected namespace's values, schema and revision at one profile checkpoint.</summary>
    /// <remarks>Unavailable entries are omitted. The native provider publishes live values only; base and user remain absent.</remarks>
    public async Task<SettingsDescribeSnapshot> DescribeSettingsAsync(
        IReadOnlyList<SettingsNamespaceSelection> selections,
        bool writable,
        bool hasDocument,
        CancellationToken cancellationToken = default)
    {
        var selected = SnapshotSettingsSelections(selections);
        SettingsDescribeSnapshot result = null!;
        await ConfigurationTransactionAsync(
            async () =>
            {
                await ReloadAsync(null);
                var layers = (await ProfileComposition.RefreshAsync(launch)).Layers;
                await include.Context.RunAsync(_ =>
                {
                    var namespaces = new List<SettingsNamespaceView>();
                    var diagnostics = new List<string>();
                    var entries = include.Loader.Entries().ToDictionary(entry => entry.Id, StringComparer.Ordinal);
                    foreach (var selection in selected)
                    {
                        if (!entries.TryGetValue(selection.EntryId, out var entry) ||
                            entry.Fiber?.State != FiberState.Active || entry.Fiber.ConfigDescription is null)
                        {
                            diagnostics.Add(selection.Ns + ": namespace is unavailable");
                            continue;
                        }

                        var captured = CaptureSettings(entry, selection.Policy);
                        if (captured.Descriptor.Properties.Count == 0)
                        {
                            diagnostics.AddRange(captured.Diagnostics.Select(message => selection.Ns + ": " + message));
                            diagnostics.Add(selection.Ns + ": no supported live fields were selected");
                            continue;
                        }

                        var schema = ConfigurationSchemaExporter.ToSchemastery(captured.Descriptor, true, true);
                        diagnostics.AddRange(
                            captured
                                .Diagnostics.Concat(schema.Diagnostics)
                                .Select(message => selection.Ns + ": " + message));
                        if (!ConfigurationSchemaExporter.TryJson(captured.Value, out var encoded))
                            throw new RemoteError(
                                "gateway/internal",
                                "settings namespace \"" + selection.Ns +
                                "\" could not encode its selected live values");
                        using var valueDocument = JsonDocument.Parse(encoded!.ToJsonString());
                        using var schemaDocument = JsonDocument.Parse(schema.Document);
                        namespaces.Add(
                            new(
                                selection.Ns,
                                selection.AutoGenerate,
                                schemaDocument.RootElement.Clone(),
                                valueDocument.RootElement.Clone(),
                                Revision(entry, layers))
                            {
                                Secrets = Array.AsReadOnly(
                                    captured
                                        .Secrets.Select(secret =>
                                            new SettingsSecretView(Array.AsReadOnly(secret.Path.ToArray()), secret.Set))
                                        .ToArray())
                            });
                    }

                    result = new(new(writable, hasDocument, namespaces.AsReadOnly()), diagnostics.AsReadOnly());
                    return Task.CompletedTask;
                });
            },
            cancellationToken);
        return result;
    }

    internal static IReadOnlyList<SettingsNamespaceSelection> SnapshotSettingsSelections(
        IEnumerable<SettingsNamespaceSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(selections);
        var snapshot = new List<SettingsNamespaceSelection>();
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selection in selections)
        {
            ArgumentNullException.ThrowIfNull(selection);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.Ns);
            ArgumentException.ThrowIfNullOrWhiteSpace(selection.EntryId);
            ArgumentNullException.ThrowIfNull(selection.Policy);
            if (!namespaces.Add(selection.Ns))
                throw new ArgumentException(
                    "Settings namespace selections must have unique names.",
                    nameof(selections));
            snapshot.Add(
                selection with
                {
                    Policy = new(selection.Policy.Fields, selection.Policy.HiddenFields)
                });
        }

        return snapshot.AsReadOnly();
    }
}
