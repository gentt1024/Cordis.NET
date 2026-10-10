#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Cordis.Composition;

namespace Author;

[RemoteService("primitives", typeof(PrimitiveJson), Namespace = "primitives")]
public sealed partial class PrimitiveService
{
    [RemoteMethod("count")]
    public Task<int> Count(string? request, int cancellationToken, bool httpClient, string? __cordisRequest, int _) =>
        Task.FromResult(cancellationToken);

    [RemoteMethod("text")]
    public Task<string?> Text() => Task.FromResult<string?>(null);

    [RemoteMethod("values")]
    public Task<int[]> Values() => Task.FromResult(new[] { 1, 2 });
}

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int[]))]
public partial class PrimitiveJson : JsonSerializerContext;

[RemoteService("nullable", typeof(NullableJson), Namespace = "nullable")]
public sealed partial class NullableService
{
    [RemoteMethod("value")]
    public Task<NullableValue> Value() => Task.FromResult(new NullableValue("yes"));
}

public sealed class NullableValue
{
    [JsonConstructor]
    public NullableValue([DisallowNull] string? text) => Text = text;

    [DisallowNull, JsonPropertyName("text")]
    public string? Text
    {
        get;
        init;
    }

    [DisallowNull, JsonPropertyName("strict")]
    public string? Strict
    {
        get;
        init;
    }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NullableValue))]
public partial class NullableJson : JsonSerializerContext;

[RemoteService("readonly", typeof(ReadonlyJson), Namespace = "readonly")]
public sealed partial class ReadonlyService
{
    [RemoteMethod("value")]
    public Task<ReadonlyValue> Value() => Task.FromResult(new ReadonlyValue());
}

public sealed class ReadonlyValue
{
    [JsonPropertyName("applies")] public string Applies => "live";
}

[JsonSerializable(typeof(ReadonlyValue))]
public partial class ReadonlyJson : JsonSerializerContext;

[RemoteService("collision", typeof(PrimitiveJson), Namespace = "collision")]
public sealed partial class CollisionService
{
    [RemoteMethod("ping")]
    public Task<int> Lower() => Task.FromResult(0);

    [RemoteMethod("Ping")]
    public Task<int> Upper() => Task.FromResult(0);
}

[RemoteService("recordStruct", typeof(StructJson), Namespace = "recordStruct")]
public sealed partial class StructService
{
    [RemoteMethod("value")]
    public Task<ValueRecord> Value() => Task.FromResult(new ValueRecord(1));
}

public readonly record struct ValueRecord(int Count);

[JsonSerializable(typeof(ValueRecord))]
public partial class StructJson : JsonSerializerContext;

#if CORDIS_MODEL_OPTIONAL
[RemoteService("optional", typeof(PrimitiveJson), Namespace = "optional")]
public sealed partial class OptionalService
{
    [RemoteMethod("count")]
    public Task<int> Count(int limit = 7) => Task.FromResult(limit);
}
#endif
