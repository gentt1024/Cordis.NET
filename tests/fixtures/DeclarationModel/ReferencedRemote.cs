using System.Text.Json.Serialization;
using Cordis.Composition;

namespace IndependentDeclarations;

[RemoteService("referencedData", typeof(ReferencedRemoteJson))]
public partial class ReferencedRemote
{
    [RemoteMethod("echo")]
    public Task<ReferencedData> EchoAsync(ReferencedData value) => Task.FromResult(value);
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ReferencedData))]
internal partial class ReferencedRemoteJson : JsonSerializerContext;
