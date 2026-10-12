using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using IndependentDeclarations.External;

namespace IndependentDeclarations;

/// <summary>Selected ordinary configuration data.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DataOptions
{
    public DataOptions() => Ratio = 99;

    [JsonPropertyName("display_name")]
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

    public string? Note
    {
        get;
        set;
    } = "initial";

    public float? Ratio
    {
        get;
        init;
    } = 1.25f;

    public decimal? Amount
    {
        get;
        init;
    } = 0.1m;

    public string? Empty
    {
        get;
        init;
    } = null;

    [DisallowNull]
    public string? Strict
    {
        get;
        set;
    } = "strict";

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public int Count
    {
        get;
        set;
    } = 7;

    [JsonIgnore]
    public string Local
    {
        get;
        set;
    } = "local";

    public DataChild? Child
    {
        get;
        set;
    }

    public IReadOnlyList<DataChild>? Items
    {
        get;
        set;
    }

    public List<int>? Counts
    {
        get;
        set;
    }

    public int[]? Numbers
    {
        get;
        set;
    }

    public JsonElement? Extra
    {
        get;
        set;
    }

    public int Computed
    {
        get => 100;
        set
        {
        }
    }
}

public sealed class DataChild
{
    public required string Label
    {
        get;
        init;
    }

    public DataChild? Next
    {
        get;
        set;
    }
}

public sealed class DataExpression
{
    public string Value
    {
        get;
        set;
    } = Guid.NewGuid().ToString();
}

public sealed class DataExternal
{
    public Envelope<DataOptions>? Value
    {
        get;
        set;
    }
}

public sealed class DataReadOnly
{
    public string Value
    {
        get;
    } = "fixed";
}

public sealed class DataWireCollision
{
    [JsonPropertyName("same")]
    public int First
    {
        get;
        set;
    }

    [JsonPropertyName("same")]
    public int Second
    {
        get;
        set;
    }
}

public sealed class ReferencedData
{
    public Dictionary<string, ImportedData?>? ByName
    {
        get;
        set;
    }

    public IReadOnlyDictionary<string, ImportedData[]>? Groups
    {
        get;
        set;
    }

    public IDictionary<string, string?>? Labels
    {
        get;
        set;
    }

    public required ImportedData External
    {
        get;
        init;
    }

    public IReadOnlyList<ImportedData>? Items
    {
        get;
        set;
    }
}

public sealed class ReferencedHiddenData
{
    public ImportedHiddenData? Value
    {
        get;
        set;
    }
}

public sealed class NonStringDictionaryData
{
    public Dictionary<int, string>? Values
    {
        get;
        set;
    }
}
