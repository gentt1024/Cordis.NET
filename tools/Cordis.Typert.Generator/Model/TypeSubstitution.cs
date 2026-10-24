namespace Cordis.Typert.Generator.Model;

internal static class TypeSubstitution
{
    internal static bool ValueType(NativeTypeReference type, IReadOnlyDictionary<string, NativeTypeDeclaration> types) =>
        type.Kind is "guid" or "dateTimeOffset" or "nullable" ||
        type.Kind == "primitive" && type.Name != "System_String" ||
        type.Kind == "collection" && type.Name == "System.Collections.Immutable.ImmutableArray<T>" ||
        type.Kind == "named" && types.TryGetValue(type.Name, out var declaration) &&
        declaration.Kind is "enum" or "struct" or "recordStruct";

    internal static IReadOnlyDictionary<string, NativeTypeReference> Bind(
        NativeTypeDeclaration declaration, NativeTypeReference reference)
    {
        if (declaration.TypeParameters.Length != reference.Arguments.Length)
            throw new InvalidDataException("Closed data reference has the wrong type argument count: " + reference.Name);
        return declaration.TypeParameters.Select((parameter, index) => (parameter.Name, reference.Arguments[index]))
            .ToDictionary(pair => pair.Name, pair => pair.Item2, StringComparer.Ordinal);
    }

    internal static NativeTypeReference Apply(
        NativeTypeReference reference,
        IReadOnlyDictionary<string, NativeTypeReference> bindings,
        IReadOnlyDictionary<string, NativeTypeDeclaration> types)
    {
        if (reference.Kind == "parameter")
        {
            if (!bindings.TryGetValue(reference.Name, out var argument))
                throw new InvalidDataException("Unbound data type parameter: " + reference.Name);
            return reference.Nullability == "Annotated" && !ValueType(argument, types)
                ? argument with
                {
                    Nullability = "Annotated"
                }
                : argument;
        }
        return reference with
        {
            Arguments = reference.Arguments.Select(value => Apply(value, bindings, types)).ToArray()
        };
    }

    internal static DeclarationReference Apply(
        DeclarationReference reference,
        DeclarationType definition,
        DeclarationReference[] arguments,
        IReadOnlyDictionary<string, DeclarationType> types)
    {
        if (definition.TypeParameters.Length != arguments.Length)
            throw new InvalidDataException("Closed declaration reference has the wrong type argument count: " + definition.Id);
        if (reference.Kind == "parameter" && reference.Owner == definition.Id && reference.Ordinal is { } ordinal)
        {
            var argument = arguments[ordinal];
            var valueType = argument.Kind == "primitive" && argument.Name is not ("System_String" or "System_Object") ||
                argument.Kind == "named" && types.TryGetValue(argument.Name, out var type) &&
                type.Kind is "struct" or "recordStruct" or "enum";
            return reference.Nullability == "Annotated" && !valueType
                ? argument with
                {
                    Nullability = "Annotated"
                }
                : argument;
        }
        return reference with
        {
            Arguments = reference.Arguments.Select(value => Apply(value, definition, arguments, types)).ToArray(),
            ContainingType = reference.ContainingType is { } containing
                ? Apply(containing, definition, arguments, types) : null
        };
    }
}
