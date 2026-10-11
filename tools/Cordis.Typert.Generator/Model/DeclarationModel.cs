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

internal sealed record DeclarationParameter(
    string Name,
    DeclarationReference Type,
    string RefKind,
    bool This,
    string Scoped,
    bool Optional,
    NativeConstant? Default,
    bool Params,
    NativeAttribute[] Attributes);

internal sealed record DeclarationMember(
    string Id,
    string Name,
    string Kind,
    string Accessibility,
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
    NativeAttribute[] Attributes,
    NativeAttribute[] ReturnAttributes,
    NativeAttribute[] GetterAttributes,
    NativeAttribute[] SetterAttributes,
    NativeAttribute[] SetterValueAttributes,
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
    NativeAttribute[] Attributes,
    string Documentation,
    NativeLocation? Location);

internal sealed record DeclarationModelDocument(
    int FormatVersion,
    string ContractIdentity,
    NativeCompilation Compilation,
    string[] Roots,
    DeclarationType[] Declarations,
    NativeDiagnostic[] Diagnostics);

internal static class DeclarationModelJson
{
    internal const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
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
        var model = JsonSerializer.Deserialize<DeclarationModelDocument>(json, Options) ??
            throw new InvalidDataException("Declaration model is null.");
        if (model.FormatVersion != CurrentVersion)
            throw new InvalidDataException($"Unsupported declaration model format {model.FormatVersion}.");
        ValidateStructure(model);
        if (model.ContractIdentity != Identity(model))
            throw new InvalidDataException("Declaration model identity does not match its authored facts.");
        return model;
    }

    private static void ValidateStructure(DeclarationModelDocument model)
    {
        if (model.Compilation is null || model.Roots is null || model.Declarations is null || model.Diagnostics is null)
            throw new InvalidDataException("Declaration model settings, roots and collections are required.");
        var ids = model.Declarations.Select(type => type.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != model.Declarations.Length ||
            model.Roots.Distinct(StringComparer.Ordinal).Count() != model.Roots.Length ||
            model.Roots.Any(root => !ids.Contains(root)))
            throw new InvalidDataException("Declaration model identities or roots are invalid.");
        foreach (var declaration in model.Declarations)
        {
            if (declaration.Members is null)
                throw new InvalidDataException($"Declaration '{declaration.MetadataName}' members are required.");
            if (declaration.Members.Select(member => member.Id).Distinct(StringComparer.Ordinal).Count() !=
                declaration.Members.Length)
                throw new InvalidDataException(
                    $"Declaration '{declaration.MetadataName}' contains duplicate member identities.");
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

    private static string Identity(DeclarationModelDocument model)
    {
        var node = JsonSerializer.SerializeToNode(model, Options)!.AsObject();
        node.Remove("contractIdentity");
        RemoveLocations(node);
        return "sha256:" +
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString(Options))));
    }

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
