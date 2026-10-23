using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Typert;

/// <summary>A known lossless tool-schema projection refusal.</summary>
internal sealed class ToolSchemaException(string message) : Exception("typert tool schema: " + message);

/// <summary>Projects the existing wire schema into the acyclic harness tool subset.</summary>
internal static class ToolSchemaProjection
{
    internal static void RequireJsonResult(JsonTypeInfo? metadata)
    {
        var visited = new HashSet<JsonTypeInfo>();
        Visit(metadata);

        void Visit(JsonTypeInfo? current)
        {
            if (current is null || !visited.Add(current))
                return;
            if (ClientMetadata.IsBinary(current))
                throw new ToolSchemaException("binary results are not JSON tool values");
            if (current.Kind == JsonTypeInfoKind.Object)
                foreach (var property in current.Properties)
                    if (property.Get is not null)
                        Visit(ClientMetadata.Property(current, property.Name));
            if (current.ElementType is { } elementType)
                Visit(current.Options.GetTypeInfo(elementType));
        }
    }

    internal static JsonElement Project(JsonElement schema, bool strict = false)
    {
        var active = new HashSet<string>(StringComparer.Ordinal);
        var projected = ProjectNode(schema);
        using var document = JsonDocument.Parse(projected.ToJsonString());
        return document.RootElement.Clone();

        JsonObject ProjectNode(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.True)
                return new();
            if (node.ValueKind != JsonValueKind.Object)
                throw new ToolSchemaException("unsupported empty or non-object value schema");
            foreach (var field in node.EnumerateObject())
                if (field.Name is not ("$ref" or "$defs" or "definitions" or "$schema" or "$comment" or "title" or
                    "description" or "default" or "deprecated" or "readOnly" or "writeOnly" or "type" or "const" or
                    "enum" or "anyOf" or "oneOf" or "properties" or "required" or "additionalProperties" or "items" or
                    "contentEncoding" or "contentMediaType"))
                    throw new ToolSchemaException($"unsupported constraint '{field.Name}'");
            JsonObject result;
            if (node.TryGetProperty("$ref", out var reference))
            {
                if (node
                    .EnumerateObject()
                    .Any(field => field.Name is not ("$ref" or "$defs" or "definitions" or "$schema" or
                        "$comment" or "title" or "description" or "default" or "deprecated" or "readOnly"
                        or "writeOnly" or "contentEncoding" or "contentMediaType")))
                    throw new ToolSchemaException("a constrained reference cannot be represented");
                var path = reference.GetString()!;
                if (!active.Add(path))
                    throw new ToolSchemaException(
                        "recursive selected type requires a supported JSON value declaration");
                try
                {
                    result = ProjectNode(Resolve(path));
                }
                finally
                {
                    active.Remove(path);
                }
            }
            else if (node.TryGetProperty("oneOf", out var oneOf) || node.TryGetProperty("anyOf", out oneOf))
            {
                if (node
                    .EnumerateObject()
                    .Any(field => field.Name is not ("oneOf" or "anyOf" or "$defs" or "definitions" or
                        "$schema" or "$comment" or "title" or "description" or "default" or "deprecated" or "readOnly"
                        or "writeOnly" or "contentEncoding" or "contentMediaType")))
                    throw new ToolSchemaException("a constrained union cannot be represented");
                if (node.TryGetProperty("oneOf", out _) && node.TryGetProperty("anyOf", out _))
                    throw new ToolSchemaException("combined union constraints cannot be represented");
                var alternatives = oneOf.EnumerateArray().Select(ProjectNode).ToList();
                if (node.TryGetProperty("oneOf", out _))
                    for (var left = 0;left < alternatives.Count;left++)
                        for (var right = left + 1;right < alternatives.Count;right++)
                            if (!Disjoint(alternatives[left], alternatives[right]))
                                throw new ToolSchemaException("exclusive union branches overlap");
                result = Union(alternatives);
            }
            else if (node.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
            {
                var branches = new List<JsonObject>();
                foreach (var item in type.EnumerateArray())
                {
                    var branch = JsonNode.Parse(node.GetRawText())!.AsObject();
                    branch["type"] = item.GetString();
                    using var parsed = JsonDocument.Parse(branch.ToJsonString());
                    branches.Add(ProjectNode(parsed.RootElement));
                }

                result = Union(branches);
            }
            else
            {
                result = new();
                var kind = node.TryGetProperty("type", out type) ? type.GetString() : null;
                if (kind is not (null or "string" or "number" or "boolean" or "null" or "object" or "array"))
                    throw new ToolSchemaException($"unsupported {kind} value");
                if (kind is null && (node.TryGetProperty("properties", out _) || node.TryGetProperty("items", out _) ||
                        node.TryGetProperty("required", out _) || node.TryGetProperty("additionalProperties", out _)))
                    throw new ToolSchemaException("a constrained schema requires an explicit type");
                if (kind is not null)
                    result["type"] = kind;
                if (node.TryGetProperty("const", out _) && node.TryGetProperty("enum", out _))
                    throw new ToolSchemaException("combined scalar literal constraints cannot be represented");
                foreach (var keyword in new[] { "const", "enum" })
                    if (node.TryGetProperty(keyword, out var literal))
                    {
                        if (kind is null or "object" or "array")
                            throw new ToolSchemaException("only typed scalar literals are supported");
                        if (keyword == "const"
                                ? !Scalar(literal)
                                : literal.EnumerateArray().Any(value => !Scalar(value)))
                            throw new ToolSchemaException("only finite JSON scalar literals are supported");
                        result[keyword] = JsonNode.Parse(literal.GetRawText());
                    }

                if (kind == "array")
                    result["items"] = node.TryGetProperty("items", out var items)
                        ? ProjectNode(items)
                        : new JsonObject();
                if (kind == "object")
                {
                    var properties = new JsonObject();
                    var declared = node.TryGetProperty("properties", out var fields);
                    if (declared)
                        foreach (var field in fields.EnumerateObject())
                            properties[field.Name] = ProjectNode(field.Value);
                    var indexed = false;
                    var closed = false;
                    if (node.TryGetProperty("additionalProperties", out var additional))
                    {
                        closed = additional.ValueKind == JsonValueKind.False;
                        indexed = additional.ValueKind == JsonValueKind.True ||
                            additional.ValueKind == JsonValueKind.Object;
                        if (additional.ValueKind == JsonValueKind.Object && ProjectNode(additional).Count != 0)
                            throw new ToolSchemaException("selected dictionaries require unconstrained JSON values");
                    }

                    result["properties"] = properties;
                    result["required"] = node.TryGetProperty("required", out var required)
                        ? JsonNode.Parse(required.GetRawText())
                        : new JsonArray();
                    result["additionalProperties"] = !closed && (indexed || !declared || !strict);
                }
            }

            if (node.TryGetProperty("description", out var description))
                result["description"] = description.GetString();
            return result;
        }

        JsonElement Resolve(string path)
        {
            if (path == "#")
                return schema;
            if (!path.StartsWith("#/", StringComparison.Ordinal))
                throw new ToolSchemaException("references must be local JSON pointers");
            var current = schema;
            foreach (var part in path[2..].Split('/'))
            {
                var key = Uri
                    .UnescapeDataString(part)
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind == JsonValueKind.Array && int.TryParse(key, out var index) && index >= 0 &&
                    index < current.GetArrayLength())
                    current = current[index];
                else if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out current))
                    throw new ToolSchemaException($"unresolved reference '{path}'");
            }

            return current;
        }
    }

    private static bool Scalar(JsonElement value) => value.ValueKind is JsonValueKind.String or
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null ||
        value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number);

    private static JsonObject Union(List<JsonObject> input)
    {
        if (input.Any(branch => branch.Count == 0))
            return new();
        var branches = new List<JsonObject>();
        foreach (var branch in input)
        {
            var type = branch["type"]?.GetValue<string>();
            var previous = type is not (null or "object" or "array")
                ? branches.Find(candidate => candidate["type"]?.GetValue<string>() == type)
                : null;
            if (previous is null)
                branches.Add(branch);
            else
            {
                var values = Literals(previous);
                var next = Literals(branch);
                previous.Remove("const");
                previous.Remove("enum");
                if (values is not null && next is not null)
                {
                    var literals = new JsonArray();
                    foreach (var value in values.Concat(next))
                        if (!literals.Any(existing => JsonNode.DeepEquals(existing, value)))
                            literals.Add(value?.DeepClone());
                    previous["enum"] = literals;
                }
            }
        }

        if (branches.Count == 0)
            throw new ToolSchemaException("empty selected union");
        if (branches.Count == 1)
            return branches[0];
        for (var left = 0;left < branches.Count;left++)
            for (var right = left + 1;right < branches.Count;right++)
                if (!Disjoint(branches[left], branches[right]))
                    throw new ToolSchemaException("selected union branches overlap or have no required discriminator");
        return new()
        {
            ["oneOf"] = new JsonArray(branches.Cast<JsonNode>().ToArray())
        };
    }

    private static IReadOnlyList<JsonNode?>? Literals(JsonObject value) => value.ContainsKey("const")
        ? [value["const"]]
        : value["enum"]?.AsArray().ToArray();

    private static bool Disjoint(JsonObject left, JsonObject right)
    {
        if (left["oneOf"] is JsonArray leftBranches)
            return leftBranches.All(branch => Disjoint(branch!.AsObject(), right));
        if (right["oneOf"] is JsonArray rightBranches)
            return rightBranches.All(branch => Disjoint(left, branch!.AsObject()));
        var firstType = left["type"]?.GetValue<string>();
        var secondType = right["type"]?.GetValue<string>();
        if (firstType != secondType)
            return firstType is not null && secondType is not null;
        if (firstType != "object")
            return Literals(left) is { } firstValues && Literals(right) is { } secondValues &&
                firstValues.All(value => secondValues.All(other => !JsonNode.DeepEquals(value, other)));
        foreach (var name in left["required"]!.AsArray().Select(value => value!.GetValue<string>()))
        {
            if (!right["required"]!.AsArray().Any(value => value!.GetValue<string>() == name))
                continue;
            var first = left["properties"]?[name] as JsonObject;
            var second = right["properties"]?[name] as JsonObject;
            if (first is not null && second is not null && Literals(first) is { } a && Literals(second) is { } b &&
                a.All(value => b.All(other => !JsonNode.DeepEquals(value, other))))
                return true;
        }

        return false;
    }
}
