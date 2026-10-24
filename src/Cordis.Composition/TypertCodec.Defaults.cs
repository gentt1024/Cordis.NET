using System.Text.Json;

namespace Cordis.Composition;

public abstract partial class TypertCodec
{
    private static JsonElement ApplyInputDefaults(JsonElement value, JsonElement schema, bool caseInsensitive)
    {
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            return value;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            Write(value, schema, writer, 0);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();

        void Write(JsonElement current, JsonElement contract, Utf8JsonWriter writer, int depth)
        {
            if (depth > 256)
                throw new JsonException("Typert input default recursion exceeded the supported depth.");
            if (contract.ValueKind != JsonValueKind.Object || current.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                current.WriteTo(writer);
                return;
            }
            if (contract.TryGetProperty("$ref", out var reference))
            {
                Write(current, ResolveReference(schema, reference.GetString()), writer, depth + 1);
                return;
            }
            if (contract.TryGetProperty("anyOf", out var alternatives))
                foreach (var alternative in alternatives.EnumerateArray())
                    if (alternative.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
                        MatchesType(current, type.GetString()))
                    {
                        Write(current, alternative, writer, depth + 1);
                        return;
                    }
            if (current.ValueKind == JsonValueKind.Array)
            {
                if (!contract.TryGetProperty("items", out var item))
                    current.WriteTo(writer);
                else
                {
                    writer.WriteStartArray();
                    foreach (var child in current.EnumerateArray())
                        Write(child, item, writer, depth + 1);
                    writer.WriteEndArray();
                }
                return;
            }
            var names = new HashSet<string>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            contract.TryGetProperty("properties", out var properties);
            contract.TryGetProperty("additionalProperties", out var additional);
            writer.WriteStartObject();
            foreach (var property in current.EnumerateObject())
            {
                names.Add(property.Name);
                writer.WritePropertyName(property.Name);
                var member = additional;
                if (properties.ValueKind == JsonValueKind.Object)
                    foreach (var declared in properties.EnumerateObject())
                        if (names.Comparer.Equals(declared.Name, property.Name))
                        {
                            member = declared.Value;
                            break;
                        }
                Write(property.Value, member, writer, depth + 1);
            }
            if (properties.ValueKind == JsonValueKind.Object)
                foreach (var property in properties.EnumerateObject())
                    if (!names.Contains(property.Name) && property.Value.ValueKind == JsonValueKind.Object &&
                        property.Value.TryGetProperty("default", out var initial) &&
                        !(contract.TryGetProperty("required", out var required) &&
                          required.EnumerateArray().Any(field => field.GetString() == property.Name)))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(initial, property.Value, writer, depth + 1);
                    }
            writer.WriteEndObject();
        }
    }
}
