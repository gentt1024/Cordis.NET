using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>Declared null admission at an exported JSON Schema path; null keeps the serializer's own rule.</summary>
public delegate bool? TypertNullability(ReadOnlySpan<string> schemaPath);

public abstract partial class TypertCodec
{
    /// <summary>Create a metadata codec retaining declaration nullability erased from CLR generic arguments.</summary>
    /// <remarks>The generated query reads common declaration facts, not reflection. Schema is exported lazily;
    /// metadata still owns names, converters and serialization. Unmapped schema paths keep the exporter rule.</remarks>
    public static TypertCodec CreateDeclared<T>(
        JsonTypeInfo<T> metadata,
        TypertNullability nullability,
        string? typeSymbol = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(nullability);
        return new TypedCodec<T>(
            metadata,
            typeSymbol ?? metadata.Type.FullName ?? metadata.Type.Name,
            null,
            nullability);
    }

    private static JsonNode AnnotateNullability(JsonNode node, bool? nullable)
    {
        if (nullable is null || node is not JsonObject schema)
            return node;
        if (schema.ContainsKey("$ref"))
            return nullable.Value
                ? new JsonObject
                {
                    ["anyOf"] = new JsonArray(
                        node,
                        new JsonObject
                        {
                            ["type"] = "null"
                        })
                }
                : new JsonObject
                {
                    ["allOf"] = new JsonArray(
                        node,
                        new JsonObject
                        {
                            ["not"] = new JsonObject
                            {
                                ["type"] = "null"
                            }
                        })
                };
        if (schema["type"] is JsonArray types)
        {
            for (var i = types.Count - 1;i >= 0;i--)
                if (types[i]?.GetValue<string>() == "null")
                    types.RemoveAt(i);
            if (nullable.Value)
                types.Add((JsonNode?)JsonValue.Create("null"));
        }
        else if (schema["type"] is JsonValue type && nullable.Value && type.GetValue<string>() != "null")
            schema["type"] = new JsonArray(type.DeepClone(), JsonValue.Create("null"));

        if (schema["enum"] is JsonArray values)
        {
            for (var i = values.Count - 1;i >= 0;i--)
                if (values[i] is null)
                    values.RemoveAt(i);
            if (nullable.Value)
                values.Add((JsonNode?)null);
        }

        return node;
    }
}
