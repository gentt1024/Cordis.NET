using System.Text.Json;

namespace Cordis.Typert.Generator.Model;

internal static class DeclarationWireProjection
{
    // An authored member name is a declaration fact; its serializer's naming policy is a projection input.
    internal static string MemberName(string name, NativeAttribute[] attributes, string namingPolicy = "0")
    {
        var explicitName = attributes.FirstOrDefault(attribute =>
            attribute.Name == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
        if (explicitName is not null)
            return JsonSerializer.Deserialize<string>(explicitName.Arguments.Single().Json) ??
                throw new InvalidDataException("JSON property name cannot be null.");
        return namingPolicy switch
        {
            "0" => name,
            "1" => JsonNamingPolicy.CamelCase.ConvertName(name),
            "2" => JsonNamingPolicy.SnakeCaseLower.ConvertName(name),
            "3" => JsonNamingPolicy.SnakeCaseUpper.ConvertName(name),
            "4" => JsonNamingPolicy.KebabCaseLower.ConvertName(name),
            "5" => JsonNamingPolicy.KebabCaseUpper.ConvertName(name),
            _ => throw new InvalidDataException("Unsupported source JSON naming policy value " + namingPolicy)
        };
    }
}
