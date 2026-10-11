using System.Text.Json;

namespace Cordis.Typert.Generator.Model;

internal static class DeclarationWireProjection
{
    internal static NativeMember Member(DeclarationMember facts, Func<DeclarationReference, NativeTypeReference> reference,
        NativeAttribute[] attributes, string namingPolicy, NativeConstant? initializer, NativeConstant? getterConstant) =>
        new(facts.Name, MemberName(facts.Name, attributes, namingPolicy), facts.Kind,
            reference(facts.Type!), facts.Required,
            attributes.Any(attribute => attribute.Name == DeclarationAttributeProjection.JsonPrefix + "JsonRequiredAttribute"),
            facts.Getter == "Public" || facts.Kind == "field",
            facts.Setter == "Public" ? facts.InitOnly ? "init" : "set" :
                facts.Kind == "field" && !facts.Modifiers.Contains("readonly", StringComparer.Ordinal) ? "field" : "none",
            facts.Accessibility, attributes, initializer, getterConstant, facts.Documentation, facts.Location);

    internal static bool Framework(DeclarationType type) =>
        (type.Assembly.StartsWith("System.Runtime, ", StringComparison.Ordinal) ||
            type.Assembly.StartsWith("System.Collections, ", StringComparison.Ordinal)) &&
        type.Assembly.EndsWith(", Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", StringComparison.Ordinal) ||
        type.Assembly.StartsWith("System.Text.Json, ", StringComparison.Ordinal) &&
        type.Assembly.EndsWith(", Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51", StringComparison.Ordinal);

    internal static NativeTypeReference? FrameworkReference(DeclarationType type, DeclarationReference reference,
        Func<DeclarationReference, NativeTypeReference> project)
    {
        if (!Framework(type))
            return null;
        if (type.MetadataName == "System.Text.Json.JsonElement" && reference.Arguments.Length == 0)
            return new("json", "System.Text.Json.JsonElement", reference.Nullability, []);
        if (type.MetadataName == "System.Nullable`1" && reference.Arguments.Length == 1)
            return new("nullable", "System.Nullable<T>", reference.Nullability, [project(reference.Arguments[0])]);
        var name = type.MetadataName switch
        {
            "System.Collections.Generic.List`1" => "System.Collections.Generic.List<T>",
            "System.Collections.Generic.IReadOnlyList`1" => "System.Collections.Generic.IReadOnlyList<T>",
            "System.Collections.Generic.IList`1" => "System.Collections.Generic.IList<T>",
            "System.Collections.Generic.IEnumerable`1" => "System.Collections.Generic.IEnumerable<T>",
            "System.Collections.Generic.ICollection`1" => "System.Collections.Generic.ICollection<T>",
            "System.Collections.Generic.IReadOnlyCollection`1" => "System.Collections.Generic.IReadOnlyCollection<T>",
            "System.Collections.Generic.Dictionary`2" => "System.Collections.Generic.Dictionary<TKey, TValue>",
            "System.Collections.Generic.IReadOnlyDictionary`2" => "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>",
            "System.Collections.Generic.IDictionary`2" => "System.Collections.Generic.IDictionary<TKey, TValue>",
            _ => null
        };
        return name is not null && reference.Arguments.Length == type.TypeParameters.Length
            ? new("collection", name, reference.Nullability, reference.Arguments.Select(project).ToArray()) : null;
    }

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
