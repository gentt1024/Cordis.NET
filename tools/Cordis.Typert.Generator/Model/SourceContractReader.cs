using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cordis.Typert.Generator.Model;

internal sealed class SourceContractReader
{
    private const string JsonPrefix = "System.Text.Json.Serialization.";
    private readonly CSharpCompilation compilation;
    private readonly string projectDirectory;
    private readonly Dictionary<string, NativeTypeDeclaration> types = new(StringComparer.Ordinal);
    private readonly List<NativeDiagnostic> diagnostics = [];
    private readonly HashSet<string> visiting = new(StringComparer.Ordinal);

    private readonly Dictionary<IAssemblySymbol, NativeXmlDocumentation?> metadataDocumentation =
        new(SymbolEqualityComparer.Default);

    private string namingPolicy = "0";

    private SourceContractReader(CSharpCompilation compilation, string projectDirectory)
    {
        this.compilation = compilation;
        this.projectDirectory = Path.GetFullPath(projectDirectory);
    }

    internal static NativeModelDocument Read(
        CSharpCompilation compilation,
        string projectDirectory,
        string? service = null)
    {
        var reader = new SourceContractReader(compilation, projectDirectory);
        var roots = compilation
            .SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Select(node => compilation.GetSemanticModel(node.SyntaxTree).GetDeclaredSymbol(node))
            .OfType<INamedTypeSymbol>()
            .Where(type => Attribute(type, "Cordis.Composition.RemoteServiceAttribute") is not null)
            .GroupBy(type => Id(type), StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(type => service is null || type.ToDisplayString() == service ||
                Attribute(type, "Cordis.Composition.RemoteServiceAttribute")!.ConstructorArguments[0].Value as string ==
                service)
            .OrderBy(type => Id(type), StringComparer.Ordinal)
            .ToArray();
        if (service is not null && roots.Length != 1)
            throw new InvalidDataException(
                $"Exactly one authored Remote service must match '{service}'; found {roots.Length}.");
        var services = roots.Select(reader.Service).ToArray();
        var options = compilation.SyntaxTrees.FirstOrDefault()?.Options as CSharpParseOptions ??
            CSharpParseOptions.Default;
        return NativeModelJson.WithIdentity(
            new(
                NativeModelJson.CurrentVersion,
                "",
                new(
                    compilation.AssemblyName ?? "",
                    options.LanguageVersion.ToString(),
                    compilation.Options.NullableContextOptions.ToString(),
                    options.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray(),
                    compilation.Options.AllowUnsafe,
                    compilation.Options.CheckOverflow,
                    compilation.Options.OutputKind.ToString(),
                    compilation.Options.Platform.ToString(),
                    compilation.Options.OptimizationLevel.ToString()),
                services,
                reader.types.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
                reader
                    .diagnostics.OrderBy(value => value.Symbol, StringComparer.Ordinal)
                    .ThenBy(value => value.Code, StringComparer.Ordinal)
                    .ToArray()));
    }

    private NativeService Service(INamedTypeSymbol type)
    {
        var marker = Attribute(type, "Cordis.Composition.RemoteServiceAttribute")!;
        var key = marker.ConstructorArguments.FirstOrDefault().Value as string ?? "";
        var space = Named(marker, "Namespace")?.Value as string ?? key;
        var context = marker.ConstructorArguments.ElementAtOrDefault(1).Value as INamedTypeSymbol;
        NativeSerializerContext? serializer = null;
        if (context is null)
            Diagnose(
                "ProjectionUnsupported",
                "TYPM101",
                "An explicit source JSON context is required for native wire projection.",
                Id(type));
        else
        {
            var attributes = Attributes(context);
            var options = Attribute(context, JsonPrefix + "JsonSourceGenerationOptionsAttribute");
            namingPolicy = Named(options, "PropertyNamingPolicy")?.Value?.ToString() ?? "0";
            if (Named(options, "Converters") is { } converters && converters.Values.Length > 0)
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM102",
                    "Context converter policies are retained but cannot be copied into a standalone caller.",
                    Id(context));
            var authoredMetadata = context
                .GetMembers()
                .Any(member => member.DeclaringSyntaxReferences.Length > 0 &&
                    member.Name is "Default" or "GetTypeInfo" or "GeneratedSerializerOptions");
            if (authoredMetadata)
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM103",
                    "Authored JSON metadata implementations cannot be replayed as source JSON options.",
                    Id(context));
            serializer = new(
                Id(context),
                attributes,
                context
                    .GetAttributes()
                    .Where(value => value.AttributeClass?.ToDisplayString() == JsonPrefix + "JsonSerializableAttribute")
                    .Select(value => value.ConstructorArguments.FirstOrDefault().Value as ITypeSymbol)
                    .OfType<ITypeSymbol>()
                    .Select(value => Type(value))
                    .ToArray(),
                authoredMetadata);
        }

        var methods = type
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Where(method => Attribute(method, "Cordis.Composition.RemoteMethodAttribute") is not null)
            .OrderBy(method => method.Locations.FirstOrDefault()?.SourceSpan.Start ?? 0)
            .Select(Method)
            .ToArray();
        if (methods.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count() != methods.Length)
            Diagnose(
                "AnalysisUnsupported",
                "TYPM001",
                "Duplicate exported method names are not a unique invocation contract.",
                Id(type));
        return new(key, space, type.ToDisplayString(), serializer, methods, Docs(type), Location(type));
    }

    private NativeMethod Method(IMethodSymbol method)
    {
        var marker = Attribute(method, "Cordis.Composition.RemoteMethodAttribute")!;
        var name = Named(marker, "Name")?.Value as string ??
            marker.ConstructorArguments.FirstOrDefault().Value as string ?? method.Name;
        var stream = Named(marker, "Stream")?.Value as bool? ?? false;
        var context = Named(marker, "Context")?.Value as string;
        var cancellation = method.Parameters.LastOrDefault()?.Type.ToDisplayString() ==
            "System.Threading.CancellationToken";
        var parameters = cancellation ? method.Parameters[..^1] : method.Parameters;
        var namedReturn = method.ReturnType as INamedTypeSymbol;
        var wrapper = namedReturn?.OriginalDefinition.ToDisplayString() ?? method.ReturnType.ToDisplayString();
        var result = namedReturn is { TypeArguments.Length: 1 } ? namedReturn.TypeArguments[0] : method.ReturnType;
        if (wrapper != (stream
                ? "System.Collections.Generic.IAsyncEnumerable<T>"
                : "System.Threading.Tasks.Task<TResult>"))
            Diagnose(
                "AnalysisUnsupported",
                "TYPM002",
                "Remote return wrapper is not the declared Task<T>/IAsyncEnumerable<T> boundary.",
                method.ToDisplayString());
        if (method.IsStatic || method.IsGenericMethod || method.DeclaredAccessibility != Accessibility.Public)
            Diagnose(
                "AnalysisUnsupported",
                "TYPM003",
                "Static, generic, or non-public Remote methods are retained but unsupported.",
                method.ToDisplayString());
        return new(
            name,
            method.Name,
            wrapper,
            Type(result),
            stream,
            cancellation,
            context,
            Named(marker, "Wire")?.Value as string ?? "contextId",
            parameters
                .Select(parameter =>
                {
                    var lookup = Attribute(parameter, "Cordis.Composition.RemoteLookupAttribute");
                    return new NativeParameter(
                        parameter.Name,
                        lookup?.ConstructorArguments.ElementAtOrDefault(1).Value as string ?? parameter.Name,
                        Type(parameter.Type),
                        lookup?.ConstructorArguments.FirstOrDefault().Value as string,
                        lookup?.ConstructorArguments.ElementAtOrDefault(2).Value is ITypeSymbol wireType
                            ? Type(wireType)
                            : null,
                        parameter.RefKind.ToString(),
                        parameter.HasExplicitDefaultValue,
                        parameter.HasExplicitDefaultValue
                            ? Constant(parameter.Type, parameter.ExplicitDefaultValue)
                            : null,
                        parameter.IsParams,
                        Attributes(parameter),
                        Docs(parameter));
                })
                .ToArray(),
            Docs(method),
            Location(method));
    }

    private NativeTypeReference Type(ITypeSymbol symbol, int depth = 0)
    {
        var nullable = symbol.NullableAnnotation.ToString();
        if (depth > 64)
        {
            Diagnose(
                "AnalysisUnsupported",
                "TYPM004",
                "Type nesting exceeds the bounded native analysis depth 64.",
                symbol.ToDisplayString());
            return new("unsupported", symbol.ToDisplayString(), nullable, []);
        }

        if (symbol is IArrayTypeSymbol array)
            return new("array", "", nullable, [Type(array.ElementType, depth + 1)], array.Rank);
        if (symbol.SpecialType is not SpecialType.None && symbol.SpecialType != SpecialType.System_Object)
            return new("primitive", symbol.SpecialType.ToString(), nullable, []);
        if (symbol.ToDisplayString() == "System.Text.Json.JsonElement")
            return new("json", "System.Text.Json.JsonElement", nullable, []);
        if (symbol is not INamedTypeSymbol named ||
            symbol.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.Pointer or TypeKind.TypeParameter)
        {
            Diagnose(
                "AnalysisUnsupported",
                "TYPM005",
                "Type cannot be resolved to a supported authored native declaration.",
                symbol.ToDisplayString());
            return new("unsupported", symbol.ToDisplayString(), nullable, []);
        }

        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            return new("nullable", "System.Nullable<T>", nullable, [Type(named.TypeArguments[0], depth + 1)]);
        var definition = named.OriginalDefinition.ToDisplayString();
        if (definition is "System.Collections.Generic.List<T>" or "System.Collections.Generic.IReadOnlyList<T>" or
            "System.Collections.Generic.IList<T>" or "System.Collections.Generic.IEnumerable<T>" or
            "System.Collections.Generic.ICollection<T>" or "System.Collections.Generic.IReadOnlyCollection<T>" or
            "System.Collections.Generic.Dictionary<TKey, TValue>"
            or "System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>" or
            "System.Collections.Generic.IDictionary<TKey, TValue>")
            return new(
                "collection",
                definition,
                nullable,
                named.TypeArguments.Select(value => Type(value, depth + 1)).ToArray());
        var id = Id(named);
        if (named.SpecialType == SpecialType.System_Object)
        {
            Diagnose(
                "ProjectionUnsupported",
                "TYPM104",
                "System.Object wire data has no exact standalone source DTO contract; use an authored JsonElement boundary.",
                id);
            return new("object", "System.Object", nullable, []);
        }

        if (!types.ContainsKey(id) && visiting.Add(id))
        {
            if (types.Count + visiting.Count > 256)
                Diagnose(
                    "AnalysisUnsupported",
                    "TYPM006",
                    "Reachable named types exceed the bounded native graph size 256.",
                    id);
            else
                Declaration(named, id, depth);
            visiting.Remove(id);
        }

        return new("named", id, nullable, named.TypeArguments.Select(value => Type(value, depth + 1)).ToArray());
    }

    private void Declaration(INamedTypeSymbol type, string id, int depth)
    {
        if (type.TypeKind != TypeKind.Class || type.IsRefLikeType || type.IsUnboundGenericType ||
            type.TypeParameters.Length > 0)
            Diagnose(
                "ProjectionUnsupported",
                "TYPM105",
                "Only closed ordinary data records/classes are currently projected; this named declaration is retained.",
                id);
        if (type.ContainingType is not null)
            Diagnose(
                "ProjectionUnsupported",
                "TYPM106",
                "Nested named declarations are retained but standalone projection is not supported.",
                id);
        var baseType = type.BaseType is { SpecialType: not SpecialType.System_Object } parent &&
            parent.ToDisplayString() is not "System.ValueType"
                ? Type(parent, depth + 1)
                : null;
        if (baseType is not null)
            Diagnose(
                "ProjectionUnsupported",
                "TYPM107",
                "Data inheritance is retained but standalone projection currently requires a declaration without a data base type.",
                id);
        var members = type
            .GetMembers()
            .Where(member => !member.IsStatic &&
                (member.DeclaredAccessibility == Accessibility.Public ||
                    Attribute(member, JsonPrefix + "JsonIncludeAttribute") is not null) &&
                (member is IPropertySymbol { IsIndexer: false } ||
                    member is IFieldSymbol { IsImplicitlyDeclared: false }))
            .Select(Member)
            .ToArray();
        if (members.Select(member => member.WireName).Distinct(StringComparer.Ordinal).Count() != members.Length)
            Diagnose("ProjectionUnsupported", "TYPM108", "Serialized member names collide.", id);
        var constructors = type
            .InstanceConstructors.Where(value => !value.IsImplicitlyDeclared || value.Parameters.Length == 0)
            .Where(value =>
                !(type.IsRecord && value.Parameters.Length == 1 &&
                    SymbolEqualityComparer.Default.Equals(value.Parameters[0].Type, type)))
            .Select(value => new NativeConstructor(
                value.DeclaredAccessibility.ToString(),
                Attribute(value, JsonPrefix + "JsonConstructorAttribute") is not null,
                Attribute(value, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") is not null,
                value
                    .Parameters.Select(parameter => new NativeConstructorParameter(
                        parameter.Name,
                        Type(parameter.Type, depth + 1),
                        members.FirstOrDefault(member => member.Name.Equals(
                                parameter.Name,
                                StringComparison.OrdinalIgnoreCase))
                            ?.Name,
                        parameter.HasExplicitDefaultValue,
                        parameter.HasExplicitDefaultValue
                            ? Constant(parameter.Type, parameter.ExplicitDefaultValue)
                            : null,
                        Attributes(parameter)))
                    .ToArray()))
            .ToArray();
        var attributes = Attributes(type);
        if (attributes.Any(value => value.Name is JsonPrefix + "JsonConverterAttribute"
                or JsonPrefix + "JsonPolymorphicAttribute" or JsonPrefix + "JsonDerivedTypeAttribute"))
            Diagnose(
                "ProjectionUnsupported",
                "TYPM109",
                "Custom converter/polymorphic data semantics are retained and cannot be safely projected.",
                id);
        types[id] = new(
            id,
            type.Name,
            type.ContainingNamespace.ToDisplayString(),
            type.IsRecord
                ? type.TypeKind == TypeKind.Class ? "record" : "recordStruct"
                : type.TypeKind.ToString().ToLowerInvariant(),
            type.DeclaringSyntaxReferences.Length == 0,
            baseType,
            type.TypeArguments.Select(value => Type(value, depth + 1)).ToArray(),
            members,
            constructors,
            attributes,
            Docs(type),
            Location(type));
    }

    private NativeMember Member(ISymbol member)
    {
        var property = member as IPropertySymbol;
        var field = member as IFieldSymbol;
        var attributes = Attributes(member);
        var wire = Attribute(member, JsonPrefix + "JsonPropertyNameAttribute")
            ?.ConstructorArguments.FirstOrDefault()
            .Value as string ?? WireName(member.Name);
        var write = property?.SetMethod is { DeclaredAccessibility: Accessibility.Public } setter
            ? setter.IsInitOnly ? "init" : "set"
            : field is { IsReadOnly: false }
                ? "field"
                : "none";
        var initializer = Initializer(member);
        if (write == "none")
            Diagnose(
                "ProjectionUnsupported",
                "TYPM112",
                "Read-only wire members are retained but require an explicit standalone caller projection.",
                Id(member.ContainingType!) + "." + member.Name);
        if (attributes.Any(value =>
                value.Name is JsonPrefix + "JsonConverterAttribute" or JsonPrefix + "JsonExtensionDataAttribute") ||
            member.DeclaredAccessibility != Accessibility.Public)
            Diagnose(
                "ProjectionUnsupported",
                "TYPM110",
                "Custom converters, extension data and non-public inclusion require an explicit standalone projection.",
                Id(member.ContainingType!) + "." + member.Name);
        return new(
            member.Name,
            wire,
            property is null ? "field" : "property",
            Type(property?.Type ?? field!.Type),
            property?.IsRequired ?? field!.IsRequired,
            Attribute(member, JsonPrefix + "JsonRequiredAttribute") is not null,
            property?.GetMethod?.DeclaredAccessibility == Accessibility.Public || field is not null,
            write,
            member.DeclaredAccessibility.ToString(),
            attributes,
            initializer,
            GetterConstant(member),
            Docs(member),
            Location(member));
    }

    private NativeConstant? GetterConstant(ISymbol member)
    {
        if (member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is not PropertyDeclarationSyntax property)
            return null;
        var expression = property.ExpressionBody?.Expression ?? property.AccessorList?.Accessors
            .FirstOrDefault(value => value.IsKind(SyntaxKind.GetAccessorDeclaration))
            ?.ExpressionBody?.Expression;
        if (expression is null)
            return null;
        var semantic = compilation.GetSemanticModel(expression.SyntaxTree);
        var constant = semantic.GetConstantValue(expression);
        return constant.HasValue
            ? Constant(
                semantic.GetTypeInfo(expression).ConvertedType ?? semantic.GetTypeInfo(expression).Type!,
                constant.Value)
            : new("computedGetter", semantic.GetTypeInfo(expression).Type?.ToDisplayString() ?? "", "null");
    }

    private NativeConstant? Initializer(ISymbol member)
    {
        var syntax = member.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var expression = syntax switch
        {
            PropertyDeclarationSyntax property => property.Initializer?.Value,
            VariableDeclaratorSyntax variable => variable.Initializer?.Value,
            _ => null
        };
        if (expression is null)
            return null;
        var semantic = compilation.GetSemanticModel(expression.SyntaxTree);
        var value = semantic.GetConstantValue(expression);
        if (value.HasValue)
            return Constant(
                semantic.GetTypeInfo(expression).ConvertedType ?? semantic.GetTypeInfo(expression).Type!,
                value.Value);
        if (expression is CollectionExpressionSyntax { Elements.Count: 0 })
            return new("emptyCollection", "", "[]");
        Diagnose(
            "ProjectionUnsupported",
            "TYPM111",
            "Non-constant member initializer is retained as an unsupported projection fact.",
            Id(member.ContainingType!) + "." + member.Name);
        return new("unsupported", semantic.GetTypeInfo(expression).Type?.ToDisplayString() ?? "", "null");
    }

    private string WireName(string name) => namingPolicy switch
    {
        "0" => name,
        "1" => JsonNamingPolicy.CamelCase.ConvertName(name),
        "2" => JsonNamingPolicy.SnakeCaseLower.ConvertName(name),
        "3" => JsonNamingPolicy.SnakeCaseUpper.ConvertName(name),
        "4" => JsonNamingPolicy.KebabCaseLower.ConvertName(name),
        "5" => JsonNamingPolicy.KebabCaseUpper.ConvertName(name),
        _ => throw new InvalidDataException("Unsupported source JSON naming policy value " + namingPolicy)
    };

    private NativeLocation? Location(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(value => value.IsInSource);
        if (location is null)
            return null;
        var span = location.GetLineSpan();
        var path = Path.GetRelativePath(projectDirectory, Path.GetFullPath(span.Path)).Replace('\\', '/');
        // Referenced or linked declarations outside the project keep their filename, not a machine path.
        if (path == ".." || path.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(path))
            path = Path.GetFileName(span.Path);
        return new(path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }

    private void Diagnose(string category, string code, string message, string symbol) =>
        diagnostics.Add(new(category, code, message, symbol));

    private static string Id(INamedTypeSymbol type) => type.ContainingAssembly.Identity.Name + ":" +
        type
            .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "", StringComparison.Ordinal);

    private string Docs(ISymbol symbol)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0 && symbol.ContainingAssembly is { } assembly)
        {
            if (!metadataDocumentation.TryGetValue(assembly, out var documentation))
            {
                foreach (var reference in compilation.References.OfType<PortableExecutableReference>())
                {
                    var referenced = compilation.GetAssemblyOrModuleSymbol(reference);
                    var referencedAssembly = referenced as IAssemblySymbol ??
                        (referenced as IModuleSymbol)?.ContainingAssembly;
                    if (!SymbolEqualityComparer.Default.Equals(assembly, referencedAssembly) ||
                        reference.FilePath is null)
                        continue;
                    var xml = Path.ChangeExtension(reference.FilePath, ".xml");
                    if (File.Exists(xml))
                    {
                        documentation = new NativeXmlDocumentation(xml);
                        break;
                    }
                }

                metadataDocumentation[assembly] = documentation;
            }

            if (documentation is not null && symbol.OriginalDefinition.GetDocumentationCommentId() is { } id)
                return documentation.Read(id);
        }

        return NativeXmlDocumentation.NormalizeLineEndings(
            symbol.GetDocumentationCommentXml(expandIncludes: false) ?? "");
    }

    private static AttributeData? Attribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().FirstOrDefault(value => value.AttributeClass?.ToDisplayString() == name);

    private static TypedConstant? Named(AttributeData? attribute, string name) => attribute
        ?.NamedArguments
        .Where(value => value.Key == name)
        .Select(value => (TypedConstant?)value.Value)
        .FirstOrDefault();

    private static NativeAttribute[] Attributes(ISymbol symbol) => symbol
        .GetAttributes()
        .Where(value => value.AttributeClass?.ToDisplayString() is { } name &&
            (name.StartsWith(JsonPrefix, StringComparison.Ordinal) || name is
                "System.Diagnostics.CodeAnalysis.AllowNullAttribute"
                or "System.Diagnostics.CodeAnalysis.DisallowNullAttribute" or
                "System.Diagnostics.CodeAnalysis.MaybeNullAttribute"
                or "System.Diagnostics.CodeAnalysis.NotNullAttribute"))
        .Select(value => new NativeAttribute(
            value.AttributeClass!.ToDisplayString(),
            value.ConstructorArguments.Select(Constant).ToArray(),
            value
                .NamedArguments.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new NativeNamedConstant(pair.Key, Constant(pair.Value)))
                .ToArray()))
        .ToArray();

    private static NativeConstant Constant(TypedConstant value)
    {
        if (value.Kind == TypedConstantKind.Error)
            return new("error", value.Type?.ToDisplayString() ?? "", "null");
        if (value.Kind == TypedConstantKind.Array)
            return new(
                "array",
                value.Type?.ToDisplayString() ?? "",
                JsonSerializer.Serialize(value.Values.Select(Constant).ToArray()));
        if (value.Kind == TypedConstantKind.Type)
            return new(
                "type",
                value.Type?.ToDisplayString() ?? "",
                JsonSerializer.Serialize(
                    (value.Value as ITypeSymbol)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        return new(
            value.Kind == TypedConstantKind.Enum ? "enum" : "value",
            value.Type?.ToDisplayString() ?? "",
            JsonSerializer.Serialize(value.Value));
    }

    private static NativeConstant Constant(ITypeSymbol type, object? value) =>
        new(type.TypeKind == TypeKind.Enum ? "enum" : "value", type.ToDisplayString(), JsonSerializer.Serialize(value));
}
