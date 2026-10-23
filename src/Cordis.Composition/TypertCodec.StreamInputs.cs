using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

public abstract partial class TypertCodec
{
    /// <summary>Require a lossless JSON uplink item, permitting absence only at the item root.</summary>
    public static void ValidateUplinkItem(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
            return;
        Visit(value);

        static void Visit(JsonElement item)
        {
            if (item.ValueKind == JsonValueKind.Number)
            {
                if (!item.TryGetDouble(out var number) || !double.IsFinite(number) ||
                    number == 0 && double.IsNegative(number))
                    throw new JsonException("Remote uplink numbers must be finite and cannot be negative zero.");
            }
            else if (item.ValueKind == JsonValueKind.Array)
                foreach (var child in item.EnumerateArray())
                    Visit(child);
            else if (item.ValueKind == JsonValueKind.Object)
                foreach (var field in item.EnumerateObject())
                    Visit(field.Value);
        }
    }

    /// <summary>Whether this codec admits an absent wire value, independently of JSON null.</summary>
    public virtual bool AcceptsUndefined => false;

    /// <summary>Use an explicitly declared item codec with the supplied collection serialization metadata.</summary>
    public static TypertCodec CreateSequence<T>(
        JsonTypeInfo<T> metadata,
        TypertCodec item,
        string? typeSymbol = null,
        bool nullable = false) =>
        Collection(metadata, item, JsonTypeInfoKind.Enumerable, typeSymbol, nullable);

    /// <summary>Use an explicitly declared value codec with the supplied JSON object serialization metadata.</summary>
    public static TypertCodec CreateDictionary<T>(
        JsonTypeInfo<T> metadata,
        TypertCodec value,
        string? typeSymbol = null,
        bool nullable = false) =>
        Collection(metadata, value, JsonTypeInfoKind.Dictionary, typeSymbol, nullable);

    /// <summary>Allow absence while retaining the inner value's independent null and validation rules.</summary>
    public static TypertCodec CreateOptional<T>(TypertCodec value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new OptionalCodec<T>(value);
    }

    private static TypertCodec Collection<T>(
        JsonTypeInfo<T> metadata,
        TypertCodec item,
        JsonTypeInfoKind kind,
        string? symbol,
        bool nullable)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(item);
        if (metadata.Kind != kind)
            throw new NotSupportedException("The declared collection requires ordinary collection JSON metadata.");
        TypertCodec codec = new CollectionCodec<T>(
            metadata,
            item,
            kind,
            symbol ?? metadata.Type.FullName ?? metadata.Type.Name);
        return nullable ? new NullableCodec(codec) : codec;
    }

    private sealed class OptionalCodec<T>(TypertCodec inner) : TypertCodec
    {
        public override bool AcceptsUndefined => true;
        public override string TypeSymbol => inner.TypeSymbol + " | undefined";
        public override JsonElement Schema => inner.Schema;
        internal override JsonElement ResultSchemaCore => inner.ResultSchema;
        internal override TypertNullability? DeclarationQuery => inner.DeclarationQuery;

        public override object Decode(JsonElement value)
        {
            _ = inner.Schema;
            return value.ValueKind == JsonValueKind.Undefined
                ? RemoteOptional<T>.Undefined
                : new RemoteOptional<T>((T)inner.Decode(value)!);
        }

        public override JsonElement Encode(object? value)
        {
            var item = (RemoteOptional<T>)value!;
            return item.HasValue ? inner.Encode(item.Value) : default;
        }

        public override JsonElement EncodeInput(object? value)
        {
            var item = (RemoteOptional<T>)value!;
            return item.HasValue ? inner.EncodeInput(item.Value) : default;
        }

        internal override ResourceFieldRead ReadResourceField(object? decoded, string field)
        {
            if (decoded is JsonElement)
                return base.ReadResourceField(decoded, field);
            if (ReferenceEquals(decoded, Undefined.Value))
                return new(ResourceFieldStatus.NotObject);
            var item = (RemoteOptional<T>)decoded!;
            return item.HasValue
                ? inner.ReadResourceField(item.Value, field)
                : new(ResourceFieldStatus.NotObject);
        }

        internal override object? ResourceIdentity(object? decoded)
        {
            if (decoded is JsonElement || ReferenceEquals(decoded, Undefined.Value))
                return decoded;
            var item = (RemoteOptional<T>)decoded!;
            return item.HasValue ? inner.ResourceIdentity(item.Value) : Undefined.Value;
        }

        public override object DecodeOutput(JsonElement value)
        {
            _ = inner.ResultSchema;
            return value.ValueKind == JsonValueKind.Undefined
                ? RemoteOptional<T>.Undefined
                : new RemoteOptional<T>((T)inner.DecodeOutput(value)!);
        }
    }

    private sealed class CollectionCodec<T>(
        JsonTypeInfo<T> metadata,
        TypertCodec item,
        JsonTypeInfoKind kind,
        string symbol) : TypertCodec
    {
        private TypertCodec? inner;

        private TypertCodec Inner => inner ??= new TypedCodec<T>(
            DeclarationQuery is { } query ? ApplyAccessorNullability(metadata, query) : metadata,
            symbol,
            ExportSchema(),
            DeclarationQuery);

        public override string TypeSymbol => symbol;
        public override JsonElement Schema => Inner.Schema;
        public override object? Decode(JsonElement value) => Inner.Decode(value);
        public override JsonElement Encode(object? value) => Inner.Encode(value);
        public override JsonElement EncodeInput(object? value) => Inner.EncodeInput(value);
        internal override object? ResourceIdentity(object? decoded) => Inner.ResourceIdentity(decoded);

        internal override ResourceFieldRead ReadResourceField(object? decoded, string field) =>
            Inner.ReadResourceField(decoded, field);

        public override object? DecodeOutput(JsonElement value)
        {
            Validate(value, ResultSchema, ResultSchema, 0);
            return JsonSerializer.Deserialize(
                value,
                TypertJsonMetadata.ReadOutput((JsonTypeInfo<T>)Inner.ResultMetadata!));
        }

        public override TypertRemoteResult EncodeResult(object? value) => Inner.EncodeResult(value);
        internal override JsonTypeInfo? ResultMetadata => Inner.ResultMetadata;
        private JsonElement? resultSchema;
        internal override JsonElement ResultSchemaCore => resultSchema ??= PrepareSchema(ExportSchema(output: true));
        internal override TypertNullability? DeclarationQuery => item.DeclarationQuery is null ? null : Query;

        private TypertNullabilityInfo? Query(ReadOnlySpan<string> path)
        {
            if (path.IsEmpty)
                return new(0, false);
            var field = kind == JsonTypeInfoKind.Dictionary ? "additionalProperties" : "items";
            return path[0] == field && item.DeclarationQuery?.Invoke(path[1..]) is { } value
                ? value with
                {
                    Id = value.Id + 1
                }
                : null;
        }

        private JsonElement ExportSchema(bool output = false)
        {
            var field = kind == JsonTypeInfoKind.Dictionary ? "additionalProperties" : "items";
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("type", kind == JsonTypeInfoKind.Dictionary ? "object" : "array");
                writer.WritePropertyName(field);
                WriteNestedSchema(writer, output ? item.ResultSchema : item.Schema, "#/" + field);
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            return document.RootElement.Clone();
        }
    }
}
