using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Typert;

namespace Cordis.Composition;

/// <summary>A generated client's named dispatch contract. Returned tasks and stream handles retain their identity.</summary>
public interface ITypertNamedRemote
{
    /// <summary>Invoke an endpoint with already decoded named arguments and a carrier cancellation signal.</summary>
    object Invoke(string endpoint, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken);
}

/// <summary>Lazy metadata over one package's ordered descriptor snapshot.</summary>
public sealed class TypertRemoteInfo
{
    private readonly IReadOnlyList<TypertInvocationDescriptor> source;
    private Index? index;
    private readonly object gate = new();

    /// <summary>Retain a descriptor snapshot without preparing codecs or tool schemas.</summary>
    public TypertRemoteInfo(string package, IReadOnlyList<TypertInvocationDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(descriptors);
        Package = package;
        source = descriptors;
    }

    /// <summary>Gets the declaring package.</summary>
    public string Package
    {
        get;
    }

    /// <summary>Gets the ordered descriptors, including Context receivers.</summary>
    public IReadOnlyList<TypertInvocationDescriptor> Descriptors => Resolve().Descriptors;

    /// <summary>Gets direct methods indexed by endpoint, sharing the descriptor objects.</summary>
    public IReadOnlyDictionary<string, TypertInvocationDescriptor> Methods => Resolve().Methods;

    private Index Resolve()
    {
        lock (gate)
        {
            if (index is not null)
                return index;
            var descriptors = Array.AsReadOnly(source.ToArray());
            var methods = new ReadOnlyDictionary<string, TypertInvocationDescriptor>(
                descriptors
                    .Where(value => value.Invocation is null)
                    .ToDictionary(value => value.Endpoint, StringComparer.Ordinal));
            return index = new(descriptors, methods);
        }
    }

    private sealed record Index(
        IReadOnlyList<TypertInvocationDescriptor> Descriptors,
        IReadOnlyDictionary<string, TypertInvocationDescriptor> Methods);
}

public sealed partial record TypertInvocationParameter
{
    private static readonly ConditionalWeakTable<TypertInvocationParameter, InputMetadata> InputCache = new();

    /// <summary>Get the independent normal or strict wire-value parser and optional tool projection.</summary>
    public TypertRemoteValue Input(bool strict = false)
    {
        var metadata = InputCache.GetValue(this, static _ => new());
        lock (metadata)
            return strict
                ? metadata.Strict ??= new(
                    Codec,
                    Description,
                    input: true,
                    strict: true,
                    optionalInput: Lookup is null && AcceptsUndefined)
                : metadata.Normal ??= new(
                    Codec,
                    Description,
                    input: true,
                    optionalInput: Lookup is null && AcceptsUndefined);
    }

    private sealed class InputMetadata
    {
        internal TypertRemoteValue? Normal;
        internal TypertRemoteValue? Strict;
    }
}

public sealed partial record TypertInvocationDescriptor
{
    private static readonly ConditionalWeakTable<TypertInvocationDescriptor, Metadata> MetadataCache = new();

    /// <summary>Gets the authored method documentation.</summary>
    public string? Description
    {
        get;
        init;
    }

    /// <summary>Gets the authored result documentation.</summary>
    public string? ResultDescription
    {
        get;
        init;
    }

    /// <summary>Get independently cached normal or strict named-input metadata for a direct method.</summary>
    public TypertRemoteInput Input(bool strict = false)
    {
        if (Invocation is not null)
            throw new InvalidOperationException("Context receivers have no direct named input.");
        var metadata = MetadataCache.GetValue(this, static _ => new());
        lock (metadata)
            return strict ? metadata.Strict ??= new(Parameters, true) : metadata.Normal ??= new(Parameters, false);
    }

    /// <summary>Gets lazy metadata for the result's write-direction contract.</summary>
    public TypertRemoteValue ResultValue
    {
        get
        {
            var metadata = MetadataCache.GetValue(this, static _ => new());
            lock (metadata)
                return metadata.Result ??= new(Result, ResultDescription);
        }
    }

    /// <summary>Gets the uplink item's input parser and optional tool projection, when declared.</summary>
    public TypertRemoteValue? UplinkValue
    {
        get
        {
            if (Uplink is null)
                return null;
            var metadata = MetadataCache.GetValue(this, static _ => new());
            lock (metadata)
                return metadata.Uplink ??= new(Uplink, null, input: true);
        }
    }

    /// <summary>Invoke a direct method without parsing, awaiting, or replacing the returned handle.</summary>
    public object Invoke(
        ITypertNamedRemote remote,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (Invocation is not null)
            throw new InvalidOperationException("Context receivers cannot be invoked directly.");
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(arguments);
        return remote.Invoke(Endpoint, arguments, cancellationToken);
    }

    private sealed class Metadata
    {
        internal TypertRemoteInput? Normal;
        internal TypertRemoteInput? Strict;
        internal TypertRemoteValue? Result;
        internal TypertRemoteValue? Uplink;
    }
}

/// <summary>An optional lossless tool-schema projection and an independent native value parser.</summary>
public sealed class TypertRemoteValue
{
    private readonly TypertCodec codec;
    private readonly bool input;
    private readonly bool strict;
    private readonly bool optionalInput;
    private readonly object gate = new();
    private ToolProjection? projection;
    private Func<JsonElement, object?>? parser;

    internal TypertRemoteValue(
        TypertCodec codec,
        string? description,
        bool input = false,
        bool strict = false,
        bool optionalInput = false)
    {
        this.codec = codec;
        this.input = input;
        this.strict = strict;
        this.optionalInput = optionalInput;
        Description = description;
    }

    /// <summary>Gets authored value documentation even when projection is unavailable.</summary>
    public string? Description
    {
        get;
    }

    /// <summary>Parse using this value's selected input or output contract.</summary>
    public object? Parse(JsonElement value) => Create()(value);

    /// <summary>Get the cached native value parser independently of tool projection.</summary>
    public Func<JsonElement, object?> Create()
    {
        lock (gate)
            return parser ??= ParseValue;
    }

    private object? ParseValue(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Undefined)
            ValidateJson(value, 0);
        if (!input)
            return codec.DecodeOutput(value);
        if (strict)
            TypertCodec.ValidateStrictInput(value, codec.Schema);
        return codec.Decode(value);
    }

    private static void ValidateJson(JsonElement value, int depth)
    {
        if (depth > 256)
            throw new JsonException("Remote metadata JSON exceeded the supported depth.");
        if (value.ValueKind == JsonValueKind.Number &&
            (!value.TryGetDouble(out var number) || !double.IsFinite(number)))
            throw new JsonException("A Remote metadata JSON number must be finite.");
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var field in value.EnumerateObject())
                ValidateJson(field.Value, depth + 1);
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                ValidateJson(item, depth + 1);
    }

    /// <summary>Gets an optional tool schema. Reading this does not create a parser.</summary>
    public JsonElement? JsonSchema => Projection.Schema;

    /// <summary>Gets the known reason the tool subset cannot express this contract.</summary>
    public string? JsonSchemaError => Projection.Error;

    private ToolProjection Projection
    {
        get
        {
            lock (gate)
                return projection ??= ToolProjection.Create(() =>
                {
                    if (codec.AcceptsUndefined && (!input || !optionalInput))
                        throw new ToolSchemaException(
                            input
                                ? "A possibly absent input value has no tool schema."
                                : "A possibly absent result has no tool schema.");
                    if (!input)
                        ToolSchemaProjection.RequireJsonResult(codec.ResultMetadata);
                    var schema = ToolSchemaProjection.Project(input ? codec.Schema : codec.ResultSchema, strict);
                    return ToolProjection.Describe(schema, Description);
                });
        }
    }
}

/// <summary>A consumer-selected normal or strict parser over closed named arguments.</summary>
public sealed class TypertRemoteInput
{
    private readonly IReadOnlyList<TypertInvocationParameter> parameters;
    private readonly bool strict;
    private readonly object gate = new();
    private ToolProjection? projection;
    private Func<JsonElement, IReadOnlyDictionary<string, object?>>? parser;

    internal TypertRemoteInput(IReadOnlyList<TypertInvocationParameter> parameters, bool strict)
    {
        this.parameters = parameters;
        this.strict = strict;
    }

    /// <summary>Parse a closed named object, preserving decoded CLR values and omitted optional arguments.</summary>
    public IReadOnlyDictionary<string, object?> Parse(JsonElement value) => Create()(value);

    /// <summary>Get the cached named parser independently of tool projection.</summary>
    public Func<JsonElement, IReadOnlyDictionary<string, object?>> Create()
    {
        lock (gate)
            return parser ??= ParseCore;
    }

    private IReadOnlyDictionary<string, object?> ParseCore(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new JsonException("Remote named input must be an object.");
        foreach (var field in value.EnumerateObject())
            if (!parameters.Any(parameter => parameter.Wire == field.Name))
                throw new JsonException($"Unknown Remote argument '{field.Name}'.");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (!value.TryGetProperty(parameter.Wire, out var raw))
            {
                if (parameter.Lookup is null && parameter.AcceptsUndefined)
                    continue;
                throw new JsonException($"Missing Remote argument '{parameter.Wire}'.");
            }

            result.Add(parameter.Wire, parameter.Input(strict).Parse(raw));
        }

        return new ReadOnlyDictionary<string, object?>(result);
    }

    /// <summary>Gets the optional tool schema independently of parsing.</summary>
    public JsonElement? JsonSchema => Projection.Schema;

    /// <summary>Gets a known projection refusal without disabling the parser.</summary>
    public string? JsonSchemaError => Projection.Error;

    private ToolProjection Projection
    {
        get
        {
            lock (gate)
                return projection ??= Project();
        }
    }

    private ToolProjection Project()
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters)
        {
            var value = parameter.Input(strict);
            if (value.JsonSchema is not { } schema)
                return new(null, value.JsonSchemaError);
            properties.Add(parameter.Wire, JsonNode.Parse(schema.GetRawText()));
            if (parameter.Lookup is not null || !parameter.AcceptsUndefined)
                required.Add((JsonNode?)JsonValue.Create(parameter.Wire));
        }

        return new(
            ToolProjection.Element(
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = required,
                    ["additionalProperties"] = false,
                }),
            null);
    }
}

internal sealed record ToolProjection(JsonElement? Schema, string? Error)
{
    internal static ToolProjection Create(Func<JsonElement> create)
    {
        try
        {
            return new(create(), null);
        }
        catch (ToolSchemaException error)
        {
            return new(null, error.Message);
        }
    }

    internal static JsonElement Element(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static JsonElement Describe(JsonElement schema, string? description)
    {
        if (description is null)
            return schema;
        var node = JsonNode.Parse(schema.GetRawText())!.AsObject();
        node["description"] = description;
        return Element(node);
    }
}
