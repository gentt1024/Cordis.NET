using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>A declaration use within one codec's graph, including its null admission.</summary>
/// <param name="Id">Stable within this query; different nested declarations have different identities.</param>
/// <param name="Nullable">Null keeps the serializer's own null admission.</param>
public readonly record struct TypertNullabilityInfo(int Id, bool? Nullable)
{
    /// <summary>Explicit getter annotation; null preserves the serializer metadata.</summary>
    public bool? GetterNullable
    {
        get;
        init;
    }

    /// <summary>Explicit setter annotation; null preserves the serializer metadata.</summary>
    public bool? SetterNullable
    {
        get;
        init;
    }
}

/// <summary>Declaration use at a JSON Schema path; null leaves an unmapped serializer contract unchanged.</summary>
public delegate TypertNullabilityInfo? TypertNullability(ReadOnlySpan<string> schemaPath);

/// <summary>Gets a statically declared default for an absent input member at an object Schema path.</summary>
/// <remarks>Null means no default; a JSON null element is an explicit default. Values must use wire member names.
/// Required members are never defaulted. This query does not change output decoding or execute author code.</remarks>
public delegate JsonElement? TypertInputDefault(ReadOnlySpan<string> objectPath, string member);

public abstract partial class TypertCodec
{
    /// <summary>Create a metadata codec retaining declaration and accessor nullability.</summary>
    /// <remarks>The generated query reads common declaration facts, not reflection. Explicit accessor annotations
    /// correct a private metadata copy when needed; the author's context remains unchanged. Schema is exported
    /// lazily. Metadata owns names and converters; unmapped schema paths keep the exporter rule.</remarks>
    public static TypertCodec CreateDeclared<T>(
        JsonTypeInfo<T> metadata,
        TypertNullability nullability,
        string? typeSymbol = null,
        TypertInputDefault? inputDefault = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(nullability);
        return new TypedCodec<T>(
            ApplyAccessorNullability(metadata, nullability),
            typeSymbol ?? metadata.Type.FullName ?? metadata.Type.Name,
            null,
            nullability,
            inputDefault);
    }

    private static JsonNode ProjectNullability(JsonNode root, TypertNullability query, TypertInputDefault? inputDefault = null)
    {
        var projected = new Dictionary<(string Source, int? Declaration, bool? Nullable), string>();
        return Visit(root, "#", "#", []);

        JsonNode Visit(JsonNode source, string sourcePath, string outputPath, string[] path)
        {
            if (source is not JsonObject schema)
                return source.DeepClone();
            if (schema["$ref"] is JsonValue reference)
            {
                var pointer = reference.GetValue<string>();
                var target = root;
                if (pointer != "#")
                {
                    if (!pointer.StartsWith("#/", StringComparison.Ordinal))
                        throw new NotSupportedException("A declared schema requires local references.");
                    foreach (var part in pointer[2..].Split('/'))
                    {
                        var name = part
                            .Replace("~1", "/", StringComparison.Ordinal)
                            .Replace("~0", "~", StringComparison.Ordinal);
                        target = target is JsonArray array
                            ? array[int.Parse(name, System.Globalization.CultureInfo.InvariantCulture)]!
                            : target[name]!;
                    }
                }

                var resolved = Visit(target, pointer, outputPath, path);
                if (resolved is JsonObject result)
                    foreach (var sibling in schema.Where(field => field.Key != "$ref"))
                        result[sibling.Key] = sibling.Value?.DeepClone();
                return resolved;
            }

            var declaration = query(path);
            var key = (sourcePath, declaration?.Id, declaration?.Nullable);
            if (projected.TryGetValue(key, out var previous))
                return new JsonObject
                {
                    ["$ref"] = previous
                };
            projected.Add(key, outputPath);
            var copy = (JsonObject)schema.DeepClone();
            foreach (var field in schema)
            {
                var sourceChild = Pointer(sourcePath, field.Key);
                var outputChild = Pointer(outputPath, field.Key);
                var childPath = path.Append(field.Key).ToArray();
                if (field.Key is "properties" or "$defs" or "definitions" && field.Value is JsonObject members)
                {
                    var children = new JsonObject();
                    foreach (var member in members)
                    {
                        var child = Visit(
                            member.Value!,
                            Pointer(sourceChild, member.Key),
                            Pointer(outputChild, member.Key),
                            childPath.Append(member.Key).ToArray());
                        if (field.Key == "properties" && child is JsonObject memberSchema &&
                            inputDefault?.Invoke(path, member.Key) is { } defaultValue)
                            memberSchema["default"] = JsonNode.Parse(defaultValue.GetRawText());
                        children[member.Key] = child;
                    }
                    copy[field.Key] = children;
                }
                else if (field.Key is "items" or "additionalProperties" or "not" && field.Value is { } child)
                    copy[field.Key] = Visit(child, sourceChild, outputChild, childPath);
                else if (field.Key is "allOf" or "anyOf" or "oneOf" or "prefixItems" &&
                         field.Value is JsonArray alternatives)
                {
                    var children = new JsonArray();
                    for (var index = 0;index < alternatives.Count;index++)
                    {
                        var name = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        children.Add(
                            Visit(
                                alternatives[index]!,
                                Pointer(sourceChild, name),
                                Pointer(outputChild, name),
                                childPath.Append(name).ToArray()));
                    }

                    copy[field.Key] = children;
                }
            }

            return AnnotateNullability(copy, declaration?.Nullable);
        }

        static string Pointer(string parent, string name) => parent + "/" +
            name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    }

    private static JsonNode AnnotateNullability(JsonNode node, bool? nullable)
    {
        if (nullable is null || node is not JsonObject schema)
            return node;
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
