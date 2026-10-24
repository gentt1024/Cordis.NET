using System.Text.Json;

namespace Cordis.Typert.Generator.Model;

internal static class DeclarationEnumProjection
{
    internal static NativeTypeDeclaration Project(DeclarationType type)
    {
        if (type.Provenance != "source" || type.Kind != "enum" || type.Accessibility != "Public" ||
            type.ContainingType is not null || type.TypeParameters.Length != 0 ||
            type.EnumUnderlyingType is not
            {
                Kind: "primitive", Name: "System_Byte" or "System_SByte" or
                "System_Int16" or "System_UInt16" or "System_Int32" or "System_UInt32" or
                "System_Int64" or "System_UInt64"
            } underlying)
            throw new InvalidDataException(
                "ProjectionUnsupported TYPM117: Numeric enum projection requires a public top-level source enum with an integral underlying type: " +
                type.Id);
        var converter = DeclarationAttributeProjection.Attribute(
            type.Attributes,
            DeclarationAttributeProjection.JsonPrefix + "JsonConverterAttribute");
        var stringEnum = converter?.Arguments is
                [{ Kind: "type", Reference: { Kind: "named", Arguments: [var target] } factory }] &&
            converter.NamedArguments.Length == 0 &&
            factory.Name.StartsWith("System.Text.Json, ", StringComparison.Ordinal) &&
            factory.Name.EndsWith(
                ", Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51:System.Text.Json.Serialization.JsonStringEnumConverter`1",
                StringComparison.Ordinal) && target.Kind == "named" && target.Name == type.Id;
        if (type.Attributes.Any(attribute => attribute.Name.StartsWith(
                DeclarationAttributeProjection.JsonPrefix,
                StringComparison.Ordinal) && (!stringEnum || !ReferenceEquals(attribute, converter))))
            throw new InvalidDataException(
                "ProjectionUnsupported TYPM109: Enum serializer attributes require an explicitly supported projection: " +
                type.Id);
        var fields = type
            .Members.Where(member => member.Kind == "field" &&
                member.Modifiers.Contains("const", StringComparer.Ordinal))
            .ToArray();
        var members = fields
            .Select(member =>
            {
                if (member.Initializer is not { Kind: "enum" } constant)
                    throw new InvalidDataException(
                        "ProjectionUnsupported TYPM117: Enum member lacks its numeric constant: " + member.Id);
                var attributes = DeclarationAttributeProjection.Wire(member.Attributes);
                if (attributes.Any(attribute => !stringEnum || attribute.Name !=
                        DeclarationAttributeProjection.JsonPrefix + "JsonStringEnumMemberNameAttribute"))
                    throw new InvalidDataException(
                        "ProjectionUnsupported TYPM109: Unsupported enum member serializer attribute: " + member.Id);
                return new NativeEnumMember(member.Name, constant.Json, member.Documentation, attributes);
            })
            .ToArray();
        if (stringEnum &&
            members.GroupBy(member => member.Value, StringComparer.Ordinal).Any(group => group.Count() > 1) &&
            (fields.Any(member => member.Location is null) || !fields.SequenceEqual(
                fields
                    .OrderBy(member => member.Location!.Line)
                    .ThenBy(member => member.Location!.Column))))
            throw new InvalidDataException(
                "ProjectionUnsupported TYPM109: String enum aliases require declaration-ordered fields; re-extract the source model: " +
                type.Id);
        var separator = type.MetadataName.LastIndexOf('.');
        var typeAttributes = DeclarationAttributeProjection.Native(
            type.Attributes.Where(attribute => attribute.Name == "System.FlagsAttribute").ToArray());
        if (stringEnum)
            typeAttributes =
            [
                .. typeAttributes, new(
                    DeclarationAttributeProjection.JsonPrefix + "JsonConverterAttribute",
                    [
                        new(
                            "type",
                            "System.Type",
                            JsonSerializer.Serialize(
                                "global::System.Text.Json.Serialization.JsonStringEnumConverter<@" + type.Name + ">"))
                    ],
                    [])
            ];
        return new(
            type.Id,
            type.Name,
            separator < 0 ? "" : type.MetadataName[..separator],
            "enum",
            false,
            null,
            [],
            [],
            [],
            typeAttributes,
            type.Documentation,
            type.Location)
        {
            Enumeration = new(new("primitive", underlying.Name, "NotAnnotated", []), members)
        };
    }
}
