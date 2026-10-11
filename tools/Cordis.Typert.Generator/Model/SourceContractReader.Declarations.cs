using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cordis.Typert.Generator.Model;

internal sealed partial class SourceContractReader
{
    private readonly Dictionary<string, DeclarationType> declarations = new(StringComparer.Ordinal);

    internal static DeclarationModelDocument ReadDeclarations(
        CSharpCompilation compilation,
        string projectDirectory,
        IEnumerable<string> metadataNames)
    {
        var parseErrors = compilation
            .GetParseDiagnostics()
            .Where(value => value.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (parseErrors.Length > 0)
            throw new InvalidDataException("Ordinary declaration parsing failed: " + DiagnosticSummary(parseErrors));
        var declarationErrors = compilation
            .GetDeclarationDiagnostics()
            .Where(value => value.Severity == DiagnosticSeverity.Error)
            .ToArray();
        var reader = new SourceContractReader(compilation, projectDirectory);
        foreach (var diagnostic in declarationErrors)
            reader.diagnostics.Add(
                new("PreGeneratorDeclaration", diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture)));
        var roots = metadataNames
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                var type = compilation.GetTypeByMetadataName(name);
                if (type is null || !SymbolEqualityComparer.Default.Equals(
                        type.ContainingAssembly,
                        compilation.Assembly))
                    throw new InvalidDataException(
                        $"Model root '{name}' must identify one authored declaration by CLR metadata name.");
                for (var current = type;current is not null;current = current.ContainingType)
                    if (current.DeclaredAccessibility != Accessibility.Public)
                        throw new InvalidDataException(
                            $"Model root '{name}' must be publicly accessible through its containing types.");
                reader.DeclarationReference(type);
                return DeclarationId(type);
            })
            .ToArray();
        if (roots.Length == 0)
            throw new InvalidDataException("At least one ordinary declaration model root is required.");
        var parse = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ??
            CSharpParseOptions.Default;
        var document = new DeclarationModelDocument(
            DeclarationModelJson.CurrentVersion,
            "",
            new(
                compilation.AssemblyName ?? "",
                parse.LanguageVersion.ToString(),
                compilation.Options.NullableContextOptions.ToString(),
                parse.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray(),
                compilation.Options.AllowUnsafe,
                compilation.Options.CheckOverflow,
                compilation.Options.OutputKind.ToString(),
                compilation.Options.Platform.ToString(),
                compilation.Options.OptimizationLevel.ToString()),
            roots,
            reader.declarations.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
            reader
                .diagnostics.OrderBy(value => value.Symbol, StringComparer.Ordinal)
                .ThenBy(value => value.Code, StringComparer.Ordinal)
                .ToArray());
        try
        {
            return DeclarationModelJson.WithIdentity(document);
        }
        catch (InvalidDataException error) when (declarationErrors.Length > 0)
        {
            throw new InvalidDataException(error.Message + " " + DiagnosticSummary(declarationErrors), error);
        }
    }

    private DeclarationReference DeclarationReference(ITypeSymbol type, int depth = 0)
    {
        var nullable = type.NullableAnnotation.ToString();
        if (depth > 64)
            throw new InvalidDataException("Declaration reference nesting exceeds the native analysis depth 64.");
        if (type is IArrayTypeSymbol array)
            return new("array", "", nullable, [DeclarationReference(array.ElementType, depth + 1)], array.Rank);
        if (type is IPointerTypeSymbol pointer)
            return new("pointer", "", nullable, [DeclarationReference(pointer.PointedAtType, depth + 1)]);
        if (type is ITypeParameterSymbol parameter)
            return new(
                "parameter",
                parameter.Name,
                nullable,
                [],
                Owner: SymbolId(parameter.ContainingSymbol),
                Ordinal: parameter.Ordinal);
        if (type.TypeKind == TypeKind.Dynamic)
            return new("dynamic", "dynamic", nullable, []);
        if (type.SpecialType != SpecialType.None && type is not INamedTypeSymbol { IsGenericType: true })
            return new("primitive", type.SpecialType.ToString(), nullable, []);
        if (type is not INamedTypeSymbol named || type.TypeKind == TypeKind.Error)
        {
            Diagnose(
                "AnalysisUnsupported",
                "TYPM201",
                "Unresolved types and function-pointer signatures require a further declaration projection.",
                type.ToDisplayString());
            return new("unsupported", type.ToDisplayString(), nullable, []);
        }

        var definition = named.OriginalDefinition;
        var id = DeclarationId(definition);
        if (!declarations.ContainsKey(id) && visiting.Add(id))
        {
            if (declarations.Count + visiting.Count > 256)
                throw new InvalidDataException("Reachable declaration definitions exceed the native graph size 256.");
            ReadDeclaration(definition, id, depth);
            visiting.Remove(id);
        }

        return new(
            named.IsTupleType ? "tuple" : "named",
            id,
            nullable,
            named.TypeArguments.Select(argument => DeclarationReference(argument, depth + 1)).ToArray(),
            ContainingType: named.ContainingType is null ? null : DeclarationReference(named.ContainingType, depth + 1),
            ElementNames: named.IsTupleType ? named.TupleElements.Select(element => element.Name).ToArray() : null);
    }

    private void ReadDeclaration(INamedTypeSymbol type, string id, int depth)
    {
        var source = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly);
        var parent = source && type.ContainingType is not null
            ? DeclarationReference(type.ContainingType, depth + 1).Name
            : null;
        var parameters = TypeParameters(type.TypeParameters, depth + 1);
        var baseType = source && type.BaseType is not null ? DeclarationReference(type.BaseType, depth + 1) : null;
        var interfaces =
            source ? type.Interfaces.Select(value => DeclarationReference(value, depth + 1)).ToArray() : [];
        var members = source
            ? type
                .GetMembers()
                .Where(PublicDeclarationMember)
                .OrderBy(SymbolId, StringComparer.Ordinal)
                .Select(value => ReadDeclarationMember(value, depth + 1))
                .ToArray()
            : [];
        declarations[id] = new(
            id,
            type.ContainingAssembly.Identity.ToString(),
            MetadataName(type),
            type.Name,
            type.IsRecord
                ? type.TypeKind == TypeKind.Class ? "record" : "recordStruct"
                : type.TypeKind.ToString().ToLowerInvariant(),
            type.DeclaredAccessibility.ToString(),
            source ? "source" : "metadata-reference",
            parent,
            Modifiers(type),
            parameters,
            baseType,
            type.EnumUnderlyingType is null ? null : DeclarationReference(type.EnumUnderlyingType, depth + 1),
            interfaces,
            members,
            DeclarationAttributes(type),
            Docs(type),
            Location(type));
    }

    private DeclarationMember ReadDeclarationMember(ISymbol symbol, int depth)
    {
        var method = symbol as IMethodSymbol;
        var property = symbol as IPropertySymbol;
        var field = symbol as IFieldSymbol;
        var eventSymbol = symbol as IEventSymbol;
        var nested = symbol as INamedTypeSymbol;
        var valueType = property?.Type ?? field?.Type ?? eventSymbol?.Type ?? nested ?? method?.ReturnType;
        var parameters = property?.Parameters ?? method?.Parameters ?? [];
        var kind = symbol switch
        {
            IMethodSymbol value => value.MethodKind.ToString(),
            IPropertySymbol { IsIndexer: true } => "indexer",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
        return new(
            SymbolId(symbol),
            symbol.Name,
            kind,
            symbol.DeclaredAccessibility.ToString(),
            Modifiers(symbol),
            valueType is null ? null : DeclarationReference(valueType, depth),
            method?.ReturnsByRefReadonly == true || property?.ReturnsByRefReadonly == true ? "refReadonly" :
            method?.ReturnsByRef == true || property?.ReturnsByRef == true ? "ref" : "none",
            property?.IsRequired ?? field?.IsRequired ?? false,
            property?.GetMethod?.DeclaredAccessibility.ToString(),
            property?.SetMethod?.DeclaredAccessibility.ToString(),
            property?.SetMethod?.IsInitOnly ?? false,
            parameters
                .Select(parameter => new DeclarationParameter(
                    parameter.Name,
                    DeclarationReference(parameter.Type, depth),
                    parameter.RefKind.ToString(),
                    parameter.IsThis || parameter.DeclaringSyntaxReferences.Any(reference =>
                        reference.GetSyntax() is ParameterSyntax syntax &&
                        syntax.Modifiers.Any(SyntaxKind.ThisKeyword)),
                    parameter.ScopedKind.ToString(),
                    parameter.IsOptional,
                    parameter.HasExplicitDefaultValue ? Constant(parameter.Type, parameter.ExplicitDefaultValue) : null,
                    parameter.IsParams,
                    DeclarationAttributes(parameter)))
                .ToArray(),
            method is null ? [] : TypeParameters(method.TypeParameters, depth),
            DeclarationInitializer(symbol),
            GetterConstant(symbol),
            DeclarationAttributes(symbol),
            DeclarationAttributes((method ?? property?.GetMethod)?.GetReturnTypeAttributes() ?? []),
            DeclarationAttributes(property?.GetMethod),
            DeclarationAttributes(property?.SetMethod),
            DeclarationAttributes(property?.SetMethod?.Parameters.LastOrDefault()),
            Docs(symbol),
            Location(symbol));
    }

    private DeclarationTypeParameter[] TypeParameters(IEnumerable<ITypeParameterSymbol> parameters, int depth) =>
        parameters
            .Select(parameter => new DeclarationTypeParameter(
                parameter.Name,
                parameter.Ordinal,
                parameter.Variance.ToString(),
                parameter.HasReferenceTypeConstraint,
                parameter.ReferenceTypeConstraintNullableAnnotation.ToString(),
                parameter.HasValueTypeConstraint,
                parameter.HasUnmanagedTypeConstraint,
                parameter.HasNotNullConstraint,
                parameter.HasConstructorConstraint,
                parameter.AllowsRefLikeType,
                parameter.ConstraintTypes.Select(type => DeclarationReference(type, depth)).ToArray()))
            .ToArray();

    private NativeConstant? DeclarationInitializer(ISymbol symbol)
    {
        if (symbol is IFieldSymbol { HasConstantValue: true } field)
            return Constant(field.Type, field.ConstantValue);
        var expression = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() switch
        {
            PropertyDeclarationSyntax property => property.Initializer?.Value,
            VariableDeclaratorSyntax variable => variable.Initializer?.Value,
            _ => null
        };
        if (expression is null)
            return null;
        var semantic = compilation.GetSemanticModel(expression.SyntaxTree);
        var constant = semantic.GetConstantValue(expression);
        var type = semantic.GetTypeInfo(expression).ConvertedType ?? semantic.GetTypeInfo(expression).Type;
        return constant.HasValue && type is not null
            ? Constant(type, constant.Value)
            : new(
                "expression",
                type?.ToDisplayString() ?? "",
                JsonSerializer.Serialize(expression.WithoutTrivia().ToString()));
    }

    private static bool PublicDeclarationMember(ISymbol member)
    {
        if (member.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected
            or Accessibility.ProtectedOrInternal))
            return false;
        var methodKind = (member as IMethodSymbol)?.MethodKind;
        if (member.IsImplicitlyDeclared && methodKind is not (MethodKind.Constructor or MethodKind.DelegateInvoke))
            return false;
        return member is IFieldSymbol or IPropertySymbol or IEventSymbol or INamedTypeSymbol ||
            methodKind is MethodKind.Ordinary or MethodKind.Constructor or MethodKind.DelegateInvoke
                or MethodKind.UserDefinedOperator or MethodKind.Conversion;
    }

    private static string DiagnosticSummary(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(
            "; ",
            diagnostics.Select(value => value.Id + ": " + value.GetMessage(CultureInfo.InvariantCulture)));

    private static NativeAttribute[] DeclarationAttributes(ISymbol? symbol) =>
        DeclarationAttributes(symbol?.GetAttributes() ?? []);

    private static NativeAttribute[] DeclarationAttributes(IEnumerable<AttributeData> attributes) => attributes
        .Select(attribute =>
            new NativeAttribute(
                attribute.AttributeClass?.ToDisplayString() ?? "",
                attribute.ConstructorArguments.Select(Constant).ToArray(),
                attribute
                    .NamedArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new NativeNamedConstant(pair.Key, Constant(pair.Value)))
                    .ToArray()))
        .ToArray();

    private static string[] Modifiers(ISymbol symbol)
    {
        var values = new List<string>();
        if (symbol.IsStatic)
            values.Add("static");
        if (symbol is IMethodSymbol { IsExtensionMethod: true })
            values.Add("extension");
        if (symbol.IsAbstract)
            values.Add("abstract");
        if (symbol.IsVirtual)
            values.Add("virtual");
        if (symbol.IsOverride)
            values.Add("override");
        if (symbol.IsSealed)
            values.Add("sealed");
        if (symbol is INamedTypeSymbol { IsReadOnly: true } or IFieldSymbol { IsReadOnly: true })
            values.Add("readonly");
        if (symbol is INamedTypeSymbol { IsRefLikeType: true })
            values.Add("refLike");
        if (symbol is IFieldSymbol { IsConst: true })
            values.Add("const");
        return values.ToArray();
    }

    private static string MetadataName(INamedTypeSymbol type) => type.ContainingType is not null
        ? MetadataName(type.ContainingType) + "+" + type.MetadataName
        : (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") +
        type.MetadataName;

    private static string DeclarationId(INamedTypeSymbol type) =>
        type.ContainingAssembly.Identity + ":" + MetadataName(type.OriginalDefinition);

    private static string SymbolId(ISymbol symbol) => symbol is INamedTypeSymbol type
        ? DeclarationId(type)
        : symbol.ContainingAssembly.Identity + ":" + (symbol.GetDocumentationCommentId() ??
            symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
}
