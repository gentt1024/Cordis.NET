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

[RemoteService("numericDefaults", typeof(NumericJson), Namespace = "numericDefaults")]
public sealed partial class NumericService
{
    [RemoteMethod("value")]
    public Task<NumericDefaults> Value(NumericDefaults input) => Task.FromResult(input);
}

public sealed class NumericDefaults
{
    public float? Ratio
    {
        get;
        set;
    } = 1.25f;

    public decimal? Amount
    {
        get;
        set;
    } = 0.1m;

    public float? EmptyRatio
    {
        get;
        set;
    } = null;

    public decimal? EmptyAmount
    {
        get;
        set;
    } = null;
}

[JsonSerializable(typeof(NumericDefaults))]
public partial class NumericJson : JsonSerializerContext;

[RemoteService("helperCollision", typeof(HelperCollisionJson), Namespace = "helperCollision")]
public sealed partial class HelperCollisionService
{
    [RemoteMethod("Value")]
    public Task<HelperCollisions> Value(HelperCollisions input) => Task.FromResult(input);
}

public sealed record HelperCollisions(
    ClientCollision Client,
    JsonCollisionJson Json,
    CarrierCollisionCarrierJson Carrier,
    DemoClientFailure Failure,
    ArgsCollisionValueArgs Args,
    RequestCollisionValueRequest Request,
    ResponseCollisionValueResponse Response);

public sealed record ClientCollision(int Count);

public sealed record JsonCollisionJson(int Count);

public sealed record CarrierCollisionCarrierJson(int Count);

public sealed record DemoClientFailure(int Count);

public sealed record ArgsCollisionValueArgs(int Count);

public sealed record RequestCollisionValueRequest(int Count);

public sealed record ResponseCollisionValueResponse(int Count);

[JsonSerializable(typeof(HelperCollisions))]
public partial class HelperCollisionJson : JsonSerializerContext;

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
