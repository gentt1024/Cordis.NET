#nullable enable
using System.Collections.Immutable;
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

    [RemoteMethod("immutable")]
    public Task<ImmutablePayload> Immutable(ImmutablePayload input) => Task.FromResult(input);

    [RemoteMethod("timestamp")]
    public Task<DateTimeOffset?> Timestamp(DateTimeOffset? input) => Task.FromResult(input);

    [RemoteMethod("mode")]
    public Task<WireMode> Mode(WireMode input) => Task.FromResult(input);

    [RemoteMethod("plainMode")]
    public Task<PlainMode> PlainMode(PlainMode input) => Task.FromResult(input);

    [RemoteMethod("genericInt")]
    public Task<WireEnvelope<int>> GenericInt(WireEnvelope<int> input) => Task.FromResult(input);

    [RemoteMethod("genericText")]
    public Task<WireEnvelope<string>> GenericText(WireEnvelope<string> input) => Task.FromResult(input);

    [RemoteMethod("genericNullable")]
    public Task<WireEnvelope<int?>> GenericNullable(WireEnvelope<int?> input) => Task.FromResult(input);

    [RemoteMethod("genericArray")]
    public Task<WireEnvelope<ImmutableArray<string?>>> GenericArray(WireEnvelope<ImmutableArray<string?>> input) =>
        Task.FromResult(input);

    [RemoteMethod("seed")]
    public Task<RuntimeDefault> Seed() => Task.FromResult(new RuntimeDefault());

    [RemoteMethod("passiveDefault")]
    public Task<PassiveSnapshot> PassiveDefault(PassiveSnapshot input) => Task.FromResult(input);
}

public sealed record PassiveSnapshot(int Id)
{
    public PassivePolicy Policy
    {
        get;
        init;
    } = new(false, []);

    public byte[] Bytes
    {
        get;
        init;
    } = [];

    public System.Collections.Generic.List<string> Names
    {
        get;
        init;
    } = [];
}

public sealed record PassivePolicy(bool Enabled, ImmutableArray<string> Overrides)
{
    public int? Detail
    {
        get;
        init;
    }
}

public sealed record BehavioralPolicy(string Text)
{
    public string Normalized
    {
        get;
        init;
    } = Text.ToUpperInvariant();
}

public sealed class BehavioralSnapshot
{
    public BehavioralPolicy Policy
    {
        get;
        init;
    } = new("input");
}

[RemoteService("behaviorInitializerInput", typeof(BehavioralJson))]
public sealed partial class BehavioralInputService
{
    [RemoteMethod("edit")]
    public Task<int> Edit(BehavioralSnapshot input) => Task.FromResult(input.Policy.Normalized.Length);
}

[JsonSerializable(typeof(BehavioralSnapshot))]
public partial class BehavioralJson : JsonSerializerContext;

public sealed class RuntimeDefault
{
    public string Text
    {
        get;
        init;
    } = string.Empty;
}

[RemoteService("initializerInput", typeof(RuntimeDefaultJson))]
public sealed partial class InitializerInputService
{
    [RemoteMethod("edit")]
    public Task<int> Edit(RuntimeDefault input) => Task.FromResult(input.Text.Length);
}

[JsonSerializable(typeof(RuntimeDefault))]
public partial class RuntimeDefaultJson : JsonSerializerContext;

public sealed class ConverterSnapshot
{
    [JsonConverter(typeof(PolicyConverter))]
    public PassivePolicy Policy
    {
        get;
        init;
    } = new(false, []);
}

public sealed class PolicyConverter : JsonConverter<PassivePolicy>
{
    public override PassivePolicy Read(
        ref System.Text.Json.Utf8JsonReader reader,
        System.Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options) =>
        throw new System.NotSupportedException("Author converter owns its input.");

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        PassivePolicy value,
        System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue("author-policy");
}

[RemoteService("converterInitializerInput", typeof(ConverterJson))]
public sealed partial class ConverterInputService
{
    [RemoteMethod("edit")]
    public Task<int> Edit(ConverterSnapshot input) => Task.FromResult(input.Policy.Overrides.Length);
}

[JsonSerializable(typeof(ConverterSnapshot))]
public partial class ConverterJson : JsonSerializerContext;

public sealed record WireEnvelope<T>(T? Value)
{
    public bool Accepted => Value is not null;
}

[JsonConverter(typeof(JsonStringEnumConverter<PlainMode>))]
public enum PlainMode
{
    Known = 1,
    Alias = 1
}

[Flags, JsonConverter(typeof(JsonStringEnumConverter<WireMode>))]
public enum WireMode
{
    Read = 1,
    Write = 2,

    [JsonStringEnumMemberName("wire-special")]
    Special = 4
}

public sealed class ImmutablePayload
{
    public ImmutableArray<ImmutableItem?> Items
    {
        get;
        init;
    } = [];

    public ImmutableArray<string?> Labels
    {
        get;
        init;
    } = [];

    public ImmutableArray<string> StrictLabels
    {
        get;
        init;
    } = [];

    public ImmutableArray<ImmutableItem?>? OptionalItems
    {
        get;
        init;
    }

    public ImmutableArray<string>? OptionalStrictLabels
    {
        get;
        init;
    }
}

public sealed record ImmutableItem(string? Name, ImmutableArray<int> Values);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(ImmutablePayload))]
[JsonSerializable(typeof(DateTimeOffset?))]
[JsonSerializable(typeof(WireMode))]
[JsonSerializable(typeof(PlainMode))]
[JsonSerializable(typeof(WireEnvelope<int>))]
[JsonSerializable(typeof(WireEnvelope<string>))]
[JsonSerializable(typeof(WireEnvelope<int?>))]
[JsonSerializable(typeof(WireEnvelope<ImmutableArray<string?>>))]
[JsonSerializable(typeof(RuntimeDefault))]
[JsonSerializable(typeof(PassiveSnapshot))]
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
    ArgsCollisionValueArgs Args);

public sealed record ClientCollision(int Count);

public sealed record JsonCollisionJson(int Count);

public sealed record CarrierCollisionCarrierJson(int Count);

public sealed record ArgsCollisionValueArgs(int Count);

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
