using System.Text.Json.Serialization;
using Cordis.Composition;
using IndependentDeclarations.External;

namespace IndependentDeclarations;

[RemoteService("referencedData", typeof(ReferencedRemoteJson))]
public partial class ReferencedRemote
{
    [RemoteMethod("defaults")]
    public Task<DefaultSnapshot> DefaultsAsync(DefaultSnapshot value) => Task.FromResult(value);

    [RemoteMethod("echo")]
    public Task<ReferencedData> EchoAsync(ReferencedData value) => Task.FromResult(value);

    [RemoteMethod("guid")]
    public Task<Guid> GuidAsync(Guid value = default) => Task.FromResult(value);

    [RemoteMethod("nullableGuid")]
    public Task<Guid?> NullableGuidAsync(Guid? value = null) => Task.FromResult(value);

    [RemoteMethod("item")]
    public Task<GuidItem> ItemAsync(GuidItem value) => Task.FromResult(value);
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ReferencedData))]
[JsonSerializable(typeof(DefaultSnapshot))]
[JsonSerializable(typeof(Guid))]
[JsonSerializable(typeof(Guid?))]
[JsonSerializable(typeof(GuidItem))]
internal partial class ReferencedRemoteJson : JsonSerializerContext;
