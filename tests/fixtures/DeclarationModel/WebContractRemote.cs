using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Cordis.Composition;
using IndependentDeclarations.External;

namespace IndependentDeclarations;

[RemoteService("webContract", typeof(WebContractJson))]
public partial class WebContractRemote
{
    private int calls;

    [RemoteMethod("calls")]
    public Task<int> CallsAsync() => Task.FromResult(calls);

    [RemoteMethod("revision")]
    public Task<string> RevisionAsync([DisallowNull] double? expectedRevision = null!)
    {
        calls++;
        return Task.FromResult(expectedRevision is null ? "omitted" : "supplied");
    }

    [RemoteMethod("required")]
    public Task<RequiredConstructor> RequiredAsync(RequiredConstructor value)
    {
        calls++;
        return Task.FromResult(value);
    }

    [RemoteMethod("optional")]
    public Task<OptionalConstructor> OptionalAsync(OptionalConstructor value)
    {
        calls++;
        return Task.FromResult(value);
    }

    [RemoteMethod("selected")]
    public Task<SelectedConstructor> SelectedAsync(SelectedConstructor value)
    {
        calls++;
        return Task.FromResult(value);
    }

    [RemoteMethod("direction")]
    public Task<DirectionalData> DirectionAsync(DirectionalData value)
    {
        calls++;
        return Task.FromResult(
            new DirectionalData
            {
                OutputNullable = null!
            });
    }
}

[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
#if CTOR_TRUE
    , RespectRequiredConstructorParameters = true
#elif CTOR_FALSE
    , RespectRequiredConstructorParameters = false
#endif
)]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(RequiredConstructor))]
[JsonSerializable(typeof(OptionalConstructor))]
[JsonSerializable(typeof(SelectedConstructor))]
[JsonSerializable(typeof(DirectionalData))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
public partial class WebContractJson : JsonSerializerContext;
