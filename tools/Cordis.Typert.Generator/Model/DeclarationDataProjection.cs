namespace Cordis.Typert.Generator.Model;

/// <summary>
/// Projects selected declaration facts into new data carriers. It does not replay source constructors,
/// getters or setters, and does not infer the author's runtime serializer configuration.
/// </summary>
internal sealed class DeclarationDataProjection
{
    private readonly DeclarationModelComposition declarations;
    private readonly Dictionary<string, NativeTypeDeclaration> projected = new(StringComparer.Ordinal);
    private readonly HashSet<string> visiting = new(StringComparer.Ordinal);

    private DeclarationDataProjection(DeclarationModelDocument model, DeclarationModelDocument[] references)
    {
        declarations = new(model, references);
    }

    internal static string Emit(
        DeclarationModelDocument model,
        IEnumerable<string> roots,
        string targetNamespace,
        string context,
        DeclarationModelDocument[]? referenceModels = null)
    {
        var projection = new DeclarationDataProjection(model, referenceModels ?? []);
        var selected = roots
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                var matches = model
                    .Declarations.Where(type => type.MetadataName == name &&
                        model.Roots.Contains(type.Id, StringComparer.Ordinal))
                    .ToArray();
                if (matches.Length != 1)
                    throw Refuse(name, "Data root must uniquely identify an explicitly selected declaration root.");
                var type = matches[0];
                return projection.Reference(new("named", type.Id, "NotAnnotated", []));
            })
            .ToArray();
        if (selected.Length == 0)
            throw Refuse("roots", "At least one data root is required.");
        foreach (var diagnostic in projection.declarations.Diagnostics)
            if (diagnostic.Symbol is null || projection.projected.Keys.Any(id =>
                    diagnostic.Symbol == id || diagnostic.Symbol.StartsWith(id + ".", StringComparison.Ordinal)))
                throw Refuse(
                    diagnostic.Symbol ?? "model",
                    diagnostic.Category + " " + diagnostic.Code + ": " + diagnostic.Message);
        var source = DotnetProjectionEmitter.EmitData(
            projection.projected.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
            selected,
            model.ContractIdentity,
            targetNamespace,
            context);
        return source + string.Concat(
            projection.declarations.ReferenceIdentities.Select(identity =>
                "// Referenced declaration model " + identity + "\n"));
    }

    private NativeTypeReference Reference(DeclarationReference reference, int depth = 0)
    {
        if (depth > 64)
            throw Refuse(reference.Name, "Data reference nesting exceeds 64.");
        if (reference.Kind == "primitive")
        {
            if (reference.Name is not ("System_Boolean" or "System_Byte" or "System_SByte" or
                "System_Int16" or "System_UInt16" or "System_Int32" or "System_UInt32" or
                "System_Int64" or "System_UInt64" or "System_Single" or "System_Double" or
                "System_Decimal" or "System_Char" or "System_String"))
                throw Refuse(reference.Name, "Unsupported data primitive.");
            return new("primitive", reference.Name, reference.Nullability, []);
        }

        if (reference.Kind == "array" && reference.Rank == 1 && reference.Arguments.Length == 1)
            return new("array", "", reference.Nullability, [Reference(reference.Arguments[0], depth + 1)], 1);
        if (reference.Kind != "named" || reference.ContainingType is not null)
            throw Refuse(reference.Name, "Unsupported or unresolved data reference.");
        var type = declarations.Resolve(reference.Name);
        if (type.Provenance != "source")
        {
            if (DeclarationWireProjection.FrameworkReference(type, reference, value => Reference(value, depth + 1)) is { } framework &&
                (framework.Kind != "collection" || framework.Name is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IReadOnlyList<T>"))
                return framework;
            throw Refuse(
                type.Id,
                "External data declarations require referenced-model composition; only the declared framework data types are supported.");
        }

        if (reference.Arguments.Length != 0)
            throw Refuse(type.Id, "Generic data declarations require a further projection.");
        if (!projected.ContainsKey(type.Id) && visiting.Add(type.Id))
        {
            if (projected.Count + visiting.Count > 256)
                throw Refuse(type.Id, "Reachable data definitions exceed 256.");
            Project(type, depth + 1);
            visiting.Remove(type.Id);
        }

        return new("named", type.Id, reference.Nullability, []);
    }

    private void Project(DeclarationType type, int depth)
    {
        if (type.Provenance != "source" || type.Kind != "class" || type.Accessibility != "Public" ||
            type.ContainingType is not null || type.TypeParameters.Length != 0 || type.Interfaces.Length != 0 ||
            type.Modifiers.Any(value => value is not ("sealed" or "partial")) ||
            type.BaseType is not { Kind: "primitive", Name: "System_Object" })
            throw Refuse(
                type.Id,
                "Data projection requires a public top-level non-generic class without inheritance or interfaces.");
        var members = new List<NativeMember>();
        foreach (var member in type.Members.Where(member => member.Accessibility is "Public" or "Protected" or "ProtectedOrInternal"))
        {
            if (member.Kind == "Constructor" && member.Accessibility == "Public" &&
                member.Parameters.Length == 0 && member.Attributes.Length == 0)
                continue;
            if (member.Kind != "property" || member.Accessibility != "Public" ||
                member.Getter != "Public" || member.Setter != "Public" || member.Type is null ||
                member.Parameters.Length != 0 || member.Modifiers.Length != 0 || member.ReturnRefKind != "none")
                throw Refuse(
                    member.Id,
                    "Only public readable properties with public set/init are supported; methods, fields, events and parameterized constructors are not data carriers.");
            if (member.ReturnAttributes.Length != 0 || member.GetterAttributes.Length != 0 ||
                member.SetterAttributes.Length != 0 || member.SetterValueAttributes.Length != 0)
                throw Refuse(member.Id, "Accessor-targeted attributes require a separately supported data projection.");
            var attributes = DeclarationAttributeProjection.Native(member.Attributes);
            ValidateAttributes(attributes, false, member.Id);
            if (member.Initializer is { Kind: not "value" })
                throw Refuse(
                    member.Id,
                    "Only constant data initializers are supported; source expressions are not executed or copied.");
            if (member.Initializer is { Json: not "null" } initializer && !ConstantType(initializer.Type))
                throw Refuse(member.Id, "Unsupported data initializer type " + initializer.Type + ".");
            members.Add(DeclarationWireProjection.Member(member, value => Reference(value, depth), attributes, "0", member.Initializer, null));
        }

        if (members.Select(member => member.WireName).Distinct(StringComparer.Ordinal).Count() != members.Count)
            throw Refuse(type.Id, "JSON property names collide.");
        var typeAttributes = DeclarationAttributeProjection.Native(type.Attributes);
        ValidateAttributes(typeAttributes, true, type.Id);
        projected.Add(
            type.Id,
            new(
                type.Id,
                type.Name,
                "",
                "class",
                false,
                null,
                [],
                members.ToArray(),
                [],
                typeAttributes,
                type.Documentation,
                type.Location));
    }

    private static bool ConstantType(string type) => type.TrimEnd('?') is
        "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or
        "float" or "double" or "decimal" or "char" or "string" or
        "System.Boolean" or "System.Byte" or "System.SByte" or "System.Int16" or "System.UInt16" or
        "System.Int32" or "System.UInt32" or "System.Int64" or "System.UInt64" or "System.Single" or
        "System.Double" or "System.Decimal" or "System.Char" or "System.String";

    private static void ValidateAttributes(NativeAttribute[] attributes, bool type, string origin)
    {
        foreach (var attribute in attributes)
        {
            var supported = type
                ? attribute.Name is "System.Text.Json.Serialization.JsonUnmappedMemberHandlingAttribute" or
                    "System.Text.Json.Serialization.JsonNumberHandlingAttribute"
                : attribute.Name is "System.Text.Json.Serialization.JsonPropertyNameAttribute" or
                    "System.Text.Json.Serialization.JsonIgnoreAttribute" or
                    "System.Text.Json.Serialization.JsonRequiredAttribute" or
                    "System.Text.Json.Serialization.JsonPropertyOrderAttribute" or
                    "System.Text.Json.Serialization.JsonNumberHandlingAttribute" or
                    "System.Text.Json.Serialization.JsonIncludeAttribute" or
                    "System.Diagnostics.CodeAnalysis.AllowNullAttribute" or
                    "System.Diagnostics.CodeAnalysis.DisallowNullAttribute" or
                    "System.Diagnostics.CodeAnalysis.MaybeNullAttribute" or
                    "System.Diagnostics.CodeAnalysis.NotNullAttribute";
            if (!supported)
                throw Refuse(origin, "Attribute requires an explicitly supported data projection: " + attribute.Name);
        }
    }

    private static InvalidDataException Refuse(string origin, string message) =>
        new(
            "DataProjectionUnsupported " + origin + ": " + message +
            " Data carriers do not replay authored constructor/getter/setter behavior.");
}
