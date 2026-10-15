using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

public abstract partial class TypertCodec
{
    private static JsonNode ProjectOutputObject(JsonSchemaExporterContext context, JsonNode node)
    {
        if (node is not JsonObject schema || schema["properties"] is not JsonObject properties ||
            context.TypeInfo.Kind != JsonTypeInfoKind.Object)
            return node;
        var required = new JsonArray();
        // An arbitrary ShouldSerialize predicate can omit a value; do not execute author code to guess it.
        foreach (var property in context.TypeInfo.Properties)
        {
            if (!properties.ContainsKey(property.Name) ||
                !TypertJsonMetadata.RequiredOnWrite(property, property.Options.DefaultIgnoreCondition))
                continue;
            required.Add((JsonNode?)JsonValue.Create(property.Name));
        }

        schema.Remove("required");
        if (required.Count != 0)
            schema["required"] = required;
        return schema;
    }

    private static TypertNullabilityInfo? OutputNullability(
        JsonTypeInfo metadata,
        TypertNullability? query,
        ReadOnlySpan<string> path)
    {
        var declared = query?.Invoke(path);
        JsonPropertyInfo? property = null;
        for (var index = 0;index < path.Length;index++)
        {
            if (path[index] == "properties" && ++index < path.Length && metadata.Kind == JsonTypeInfoKind.Object)
            {
                var name = path[index];
                property = metadata.Properties.FirstOrDefault(value => value.Name == name);
                if (property is null)
                    return declared;
                if (index == path.Length - 1)
                    return new(declared?.Id ?? 0, property.IsGetNullable);
                if (property.CustomConverter is not null)
                    return declared;
                metadata = metadata.Options.GetTypeInfo(property.PropertyType);
            }
            else if (path[index] is "items" or "additionalProperties" && metadata.ElementType is { } element)
            {
                metadata = metadata.Options.GetTypeInfo(element);
            }
            else
                return declared;
        }

        return declared;
    }
}
