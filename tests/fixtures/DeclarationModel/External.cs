using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace IndependentDeclarations.External;

public interface IExternalMarker;

public sealed record RequiredConstructor(string Name);

public sealed record OptionalConstructor(string Name = "constructor-default");

public sealed class SelectedConstructor
{
    public SelectedConstructor()
    {
    }

    [JsonConstructor]
    public SelectedConstructor(string name) => Name = name;

    public string Name
    {
        get;
        set;
    } = "initial";
}

public sealed class DirectionalData
{
    [JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count
    {
        get;
        set;
    }

    [JsonRequired, DisallowNull, JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Value
    {
        get;
        set;
    }

    [JsonRequired]
    public int GlobalCount
    {
        get;
        set;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int Always
    {
        get;
        set;
    }

    [JsonRequired, MaybeNull, JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string OutputNullable
    {
        get;
        set;
    } = "initial";

    [AllowNull]
    public string InputNullable
    {
        get;
        set;
    } = "initial";
}

public sealed class Envelope<T>
{
    public T? Value
    {
        get;
        init;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ImportedData
{
    [JsonPropertyName("external_name")]
    public required string Name
    {
        get;
        init;
    }

    [JsonRequired]
    public string? Code
    {
        get;
        set;
    }

    public int Count
    {
        get;
        set;
    } = 11;

    public string? Note
    {
        get;
        set;
    } = "external-default";

    public ImportedData? Next
    {
        get;
        set;
    }
}

public sealed class UnrelatedData
{
    public string Value
    {
        get;
        set;
    } = "not-reachable";
}

public sealed partial class ImportedHiddenData
{
    public string Name
    {
        get;
        set;
    } = "visible";

    [JsonInclude]
    private string Hidden
    {
        get;
        set;
    } = "wire";

    internal static string Encode() => JsonSerializer.Serialize(
        new ImportedHiddenData(),
        HiddenJson.Default.ImportedHiddenData);

    [JsonSerializable(typeof(ImportedHiddenData))]
    internal partial class HiddenJson : JsonSerializerContext;
}
