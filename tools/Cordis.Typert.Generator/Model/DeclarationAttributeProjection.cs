using System.Text.Json;

namespace Cordis.Typert.Generator.Model;

internal static class DeclarationAttributeProjection
{
    internal const string JsonPrefix = "System.Text.Json.Serialization.";

    internal static NativeAttribute[] MemberWire(DeclarationMember member) => Wire(
        member
            .Attributes.Concat(
                member.ReturnAttributes.Where(value => value.Name is
                    "System.Diagnostics.CodeAnalysis.MaybeNullAttribute"
                    or "System.Diagnostics.CodeAnalysis.NotNullAttribute"))
            .Concat(
                member.SetterValueAttributes.Where(value => value.Name is
                    "System.Diagnostics.CodeAnalysis.AllowNullAttribute"
                    or "System.Diagnostics.CodeAnalysis.DisallowNullAttribute"))
            .DistinctBy(value => value.Name)
            .ToArray());

    internal static bool? AccessorNullable(NativeAttribute[] attributes, bool getter)
    {
        var allow = "System.Diagnostics.CodeAnalysis." + (getter ? "MaybeNullAttribute" : "AllowNullAttribute");
        var deny = "System.Diagnostics.CodeAnalysis." + (getter ? "NotNullAttribute" : "DisallowNullAttribute");
        return attributes.Any(value => value.Name == allow) ? true :
            attributes.Any(value => value.Name == deny) ? false : null;
    }

    internal static NativeAttribute[] Native(DeclarationAttribute[] attributes) => attributes
        .Select(Checked)
        .Select(attribute =>
            new NativeAttribute(
                attribute.Name,
                attribute.Arguments.Select(Constant).ToArray(),
                attribute
                    .NamedArguments.Select(value => new NativeNamedConstant(value.Name, Constant(value.Value)))
                    .ToArray()))
        .ToArray();

    internal static NativeAttribute[] Wire(DeclarationAttribute[] attributes) => Native(
        attributes
            .Where(value =>
                value.Name.StartsWith(JsonPrefix, StringComparison.Ordinal) || value.Name is
                    "System.Diagnostics.CodeAnalysis.AllowNullAttribute"
                    or "System.Diagnostics.CodeAnalysis.DisallowNullAttribute" or
                    "System.Diagnostics.CodeAnalysis.MaybeNullAttribute"
                    or "System.Diagnostics.CodeAnalysis.NotNullAttribute")
            .ToArray());

    internal static NativeConstant Constant(DeclarationConstant value) => new(
        value.Kind,
        value.Type,
        value.Kind == "type" && value.Reference is { } reference
            ? JsonSerializer.Serialize("global::" + TypeName(reference))
            : value.Kind == "array" && value.Json != "null"
                ? JsonSerializer.Serialize(value.Elements.Select(Constant).ToArray())
                : value.Json);

    internal static string TypeName(DeclarationReference reference)
    {
        if (reference.Kind == "array")
            return TypeName(reference.Arguments[0]) + "[" + new string(',', reference.Rank - 1) + "]";
        if (reference.Kind == "primitive")
            return reference.Name.Replace('_', '.');
        if (reference.Kind == "parameter")
            return reference.Name;
        var metadataName = reference.Name[(reference.Name.IndexOf(':') + 1)..];
        var name = reference.ContainingType is null
            ? metadataName.Replace('+', '.')
            : metadataName[(metadataName.LastIndexOf('+') + 1)..];
        var arity = name.IndexOf('`');
        if (arity >= 0)
            name = name[..arity];
        if (reference.ContainingType is { } containing)
            name = TypeName(containing) + "." + name;
        if (reference.Kind == "unbound")
            return name + (reference.Arguments.Length == 0
                ? ""
                : "<" + new string(',', reference.Arguments.Length - 1) + ">");
        return name + (reference.Arguments.Length == 0
            ? ""
            : "<" + string.Join(", ", reference.Arguments.Select(TypeName)) + ">");
    }

    internal static DeclarationAttribute? Attribute(DeclarationAttribute[] attributes, string name) =>
        attributes.Where(value => value.Name == name).Select(Checked).FirstOrDefault();

    private static DeclarationAttribute Checked(DeclarationAttribute attribute)
    {
        var assembly = attribute.Name.StartsWith(JsonPrefix, StringComparison.Ordinal) ? "System.Text.Json" :
            attribute.Name.StartsWith("Cordis.Composition.Remote", StringComparison.Ordinal) ? "Cordis.Composition" :
            null;
        if (assembly is not null && (attribute.Type.Kind != "named" ||
                !attribute.Type.Name.StartsWith(assembly + ",", StringComparison.Ordinal) ||
                !attribute.Type.Name.EndsWith(":" + attribute.Name, StringComparison.Ordinal)))
            throw new InvalidDataException(
                "Attribute identity does not identify the supported contract attribute: " + attribute.Type.Name);
        return attribute;
    }

    internal static DeclarationConstant? Named(DeclarationAttribute? attribute, string name) =>
        attribute?.NamedArguments.FirstOrDefault(value => value.Name == name)?.Value;

    internal static string? String(DeclarationConstant? value) => value is null || value.Json == "null"
        ? null
        : JsonSerializer.Deserialize<string>(value.Json);

    internal static bool Boolean(DeclarationConstant? value) => value is not null && value.Json == "true";
}
