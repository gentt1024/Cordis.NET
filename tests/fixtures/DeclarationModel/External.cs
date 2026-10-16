using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace IndependentDeclarations.External;

public interface IExternalMarker;

public sealed record GuidItem(Guid Id)
{
#if GUID_CUSTOM_CONVERTER
    [JsonConverter(typeof(GuidTextConverter))]
#endif
    public Guid? Optional
    {
        get;
        init;
    }

    public Guid[]? Array
    {
        get;
        init;
    }

    public List<Guid?>? Values
    {
        get;
        init;
    }

    public Dictionary<string, Guid>? ByName
    {
        get;
        init;
    }
}

#if GUID_CUSTOM_CONVERTER
public sealed class GuidTextConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.Parse(reader.GetString()![3..]);

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue("id:" + value);
}
#endif

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
    public JsonElement OptionalJson
    {
        get;
        set;
    }

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
    public NumericStatus? Previous
    {
        get;
        set;
    } = NumericStatus.Succeeded;

    public NumericStatus Status
    {
        get;
        set;
    } = NumericStatus.Unavailable;

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

#if ENUM_CUSTOM_CONVERTER
[JsonConverter(typeof(JsonStringEnumConverter<NumericStatus>))]
#endif
public enum NumericStatus
{
    Unavailable = -1,
    Succeeded = 4,
    Alias = Succeeded
}

[Flags]
public enum NumericFlags : byte
{
    None = 0,
    Read = 1,
    Write = 2
}

public sealed class EnumData
{
    public NumericStatus? Previous
    {
        get;
        set;
    } = NumericStatus.Succeeded;

    public NumericStatus? Missing
    {
        get;
        set;
    } = NumericStatus.Unavailable;

    public NumericStatus? Absent
    {
        get;
        set;
    } = null;

    public NumericStatus? Empty
    {
        get;
        set;
    } = default;

    public NumericStatus Status
    {
        get;
        set;
    } = NumericStatus.Unavailable;

    public NumericStatus? Optional
    {
        get;
        set;
    }

    public NumericStatus[] History
    {
        get;
        set;
    } = [];

    public NumericFlags Flags
    {
        get;
        set;
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public NumericStatus Always
    {
        get;
        set;
    }
}
