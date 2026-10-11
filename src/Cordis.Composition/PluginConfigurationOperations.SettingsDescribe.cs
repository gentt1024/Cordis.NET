using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>One declared secret's presence, without its value.</summary>
/// <param name="Path">Object keys and concrete collection positions.</param>
/// <param name="Set">Whether the live value contains the secret.</param>
public sealed record SettingsSecretView(
    [property: JsonPropertyName("path")] IReadOnlyList<string> Path,
    [property: JsonPropertyName("set")] bool Set);

/// <summary>One redacted live namespace with its Settings owner's numeric form revision.</summary>
/// <param name="Ns">The unique profile entry identity.</param>
/// <param name="AutoGenerate">Whether a consumer may generate a page.</param>
/// <param name="Schema">The Schemastery declaration with safe defaults preserved and secret defaults and required metadata removed.</param>
/// <param name="Value">The selected redacted live values.</param>
/// <param name="Revision">The observed form revision in this Settings owner. It starts at zero and is not persisted.</param>
public sealed record SettingsNamespaceView(
    [property: JsonPropertyName("ns")] string Ns,
    [property: JsonPropertyName("autoGenerate")]
    bool AutoGenerate,
    [property: JsonPropertyName("schema")] JsonElement Schema,
    [property: JsonPropertyName("value")] JsonElement Value,
    [property: JsonPropertyName("revision")]
    double Revision)
{
    /// <summary>The redacted resolved configuration beneath the user override.</summary>
    [JsonPropertyName("base")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Base
    {
        get;
        init;
    }

    /// <summary>The redacted raw user override; field presence marks an override.</summary>
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
