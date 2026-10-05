using System.Globalization;
using System.Text.Json.Nodes;

namespace Cordis.Composition;

/// <summary>A data-only declaration projection, with omissions that require the captured runtime validator.</summary>
/// <param name="Format">The serialized document vocabulary.</param>
/// <param name="Document">The standalone JSON document.</param>
/// <param name="Complete">Whether acceptance and normalization are fully represented; arbitrary native validators always require runtime validation.</param>
/// <param name="Diagnostics">Explicit limitations of the projection.</param>
public sealed record ConfigurationSchemaExport(string Format, string Document, bool Complete, IReadOnlyList<string> Diagnostics);

/// <summary>Projects explicitly authored descriptors without reflection, validator execution or lazy-builder execution.</summary>
/// <remarks>These documents describe declared data. They do not reproduce arbitrary validator acceptance, transforms,
/// getters or Core stable-reference behavior. Metadata must be explicitly authored; it is never inferred from callbacks.</remarks>
public static class ConfigurationSchemaExporter
{
    private static readonly HashSet<string> MetadataKeys = new(StringComparer.Ordinal)
    { "role", "extra", "description", "hidden", "disabled", "collapse", "badges", "link", "comment", "min", "max", "step", "pattern", "loose" };
    /// <summary>Export the Schemastery uid/refs envelope for supported data nodes, preserving shared and recursive edges.</summary>
    /// <param name="descriptor">The author's declaration or a captured resolved declaration.</param>
    /// <param name="plainValues">Omit volatile wrappers for a browser form's plain values.</param>
    /// <param name="omitDefaults">Remove all defaults when exporting a redacted settings declaration.</param>
    /// <returns>A distinct Schemastery document and explicit validation limitations.</returns>
    public static ConfigurationSchemaExport ToSchemastery(ConfigDescriptor descriptor, bool plainValues = false, bool omitDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var diagnostics = Limitations();
        var nodes = new List<ConfigDescriptor>();
        var ids = new Dictionary<ConfigDescriptor, int>(ReferenceEqualityComparer.Instance);
        int Id(ConfigDescriptor node)
        {
            if (ids.TryGetValue(node, out var id)) return id;
            // Identity preserves shared declarations and terminates recursive graph traversal.
            id = nodes.Count;
            ids.Add(node, id);
            nodes.Add(node);
            return id;
        }
        Id(descriptor);
        var refs = new JsonObject();
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var type = node.Kind;
            if (type is "transform" or "getter" || type == "lazy" && node.Inner is null)
            {
                diagnostics.Add($"node {index}: {type} behavior is not executable data; exported as any.");
                type = "any";
            }
            var meta = new JsonObject { ["required"] = !node.IsOptional && !node.HasDefault };
            foreach (var (key, annotation) in node.Annotations)
                if (!MetadataKeys.Contains(key)) diagnostics.Add($"node {index}: unknown annotation {key} was omitted.");
                else if (TryJson(annotation, out var encoded)) meta[key] = encoded;
                else diagnostics.Add($"node {index}: annotation {key} is not lossless JSON and was omitted.");
            if (omitDefaults && node.Annotations.GetValueOrDefault("role") is "secret") meta.Remove("required");
            if (node.IsVolatile && !plainValues) meta["volatile"] = true;
            if (!omitDefaults && node.HasDefault && TryJson(node.DefaultValue, out var value)) meta["default"] = value;
            else if (!omitDefaults && node.HasDefault) diagnostics.Add($"node {index}: default is not lossless JSON and was omitted.");
            var record = new JsonObject { ["type"] = type, ["meta"] = meta };
            if (type != "any")
            {
                if (node.Inner is not null) record["inner"] = Id(node.Inner);
                if (node.Key is not null) record["sKey"] = Id(node.Key);
                if (type is "tuple" or "union" or "intersect")
                    record["list"] = new JsonArray(node.Children.Select(child => (JsonNode?)JsonValue.Create(Id(child))).ToArray());
                if (node.Kind == "object")
                {
                    var dict = new JsonObject();
                    foreach (var (name, child) in node.Properties)
                        if (!omitDefaults || child.Annotations.GetValueOrDefault("hidden") is not true) dict[name] = Id(child);
                    record["dict"] = dict;
                }
            }
            refs[index.ToString(CultureInfo.InvariantCulture)] = record;
        }
        var document = new JsonObject { ["uid"] = 0, ["refs"] = refs };
        return new("schemastery", document.ToJsonString(), false, diagnostics.AsReadOnly());
    }

    /// <summary>Export JSON Schema 2020-12 declaration references, separately from the Schemastery envelope.</summary>
    /// <param name="descriptor">The explicit data declaration.</param>
    /// <param name="omitDefaults">Remove all defaults when exporting a redacted settings declaration.</param>
    /// <returns>A JSON Schema document with runtime-validation annotations and omission diagnostics.</returns>
    public static ConfigurationSchemaExport ToJsonSchema(ConfigDescriptor descriptor, bool omitDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var diagnostics = Limitations();
        var nodes = new List<ConfigDescriptor>();
        var ids = new Dictionary<ConfigDescriptor, int>(ReferenceEqualityComparer.Instance);
        int Id(ConfigDescriptor node)
        {
            if (ids.TryGetValue(node, out var id)) return id;
            id = nodes.Count;
            ids.Add(node, id);
            nodes.Add(node);
            return id;
        }
        JsonObject Ref(ConfigDescriptor node) => new() { ["$ref"] = "#/$defs/node" + Id(node) };
        Id(descriptor);
        var definitions = new JsonObject();
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            var schema = new JsonObject();
            switch (node.Kind)
            {
                case "number": case "string": case "boolean": schema["type"] = node.Kind; break;
                case "object":
                    schema["type"] = "object";
                    var properties = new JsonObject();
                    var required = new JsonArray();
                    foreach (var (name, child) in node.Properties)
                    {
                        if (omitDefaults && child.Annotations.GetValueOrDefault("hidden") is true) continue;
                        properties[name] = Ref(child);
                        if (!child.IsOptional && !child.HasDefault && !(omitDefaults && child.Annotations.GetValueOrDefault("role") is "secret"))
                            required.Add((JsonNode?)JsonValue.Create(name));
                    }
                    schema["properties"] = properties;
                    if (required.Count > 0) schema["required"] = required;
                    break;
                case "array":
                    schema["type"] = "array";
                    if (node.Inner is not null) schema["items"] = Ref(node.Inner);
                    break;
                case "dict":
                    schema["type"] = "object";
                    if (node.Inner is not null) schema["additionalProperties"] = Ref(node.Inner);
                    if (node.Key is not null) schema["propertyNames"] = Ref(node.Key);
                    break;
                case "tuple":
                    schema["type"] = "array";
                    if (node.Children.Count > 0)
                        schema["prefixItems"] = new JsonArray(node.Children.Select(child => (JsonNode?)Ref(child)).ToArray());
                    schema["minItems"] = node.Children.Select((child, position) => !child.IsOptional && !child.HasDefault ? position + 1 : 0).DefaultIfEmpty().Max();
                    break;
                case "union": case "intersect":
                    if (node.Children.Count > 0)
                        schema[node.Kind == "union" ? "anyOf" : "allOf"] = new JsonArray(node.Children.Select(child => (JsonNode?)Ref(child)).ToArray());
                    else if (node.Kind == "union") schema["not"] = new JsonObject();
                    diagnostics.Add($"node {index}: branch selection and conversion require the captured validator.");
                    break;
                case "lazy" when node.Inner is not null:
                    schema["$ref"] = "#/$defs/node" + Id(node.Inner);
                    break;
                case "transform" when node.Inner is not null:
                    schema["$ref"] = "#/$defs/node" + Id(node.Inner);
                    diagnostics.Add($"node {index}: transform callback acceptance and normalization are omitted.");
                    break;
                case "any": break;
                default: diagnostics.Add($"node {index}: {node.Kind} is not statically projected."); break;
            }
            if (!omitDefaults && node.HasDefault && TryJson(node.DefaultValue, out var value)) schema["default"] = value;
            else if (!omitDefaults && node.HasDefault) diagnostics.Add($"node {index}: default is not lossless JSON and was omitted.");
            var annotations = new JsonObject { ["volatile"] = node.IsVolatile, ["optional"] = node.IsOptional, ["validation"] = "runtime" };
            foreach (var (key, annotation) in node.Annotations)
                if (!MetadataKeys.Contains(key)) diagnostics.Add($"node {index}: unknown annotation {key} was omitted.");
                else if (TryJson(annotation, out var encoded)) annotations[key] = encoded;
                else diagnostics.Add($"node {index}: annotation {key} is not lossless JSON and was omitted.");
            schema["x-cordis"] = annotations;
            if (node.Annotations.GetValueOrDefault("description") is string description) schema["description"] = description;
            if (node.Annotations.GetValueOrDefault("comment") is string comment) schema["$comment"] = comment;
            if (node.Annotations.GetValueOrDefault("role") is "secret") schema["writeOnly"] = true;
            ProjectConstraints(node, schema, index, diagnostics);
            definitions["node" + index] = schema;
        }
        var document = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema", ["$ref"] = "#/$defs/node0", ["$defs"] = definitions,
            ["x-cordis"] = new JsonObject { ["complete"] = false, ["validation"] = "runtime",
                ["diagnostics"] = new JsonArray(diagnostics.Select(message => (JsonNode?)JsonValue.Create(message)).ToArray()) },
        };
        return new("json-schema-2020-12", document.ToJsonString(), false, diagnostics.AsReadOnly());
    }

    private static List<string> Limitations() =>
    [
        "The descriptor is not its captured validator; acceptance and normalization require runtime validation.",
        "Required/default annotations describe authored omission intent; native and Schemastery null/default semantics may differ.",
    ];

    private static void ProjectConstraints(ConfigDescriptor node, JsonObject schema, int index, List<string> diagnostics)
    {
        double? Number(string key) => node.Annotations.GetValueOrDefault(key) is { } value
            && value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal
            && double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)) ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : null;
        var min = Number("min"); var max = Number("max"); var step = Number("step");
        if (node.Annotations.GetValueOrDefault("loose") is true)
        {
            // A tolerant callback may accept otherwise invalid values. Preserve hints without asserting strict acceptance.
            var hints = new JsonObject();
            foreach (var key in new[] { "x-cordis", "description", "$comment", "default", "writeOnly" })
                if (schema[key] is { } hint) hints[key] = hint.DeepClone();
            schema.Clear();
            foreach (var (key, hint) in hints) schema[key] = hint?.DeepClone();
            diagnostics.Add($"node {index}: loose acceptance and recovery require the captured validator.");
            return;
        }
        if (node.Kind == "number")
        {
            if (min is { } lower) schema["minimum"] = lower;
            if (max is { } upper) schema["maximum"] = upper;
            if (step is { } stride && stride != 0)
            {
                if ((min ?? 0) == 0) schema["multipleOf"] = Math.Abs(stride);
                else diagnostics.Add($"node {index}: step relative to a nonzero minimum requires runtime validation.");
            }
        }
        else if (node.Kind is "array" or "tuple")
        {
            if (min is >= 0 && min <= int.MaxValue) schema["minItems"] = Math.Max(schema["minItems"]?.GetValue<int>() ?? 0, (int)Math.Ceiling(min.Value));
            if (max is >= 0 && max <= int.MaxValue) schema["maxItems"] = (int)Math.Floor(max.Value);
            else if (max is < 0) schema["not"] = new JsonObject();
        }
        else if (node.Kind == "string")
        {
            // Schemastery counts UTF-16 code units; JSON Schema counts Unicode scalar values.
            if (min is <= 1) schema["minLength"] = Math.Max(0, (int)Math.Ceiling(min.Value));
            if (max is < 0) schema["not"] = new JsonObject();
            else if (max is >= 0 && max <= int.MaxValue) schema["maxLength"] = (int)Math.Floor(max.Value);
            if (min is > 1 || max is > 0) diagnostics.Add($"node {index}: UTF-16 length constraints require runtime validation.");
            if (node.Annotations.ContainsKey("pattern")) diagnostics.Add($"node {index}: ECMAScript pattern syntax and flags remain in x-cordis; runtime validation is required.");
        }
    }

    private static bool TryJson(object? value, out JsonNode? result)
    {
        result = null;
        switch (value)
        {
            case null: return true;
            case string text: result = JsonValue.Create(text); return true;
            case char character: result = JsonValue.Create(character.ToString()); return true;
            case bool boolean: result = JsonValue.Create(boolean); return true;
            case byte n: result = JsonValue.Create(n); return true;
            case sbyte n: result = JsonValue.Create(n); return true;
            case short n: result = JsonValue.Create(n); return true;
            case ushort n: result = JsonValue.Create(n); return true;
            case int n: result = JsonValue.Create(n); return true;
            case uint n: result = JsonValue.Create(n); return true;
            case long n: result = JsonValue.Create(n); return true;
            case ulong n: result = JsonValue.Create(n); return true;
            case decimal n: result = JsonValue.Create(n); return true;
            case float n when float.IsFinite(n): result = JsonValue.Create(n); return true;
            case double n when double.IsFinite(n): result = JsonValue.Create(n); return true;
            case IReadOnlyDictionary<string, object?> map:
                var obj = new JsonObject();
                foreach (var (name, child) in map)
                {
                    if (!TryJson(child, out var encoded)) return false;
                    obj[name] = encoded;
                }
                result = obj; return true;
            case IReadOnlyList<object?> list:
                var array = new JsonArray();
                foreach (var child in list)
                {
                    if (!TryJson(child, out var encoded)) return false;
                    array.Add(encoded);
                }
                result = array; return true;
            default: return false;
        }
    }
}
