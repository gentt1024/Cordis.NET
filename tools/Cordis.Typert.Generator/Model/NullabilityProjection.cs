using System.Text;
using System.Text.Json;

namespace Cordis.Typert.Generator.Model;

// A generated query over the existing declaration graph, not another saved model or runtime registry.
internal sealed class NullabilityProjection
{
    private sealed record Node(
        bool? Nullable,
        Dictionary<string, int> Properties,
        int? Item = null,
        string? ItemKey = null);

    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    private readonly List<Node?> nodes = [];

    internal static string Declaration(
        DeclarationReference root,
        DeclarationModelDocument model,
        DeclarationReference? context,
        string method)
    {
        var graph = new NullabilityProjection();
        var types = model.Declarations.ToDictionary(type => type.Id, StringComparer.Ordinal);
        var naming = context is not null && types.TryGetValue(context.Name, out var serializer)
            ? DeclarationAttributeProjection.Named(
                    DeclarationAttributeProjection.Attribute(
                        serializer.Attributes,
                        DeclarationAttributeProjection.JsonPrefix + "JsonSourceGenerationOptionsAttribute"),
                    "PropertyNamingPolicy")
                ?.Json ?? "0"
            : "0";
        Read(root);
        return graph.Emit(method);

        int Read(DeclarationReference reference)
        {
            var key = JsonSerializer.Serialize(reference);
            if (graph.ids.TryGetValue(key, out var found))
                return found;
            var id = graph.nodes.Count;
            graph.ids.Add(key, id);
            graph.nodes.Add(null);
            var nullable = Nullable(reference.Kind, reference.Name, reference.Nullability);
            var properties = new Dictionary<string, int>(StringComparer.Ordinal);
            var node = new Node(nullable, properties);
            if (reference.Kind == "array" && reference.Arguments.Length == 1)
                node = node with
                {
                    Item = Read(reference.Arguments[0]),
                    ItemKey = "items"
                };
            else if (reference.Kind == "named" && types.TryGetValue(reference.Name, out var type))
            {
                if (DeclarationWireProjection.Framework(type) && reference.Arguments.Length > 0)
                {
                    if (type.MetadataName == "System.Nullable`1")
                        node = node with
                        {
                            Nullable = true
                        };
                    else if (type.MetadataName.StartsWith("System.Collections.Generic.", StringComparison.Ordinal))
                        node = node with
                        {
                            Item = Read(reference.Arguments[^1]),
                            ItemKey = reference.Arguments.Length == 2 ? "additionalProperties" : "items"
                        };
                }
                else if (type.MetadataName == "System.Text.Json.JsonElement")
                    node = node with
                    {
                        Nullable = null
                    };
                else
                    foreach (var member in type.Members.Where(member => member.Kind is "property" or "field" &&
                                 member.Type is not null &&
                                 !member.Modifiers.Contains("static", StringComparer.Ordinal)))
                    {
                        var name = DeclarationWireProjection.MemberName(
                            member.Name,
                            DeclarationAttributeProjection.Native(
                                member
                                    .Attributes.Where(attribute => attribute.Name ==
                                        DeclarationAttributeProjection.JsonPrefix + "JsonPropertyNameAttribute")
                                    .ToArray()),
                            naming);
                        var value = member.Type!;
                        if (member.Attributes.Any(attribute =>
                                attribute.Name == "System.Diagnostics.CodeAnalysis.AllowNullAttribute"))
                            value = value with
                            {
                                Nullability = "Annotated"
                            };
                        if (member.Attributes.Any(attribute =>
                                attribute.Name == "System.Diagnostics.CodeAnalysis.DisallowNullAttribute"))
                            value = value with
                            {
                                Nullability = "NotAnnotated"
                            };
                        properties[name] = Read(value);
                    }
            }

            graph.nodes[id] = node;
            return id;
        }
    }

    internal static string Native(
        NativeTypeReference root,
        IReadOnlyDictionary<string, NativeTypeDeclaration> types,
        string method)
    {
        var graph = new NullabilityProjection();
        Read(root);
        return graph.Emit(method);

        int Read(NativeTypeReference reference)
        {
            var key = JsonSerializer.Serialize(reference);
            if (graph.ids.TryGetValue(key, out var found))
                return found;
            var id = graph.nodes.Count;
            graph.ids.Add(key, id);
            graph.nodes.Add(null);
            var properties = new Dictionary<string, int>(StringComparer.Ordinal);
            var node = new Node(Nullable(reference.Kind, reference.Name, reference.Nullability), properties);
            if (reference.Kind is "array" or "collection")
                node = node with
                {
                    Item = Read(reference.Arguments[^1]),
                    ItemKey = reference.Arguments.Length == 2 ? "additionalProperties" : "items"
                };
            else if (reference.Kind == "named" && types.TryGetValue(reference.Name, out var type))
                foreach (var member in type.Members)
                {
                    var value = member.Type;
                    if (member.Attributes.Any(attribute =>
                            attribute.Name == "System.Diagnostics.CodeAnalysis.AllowNullAttribute"))
                        value = value with
                        {
                            Nullability = "Annotated"
                        };
                    if (member.Attributes.Any(attribute =>
                            attribute.Name == "System.Diagnostics.CodeAnalysis.DisallowNullAttribute"))
                        value = value with
                        {
                            Nullability = "NotAnnotated"
                        };
                    properties[member.WireName] = Read(value);
                }

            graph.nodes[id] = node;
            return id;
        }
    }

    private static bool? Nullable(string kind, string name, string annotation) =>
        kind is "json" or "object" or "dynamic" or "parameter" || name == "System_Object" ? null :
        kind == "nullable" || annotation == "Annotated" ? true :
        annotation == "None" ? null : false;

    private string Emit(string method)
    {
        if (nodes.All(node => node!.Properties.Count == 0 && node.Item is null))
            return "private static bool? " + method +
                "(global::System.ReadOnlySpan<string> path) => path.IsEmpty ? (bool?)" +
                (nodes[0]!.Nullable is { } value ? value ? "true" : "false" : "null") + " : null;\n";
        var code = new StringBuilder(
            "private static bool? " + method +
            "(global::System.ReadOnlySpan<string> path) {\nint state = 0;\nfor (int i = 0; i < path.Length; i++) {\nswitch (state) {\n");
        for (var id = 0;id < nodes.Count;id++)
        {
            var node = nodes[id]!;
            if (node.Properties.Count == 0 && node.Item is null)
                continue;
            code.Append("case ").Append(id).Append(":\n");
            if (node.Item is { } item)
                code
                    .Append("if (path[i] != ")
                    .Append(JsonSerializer.Serialize(node.ItemKey))
                    .Append(") return null; state = ")
                    .Append(item)
                    .Append("; break;\n");
            else
            {
                code.Append(
                    "if (path[i] != \"properties\" || ++i >= path.Length) return null;\nstate = path[i] switch {\n");
                foreach (var property in node.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    code
                        .Append(JsonSerializer.Serialize(property.Key))
                        .Append(" => ")
                        .Append(property.Value)
                        .Append(",\n");
                code.Append("_ => -1 }; if (state < 0) return null; break;\n");
            }
        }

        code.Append("default: return null;\n}\n}\nreturn state switch {\n");
        for (var id = 0;id < nodes.Count;id++)
            if (nodes[id]!.Nullable is { } nullable)
                code.Append(id).Append(" => ").Append(nullable ? "true" : "false").Append(",\n");
        return code.Append("_ => null };\n}\n").ToString();
    }
}
