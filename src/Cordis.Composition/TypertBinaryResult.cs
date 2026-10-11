using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>A unary binary attachment and its result-relative JSON property/index path.</summary>
public sealed record TypertBinaryAttachment(IReadOnlyList<JsonElement> Path, ReadOnlyMemory<byte> Bytes);

internal static class TypertBinaryResult
{
    internal static TypertRemoteResult Encode(JsonElement value, JsonTypeInfo metadata)
    {
        var attachments = new List<TypertBinaryAttachment>();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value, metadata, [], attachments);
        using var document = JsonDocument.Parse(buffer.ToArray());
        return TypertRemoteResult.Success(document.RootElement.Clone()) with
        {
            Attachments = attachments
        };
    }

    private static JsonElement Segment(JsonNode value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement value, JsonTypeInfo metadata,
        List<JsonElement> path, List<TypertBinaryAttachment> attachments)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            value.WriteTo(writer);
            return;
        }
        if (metadata.Type == typeof(byte[]))
        {
            attachments.Add(new(path.ToArray(), value.GetBytesFromBase64()));
            writer.WriteNullValue();
            return;
        }
        if (metadata.Kind == JsonTypeInfoKind.Object && value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject())
            {
                writer.WritePropertyName(field.Name);
                var property = metadata.Properties.FirstOrDefault(property => property.Name == field.Name);
                if (property is null || property.CustomConverter is not null)
                    field.Value.WriteTo(writer);
                else
                {
                    path.Add(Segment(JsonValue.Create(field.Name)!));
                    Write(writer, field.Value, metadata.Options.GetTypeInfo(property.PropertyType), path, attachments);
                    path.RemoveAt(path.Count - 1);
                }
            }
            writer.WriteEndObject();
            return;
        }
        if (metadata.ElementType is { } elementType)
        {
            var element = metadata.Options.GetTypeInfo(elementType);
            if (metadata.Kind == JsonTypeInfoKind.Enumerable && value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                var index = 0;
                foreach (var child in value.EnumerateArray())
                {
                    path.Add(Segment(JsonValue.Create(index++)!));
                    Write(writer, child, element, path, attachments);
                    path.RemoveAt(path.Count - 1);
                }
                writer.WriteEndArray();
                return;
            }
            if (metadata.Kind == JsonTypeInfoKind.Dictionary && value.ValueKind == JsonValueKind.Object)
            {
                writer.WriteStartObject();
                foreach (var field in value.EnumerateObject())
                {
                    writer.WritePropertyName(field.Name);
                    path.Add(Segment(JsonValue.Create(field.Name)!));
                    Write(writer, field.Value, element, path, attachments);
                    path.RemoveAt(path.Count - 1);
                }
                writer.WriteEndObject();
                return;
            }
        }
        value.WriteTo(writer);
    }
}
