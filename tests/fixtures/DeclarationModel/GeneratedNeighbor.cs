using System.Text.Json.Serialization;

namespace IndependentDeclarations.Generated;

public sealed class Root
{
    public string Label
    {
        get;
        set;
    } = "generated neighbor";
}

[JsonSerializable(typeof(Root))]
internal partial class FixtureJson : JsonSerializerContext;
