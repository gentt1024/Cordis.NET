using System.Text.Json;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>Declare the Cordis service key, namespace and source-generated serialization context of a Remote service.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RemoteServiceAttribute(string service, Type? jsonContext = null) : Attribute
{
    /// <summary>Gets the exact Cordis service key.</summary>
    public string Service
    {
        get;
    } = service;

    /// <summary>Gets the source-generated JSON serialization context type.</summary>
    public Type? JsonContext
    {
        get;
    } = jsonContext;

    /// <summary>Gets or sets the wire namespace; the service key is the default.</summary>
    public string? Namespace
    {
        get;
        set;
    }
}

/// <summary>Declare one public Remote method. Cancellation is a final transport-only signal parameter.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RemoteMethodAttribute : Attribute
{
    /// <summary>Declare a method under its source name.</summary>
    public RemoteMethodAttribute(string? name = null) => Name = name;

    /// <summary>Gets or sets the exported method name.</summary>
    public string? Name
    {
        get;
        set;
    }

    /// <summary>Gets or sets whether the method returns a logical stream.</summary>
    public bool Stream
    {
        get;
        set;
    }

    /// <summary>Gets or sets the Context provider selecting the receiver.</summary>
    public string? Context
    {
        get;
        set;
    }

    /// <summary>Gets or sets the Context identity wire field.</summary>
    public string Wire
    {
        get;
        set;
    } = "contextId";
}

/// <summary>Declare the wire identity and provider of a non-JSON Host object parameter.</summary>
[AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
public sealed class RemoteLookupAttribute(string key, string wire, Type wireType) : Attribute
{
    /// <summary>Gets the lookup provider key.</summary>
    public string Key
    {
        get;
    } = key;

    /// <summary>Gets the wire identity field.</summary>
    public string Wire
    {
        get;
    } = wire;

    /// <summary>Gets the serialization type of the wire identity.</summary>
    public Type WireType
    {
        get;
    } = wireType;
}

/// <summary>Compiler-independent, strict boundary codec using explicitly supplied JSON type metadata.</summary>
public abstract class TypertCodec
{
    /// <summary>Gets the stable declaration identity used to match dependency providers.</summary>
    public abstract string TypeSymbol
    {
        get;
    }

    /// <summary>Gets the codec's JSON Schema projection.</summary>
    public abstract JsonElement Schema
    {
        get;
    }

    /// <summary>Validate and decode a JSON boundary value.</summary>
    public abstract object? Decode(JsonElement value);

    /// <summary>Encode a native boundary value.</summary>
    public abstract JsonElement Encode(object? value);

    /// <summary>Create an AOT-compatible codec from source-generated JSON metadata and an optional explicit schema.</summary>
    public static TypertCodec Create<T>(JsonTypeInfo<T> metadata, string? typeSymbol = null, JsonElement? schema = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new TypedCodec<T>(metadata, typeSymbol ?? metadata.Type.FullName ?? metadata.Type.Name, schema?.Clone());
    }

    /// <summary>Create a nullable reference boundary codec, preserving source nullability erased by JSON type metadata.</summary>
    public static TypertCodec CreateNullable<T>(
        JsonTypeInfo<T> metadata,
        string? typeSymbol = null,
        JsonElement? schema = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return new NullableCodec(
            Create(metadata, typeSymbol ?? (metadata.Type.FullName ?? metadata.Type.Name) + "?", schema));
    }

    private sealed class NullableCodec(TypertCodec inner) : TypertCodec
    {
        private JsonElement? schema;
        public override string TypeSymbol => inner.TypeSymbol;
        public override JsonElement Schema => schema ??= ExportSchema();

        public override object? Decode(JsonElement value)
        {
            _ = inner.Schema;
            return value.ValueKind == JsonValueKind.Null ? null : inner.Decode(value);
        }

        public override JsonElement Encode(object? value) => inner.Encode(value);

        private JsonElement ExportSchema()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("anyOf");
                WriteSchema(writer, inner.Schema);
                writer.WriteStartObject();
                writer.WriteString("type", "null");
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }

        private static void WriteSchema(Utf8JsonWriter writer, JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                value.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject())
            {
                writer.WritePropertyName(field.Name);
                switch (field.Name)
                {
                    case "$ref":
                        writer.WriteStringValue("#/anyOf/0" + field.Value.GetString()![1..]);
                        break;
                    case "properties":
                    case "$defs":
                    case "definitions":
                        writer.WriteStartObject();
                        foreach (var child in field.Value.EnumerateObject())
                        {
                            writer.WritePropertyName(child.Name);
                            WriteSchema(writer, child.Value);
                        }

                        writer.WriteEndObject();
                        break;
                    case "items":
                    case "additionalProperties":
                    case "not":
                        WriteSchema(writer, field.Value);
                        break;
                    case "allOf":
                    case "anyOf":
                    case "oneOf":
                    case "prefixItems":
                        writer.WriteStartArray();
                        foreach (var child in field.Value.EnumerateArray())
                            WriteSchema(writer, child);
                        writer.WriteEndArray();
                        break;
                    default:
                        field.Value.WriteTo(writer);
                        break;
                }
            }

            writer.WriteEndObject();
        }
    }

    private sealed class TypedCodec<T>(JsonTypeInfo<T> metadata, string symbol, JsonElement? explicitSchema)
        : TypertCodec
    {
        private JsonElement? schema;
        public override string TypeSymbol => symbol;
        public override JsonElement Schema => schema ??= PrepareSchema(explicitSchema?.Clone() ?? ExportSchema());

        public override object? Decode(JsonElement value)
        {
            Validate(value, Schema, Schema, 0);
            return JsonSerializer.Deserialize(value, metadata);
        }

        public override JsonElement Encode(object? value) => JsonSerializer.SerializeToElement((T)value!, metadata);

        private JsonElement ExportSchema()
        {
            var node = metadata.GetJsonSchemaAsNode(
                new JsonSchemaExporterOptions
                {
                    TreatNullObliviousAsNonNullable = true
                });
            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.Clone();
        }
    }

    private static JsonElement PrepareSchema(JsonElement schema)
    {
        ValidateSchema(schema, schema);
        return schema;
    }

    private static void ValidateSchema(JsonElement schema, JsonElement root, HashSet<string>? references = null)
    {
        references ??= new(StringComparer.Ordinal);
        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return;
        if (schema.ValueKind != JsonValueKind.Object)
            throw new NotSupportedException("A Typert boundary schema must be an object or boolean.");
        foreach (var field in schema.EnumerateObject())
        {
            switch (field.Name)
            {
                case "$ref":
                    var reference = field.Value.GetString();
                    var target = ResolveReference(root, reference);
                    if (references.Add(reference!))
                        ValidateSchema(target, root, references);
                    break;
                case "properties":
                case "$defs":
                case "definitions":
                    foreach (var child in field.Value.EnumerateObject())
                        ValidateSchema(child.Value, root, references);
                    break;
                case "items":
                case "additionalProperties":
                case "not":
                    ValidateSchema(field.Value, root, references);
                    break;
                case "allOf":
                case "anyOf":
                case "oneOf":
                case "prefixItems":
                    foreach (var child in field.Value.EnumerateArray())
                        ValidateSchema(child, root, references);
                    break;
                case "$schema":
                case "$comment":
                case "title":
                case "description":
                case "default":
                case "deprecated":
                case "readOnly":
                case "writeOnly":
                case "format":
                case "contentEncoding":
                case "contentMediaType":
                case "type":
                case "enum":
                case "const":
                case "required":
                case "minItems":
                case "maxItems":
                case "minimum":
                case "maximum":
                case "exclusiveMinimum":
                case "exclusiveMaximum":
                    break;
                default:
                    throw new NotSupportedException($"Typert boundary schema keyword '{field.Name}' is not supported.");
            }
        }
    }

    private static JsonElement ResolveReference(JsonElement root, string? reference)
    {
        if (reference == "#")
            return root;
        if (reference is null || !reference.StartsWith("#/", StringComparison.Ordinal))
            throw new NotSupportedException("Typert boundary schema references must be local JSON pointers.");
        var current = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            var key = Uri
                .UnescapeDataString(segment)
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(
                    key,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var index) && index < current.GetArrayLength())
                current = current[index];
            else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                throw new NotSupportedException($"Typert boundary schema reference '{reference}' is unresolved.");
        }

        return current;
    }

    private static void Validate(JsonElement value, JsonElement schema, JsonElement root, int depth)
    {
        if (depth > 256)
            throw new JsonException("Typert boundary schema recursion exceeded the supported depth.");
        if (schema.ValueKind == JsonValueKind.False)
            throw new JsonException("The boundary schema refuses this value.");
        if (schema.ValueKind != JsonValueKind.Object)
            return;
        if (schema.TryGetProperty("$ref", out var reference))
            Validate(value, ResolveReference(root, reference.GetString()), root, depth + 1);
        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(item => JsonElement.DeepEquals(item, value)))
            throw new JsonException("The boundary value does not match a declared enum member.");
        if (schema.TryGetProperty("const", out var constant) && !JsonElement.DeepEquals(constant, value))
            throw new JsonException("The boundary value does not match the declared constant.");
        if (schema.TryGetProperty("type", out var type))
        {
            var matches = type.ValueKind == JsonValueKind.Array
                ? type.EnumerateArray().Any(item => MatchesType(value, item.GetString()))
                : MatchesType(value, type.GetString());
            if (!matches)
                throw new JsonException("The boundary value has the wrong JSON type.");
        }

        if (schema.TryGetProperty("anyOf", out var variants) &&
            !variants.EnumerateArray().Any(item => Accepts(value, item, root, depth + 1)))
            throw new JsonException("The boundary value matches none of the declared variants.");
        if (schema.TryGetProperty("oneOf", out var alternatives) &&
            alternatives.EnumerateArray().Count(item => Accepts(value, item, root, depth + 1)) != 1)
            throw new JsonException("The boundary value must match exactly one declared variant.");
        if (schema.TryGetProperty("not", out var excluded) && Accepts(value, excluded, root, depth + 1))
            throw new JsonException("The boundary value matches an excluded schema.");
        if (schema.TryGetProperty("allOf", out var requirements))
            foreach (var requirement in requirements.EnumerateArray())
                Validate(value, requirement, root, depth + 1);
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var field in required.EnumerateArray())
                    if (!value.TryGetProperty(field.GetString()!, out _))
                        throw new JsonException($"Required boundary field '{field.GetString()}' is absent.");
            var hasProperties = schema.TryGetProperty("properties", out var properties);
            var hasAdditional = schema.TryGetProperty("additionalProperties", out var additional);
            foreach (var field in value.EnumerateObject())
            {
                if (hasProperties && properties.TryGetProperty(field.Name, out var fieldSchema))
                    Validate(field.Value, fieldSchema, root, depth + 1);
                else if (hasAdditional)
                    Validate(field.Value, additional, root, depth + 1);
            }
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var length = value.GetArrayLength();
            if ((schema.TryGetProperty("minItems", out var minimum) && length < minimum.GetInt32()) ||
                (schema.TryGetProperty("maxItems", out var maximum) && length > maximum.GetInt32()))
                throw new JsonException("The boundary array has an invalid length.");
            var prefixLength = 0;
            if (schema.TryGetProperty("prefixItems", out var prefix))
            {
                foreach (var item in prefix.EnumerateArray())
                {
                    if (prefixLength >= length)
                        break;
                    Validate(value[prefixLength++], item, root, depth + 1);
                }
            }

            if (schema.TryGetProperty("items", out var items))
                for (var index = prefixLength;index < length;index++)
                    Validate(value[index], items, root, depth + 1);
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (!double.IsFinite(number) ||
                (schema.TryGetProperty("minimum", out var minimum) && number < minimum.GetDouble()) ||
                (schema.TryGetProperty("maximum", out var maximum) && number > maximum.GetDouble()) ||
                (schema.TryGetProperty("exclusiveMinimum", out var exclusiveMinimum) &&
                    number <= exclusiveMinimum.GetDouble()) ||
                (schema.TryGetProperty("exclusiveMaximum", out var exclusiveMaximum) &&
                    number >= exclusiveMaximum.GetDouble()))
                throw new JsonException("The boundary number is outside its declared range.");
        }
    }

    private static bool Accepts(JsonElement value, JsonElement schema, JsonElement root, int depth)
    {
        try
        {
            Validate(value, schema, root, depth);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "null" => value.ValueKind == JsonValueKind.Null,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
            decimal.Truncate(number) == number,
        "string" => value.ValueKind == JsonValueKind.String,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        _ => throw new JsonException($"Unsupported boundary schema type '{type}'."),
    };
}

/// <summary>One ordered Remote parameter, represented by JSON or a lookup identity.</summary>
public sealed record TypertInvocationParameter(string Name, string Wire, TypertCodec Codec)
{
    /// <summary>Gets the provider key for an object lookup; null denotes an ordinary JSON parameter.</summary>
    public string? Lookup
    {
        get;
        init;
    }

    /// <summary>Gets whether an omitted field represents source-level absence.</summary>
    public bool AcceptsUndefined
    {
        get;
        init;
    }
}

/// <summary>Wire-to-Context receiver selection.</summary>
public sealed record TypertContextInvocation(string Context, string Wire, TypertCodec Codec);

/// <summary>Consuming Context projection of the only lookup argument of a direct invocation.</summary>
public sealed record TypertScopeProjection(string Context, string Wire);

/// <summary>Carrier-independent generated identity, receiver and codec contract of one Remote method.</summary>
public sealed record TypertInvocationDescriptor(
    string Id,
    string Service,
    string Namespace,
    string Method,
    IReadOnlyList<TypertInvocationParameter> Parameters,
    TypertCodec Result)
{
    /// <summary>Gets the endpoint namespace and method.</summary>
    public string Endpoint => Namespace + "/" + Method;

    /// <summary>Gets scoped receiver selection; null denotes the direct receiver.</summary>
    public TypertContextInvocation? Invocation
    {
        get;
        init;
    }

    /// <summary>Gets the optional Client consuming-Context projection.</summary>
    public TypertScopeProjection? Scope
    {
        get;
        init;
    }

    /// <summary>Gets whether the method uses a logical stream.</summary>
    public bool IsStream
    {
        get;
        init;
    }

    /// <summary>Gets whether the final native method argument is cancellation.</summary>
    public bool Cancellation
    {
        get;
        init;
    }

    /// <summary>Gets the optional codec for Client-to-Host stream items.</summary>
    public TypertCodec? Uplink
    {
        get;
        init;
    }

    /// <summary>Gets the implementation member name when the method has an exported alias.</summary>
    public string? Implementation
    {
        get;
        init;
    }
}

/// <summary>Shared Remote failure with a stable code and structured details.</summary>
public class RemoteError(string code, string message, JsonElement? details = null, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Gets the stable error code.</summary>
    public string Code
    {
        get;
    } = code;

    /// <summary>Gets the owner-defined error details.</summary>
    public JsonElement? Details
    {
        get;
    } = details?.Clone();
}

/// <summary>One success-cached runtime schema factory.</summary>
public sealed record TypertSchemaFactory(string Name, Func<JsonElement> Create);

/// <summary>Generated public member signature.</summary>
public sealed record TypertMemberModel(string Kind, string Name, string Signature);

/// <summary>Generated named type declaration.</summary>
public sealed record TypertTypeModel(string Name, string Declaration);

/// <summary>Reflected Cordis service and its reachable public types.</summary>
public sealed record TypertServiceModel(
    string Key,
    string ExportName,
    IReadOnlyList<TypertMemberModel> Members,
    IReadOnlyList<TypertTypeModel> Types);

/// <summary>Reflected Cordis event signature.</summary>
public sealed record TypertEventModel(string Name, string Signature, string? Mode = null);

/// <summary>Reflected reference-object signature and its reachable public types.</summary>
public sealed record TypertObjectModel(
    string Name,
    string ExportName,
    IReadOnlyList<TypertMemberModel> Members,
    IReadOnlyList<TypertTypeModel> Types);

/// <summary>Compiler-independent public package model, separate from schema factories.</summary>
public sealed record TypertPackageModel(
    IReadOnlyList<TypertServiceModel> Services,
    IReadOnlyList<TypertEventModel> Events,
    IReadOnlyList<TypertObjectModel> Objects)
{
    /// <summary>Gets the empty public package model.</summary>
    public static TypertPackageModel Empty
    {
        get;
    } = new([], [], []);
}

/// <summary>One atomic generated package-face contribution.</summary>
public sealed record TypertContribution(
    string Package,
    string Face,
    IReadOnlyList<TypertSchemaFactory> Schemas,
    TypertPackageModel Model,
    IReadOnlyList<TypertInvocationDescriptor> Invocations)
{
    /// <summary>Create a Host contribution that exports only Remote contracts.</summary>
    public TypertContribution(string package, IReadOnlyList<TypertInvocationDescriptor> invocations) : this(
        package,
        "host",
        [],
        TypertPackageModel.Empty,
        invocations)
    {
    }
}

/// <summary>Consumer-selected Host Remote definitions.</summary>
public sealed record TypertRemoteContribution(string Package, IReadOnlyList<TypertInvocationDescriptor> Descriptors);

/// <summary>Live object lookup provider and its stable declaration.</summary>
public sealed record TypertLookupProvider(
    string Parameter,
    string Wire,
    string HostTypeSymbol,
    string WireTypeSymbol,
    Func<object?, ValueTask<object?>> Resolve);

/// <summary>Lookup declaration retained when its provider unloads.</summary>
public sealed record TypertLookupDefinition(
    string Key,
    string Parameter,
    string Wire,
    string HostTypeSymbol,
    string WireTypeSymbol);

/// <summary>Host wire-to-Context provider.</summary>
public sealed record TypertHostContextAdapter(
    string Wire,
    string WireTypeSymbol,
    Func<object?, ValueTask<Context?>> Resolve);

/// <summary>Client Context-to-wire and wire-to-Context provider.</summary>
public sealed record TypertClientContextAdapter(Func<Context, object?> Identity, Func<object?, Context?> Resolve);

/// <summary>Committed registry notification.</summary>
public sealed record TypertRegistryChange(string Kind, string Key);
