using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cordis.Composition;

namespace IndependentDeclarations;

public sealed record NumericFrame
{
    public decimal Scale
    {
        get;
        init;
    } = 1;
}

public sealed record NumericDefinition
{
    public required string Name
    {
        get;
        init;
    }

    public bool FrameLocked
    {
        get;
        init;
    } = true;

    public NumericFrame Frame
    {
        get;
        init;
    } = new();

    public double?[] Samples
    {
        get;
        init;
    } = [];

    public Dictionary<string, uint> Counts
    {
        get;
        init;
    } = [];
}

[RemoteEvent("webNumeric/changed", typeof(WebNumericJson))]
public delegate void NumericChanged(NumericDefinition value);

// Ordinary JIT authors may provide their actual metadata. This is not a saved-model or AOT provider.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
public sealed class WebNumericJson : JsonSerializerContext
{
    private static readonly JsonSerializerOptions metadataOptions = CreateOptions();

    public static WebNumericJson Default
    {
        get;
    } = new();

    private WebNumericJson() : base(null)
    {
    }

    protected override JsonSerializerOptions? GeneratedSerializerOptions => null;
    public override JsonTypeInfo? GetTypeInfo(Type type) => metadataOptions.GetTypeInfo(type);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true
        };
        options.MakeReadOnly();
        return options;
    }
}

[RemoteService("webNumeric", typeof(WebNumericJson))]
public partial class WebNumericRemote
{
    [RemoteMethod("echo")]
    public Task<NumericDefinition> EchoAsync(NumericDefinition value) => Task.FromResult(value);

    [RemoteMethod("stream")]
    public async IAsyncEnumerable<NumericDefinition> StreamAsync(
        NumericDefinition value,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return value;
    }
}
