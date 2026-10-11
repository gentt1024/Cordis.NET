using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cordis.Typert.Generator.Model;

internal sealed partial class SourceContractReader
{
    private const string JsonPrefix = "System.Text.Json.Serialization.";
    private readonly CSharpCompilation compilation;
    private readonly string projectDirectory;
    private readonly List<NativeDiagnostic> diagnostics = [];
    private readonly IReadOnlyDictionary<string, NativeXmlDocumentation>? referenceDocumentation;

    private readonly Dictionary<IAssemblySymbol, NativeXmlDocumentation?> metadataDocumentation =
        new(SymbolEqualityComparer.Default);


    private SourceContractReader(
        CSharpCompilation compilation,
        string projectDirectory,
        IReadOnlyDictionary<string, NativeXmlDocumentation>? referenceDocumentation = null)
    {
        this.compilation = compilation;
        this.projectDirectory = Path.GetFullPath(projectDirectory);
        this.referenceDocumentation = referenceDocumentation;
    }

    internal static DeclarationModelDocument ReadServices(
        CSharpCompilation compilation,
        string projectDirectory,
        string? service = null,
        IReadOnlyDictionary<string, NativeXmlDocumentation>? referenceDocumentation = null) =>
        ReadModel(compilation, projectDirectory, [], service ?? "*", referenceDocumentation);

    internal static DeclarationModelDocument ReadModel(
        CSharpCompilation compilation,
        string projectDirectory,
        IEnumerable<string> metadataNames,
        string? service,
        IReadOnlyDictionary<string, NativeXmlDocumentation>? referenceDocumentation = null)
    {
        if (string.IsNullOrWhiteSpace(service))
            return ReadDeclarations(
                compilation,
                projectDirectory,
                metadataNames,
                referenceDocumentation: referenceDocumentation);
        var roots = compilation
            .SyntaxTrees
            .SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
            .Select(node => compilation.GetSemanticModel(node.SyntaxTree).GetDeclaredSymbol(node))
            .OfType<INamedTypeSymbol>()
            .Where(type => Attribute(type, "Cordis.Composition.RemoteServiceAttribute") is not null)
            .GroupBy(DeclarationId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Where(type => service == "*" || type.ToDisplayString() == service ||
                Attribute(type, "Cordis.Composition.RemoteServiceAttribute")!.ConstructorArguments[0].Value as string ==
                service)
            .OrderBy(DeclarationId, StringComparer.Ordinal)
            .ToArray();
        if (service != "*" && roots.Length != 1)
            throw new InvalidDataException(
                $"Exactly one authored Remote service must match '{service}'; found {roots.Length}.");
        return ReadDeclarations(
            compilation,
            projectDirectory,
            metadataNames.Concat(roots.Select(MetadataName)),
            true,
            referenceDocumentation);
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

    private string Docs(ISymbol symbol)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0 && symbol.ContainingAssembly is { } assembly)
        {
            if (referenceDocumentation is not null && referenceDocumentation.TryGetValue(
                    assembly.Identity.ToString(),
                    out var explicitDocumentation) &&
                symbol.OriginalDefinition.GetDocumentationCommentId() is { } explicitId)
                return explicitDocumentation.Read(explicitId);
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

    private static NativeConstant Constant(TypedConstant value)
    {
        if (value.Kind == TypedConstantKind.Error)
            return new("error", value.Type?.ToDisplayString() ?? "", "null");
        if (value.Kind == TypedConstantKind.Array)
            return new(
                "array",
                value.Type?.ToDisplayString() ?? "",
                value.IsNull ? "null" : JsonSerializer.Serialize(value.Values.Select(Constant).ToArray()));
        if (value.Kind == TypedConstantKind.Type)
            return new(
                "type",
                value.Type?.ToDisplayString() ?? "",
                JsonSerializer.Serialize(
                    (value.Value as ITypeSymbol)?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        return new(
            value.Kind == TypedConstantKind.Enum ? "enum" : "value",
            value.Type?.ToDisplayString() ?? "",
            ConstantJson(value.Value));
    }

    private static NativeConstant Constant(ITypeSymbol type, object? value) =>
        new(type.TypeKind == TypeKind.Enum ? "enum" : "value", type.ToDisplayString(), ConstantJson(value));

    private static string ConstantJson(object? value) => value switch
    {
        double number when !double.IsFinite(number) => JsonSerializer.Serialize(
            number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        float number when !float.IsFinite(number) => JsonSerializer.Serialize(
            number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        _ => JsonSerializer.Serialize(value)
    };
}
