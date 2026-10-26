using System.Text;
using System.Text.Json;

namespace Cordis.Typert.Generator.Model;

// A generated query over the existing declaration graph, not another saved model or runtime registry.
internal sealed class NullabilityProjection
{
    private sealed record Node(
        bool? Nullable,
        Dictionary<string, int> Properties,
        bool? Getter = null,
        bool? Setter = null,
        int? Item = null,
        string? ItemKey = null)
    {
        internal Dictionary<string, string> Defaults
        {
            get;
        } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    private readonly List<Node?> nodes = [];
    private readonly Dictionary<string, int> activeInstantiations = new(StringComparer.Ordinal);

    private void EnterInstantiation(string name)
    {
        activeInstantiations.TryGetValue(name, out var count);
        if (count >= 256)
            throw new InvalidDataException(
                "Recursive generic nullability projection exceeds 256 active instantiations of " + name + ".");
        activeInstantiations[name] = count + 1;
    }

    internal static string Declaration(
        DeclarationReference root,
        DeclarationModelDocument model,
        DeclarationReference? context,
        string method)
    {
        var graph = new NullabilityProjection();
        var types = model.Declarations.ToDictionary(type => type.Id, StringComparer.Ordinal);
        var options = context is not null && types.TryGetValue(context.Name, out var serializer)
            ? DeclarationAttributeProjection.Attribute(
                serializer.Attributes,
                DeclarationAttributeProjection.JsonPrefix + "JsonSourceGenerationOptionsAttribute")
            : null;
        var naming = DeclarationAttributeProjection.Named(options, "PropertyNamingPolicy")?.Json ?? "0";
        var includeFields =
            DeclarationAttributeProjection.Boolean(DeclarationAttributeProjection.Named(options, "IncludeFields"));
        Read(root);
        return graph.Emit(method);

        int Read(DeclarationReference reference, bool? getter = null, bool? setter = null)
        {
            if (reference.Kind == "named" && types.TryGetValue(reference.Name, out var nullableType) &&
                DeclarationWireProjection.Framework(nullableType) && nullableType.MetadataName == "System.Nullable`1")
                return Read(
                    reference.Arguments.Single() with
                    {
                        Nullability = "Annotated"
                    },
                    getter,
                    setter);
            var key = JsonSerializer.Serialize(reference) + ":" + getter + ":" + setter;
            if (graph.ids.TryGetValue(key, out var found))
                return found;
            var instantiation = reference.Kind == "named" && reference.Arguments.Length > 0 ? reference.Name : null;
            if (instantiation is not null)
                graph.EnterInstantiation(instantiation);
            try
            {
                return Populate(reference, getter, setter, key);
            }
            finally
            {
                if (instantiation is not null)
                    graph.activeInstantiations[instantiation]--;
            }
        }

        int Populate(DeclarationReference reference, bool? getter, bool? setter, string key)
        {
            var id = graph.nodes.Count;
            graph.ids.Add(key, id);
            graph.nodes.Add(null);
            var nullable = Nullable(reference.Kind, reference.Name, reference.Nullability);
            var properties = new Dictionary<string, int>(StringComparer.Ordinal);
            var node = new Node(nullable, properties, getter, setter);
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
                    else if (type.MetadataName.StartsWith("System.Collections.Generic.", StringComparison.Ordinal) ||
                             type.MetadataName == "System.Collections.Immutable.ImmutableArray`1")
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
                {
                    var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in type.Members.Where(member => member.Kind is "property" or "field" &&
                                 member.Type is not null &&
                                 !member.Modifiers.Contains("static", StringComparer.Ordinal)))
                    {
                        var attributes = DeclarationAttributeProjection.MemberWire(member);
                        if (Ignored(attributes))
                            continue;
                        var included = attributes.Any(attribute =>
                            attribute.Name == DeclarationAttributeProjection.JsonPrefix + "JsonIncludeAttribute");
                        if (member.Kind == "field"
                                ? !included && (!includeFields || member.Accessibility != "Public")
                                : !included && member.Getter != "Public" && member.Setter != "Public")
                            continue;
                        var name = DeclarationWireProjection.MemberName(
                            member.Name,
                            DeclarationAttributeProjection.Native(
                                member
                                    .Attributes.Where(attribute => attribute.Name ==
                                        DeclarationAttributeProjection.JsonPrefix + "JsonPropertyNameAttribute")
                                    .ToArray()),
                            naming);
                        var value = TypeSubstitution.Apply(member.Type!, type, reference.Arguments, types);
                        if (attributes.Any(attribute =>
                                attribute.Name == "System.Diagnostics.CodeAnalysis.AllowNullAttribute"))
                            value = value with
                            {
                                Nullability = "Annotated"
                            };
                        if (attributes.Any(attribute =>
                                attribute.Name == "System.Diagnostics.CodeAnalysis.DisallowNullAttribute"))
                            value = value with
                            {
                                Nullability = "NotAnnotated"
                            };
                        var defaultTypeSupported = member.Type!.Kind != "named" ||
                            !types.TryGetValue(member.Type.Name, out var defaultType) ||
                            !defaultType.Attributes.Any(attribute => attribute.Name ==
                                DeclarationAttributeProjection.JsonPrefix + "JsonConverterAttribute") ||
                            defaultType.Kind == "enum" && KnownEnum(defaultType);
                        if (defaultTypeSupported &&
                            DeclarationAttributeProjection.Named(options, "Converters") is null &&
                            !member.Required && !attributes.Any(attribute => attribute.Name is
                                DeclarationAttributeProjection.JsonPrefix + "JsonRequiredAttribute" or
                                DeclarationAttributeProjection.JsonPrefix + "JsonConverterAttribute") &&
                            DeclarationDefaultProjection.Json(
                                member.Initializer,
                                (target, argument) =>
                                {
                                    if (!types.TryGetValue(target.Name, out var targetType))
                                        return null;
                                    var targetMember = targetType.Members.FirstOrDefault(value =>
                                        value.Name == argument && value.Kind == "property");
                                    return targetMember is null
                                        ? null
                                        : DeclarationWireProjection.MemberName(
                                            argument,
                                            DeclarationAttributeProjection.MemberWire(targetMember),
                                            naming);
                                }) is { } defaultJson)
                            node.Defaults.Add(name, defaultJson);
                        AddProperty(
                            properties,
                            ambiguous,
                            name,
                            Read(
                                value,
                                DeclarationAttributeProjection.AccessorNullable(attributes, getter: true),
                                DeclarationAttributeProjection.AccessorNullable(attributes, getter: false)));
                    }
                }
            }

            graph.nodes[id] = node;
            return id;
        }
    }

    private static bool KnownEnum(DeclarationType type)
    {
        try
        {
            _ = DeclarationEnumProjection.Project(type);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
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

        int Read(NativeTypeReference reference, bool? getter = null, bool? setter = null)
        {
            if (reference.Kind == "nullable")
                return Read(
                    reference.Arguments.Single() with
                    {
                        Nullability = "Annotated"
                    },
                    getter,
                    setter);
            var key = JsonSerializer.Serialize(reference) + ":" + getter + ":" + setter;
            if (graph.ids.TryGetValue(key, out var found))
                return found;
            var instantiation = reference.Kind == "named" && reference.Arguments.Length > 0 ? reference.Name : null;
            if (instantiation is not null)
                graph.EnterInstantiation(instantiation);
            try
            {
                return Populate(reference, getter, setter, key);
            }
            finally
            {
                if (instantiation is not null)
                    graph.activeInstantiations[instantiation]--;
            }
        }

        int Populate(NativeTypeReference reference, bool? getter, bool? setter, string key)
        {
            var id = graph.nodes.Count;
            graph.ids.Add(key, id);
            graph.nodes.Add(null);
            var properties = new Dictionary<string, int>(StringComparer.Ordinal);
            var node = new Node(
                Nullable(reference.Kind, reference.Name, reference.Nullability),
                properties,
                getter,
                setter);
            if (reference.Kind is "array" or "collection")
                node = node with
                {
                    Item = Read(reference.Arguments[^1]),
                    ItemKey = reference.Arguments.Length == 2 ? "additionalProperties" : "items"
                };
            else if (reference.Kind == "named" && types.TryGetValue(reference.Name, out var type))
            {
                var bindings = TypeSubstitution.Bind(type, reference);
                var ambiguous = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in type.Members)
                {
                    if (Ignored(member.Attributes))
                        continue;
                    var value = TypeSubstitution.Apply(member.Type, bindings, types);
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
                    if (!member.Required && !member.JsonRequired &&
                        DeclarationDefaultProjection.Json(
                            member.Initializer,
                            (target, argument) =>
                                types.TryGetValue(target.Name, out var targetType)
                                    ? targetType.Members.FirstOrDefault(value => value.Name == argument)?.WireName
                                    : null) is { } defaultJson)
                        node.Defaults.Add(member.WireName, defaultJson);
                    AddProperty(
                        properties,
                        ambiguous,
                        member.WireName,
                        Read(
                            value,
                            DeclarationAttributeProjection.AccessorNullable(member.Attributes, getter: true),
                            DeclarationAttributeProjection.AccessorNullable(member.Attributes, getter: false)));
                }
            }

            graph.nodes[id] = node;
            return id;
        }
    }

    private static bool? Nullable(string kind, string name, string annotation) =>
        kind is "json" or "object" or "dynamic" or "parameter" || name == "System_Object" ? null :
        kind == "nullable" || annotation == "Annotated" ? true :
        annotation == "None" ? null : false;

    private static bool Ignored(NativeAttribute[] attributes)
    {
        var attribute = attributes.FirstOrDefault(value =>
            value.Name == DeclarationAttributeProjection.JsonPrefix + "JsonIgnoreAttribute");
        return attribute is not null &&
            (attribute.NamedArguments.FirstOrDefault(value => value.Name == "Condition")?.Value.Json is null or "1");
    }

    private static void AddProperty(Dictionary<string, int> properties, HashSet<string> ambiguous, string name, int id)
    {
        if (ambiguous.Contains(name))
            return;
        if (!properties.TryAdd(name, id))
        {
            properties.Remove(name);
            ambiguous.Add(name);
        }
    }

    private string Emit(string method) => EmitNullability(method) + EmitDefaults(method);

    private string EmitDefaults(string method)
    {
        var values = nodes
            .SelectMany((node, id) => node!.Defaults.Select(pair => (Id: id, Name: pair.Key, Json: pair.Value)))
            .ToArray();
        var code = new StringBuilder(
            "private static global::Cordis.Composition.TypertInputDefault? " + method + "Defaults => ");
        if (values.Length == 0)
            return code.Append("null;\n").ToString();
        code.Append(method).Append("DefaultCore;\nprivate static class ").Append(method).Append("DefaultValues {\n");
        for (var index = 0;index < values.Length;index++)
            code
                .Append("internal static readonly global::System.Text.Json.JsonElement Value")
                .Append(index)
                .Append(" = ")
                .Append(method)
                .Append("ReadDefault(")
                .Append(JsonSerializer.Serialize(values[index].Json))
                .Append(");\n");
        code
            .Append("private static global::System.Text.Json.JsonElement ")
            .Append(method)
            .Append(
                "ReadDefault(string json) { using var document = global::System.Text.Json.JsonDocument.Parse(json); return document.RootElement.Clone(); }\n}\n")
            .Append("private static global::System.Text.Json.JsonElement? ")
            .Append(method)
            .Append("DefaultCore(global::System.ReadOnlySpan<string> path, string member) => (")
            .Append(method)
            .Append("(path)?.Id, member) switch {\n");
        for (var index = 0;index < values.Length;index++)
            code
                .Append('(')
                .Append(values[index].Id)
                .Append(", ")
                .Append(JsonSerializer.Serialize(values[index].Name))
                .Append(") => ")
                .Append(method)
                .Append("DefaultValues.Value")
                .Append(index)
                .Append(",\n");
        return code.Append("_ => null };\n").ToString();
    }

    private string EmitNullability(string method)
    {
        if (nodes.All(node => node!.Properties.Count == 0 && node.Item is null))
            return "private static global::Cordis.Composition.TypertNullabilityInfo? " + method +
                "(global::System.ReadOnlySpan<string> path) => path.IsEmpty ? new global::Cordis.Composition.TypertNullabilityInfo(0, " +
                (nodes[0]!.Nullable is { } value ? value ? "true" : "false" : "null") + ") : null;\n";
        var code = new StringBuilder(
            "private static global::Cordis.Composition.TypertNullabilityInfo? " + method +
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

        code.Append(
            "default: return null;\n}\n}\nreturn new global::Cordis.Composition.TypertNullabilityInfo(state, state switch {\n");
        for (var id = 0;id < nodes.Count;id++)
            if (nodes[id]!.Nullable is { } nullable)
                code.Append(id).Append(" => ").Append(nullable ? "true" : "false").Append(",\n");
        code.Append("_ => (bool?)null }) { GetterNullable = state switch {\n");
        for (var id = 0;id < nodes.Count;id++)
            if (nodes[id]!.Getter is { } getter)
                code.Append(id).Append(" => ").Append(getter ? "true" : "false").Append(",\n");
        code.Append("_ => (bool?)null }, SetterNullable = state switch {\n");
        for (var id = 0;id < nodes.Count;id++)
            if (nodes[id]!.Setter is { } setter)
                code.Append(id).Append(" => ").Append(setter ? "true" : "false").Append(",\n");
        return code.Append("_ => (bool?)null } };\n}\n").ToString();
    }
}
