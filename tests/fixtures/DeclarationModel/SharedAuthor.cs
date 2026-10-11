using System.Text.Json.Serialization;
using Cordis.Composition;

namespace IndependentDeclarations;

// One authored DTO is selected as an ordinary root and used at the Remote boundary.
public sealed class SharedData
{
    [JsonPropertyName("display_name")]
    public required string Name
    {
        get; set;
    }
    public string? Note { get; set; } = "initial";
    public int Count { get; set; } = 7;
    public SharedData? Child
    {
        get; set;
    }
}

public sealed class SharedEnvelope
{
    [JsonPropertyName("child")]
    public SharedData? Child
    {
        get; set;
    }
}

[RemoteService("sharedData", typeof(SharedRemoteJson))]
public partial class SharedRemote
{
    [RemoteMethod("echo")]
    public Task<SharedData> EchoAsync(SharedData value) => Task.FromResult(value);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SharedData))]
public partial class SharedRemoteJson : JsonSerializerContext;
