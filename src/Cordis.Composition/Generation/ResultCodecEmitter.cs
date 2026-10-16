using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Typert;

// Compiles the existing output projection, rather than accepting schemas from the peer.
internal static class ResultCodecEmitter
{
    internal sealed record Output(string Source, bool HasBinary);

    internal static Output Generate(ClientCodec codec, string name, bool binaryResults)
    {
        var source = new StringBuilder();
        var nodes = new Dictionary<(string, JsonTypeInfo?), string>();
        var pending = new Queue<(string Name, JsonElement Schema, JsonTypeInfo? Metadata)>();
        var hasBinary = false;
        var root = codec.IsVoid ? "false" : Call(codec.Schema, binaryResults ? codec.ResultMetadata : null);
        while (pending.TryDequeue(out var item))
        {
            var expression = Predicate(item.Schema, item.Metadata);
            source
                .Append("function ")
                .Append(item.Name)
                .Append("(v,d) { return d <= 256 && v !== undefined && (")
                .Append(expression)
                .Append("); }\n");
        }

        source
            .Append("const ")
            .Append(name)
            .Append(" = createResultCodec(")
            .Append("(v) => v === undefined ? ")
            .Append(codec.IsVoid || codec.AcceptsUndefined ? "true" : "false")
            .Append(" : ")
            .Append(root.Replace("d+1", "0", StringComparison.Ordinal))
            .Append(", ")
            .Append(Quote(name))
            .Append(");\n");
        return new(source.ToString(), hasBinary);

        string Call(JsonElement node, JsonTypeInfo? metadata, string value = "v")
        {
            var key = (node.GetRawText(), metadata);
            if (!nodes.TryGetValue(key, out var function))
            {
                function = name + "Node" + nodes.Count;
                nodes.Add(key, function);
                pending.Enqueue((function, node, metadata));
            }

            return function + "(" + value + ",d+1)";
        }

        string Predicate(JsonElement node, JsonTypeInfo? metadata)
        {
            var binary = ClientMetadata.IsBinary(metadata);
            hasBinary |= binary;
            if (node.ValueKind == JsonValueKind.False)
                return "false";
            if (node.ValueKind == JsonValueKind.True)
            {
                if (!ClientMetadata.IsBinary(metadata))
                    return "resultJson(v,d)";
                hasBinary = true;
                return "v instanceof Uint8Array";
            }

            if (node.ValueKind != JsonValueKind.Object)
                throw new NotSupportedException("Invalid output codec schema.");
            var terms = new List<string>();
            foreach (var field in node.EnumerateObject())
            {
                switch (field.Name)
                {
                    case "$ref":
                        terms.Add(Call(Resolve(field.Value.GetString()!), metadata));
                        break;
                    case "type":
                        var types = field.Value.ValueKind == JsonValueKind.Array
                            ? field.Value.EnumerateArray().Select(value => value.GetString()!).ToArray()
                            : [field.Value.GetString()!];
                        terms.Add("(" + string.Join(" || ", types.Select(kind => Type(kind, binary))) + ")");
                        if (binary && types.Contains("string", StringComparer.Ordinal))
                            hasBinary = true;
                        break;
                    case "const":
                    case "enum":
                        terms.Add(
                            field.Name == "const"
                                ? Literal(field.Value, binary)
                                : "[" + string.Join(
                                    ",",
                                    field
                                        .Value.EnumerateArray()
                                        .Select(value =>
                                            Literal(value, binary))) + "].some(Boolean)");
                        break;
                    case "allOf":
                    case "anyOf":
                    case "oneOf":
                        var variants = field.Value.EnumerateArray().Select(child => Call(child, metadata)).ToArray();
                        terms.Add(
                            field.Name == "oneOf"
                                ? "[" + string.Join(",", variants) + "].filter(Boolean).length === 1"
                                : "(" + string.Join(
                                    field.Name == "allOf" ? " && " : " || ",
                                    variants.DefaultIfEmpty(field.Name == "allOf" ? "true" : "false")) + ")");
                        break;
                    case "not":
                        terms.Add("!(" + Call(field.Value, metadata) + ")");
                        break;
                    case "pattern":
                        if (binary || !NumericPatterns.Contains(field.Value.GetString()!, StringComparer.Ordinal))
                            throw new NotSupportedException(
                                "Output codecs support only the existing numeric string patterns.");
                        var pattern = field.Value.GetString()!;
                        if (!pattern.EndsWith("$(?![\\s\\S])", StringComparison.Ordinal))
                            pattern += "(?![\\s\\S])";
                        terms.Add("(typeof v !== 'string' || new RegExp(" + Quote(pattern) + ").test(v))");
                        if (node.TryGetProperty("format", out var format) && format.GetString() is "float" or "double")
                            terms.Add(
                                "(typeof v !== 'string' || " + Finite(
                                    "Number(v)",
                                    format.GetString() == "float") + ")");
                        break;
                    case "minimum":
                    case "maximum":
                    case "exclusiveMinimum":
                    case "exclusiveMaximum":
                        var operation = field.Name switch
                        {
                            "minimum" => ">=",
                            "maximum" => "<=",
                            "exclusiveMinimum" => ">",
                            _ => "<"
                        };
                        terms.Add("(typeof v !== 'number' || v " + operation + " " + field.Value.GetRawText() + ")");
                        break;
                    case "format":
                        if (field.Value.GetString() == "float")
                            terms.Add("(typeof v !== 'number' || " + Finite("v", true) + ")");
                        break;
                    case "properties":
                    case "required":
                    case "additionalProperties":
                    case "items":
                    case "prefixItems":
                    case "minItems":
                    case "maxItems":
                    case "$defs":
                    case "definitions":
                    case "$schema":
                    case "$id":
                    case "$comment":
                    case "title":
                    case "description":
                    case "default":
                    case "deprecated":
                    case "readOnly":
                    case "writeOnly":
                    case "contentEncoding":
                    case "contentMediaType":
                        break;
                    default:
                        throw new NotSupportedException("Unsupported output codec keyword: " + field.Name);
                }
            }

            var properties = node.TryGetProperty("properties", out var fields)
                ? fields.EnumerateObject().ToArray()
                : [];
            var objectTerms = new List<string>();
            if (node.TryGetProperty("required", out var required))
                objectTerms.AddRange(
                    required.EnumerateArray().Select(field => "Object.hasOwn(v," + field.GetRawText() + ")"));
            foreach (var field in properties)
                objectTerms.Add(
                    "(!Object.hasOwn(v," + Quote(field.Name) + ") || " +
                    Call(field.Value, ClientMetadata.Property(metadata, field.Name), "v[" + Quote(field.Name) + "]") +
                    ")");
            var additional = node.TryGetProperty("additionalProperties", out var extra)
                ? Call(extra, metadata?.Kind == JsonTypeInfoKind.Dictionary ? Element(metadata) : null, "v[k]")
                : "resultJson(v[k],d+1)";
            objectTerms.Add(
                "Object.keys(v).every(k => " +
                "[" + string.Join(",", properties.Select(field => Quote(field.Name))) + "].includes(k) || " +
                additional + ")");
            if (properties.Length != 0 || node.TryGetProperty("required", out _) ||
                node.TryGetProperty("additionalProperties", out _) || HasType(node, "object"))
                terms.Add("(!resultObject(v) || (" + string.Join(" && ", objectTerms) + "))");

            var arrayTerms = new List<string>();
            if (node.TryGetProperty("minItems", out var minimum))
                arrayTerms.Add("v.length >= " + minimum.GetInt32());
            if (node.TryGetProperty("maxItems", out var maximum))
                arrayTerms.Add("v.length <= " + maximum.GetInt32());
            var prefixLength = 0;
            if (node.TryGetProperty("prefixItems", out var prefix))
                foreach (var child in prefix.EnumerateArray())
                {
                    var index = prefixLength++;
                    arrayTerms.Add(
                        "(v.length <= " + index + " || " + Call(child, Element(metadata), "v[" + index + "]") + ")");
                }

            var tail = node.TryGetProperty("items", out var items)
                ? Call(items, Element(metadata), "v[i]")
                : "resultJson(v[i],d+1)";
            arrayTerms.Add(
                "Array.from({length:v.length},(_,i) => i).every(i => Object.hasOwn(v,i) && (i < " +
                prefixLength + " || " + tail + "))");
            if (node.TryGetProperty("items", out _) || node.TryGetProperty("prefixItems", out _) ||
                node.TryGetProperty("minItems", out _) || node.TryGetProperty("maxItems", out _) ||
                HasType(node, "array"))
                terms.Add("(!Array.isArray(v) || (" + string.Join(" && ", arrayTerms) + "))");
            if (!node.TryGetProperty("type", out _) && !node.TryGetProperty("$ref", out _) &&
                !node.TryGetProperty("anyOf", out _) && !node.TryGetProperty("oneOf", out _) &&
                !node.TryGetProperty("allOf", out _) &&
                !(binary && (node.TryGetProperty("const", out _) || node.TryGetProperty("enum", out _))))
                terms.Add(
                    properties.Length == 0 && !node.TryGetProperty("additionalProperties", out _) &&
                    !node.TryGetProperty("items", out _) && !node.TryGetProperty("prefixItems", out _)
                        ? "resultJson(v,d)"
                        : "(v === null || typeof v === 'string' || typeof v === 'boolean' || " +
                        "(typeof v === 'number' && Number.isFinite(v)) || resultObject(v) || Array.isArray(v))");
            return string.Join(" && ", terms.Select(term => "(" + term + ")"));
        }

        JsonElement Resolve(string pointer)
        {
            var current = codec.Schema;
            if (pointer == "#")
                return current;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal))
                throw new NotSupportedException("Output codec references must be local.");
            foreach (var segment in pointer[2..].Split('/'))
            {
                var key = Uri
                    .UnescapeDataString(segment)
                    .Replace("~1", "/", StringComparison.Ordinal)
                    .Replace("~0", "~", StringComparison.Ordinal);
                current = current.ValueKind == JsonValueKind.Array
                    ? current[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)]
                    : current.GetProperty(key);
            }

            return current;
        }
    }

    private static JsonTypeInfo? Element(JsonTypeInfo? metadata) =>
        metadata?.Kind is JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary &&
        metadata.ElementType is { } element
            ? metadata.Options.GetTypeInfo(element)
            : null;

    private static string Quote(string value) => JsonSerializer.Serialize(value, ClientEmitterJson.Default.String);

    private static string Literal(JsonElement value, bool binary)
    {
        if (!binary || value.ValueKind == JsonValueKind.Null)
            return "resultEqual(v," + value.GetRawText() + ")";
        if (value.ValueKind != JsonValueKind.String)
            throw new NotSupportedException("A binary output constant must describe serialized bytes.");
        var bytes = Convert.FromBase64String(value.GetString()!);
        return "(v instanceof Uint8Array && v.length === " + bytes.Length + " && [" +
            string.Join(",", bytes) + "].every((b,i) => v[i] === b))";
    }

    private static bool HasType(JsonElement schema, string expected) =>
        schema.TryGetProperty("type", out var type) && (type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(value => value.GetString() == expected)
            : type.GetString() == expected);

    private static string Finite(string value, bool single) =>
        "Number.isFinite(" + (single ? "Math.fround(" + value + ")" : value) + ")";

    private static string Type(string kind, bool binary) => kind switch
    {
        "null" => "v === null",
        "boolean" => "typeof v === 'boolean'",
        "string" => binary ? "v instanceof Uint8Array" : "typeof v === 'string'",
        "integer" => "Number.isInteger(v)",
        "number" => "typeof v === 'number' && Number.isFinite(v)",
        "array" => "Array.isArray(v)",
        "object" => "resultObject(v)",
        _ => throw new NotSupportedException("Unsupported output codec type: " + kind),
    };

    private static readonly string[] NumericPatterns =
    [
        @"^[+-]?[0-9]+$(?![\s\S])", @"^[0-9]+$(?![\s\S])",
        @"^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?$(?![\s\S])",
        @"^-?(?:0|[1-9]\d*)$", @"^-?(?:0|[1-9]\d*)(?:\.\d+)?$",
        @"^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$"
    ];
}
