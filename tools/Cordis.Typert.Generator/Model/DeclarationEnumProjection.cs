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
        if (type.Attributes.Any(attribute => attribute.Name.StartsWith(
                DeclarationAttributeProjection.JsonPrefix,
                StringComparison.Ordinal)))
            throw new InvalidDataException(
                "ProjectionUnsupported TYPM109: Enum serializer attributes require an explicitly supported projection: " +
                type.Id);
        var members = type
            .Members.Where(member => member.Kind == "field" &&
                member.Modifiers.Contains("const", StringComparer.Ordinal))
            .Select(member =>
            {
                if (member.Initializer is not { Kind: "enum" } constant)
                    throw new InvalidDataException(
                        "ProjectionUnsupported TYPM117: Enum member lacks its numeric constant: " + member.Id);
                return new NativeEnumMember(member.Name, constant.Json, member.Documentation);
            })
            .ToArray();
        var separator = type.MetadataName.LastIndexOf('.');
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
            DeclarationAttributeProjection.Native(
                type
                    .Attributes.Where(attribute =>
                        attribute.Name == "System.FlagsAttribute")
                    .ToArray()),
            type.Documentation,
            type.Location)
        {
            Enumeration = new(new("primitive", underlying.Name, "NotAnnotated", []), members)
        };
    }
}
