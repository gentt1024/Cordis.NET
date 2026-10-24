using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Typert.Generator.Model;

internal sealed record DeclarationReference(
    string Kind,
    string Name,
    string Nullability,
    DeclarationReference[] Arguments,
    int Rank = 0,
    string? Owner = null,
    int? Ordinal = null,
    DeclarationReference? ContainingType = null,
    string[]? ElementNames = null);

internal sealed record DeclarationTypeParameter(
    string Name,
    int Ordinal,
    string Variance,
    bool ReferenceType,
    string ReferenceNullability,
    bool ValueType,
    bool Unmanaged,
    bool NotNull,
    bool Constructor,
    bool AllowsRefLikeType,
    DeclarationReference[] Constraints);

internal sealed record DeclarationConstant(
    string Kind,
    string Type,
    string Json,
    DeclarationReference? Reference,
    DeclarationConstant[] Elements);

internal sealed record DeclarationNamedConstant(string Name, DeclarationConstant Value);

internal sealed record DeclarationAttribute(
    string Name,
    DeclarationReference Type,
    DeclarationConstant[] Arguments,
    DeclarationNamedConstant[] NamedArguments);

internal sealed record DeclarationParameter(
    string Name,
    DeclarationReference Type,
    string RefKind,
    bool This,
    string Scoped,
    bool Optional,
    NativeConstant? Default,
    bool Params,
    DeclarationAttribute[] Attributes,
    string Documentation);

internal sealed record DeclarationMember(
    string Id,
    string Name,
    string Kind,
    string Accessibility,
    bool Implicit,
    bool Source,
    string Implementation,
    string[] Modifiers,
    DeclarationReference? Type,
    string ReturnRefKind,
    bool Required,
    string? Getter,
    string? Setter,
    bool InitOnly,
    DeclarationParameter[] Parameters,
    DeclarationTypeParameter[] TypeParameters,
    NativeConstant? Initializer,
    NativeConstant? GetterConstant,
    DeclarationAttribute[] Attributes,
    DeclarationAttribute[] ReturnAttributes,
    DeclarationAttribute[] GetterAttributes,
    DeclarationAttribute[] SetterAttributes,
    DeclarationAttribute[] SetterValueAttributes,
    string Documentation,
    NativeLocation? Location);

internal sealed record DeclarationType(
    string Id,
    string Assembly,
    string MetadataName,
    string Name,
    string Kind,
    string Accessibility,
    string Provenance,
    string? ContainingType,
    string[] Modifiers,
    DeclarationTypeParameter[] TypeParameters,
    DeclarationReference? BaseType,
    DeclarationReference? EnumUnderlyingType,
    DeclarationReference[] Interfaces,
    DeclarationMember[] Members,
    DeclarationAttribute[] Attributes,
    string Documentation,
    NativeLocation? Location);

internal sealed record DeclarationContext(string Assembly, string Key, DeclarationReference WireType);

internal sealed record DeclarationModelDocument(
    string ContractIdentity,
    NativeCompilation Compilation,
    DeclarationContext[] Contexts,
    string[] Roots,
    DeclarationType[] Declarations,
    NativeDiagnostic[] Diagnostics);

internal static class DeclarationModelJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    internal static string Serialize(DeclarationModelDocument model) => JsonSerializer.Serialize(model, Options) + "\n";

    internal static DeclarationModelDocument WithIdentity(DeclarationModelDocument model)
    {
        ValidateStructure(model);
        return model with
        {
            ContractIdentity = Identity(model)
        };
    }

    internal static DeclarationModelDocument Deserialize(string json)
    {
        DeclarationModelDocument model;
        try
        {
            model = JsonSerializer.Deserialize<DeclarationModelDocument>(json, Options) ??
                throw new InvalidDataException("Declaration model is null.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException(
                "Declaration model lacks valid required facts. Re-extract with the current tool. " + error.Message,
                error);
        }

        ValidateStructure(model);
        if (model.ContractIdentity != Identity(model))
            throw new InvalidDataException("Declaration model identity does not match its authored facts.");
        return model;
    }

    private static void ValidateStructure(DeclarationModelDocument model)
    {
        if (model.Compilation is null || model.Contexts is null || model.Roots is null || model.Declarations is null ||
            model.Diagnostics is null)
            throw new InvalidDataException("Declaration model settings, roots and collections are required.");
        var ids = model.Declarations.Select(type => type.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != model.Declarations.Length ||
            model.Roots.Distinct(StringComparer.Ordinal).Count() != model.Roots.Length ||
            model.Roots.Any(root => !ids.Contains(root)))
            throw new InvalidDataException("Declaration model identities or roots are invalid.");
        foreach (var context in model.Contexts)
            Reference(context.WireType);
        foreach (var declaration in model.Declarations)
        {
            if (declaration.Id != declaration.Assembly + ":" + declaration.MetadataName ||
                declaration.Provenance is not ("source" or "metadata-reference" or "metadata-signature"))
                throw new InvalidDataException("Declaration identity or provenance is invalid: " + declaration.Id);
            if (declaration.Members is null)
                throw new InvalidDataException($"Declaration '{declaration.MetadataName}' members are required.");
            if (declaration.Members.Select(member => member.Id).Distinct(StringComparer.Ordinal).Count() !=
                declaration.Members.Length)
                throw new InvalidDataException(
                    $"Declaration '{declaration.MetadataName}' contains duplicate member identities.");
            Reference(declaration.BaseType);
            Reference(declaration.EnumUnderlyingType);
            foreach (var reference in declaration.Interfaces)
                Reference(reference);
            Parameters(declaration.TypeParameters);
            Attributes(declaration.Attributes);
            foreach (var member in declaration.Members)
            {
                Reference(member.Type);
                Initializer(member.Initializer);
                Parameters(member.TypeParameters);
                Attributes(member.Attributes);
                Attributes(member.ReturnAttributes);
                Attributes(member.GetterAttributes);
                Attributes(member.SetterAttributes);
                Attributes(member.SetterValueAttributes);
                foreach (var parameter in member.Parameters)
                {
                    Reference(parameter.Type);
                    Initializer(parameter.Default);
                    Attributes(parameter.Attributes);
                }
            }
        }

        void Reference(DeclarationReference? reference)
        {
            if (reference is null)
                return;
            if (reference.Kind is "named" or "tuple" or "unbound" && !ids.Contains(reference.Name))
                throw new InvalidDataException("Declaration reference has no exact definition: " + reference.Name);
            foreach (var argument in reference.Arguments)
                Reference(argument);
            Reference(reference.ContainingType);
        }

        void Parameters(DeclarationTypeParameter[] parameters)
        {
            foreach (var parameter in parameters)
                foreach (var constraint in parameter.Constraints)
                    Reference(constraint);
        }

        void Initializer(NativeConstant? value)
        {
            if (value is null)
                return;
            if (value.Kind == "construction")
            {
                var construction = value.Construction ??
                    throw new InvalidDataException(
                        "Static construction lacks its declaration facts. Re-extract the model.");
                Reference(construction.Type);
                if (construction.Arguments.Select(argument => argument.Name).Distinct(StringComparer.Ordinal).Count() !=
                    construction.Arguments.Length)
                    throw new InvalidDataException("Static construction has duplicate argument bindings.");
                foreach (var argument in construction.Arguments)
                    Initializer(argument.Value);
            }
            else if (value.Construction is not null)
                throw new InvalidDataException("Only a static construction may carry constructor facts.");
        }

        void Attributes(DeclarationAttribute[] attributes)
        {
            foreach (var attribute in attributes)
            {
                Reference(attribute.Type);
                foreach (var value in attribute.Arguments.Concat(attribute.NamedArguments.Select(value => value.Value)))
                    Constant(value);
            }
        }

        void Constant(DeclarationConstant value)
        {
            if (value.Kind == "type" && value.Json != "null" && value.Reference is null)
                throw new InvalidDataException(
                    "A typeof attribute value lacks its structured declaration reference. Re-extract with the current tool.");
            Reference(value.Reference);
            foreach (var element in value.Elements)
                Constant(element);
        }
    }

    internal static string Catalog(DeclarationModelDocument model)
    {
        var entries = model
            .Declarations.Where(type => type.Provenance == "source")
            .Select(type => new
            {
                type.Id,
                type.MetadataName,
                type.Kind,
                Root = model.Roots.Contains(type.Id, StringComparer.Ordinal),
                type.TypeParameters,
                type.BaseType,
                type.EnumUnderlyingType,
                type.Interfaces,
                type.Members,
                type.Documentation
            });
        return JsonSerializer.Serialize(
            new
            {
                model.ContractIdentity,
                Entries = entries,
                model.Diagnostics
            },
            Options) + "\n";
    }

    internal static JsonNode ContractFacts(DeclarationModelDocument model)
    {
        var node = JsonSerializer.SerializeToNode(model, Options)!.AsObject();
        node.Remove("contractIdentity");
        RemoveLocations(node);
        return node;
    }

    private static string Identity(DeclarationModelDocument model) => "sha256:" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ContractFacts(model).ToJsonString(Options))));

    private static void RemoveLocations(JsonNode? node)
    {
        if (node is JsonObject map)
        {
            map.Remove("location");
            foreach (var child in map.ToArray())
                RemoveLocations(child.Value);
        }
        else if (node is JsonArray list)
            foreach (var child in list)
                RemoveLocations(child);
    }
}
