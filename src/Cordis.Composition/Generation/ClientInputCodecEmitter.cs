using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Typert;

// Ordinary admission stays compiled. Strictness adds only declared-field checks;
// the ordinary branch parsers select the applicable union alternatives.
internal static class ClientInputCodecEmitter
{
    internal static string Generate(ClientCodec codec, string name)
    {
        var source = new StringBuilder(ResultCodecEmitter.Generate(codec, name, false).Source);
        var branches = new List<string>();
        Collect(codec.Schema, "#");
        var factories = new List<string>();
        foreach (var path in branches)
        {
            var factory = name + "Branch" + factories.Count;
            var schema = JsonNode.Parse(codec.Schema.GetRawText())!;
            Rebase(schema);
            var wrapper = new JsonObject
            {
                ["$defs"] = new JsonObject
                {
                    ["source"] = schema
                },
                ["$ref"] = "#/$defs/source" + path[1..]
            };
            using var document = JsonDocument.Parse(wrapper.ToJsonString());
            var branch = codec with
            {
                Schema = document.RootElement.Clone(),
                AcceptsUndefined = false
            };
            source
                .Append("function ")
                .Append(factory)
                .Append("Factory() {\n")
                .Append(ResultCodecEmitter.Generate(branch, factory, false).Source)
                .Append("return ")
                .Append(factory)
                .Append(";\n}\n");
            factories.Add(Quote(path) + ":" + factory + "Factory");
        }

        return source
            .Append("return createStrictInputCodec(")
            .Append(name)
            .Append(", ")
            .Append("JSON.parse(")
            .Append(Quote(codec.Schema.GetRawText()))
            .Append("), {")
            .Append(string.Join(",", factories))
            .Append("});\n")
            .ToString();

        void Collect(JsonElement node, string path)
        {
            if (node.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var child in node.EnumerateArray())
                    Collect(child, path + "/" + index++);
            }
            else if (node.ValueKind == JsonValueKind.Object)
                foreach (var field in node.EnumerateObject())
                {
                    var childPath = path + "/" + field
                        .Name.Replace("~", "~0", StringComparison.Ordinal)
                        .Replace("/", "~1", StringComparison.Ordinal);
                    if (field.Name is "anyOf" or "oneOf")
                    {
                        var index = 0;
                        foreach (var child in field.Value.EnumerateArray())
                            branches.Add(childPath + "/" + index++);
                    }

                    if (field.Name is "properties" or "$defs" or "definitions")
                    {
                        foreach (var child in field.Value.EnumerateObject())
                            Collect(
                                child.Value,
                                childPath + "/" + child
                                    .Name.Replace("~", "~0", StringComparison.Ordinal)
                                    .Replace("/", "~1", StringComparison.Ordinal));
                    }
                    else if (field.Name is "items" or "prefixItems" or "additionalProperties" or
                             "anyOf" or "oneOf" or "allOf")
                        Collect(field.Value, childPath);
                }
        }
    }

    private static void Rebase(JsonNode node)
    {
        if (node is JsonObject value)
        {
            if (value["$ref"] is JsonValue reference)
                value["$ref"] = "#/$defs/source" + reference.GetValue<string>()[1..];
            foreach (var field in value)
                if (field.Key is "properties" or "$defs" or "definitions")
                {
                    if (field.Value is JsonObject map)
                        foreach (var child in map)
                            if (child.Value is not null)
                                Rebase(child.Value);
                }
                else if (field.Key is "items" or "prefixItems" or "additionalProperties" or
                             "anyOf" or "oneOf" or "allOf" or "not" && field.Value is not null)
                    Rebase(field.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null)
                    Rebase(child);
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, ClientEmitterJson.Default.String);
}
