using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Cordis.Composition;

/// <summary>Embed one standalone Draft 2020-12 schema without sharing resource ids between module exports.</summary>
internal static class ConfigurationSchemaResource
{
    private const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private const string DialectWithFragment = Dialect + "#";

    internal static JsonNode Embed(JsonNode standalone, string identity)
    {
        var copy = standalone.DeepClone();
        if (Boolean(copy))
            return copy;

        // Resolve against the original scopes before assigning private ids. A fragment belongs to the
        // nearest schema resource, which may be a nested $id under the plugin's root.
        var resources = new Dictionary<string, Resource>(StringComparer.Ordinal);
        var schemas = new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
        var links = new List<(JsonObject Node, string Keyword, Uri Uri)>();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity + "\n" + standalone.ToJsonString()));
        var key = Convert.ToHexStringLower(digest);
        var idPrefix = "urn:cordis:config-schema:" + key + ":";
        var anonymousBase = new Uri("https://cordis.invalid/schema/" + key + "/");

        void Visit(JsonNode? node, Resource? parent)
        {
            if (node is null)
                throw new FormatException("Expected an object or boolean JSON Schema.");
            schemas.Add(node);
            if (Boolean(node))
                return;
            if (node is not JsonObject schema)
                throw new FormatException("Expected an object or boolean JSON Schema.");
            if (schema.ContainsKey("$schema") && Text(schema, "$schema") is not (Dialect or DialectWithFragment))
                throw new NotSupportedException("Only the JSON Schema draft 2020-12 dialect is supported.");

            var resource = parent;
            if (parent is null || schema.ContainsKey("$id"))
            {
                // An anonymous standalone document has no external retrieval URI. Relative identifiers are
                // sufficient when all referenced resources are present within that document.
                var baseUri = parent?.Original ?? anonymousBase;
                var original = schema.ContainsKey("$id") ? new Uri(baseUri, Text(schema, "$id")) : baseUri;
                if (original.Fragment.Length > 1)
                    throw new NotSupportedException(
                        "A draft 2020-12 schema resource id cannot contain a nonempty fragment.");
                resource = new(original, idPrefix + resources.Count.ToString(CultureInfo.InvariantCulture), schema);
                if (!resources.TryAdd(WithoutFragment(original), resource))
                    throw new FormatException("Two schema resources have the same id within one declaration.");
                schema["$id"] = resource.Embedded;
            }

            foreach (var keyword in new[] { "$anchor", "$dynamicAnchor" })
                if (schema.ContainsKey(keyword))
                {
                    var anchor = Text(schema, keyword);
                    if (anchor.Length == 0 || !(char.IsAsciiLetter(anchor[0]) || anchor[0] == '_') ||
                        anchor.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')))
                        throw new FormatException("Invalid JSON Schema anchor name.");
                    if (!resource!.Anchors.Add(anchor))
                        throw new FormatException("Two schema anchors have the same name within one resource.");
                }

            foreach (var keyword in new[] { "$ref", "$dynamicRef" })
                if (schema.ContainsKey(keyword))
                    links.Add((schema, keyword, new Uri(resource!.Original, Text(schema, keyword))));

            // Walk schema-valued keywords only. Objects inside defaults, examples, enums, extensions
            // and const values are instance data even when they contain keys such as $ref or $id.
            foreach (var (keyword, value) in schema)
                switch (keyword)
                {
                    case "$defs" or "definitions" or "properties" or "patternProperties" or "dependentSchemas":
                        foreach (var (_, child) in value?.AsObject() ??
                                 throw new FormatException("Expected a schema map."))
                            Visit(child, resource);
                        break;
                    case "allOf" or "anyOf" or "oneOf" or "prefixItems":
                        foreach (var child in value?.AsArray() ?? throw new FormatException("Expected a schema array."))
                            Visit(child, resource);
                        break;
                    case "if" or "then" or "else" or "not" or "items" or "contains" or "additionalProperties"
                        or "propertyNames" or "contentSchema" or "unevaluatedProperties" or "unevaluatedItems":
                        Visit(value, resource);
                        break;
                }
        }

        Visit(copy, null);
        foreach (var (node, keyword, uri) in links)
        {
            if (!resources.TryGetValue(WithoutFragment(uri), out var target))
                throw new NotSupportedException("A referenced resource is outside the supplied declaration.");
            var fragment = uri.Fragment.Length > 0 ? Uri.UnescapeDataString(uri.Fragment[1..]) : "";
            var found = fragment.Length == 0 || (fragment[0] == '/'
                ? Find(target.Root, fragment) is { } referenced && schemas.Contains(referenced)
                : target.Anchors.Contains(fragment));
            if (!found)
                throw new NotSupportedException("The declaration refers to a missing schema pointer or anchor.");
            node[keyword] = target.Embedded + uri.Fragment;
        }

        return copy;
    }

    private sealed record Resource(Uri Original, string Embedded, JsonObject Root)
    {
        internal HashSet<string> Anchors
        {
            get;
        } = new(StringComparer.Ordinal);
    }

    private static string WithoutFragment(Uri uri) => uri.AbsoluteUri.Split('#', 2)[0];

    private static bool Boolean(JsonNode node) => node is JsonValue value && value.TryGetValue<bool>(out _);

    private static string Text(JsonObject schema, string keyword) =>
        schema[keyword] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new FormatException(keyword + " must be a string.");

    private static JsonNode? Find(JsonNode root, string pointer)
    {
        JsonNode? node = root;
        foreach (var raw in pointer[1..].Split('/'))
        {
            var token = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node switch
            {
                JsonObject obj => obj[token],
                JsonArray array when int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    && (token.Length == 1 || token[0] != '0') && index < array.Count => array[index],
                _ => null
            };
        }

        return node;
    }
}
