using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cordis.Typert.Generator.Model;

internal sealed partial class SourceContractReader
{
    private readonly Dictionary<string, DeclarationType> declarations = new(StringComparer.Ordinal);
    private readonly HashSet<string> declarationVisiting = new(StringComparer.Ordinal);
    private readonly Dictionary<string, INamedTypeSymbol> declarationSymbols = new(StringComparer.Ordinal);
    private readonly Dictionary<ISymbol, DeclarationMember> memberFacts = new(SymbolEqualityComparer.Default);
    private readonly List<NativeDiagnostic> declarationDiagnostics = [];

    internal static DeclarationModelDocument ReadDeclarations(
        CSharpCompilation compilation,
        string projectDirectory,
        IEnumerable<string> metadataNames,
        bool allowEmpty = false,
        IReadOnlyDictionary<string, NativeXmlDocumentation>? referenceDocumentation = null)
    {
        foreach (var tree in compilation
                     .SyntaxTrees.Where(tree => tree.Options.DocumentationMode == DocumentationMode.None)
                     .ToArray())
            compilation = compilation.ReplaceSyntaxTree(
                tree,
                CSharpSyntaxTree.ParseText(
                    tree.GetText(),
                    ((CSharpParseOptions)tree.Options).WithDocumentationMode(DocumentationMode.Parse),
                    tree.FilePath));
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
        var reader = new SourceContractReader(compilation, projectDirectory, referenceDocumentation);
        foreach (var diagnostic in declarationErrors)
            reader.diagnostics.Add(
                new(
                    "PreGeneratorDeclaration",
                    diagnostic.Id,
                    diagnostic.GetMessage(CultureInfo.InvariantCulture),
                    reader.DiagnosticOwner(diagnostic)));
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
                    if (current.DeclaredAccessibility != Accessibility.Public &&
                        Attribute(type, "Cordis.Composition.RemoteServiceAttribute") is null)
                        throw new InvalidDataException(
                            $"Model root '{name}' must be publicly accessible through its containing types.");
                reader.DeclarationReference(type);
                return DeclarationId(type);
            })
            .ToArray();
        if (roots.Length == 0 && !allowEmpty)
            throw new InvalidDataException("At least one ordinary declaration model root is required.");
        var contexts = compilation
            .SourceModule.ReferencedAssemblySymbols.Prepend(compilation.Assembly)
            .SelectMany(assembly => assembly
                .GetAttributes()
                .Where(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == "Cordis.Composition.TypertContextAttribute")
                .Select(attribute =>
                {
                    if (attribute.ConstructorArguments.Length != 2 ||
                        attribute.ConstructorArguments[0].Value is not string key || string.IsNullOrWhiteSpace(key) ||
                        attribute.ConstructorArguments[1].Value is not ITypeSymbol wireType)
                        throw new InvalidDataException(
                            "TypertContext requires a non-empty key and an explicit wire type.");
                    return new DeclarationContext(
                        assembly.Identity.ToString(),
                        key,
                        reader.DeclarationReference(wireType));
                }))
            .OrderBy(context => context.Key, StringComparer.Ordinal)
            .ThenBy(context => context.Assembly, StringComparer.Ordinal)
            .ToArray();
        reader.CompleteMetadataSignatures();
        var parse = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ??
            CSharpParseOptions.Default;
        var document = new DeclarationModelDocument(
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
            contexts,
            roots,
            reader.declarations.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
            reader
                .diagnostics.Concat(reader.declarationDiagnostics)
                .OrderBy(value => value.Symbol, StringComparer.Ordinal)
                .ThenBy(value => value.Code, StringComparer.Ordinal)
                .ThenBy(value => value.Category, StringComparer.Ordinal)
                .ThenBy(value => value.Message, StringComparer.Ordinal)
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
        => ReadDeclarationReference(type, depth);

    private DeclarationReference ReadDeclarationReference(ITypeSymbol type, int depth)
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
            declarationDiagnostics.Add(
                new(
                    "AnalysisUnsupported",
                    "TYPM201",
                    "Unresolved types and function-pointer signatures require a further declaration projection.",
                    type.ToDisplayString()));
            return new("unsupported", type.ToDisplayString(), nullable, []);
        }

        var definition = named.OriginalDefinition;
        var id = DeclarationId(definition);
        declarationSymbols[id] = definition;
        if (!declarations.ContainsKey(id) && declarationVisiting.Add(id))
        {
            if (declarations.Count + declarationVisiting.Count > 256)
                throw new InvalidDataException("Reachable declaration definitions exceed the native graph size 256.");
            ReadDeclaration(definition, id, depth);
            declarationVisiting.Remove(id);
        }

        return new(
            named.IsUnboundGenericType ? "unbound" : named.IsTupleType ? "tuple" : "named",
            id,
            nullable,
            (named.IsUnboundGenericType ? definition.TypeParameters.Cast<ITypeSymbol>() : named.TypeArguments)
            .Select(argument => DeclarationReference(argument, depth + 1))
            .ToArray(),
            ContainingType: named.ContainingType is null ? null : DeclarationReference(named.ContainingType, depth + 1),
            ElementNames: named.IsTupleType ? named.TupleElements.Select(element => element.Name).ToArray() : null);
    }

    private void ReadDeclaration(INamedTypeSymbol type, string id, int depth, bool metadataSignatures = false)
    {
        var source = SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, compilation.Assembly);
        if (source && type
                .GetMembers()
                .Any(member => !member.IsStatic &&
                    member is IPropertySymbol { IsIndexer: false } or IFieldSymbol &&
                    !PublicDeclarationMember(member) &&
                    Attribute(member, JsonPrefix + "JsonIncludeAttribute") is not null))
            declarationDiagnostics.Add(
                new(
                    "ProjectionUnsupported",
                    "TYPM202",
                    "Non-public explicitly included JSON data requires a separately supported declaration projection.",
                    id));
        var signatures = source || metadataSignatures;
        var parent = signatures && type.ContainingType is not null
            ? DeclarationReference(type.ContainingType, depth + 1).Name
            : null;
        var parameters = TypeParameters(type.TypeParameters, depth + 1);
        var baseType = signatures && type.BaseType is not null ? DeclarationReference(type.BaseType, depth + 1) : null;
        var interfaces =
            signatures ? type.Interfaces.Select(value => DeclarationReference(value, depth + 1)).ToArray() : [];
        var members = signatures
            ? type
                .GetMembers()
                .Where(member => source ? RetainedDeclarationMember(member) : MetadataDataMember(member))
                .OrderBy(member => source && type.TypeKind == TypeKind.Enum
                    ? member.Locations.FirstOrDefault(location => location.IsInSource)?.SourceSpan.Start ?? int.MaxValue
                    : 0)
                .ThenBy(SymbolId, StringComparer.Ordinal)
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
            source ? "source" : metadataSignatures ? "metadata-signature" : "metadata-reference",
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
        if (memberFacts.TryGetValue(symbol, out var facts))
            return facts;
        facts = ExtractDeclarationMember(symbol, depth);
        memberFacts[symbol] = facts;
        return facts;
    }

    private DeclarationMember ExtractDeclarationMember(ISymbol symbol, int depth)
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
            symbol.IsImplicitlyDeclared,
            symbol.DeclaringSyntaxReferences.Length > 0,
            Implementation(symbol),
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
                    DeclarationAttributes(parameter),
                    Docs(parameter)))
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
        if (semantic.GetOperation(expression) is Microsoft.CodeAnalysis.Operations.IDefaultValueOperation
            {
                Type: INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            })
            return Constant(nullable, null);
        return constant.HasValue && type is not null
            ? Constant(type, constant.Value)
            : PassiveValue(semantic.GetOperation(expression), 0) ?? new(
                "expression",
                type?.ToDisplayString() ?? "",
                JsonSerializer.Serialize(expression.WithoutTrivia().ToString()));
    }

    private NativeConstant? PassiveValue(IOperation? operation, int depth)
    {
        if (operation is null || depth > 16)
            return null;
        if (operation.ConstantValue.HasValue && operation.Type is { } constantType)
            return Constant(constantType, operation.ConstantValue.Value);
        if (operation is Microsoft.CodeAnalysis.Operations.IConversionOperation conversion)
            return conversion.OperatorMethod is null ? PassiveValue(conversion.Operand, depth + 1) : null;
        if (operation is Microsoft.CodeAnalysis.Operations.ICollectionExpressionOperation { Elements.Length: 0 } &&
            operation.Type is { } collection && EmptyCollection(collection) is { } empty)
            return empty;
        if (operation is not Microsoft.CodeAnalysis.Operations.IObjectCreationOperation
            { Constructor: { } constructor, Initializer: null } creation || !PassiveConstructor(constructor))
            return null;
        var arguments = new List<NativeNamedConstant>();
        foreach (var parameter in constructor.Parameters)
        {
            var argument = creation.Arguments.SingleOrDefault(value => value.Parameter?.Ordinal == parameter.Ordinal);
            if (argument is null || PassiveValue(argument.Value, depth + 1) is not { } value)
                return null;
            arguments.Add(new(parameter.Name, value));
        }

        return new("construction", DeclarationId(constructor.ContainingType), "null")
        {
            Construction = new(DeclarationReference(constructor.ContainingType), arguments.ToArray())
        };
    }

    private static NativeConstant? EmptyCollection(ITypeSymbol type)
    {
        var json = type switch
        {
            IArrayTypeSymbol { Rank: 1, ElementType.SpecialType: SpecialType.System_Byte } => "\"\"",
            IArrayTypeSymbol { Rank: 1 } => "[]",
            INamedTypeSymbol named when named.ContainingAssembly.Identity.ToString().EndsWith(
                ", Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", StringComparison.Ordinal) =>
                named.OriginalDefinition.ToDisplayString() switch
                {
                    "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>" or
                    "System.Collections.Generic.ICollection<T>" or "System.Collections.Generic.IEnumerable<T>" or
                    "System.Collections.Generic.IReadOnlyList<T>" or "System.Collections.Generic.IReadOnlyCollection<T>" or
                    "System.Collections.Immutable.ImmutableArray<T>" => "[]",
                    "System.Collections.Generic.Dictionary<TKey, TValue>" or
                    "System.Collections.Generic.IDictionary<TKey, TValue>" or
                    "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" when
                        named.TypeArguments[0].SpecialType == SpecialType.System_String => "{}",
                    _ => null
                },
            _ => null
        };
        return json is null ? null : new("emptyCollection", type.ToDisplayString(), "[]");
    }

    private static bool PassiveConstructor(IMethodSymbol constructor)
    {
        var type = constructor.ContainingType;
        if (!type.IsRecord || type.TypeKind != TypeKind.Class || type.TypeParameters.Length != 0 ||
            type.ContainingType is not null || type.BaseType?.SpecialType != SpecialType.System_Object ||
            type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() is
                JsonPrefix + "JsonConverterAttribute" or JsonPrefix + "JsonPolymorphicAttribute" or JsonPrefix + "JsonDerivedTypeAttribute") ||
            constructor.DeclaredAccessibility != Accessibility.Public ||
            constructor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not RecordDeclarationSyntax
            { ParameterList: not null, BaseList: null })
            return false;
        foreach (var member in type.GetMembers().Where(member => !member.IsImplicitlyDeclared))
        {
            if (member is IFieldSymbol { IsConst: true })
                continue;
            if (member is IMethodSymbol method)
            {
                if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet ||
                    SymbolEqualityComparer.Default.Equals(method, constructor))
                    continue;
                return false;
            }

            if (member is not IPropertySymbol { IsStatic: false, DeclaredAccessibility: Accessibility.Public } property)
                return false;
            if (property.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() is
                    JsonPrefix + "JsonConverterAttribute" or JsonPrefix + "JsonExtensionDataAttribute") ||
                property.Type.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == JsonPrefix + "JsonConverterAttribute"))
                return false;
            var syntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
            if (syntax is ParameterSyntax)
            {
                if (property.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == JsonPrefix + "JsonIgnoreAttribute"))
                    return false;
                if (!constructor.Parameters.Any(parameter => parameter.Name == property.Name &&
                        SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type)))
                    return false;
            }
            else if (syntax is not PropertyDeclarationSyntax
            { Initializer: null, ExpressionBody: null, AccessorList: { } accessors } ||
                accessors.Accessors.Any(accessor => accessor.Body is not null || accessor.ExpressionBody is not null) ||
                property.IsRequired || property.GetAttributes().Any(attribute =>
                    attribute.AttributeClass?.ToDisplayString() == JsonPrefix + "JsonRequiredAttribute"))
                return false;
        }

        return constructor.Parameters.All(parameter => parameter.RefKind == RefKind.None &&
            type.GetMembers(parameter.Name).OfType<IPropertySymbol>().Any(property =>
                property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is ParameterSyntax));
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

    private string? DiagnosticOwner(Diagnostic diagnostic)
    {
        if (diagnostic.Location.SourceTree is not { } tree)
            return null;
        var declaration = tree
            .GetRoot()
            .FindNode(diagnostic.Location.SourceSpan)
            .AncestorsAndSelf()
            .OfType<BaseTypeDeclarationSyntax>()
            .FirstOrDefault();
        return declaration is not null && compilation.GetSemanticModel(tree).GetDeclaredSymbol(declaration) is { } type
            ? DeclarationId(type)
            : null;
    }

    private DeclarationAttribute[] DeclarationAttributes(ISymbol? symbol) =>
        DeclarationAttributes(symbol?.GetAttributes() ?? []);

    private DeclarationAttribute[] DeclarationAttributes(IEnumerable<AttributeData> attributes) => attributes
        .Select(attribute =>
            new DeclarationAttribute(
                attribute.AttributeClass?.ToDisplayString() ?? "",
                attribute.AttributeClass is { } attributeType
                    ? DeclarationReference(attributeType)
                    : new("unsupported", "unresolved attribute", "None", []),
                attribute.ConstructorArguments.Select(DeclarationConstant).ToArray(),
                attribute
                    .NamedArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new DeclarationNamedConstant(pair.Key, DeclarationConstant(pair.Value)))
                    .ToArray()))
        .ToArray();

    private DeclarationConstant DeclarationConstant(TypedConstant value)
    {
        var constant = Constant(value);
        return new(
            constant.Kind,
            constant.Type,
            constant.Json,
            value.Kind == TypedConstantKind.Type && value.Value is ITypeSymbol type ? DeclarationReference(type) : null,
            value.Kind == TypedConstantKind.Array && !value.IsNull
                ? value.Values.Select(DeclarationConstant).ToArray()
                : []);
    }

    private static bool RetainedDeclarationMember(ISymbol member) => PublicDeclarationMember(member) ||
        member is IMethodSymbol { MethodKind: MethodKind.Constructor } ||
        member is IMethodSymbol && Attribute(member, "Cordis.Composition.RemoteMethodAttribute") is not null ||
        member is IPropertySymbol or IFieldSymbol &&
        Attribute(member, JsonPrefix + "JsonIncludeAttribute") is not null ||
        member.DeclaringSyntaxReferences.Length > 0 &&
        member.Name is "Default" or "GetTypeInfo" or "GeneratedSerializerOptions" &&
        IsJsonContext(member.ContainingType);

    private static string Implementation(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
            return "implicit";
        if (symbol.DeclaringSyntaxReferences.Length == 0)
            return "metadata";
        if (symbol is not IMethodSymbol method)
            return "declaration";
        var implementation = method.PartialImplementationPart ?? method;
        return implementation.DeclaringSyntaxReferences.Any(reference =>
            reference.GetSyntax() is BaseMethodDeclarationSyntax
            {
                Body: not null
            } or BaseMethodDeclarationSyntax { ExpressionBody: not null })
            ? "body"
            : "declaration";
    }

    private static bool IsJsonContext(INamedTypeSymbol? type) =>
        type?.BaseType?.ToDisplayString() == "System.Text.Json.Serialization.JsonSerializerContext";

    private static bool MetadataDataMember(ISymbol member) =>
        member is IMethodSymbol { MethodKind: MethodKind.Constructor } ||
        !member.IsStatic && (member.DeclaredAccessibility == Accessibility.Public ||
            Attribute(member, JsonPrefix + "JsonIncludeAttribute") is not null) &&
        member is IPropertySymbol { IsIndexer: false } or IFieldSymbol { IsImplicitlyDeclared: false };

    // Every retained non-framework reference receives the same signature view, irrespective of
    // which selected root reached it first. Metadata never supplies source initializer/body facts.
    private void CompleteMetadataSignatures()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var pending = declarations
                .Values.Where(type => type.Provenance == "metadata-reference" &&
                    !visited.Contains(type.Id))
                .OrderBy(type => type.Id, StringComparer.Ordinal)
                .ToArray();
            if (pending.Length == 0)
                return;
            foreach (var type in pending)
            {
                visited.Add(type.Id);
                var symbol = declarationSymbols[type.Id];
                var token = symbol.ContainingAssembly.Identity.PublicKeyToken;
                if (symbol.ContainingAssembly.Name is "mscorlib" or "netstandard" ||
                    symbol.ContainingAssembly.Name.StartsWith("System", StringComparison.Ordinal) && !token.IsEmpty)
                    continue;
                ReadDeclaration(symbol, type.Id, 0, true);
            }
        }
    }

    private static string[] Modifiers(ISymbol symbol)
    {
        var values = new List<string>();
        if (symbol is INamedTypeSymbol && symbol.DeclaringSyntaxReferences.Length > 0 &&
            symbol.DeclaringSyntaxReferences.All(reference =>
                reference.GetSyntax() is TypeDeclarationSyntax declaration &&
                declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
            values.Add("partial");
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
