using System.Text.Json;

namespace Cordis.Composition;

public abstract partial class TypertCodec
{
    internal static void ValidateStrictInput(JsonElement value, JsonElement schema) =>
        ValidateStrictInput(value, [schema], schema, 0);

    private static void ValidateStrictInput(
        JsonElement value,
        IReadOnlyList<JsonElement> schemas,
        JsonElement root,
        int depth)
    {
        if (depth > 256)
            throw new JsonException("Remote strict input recursion exceeded the supported depth.");
        foreach (var alternative in ExpandConstraints(value, schemas, root, depth))
        {
            try
            {
                ValidateStrictShape(value, alternative, root, depth);
                return;
            }
            catch (JsonException)
            {
            }
        }

        throw new JsonException("No strict Remote input shape accepts the value.");
    }

    // Conjunctive declarations share one object's allowed names. Union alternatives remain separate.
    private static IEnumerable<IReadOnlyList<JsonElement>> ExpandConstraints(
        JsonElement value,
        IReadOnlyList<JsonElement> schemas,
        JsonElement root,
        int depth)
    {
        if (depth > 256)
            throw new JsonException("Remote strict input recursion exceeded the supported depth.");
        if (schemas.Count == 0)
        {
            yield return [];
            yield break;
        }

        var schema = schemas[0];
        var remaining = schemas.Skip(1).ToArray();
        var conjunction = new List<JsonElement>();
        var unions = new List<JsonElement>();
        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("$ref", out var reference))
                conjunction.Add(ResolveReference(root, reference.GetString()));
            if (schema.TryGetProperty("allOf", out var allOf))
                conjunction.AddRange(allOf.EnumerateArray());
            foreach (var keyword in new[] { "anyOf", "oneOf" })
                if (schema.TryGetProperty(keyword, out var branches))
                    unions.Add(branches);
        }

        foreach (var expanded in ExpandConstraints(value, conjunction, root, depth + 1))
            foreach (var selected in ExpandUnions(value, unions, root, depth + 1))
                foreach (var tail in ExpandConstraints(value, remaining, root, depth + 1))
                    yield return new[] { schema }.Concat(expanded).Concat(selected).Concat(tail).ToArray();
    }

    private static IEnumerable<IReadOnlyList<JsonElement>> ExpandUnions(
        JsonElement value,
        IReadOnlyList<JsonElement> unions,
        JsonElement root,
        int depth)
    {
        if (unions.Count == 0)
        {
            yield return [];
            yield break;
        }

        foreach (var branch in unions[0].EnumerateArray())
        {
            if (!Accepts(value, branch, root, depth + 1))
                continue;
            foreach (var expanded in ExpandConstraints(value, [branch], root, depth + 1))
                foreach (var tail in ExpandUnions(value, unions.Skip(1).ToArray(), root, depth + 1))
                    yield return expanded.Concat(tail).ToArray();
        }
    }

    private static void ValidateStrictShape(
        JsonElement value,
        IReadOnlyList<JsonElement> schemas,
        JsonElement root,
        int depth)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var declared = schemas.Any(schema =>
                schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out _));
            var indexed = schemas.Any(schema => schema.ValueKind == JsonValueKind.Object &&
                schema.TryGetProperty("additionalProperties", out var additional) &&
                additional.ValueKind != JsonValueKind.False);
            foreach (var field in value.EnumerateObject())
            {
                var children = new List<JsonElement>();
                var known = false;
                foreach (var schema in schemas)
                {
                    if (schema.ValueKind != JsonValueKind.Object)
                        continue;
                    if (schema.TryGetProperty("properties", out var properties) &&
                        properties.TryGetProperty(field.Name, out var child))
                    {
                        known = true;
                        children.Add(child);
                    }
                    else if (schema.TryGetProperty("additionalProperties", out var additional) &&
                             additional.ValueKind == JsonValueKind.Object)
                        children.Add(additional);
                }

                if (declared && !known && !indexed)
                    throw new JsonException($"Unknown strict Remote input property '{field.Name}'.");
                ValidateStrictInput(field.Value, children, root, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            for (var index = 0;index < value.GetArrayLength();index++)
            {
                var children = new List<JsonElement>();
                foreach (var schema in schemas)
                {
                    if (schema.ValueKind != JsonValueKind.Object)
                        continue;
                    if (schema.TryGetProperty("prefixItems", out var prefix) && index < prefix.GetArrayLength())
                        children.Add(prefix[index]);
                    else if (schema.TryGetProperty("items", out var items))
                        children.Add(items);
                }

                ValidateStrictInput(value[index], children, root, depth + 1);
            }
        }
    }
}
