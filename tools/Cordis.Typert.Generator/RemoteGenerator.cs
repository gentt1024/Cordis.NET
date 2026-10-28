using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Cordis.Typert.Generator.Model;

namespace Cordis.Typert.Generator;

// The SDK compiler is the only dependency. No plugin assembly is loaded during analysis.
[Generator]
public sealed class RemoteGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Unsupported = new(
        "CORDISREMOTE001",
        "Unsupported Remote declaration",
        "{0}",
        "Cordis",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor Drift = new(
        "CORDISREMOTE002",
        "Remote source model does not match this build",
        "{0}",
        "Cordis",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var services = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Cordis.Composition.RemoteServiceAttribute",
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
        var models = context
            .AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".cordis.model.json", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, cancellation) => file.GetText(cancellation)?.ToString() ?? "")
            .Collect();
        var projectDirectory = context.AnalyzerConfigOptionsProvider.Select(static (options, _) =>
            options.GlobalOptions.TryGetValue("build_property.MSBuildProjectDirectory", out var directory)
                ? directory
                : Directory.GetCurrentDirectory());
        var referenceDocumentation = context
            .AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (input, cancellation) =>
            {
                var (file, options) = input;
                return options
                    .GetOptions(file)
                    .TryGetValue("build_metadata.AdditionalFiles.CordisTypertAssemblyIdentity", out var identity)
                    ? (Identity: identity, Text: file.GetText(cancellation)?.ToString() ?? "")
                    : (Identity: "", Text: "");
            })
            .Where(static input => input.Identity.Length != 0)
            .Collect();
        context.RegisterSourceOutput(
            services
                .Combine(context.CompilationProvider)
                .Combine(models)
                .Combine(projectDirectory)
                .Combine(referenceDocumentation),
            static (output, input) =>
            {
                var ((((service, compilation), artifacts), directory), documentation) = input;
                try
                {
                    var selector = service.ToDisplayString();
                    var docs = documentation
                        .GroupBy(value => value.Identity, StringComparer.Ordinal)
                        .ToDictionary(
                            group => group.Key,
                            group => NativeXmlDocumentation.FromText(
                                group.Key,
                                group.Select(value => value.Text).Distinct(StringComparer.Ordinal).Single()),
                            StringComparer.Ordinal);
                    var authored = SourceContractReader.ReadServices(
                        (CSharpCompilation)compilation,
                        directory,
                        selector,
                        docs);
                    var contract = DeclarationRemoteContract.Read(authored, selector).Single();
                    var references = new List<DeclarationModelDocument>();
                    foreach (var json in artifacts)
                    {
                        try
                        {
                            var artifact = DeclarationModelJson.Deserialize(json);
                            references.Add(artifact);
                            var roots = artifact
                                .Roots.Select(id => artifact.Declarations.Single(value => value.Id == id))
                                .Where(value => DeclarationAttributeProjection.Attribute(
                                    value.Attributes,
                                    "Cordis.Composition.RemoteServiceAttribute") is not null)
                                .ToArray();
                            var sameSource = roots.Any(value => value.Id == contract.Facts.Id);
                            var sameKey = roots.Any(value => DeclarationAttributeProjection.String(
                                    DeclarationAttributeProjection.Attribute(
                                        value.Attributes,
                                        "Cordis.Composition.RemoteServiceAttribute")!.Arguments.FirstOrDefault()) ==
                                contract.Key);
                            if (!sameSource && !sameKey)
                                continue;
                            var differences = DeclarationModelConformance.Compare(
                                artifact,
                                authored,
                                sameSource ? selector : contract.Key);
                            if (differences.Length == 0)
                                continue;
                            output.ReportDiagnostic(
                                Diagnostic.Create(
                                    Drift,
                                    service.Locations[0],
                                    "Published declaration model drift: " + string.Join("; ", differences)));
                            return;
                        }
                        catch (Exception error)
                        {
                            output.ReportDiagnostic(
                                Diagnostic.Create(
                                    Drift,
                                    service.Locations[0],
                                    "Cannot verify declaration model: " + error.Message));
                            return;
                        }
                    }

                    var composed = DeclarationModelComposition.Compose(authored, references.ToArray());
                    Emit(output, service, compilation, composed, contract);
                }
                catch (Exception error)
                {
                    output.ReportDiagnostic(
                        Diagnostic.Create(
                            Unsupported,
                            service.Locations[0],
                            "Cannot bind Remote declaration facts: " + error.Message));
                }
            });
    }

    private static void Emit(
        SourceProductionContext output,
        INamedTypeSymbol type,
        Compilation compilation,
        DeclarationModelDocument model,
        RemoteServiceContract contract)
    {
        void Refuse(string message) =>
            output.ReportDiagnostic(Diagnostic.Create(Unsupported, type.Locations[0], message));

        var facts = contract.Facts;
        if (facts.TypeParameters.Length != 0 || facts.ContainingType is not null ||
            facts.Accessibility != "Public" || !facts.Modifiers.Contains("partial", StringComparer.Ordinal))
        {
            Refuse("Remote Host bindings require public, top-level, non-generic partial classes.");
            return;
        }

        var errors = contract
            .Diagnostics.Concat(
                DeclarationRemoteContract
                    .SourceDiagnostics(model, contract)
                    .Where(diagnostic => diagnostic.Category is "PreGeneratorDeclaration" or "AnalysisUnsupported"))
            .ToArray();
        if (errors.Length != 0)
        {
            Refuse(string.Join("; ", errors.Select(error => error.Code + ": " + error.Message)));
            return;
        }

        if (contract.JsonContext is not { Kind: "named" } contextReference ||
            Resolve(contextReference, compilation, model) is not INamedTypeSymbol metadata)
        {
            Refuse("RemoteService requires an explicit JsonSerializerContext type.");
            return;
        }

        var service = contract.Key;
        var space = contract.Namespace;
        var symbols = type.GetMembers().OfType<IMethodSymbol>().ToDictionary(SymbolId, StringComparer.Ordinal);
        var definitions = new StringBuilder();
        var unary = new StringBuilder();
        var streams = new StringBuilder();
        var streamHelpers = new StringBuilder();
        var nullabilityHelpers = new StringBuilder();
        var nullabilityIndex = 0;
        foreach (var invocation in contract.Methods)
        {
            if (!symbols.TryGetValue(invocation.Facts.Id, out var method))
                throw new InvalidDataException("No implementation symbol matches declaration " + invocation.Facts.Id);
            var name = invocation.Name;
            var stream = invocation.Stream;
            var receiver = invocation.Context;
            var wire = invocation.ContextWire;
            var cancellation = invocation.Cancellation;
            var parameters = invocation.Parameters;

            string Codec(DeclarationReference reference, bool disallowNull = false)
            {
                if (reference.Kind == "primitive" && reference.Name == "System_Void")
                    return "global::Cordis.Composition.TypertCodec.Void";
                var symbol = Resolve(reference, compilation, model);
                if (disallowNull)
                    symbol = symbol is INamedTypeSymbol named &&
                        named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                            ? named.TypeArguments[0]
                            : symbol.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
                var query = "__CordisNullability" + nullabilityIndex++;
                var declared = disallowNull
                    ? reference with
                    {
                        Nullability = "NotAnnotated"
                    }
                    : reference;
                if (disallowNull && reference.Kind == "named" && model.Declarations.Any(value =>
                        value.Id == reference.Name && value.MetadataName == "System.Nullable`1"))
                    declared = reference.Arguments[0];
                nullabilityHelpers.Append(NullabilityProjection.Declaration(declared, model, contextReference, query));
                return "global::Cordis.Composition.TypertCodec.CreateDeclared" +
                    "((global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + TypeName(symbol) + ">)" +
                    TypeName(metadata) + ".Default.GetTypeInfo(typeof(" + RuntimeTypeName(symbol) + "))!, " + query +
                    ", " +
                    Literal(symbol.ToDisplayString()) + ", inputDefault: " + query + "Defaults)";
            }

            string UplinkCodec(DeclarationReference reference)
            {
                var symbol = Resolve(reference, compilation, model);
                var collection = reference.Kind == "array" || symbol is INamedTypeSymbol named &&
                    named.ContainingNamespace.ToDisplayString() == "System.Collections.Generic" &&
                    named.Name is "List" or "IReadOnlyList" or "IList" or "IEnumerable" or "ICollection"
                        or "IReadOnlyCollection" or
                        "Dictionary" or "IReadOnlyDictionary" or "IDictionary";
                if (!collection || symbol is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
                    return Codec(reference);
                var dictionary = reference.Arguments.Length == 2;
                if (dictionary && reference.Arguments[0].Name != "System_String")
                    throw new InvalidDataException("Remote uplink dictionaries require string keys.");
                return "global::Cordis.Composition.TypertCodec." +
                    (dictionary ? "CreateDictionary" : "CreateSequence") +
                    "((global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + TypeName(symbol) + ">)" +
                    TypeName(metadata) + ".Default.GetTypeInfo(typeof(" + RuntimeTypeName(symbol) + "))!, " +
                    UplinkCodec(reference.Arguments[dictionary ? 1 : 0]) + ", nullable: " +
                    (reference.Nullability == "Annotated" ? "true" : "false") + ")";
            }

            definitions
                .Append("new global::Cordis.Composition.TypertInvocationDescriptor(package + ")
                .Append(Literal("#" + space + "/" + name))
                .Append(", ")
                .Append(Literal(service))
                .Append(", ")
                .Append(Literal(space))
                .Append(", ")
                .Append(Literal(name))
                .Append(", [");
            foreach (var parameter in parameters)
            {
                var lookup = parameter.Lookup;
                var parameterWire = parameter.Wire;
                var wireType = parameter.LookupWireType ?? parameter.Facts.Type;
                definitions
                    .Append("new global::Cordis.Composition.TypertInvocationParameter(")
                    .Append(Literal(parameter.Facts.Name))
                    .Append(", ")
                    .Append(Literal(parameterWire))
                    .Append(", ")
                    .Append(Codec(wireType, parameter.DisallowNull))
                    .Append(")");
                if (lookup is not null || parameter.Facts.Default is not null ||
                    RemoteDocumentation.Parameter(invocation.Facts.Documentation, parameter.Facts.Name) is not null)
                {
                    definitions
                        .Append(" { ");
                    if (lookup is not null)
                        definitions.Append("Lookup = ").Append(Literal(lookup));
                    else if (parameter.Facts.Default is not null)
                        definitions.Append("AcceptsUndefined = true");
                    if (RemoteDocumentation.Parameter(invocation.Facts.Documentation, parameter.Facts.Name) is
                        { } parameterDescription)
                    {
                        if (lookup is not null || parameter.Facts.Default is not null)
                            definitions.Append(", ");
                        definitions.Append("Description = ").Append(Literal(parameterDescription));
                    }

                    definitions.Append(" }");
                }

                definitions.Append(',');
            }

            definitions
                .Append("], ")
                .Append(Codec(invocation.Result))
                .Append(") { Cancellation = ")
                .Append(cancellation ? "true" : "false")
                .Append(", IsStream = ")
                .Append(stream ? "true" : "false");
            if (RemoteDocumentation.Summary(invocation.Facts.Documentation) is { } description)
                definitions.Append(", Description = ").Append(Literal(description));
            if (RemoteDocumentation.Returns(invocation.Facts.Documentation) is { } resultDescription)
                definitions.Append(", ResultDescription = ").Append(Literal(resultDescription));
            if (invocation.Uplink is { } uplink)
            {
                var codec = UplinkCodec(uplink.Type);
                if (uplink.Optional)
                    codec = "global::Cordis.Composition.TypertCodec.CreateOptional<" +
                        TypeName(Resolve(uplink.Type, compilation, model)) + ">(" + codec + ")";
                definitions.Append(", Uplink = ").Append(codec);
            }

            if (name != method.Name)
                definitions.Append(", Implementation = ").Append(Literal(method.Name));
            if (invocation.Scope is { } scope)
                definitions
                    .Append(", Scope = new global::Cordis.Composition.TypertScopeProjection(")
                    .Append(Literal(scope.Context))
                    .Append(", ")
                    .Append(Literal(scope.Wire))
                    .Append(")");
            if (receiver is not null)
                definitions
                    .Append(", Invocation = new global::Cordis.Composition.TypertContextInvocation(")
                    .Append(Literal(receiver))
                    .Append(", ")
                    .Append(Literal(wire))
                    .Append(", ")
                    .Append(
                        Codec(
                            invocation.ContextWireType ?? throw new InvalidDataException("Missing Context wire type.")))
                    .Append(")");
            definitions.Append(" },");
            var callArguments = parameters
                .Select((parameter, index) => parameter.Facts.Default is not null
                    ? "(global::System.Object.ReferenceEquals(args[" + index +
                    "], global::Cordis.Undefined.Value) ? " + DefaultValue(
                        parameter.Facts,
                        Resolve(parameter.Facts.Type, compilation, model)) + "! : (" +
                    TypeName(Resolve(parameter.Facts.Type, compilation, model)) + ")args[" + index + "]!)"
                    : "(" + TypeName(Resolve(parameter.Facts.Type, compilation, model)) + ")args[" + index +
                    "]!")
                .ToList();
            if (invocation.Uplink is { } input)
            {
                var parameter = method.Parameters.Single(value => value.Name == input.Parameter);
                var inputType = ((INamedTypeSymbol)parameter.Type).TypeArguments[0];
                callArguments.Insert(
                    parameter.Ordinal,
                    "global::Cordis.Composition.TypertInvocation.Current!.ReadUplink<" +
                    inputType.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
                            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier)) + ">()");
            }

            if (cancellation)
                callArguments.Add("token");
            var call = "((" + TypeName(type) + ")service).@" + method.Name + "(" + string.Join(", ", callArguments) +
                ")";
            if (stream)
            {
                var helper = "Stream" + streams.Length;
                streams.Append("[").Append(Literal(name)).Append("] = ").Append(helper).Append(",");
                streamHelpers
                    .Append("private static async global::System.Collections.Generic.IAsyncEnumerable<object?> ")
                    .Append(helper)
                    .Append(
                        "(object service, global::System.Collections.Generic.IReadOnlyList<object?> args, [global::System.Runtime.CompilerServices.EnumeratorCancellation] global::System.Threading.CancellationToken token) { await foreach (var item in ")
                    .Append(call)
                    .Append(".WithCancellation(token)) yield return item; }");
            }
            else if (invocation.ReturnWrapper == "System.Threading.Tasks.Task")
                unary
                    .Append("[")
                    .Append(Literal(name))
                    .Append("] = async (service, args, token) => { await ")
                    .Append(call)
                    .Append("; return global::Cordis.Undefined.Value; },");
            else
                unary
                    .Append("[")
                    .Append(Literal(name))
                    .Append("] = async (service, args, token) => (object?)await ")
                    .Append(call)
                    .Append(",");
        }

        var ns = type.ContainingNamespace.IsGlobalNamespace
            ? ""
            : "namespace " + type.ContainingNamespace.ToDisplayString() + ";";
        var source = $$"""
                       // <auto-generated/>
                       #nullable enable
                       using System.Threading.Tasks;
                       {{ns}}
                       partial class {{type.Name}} : global::Cordis.Composition.ITypertRemoteService
                       {
                           global::Cordis.Composition.TypertRemoteBinding global::Cordis.Composition.ITypertRemoteService.TypertRemote => {{type.Name}}Typert.Binding;
                       }
                       /// <summary>Generated Remote contribution for the declared service.</summary>
                       public static class {{type.Name}}Typert
                       {
                           /// <summary>Creates the package-owned Remote contribution.</summary>
                           public static global::Cordis.Composition.TypertContribution Contribution(string package) => global::Cordis.Composition.TypertArtifacts.Contribution(package, {{Literal(type.ToDisplayString())}}, Descriptors(package));
                           /// <summary>Creates reusable metadata without materializing schemas.</summary>
                           public static global::Cordis.Composition.TypertRemoteInfo Info(string package) => new(package, Descriptors(package));
                           private static global::System.Collections.Generic.IReadOnlyList<global::Cordis.Composition.TypertInvocationDescriptor> Descriptors(string package) => [{{definitions}}];
                           internal static readonly global::Cordis.Composition.TypertRemoteBinding Binding = new({{Literal(service)}}, {{Literal(space)}},
                               new global::System.Collections.Generic.Dictionary<string, global::Cordis.Composition.TypertUnaryInvoker> { {{unary}} },
                               new global::System.Collections.Generic.Dictionary<string, global::Cordis.Composition.TypertStreamInvoker> { {{streams}} });
                           {{streamHelpers}}
                           {{nullabilityHelpers}}
                       }
                       """;
        output.AddSource(type.ToDisplayString().Replace('.', '_') + ".Remote.g.cs", source);
    }

    private static string SymbolId(ISymbol symbol) => symbol.ContainingAssembly.Identity + ":" +
        (symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString());

    internal static ITypeSymbol Resolve(
        DeclarationReference reference,
        Compilation compilation,
        DeclarationModelDocument model)
    {
        ITypeSymbol symbol;
        if (reference.Kind == "primitive" && Enum.TryParse<SpecialType>(reference.Name, out var special))
            symbol = compilation.GetSpecialType(special);
        else if (reference.Kind == "dynamic")
            symbol = compilation.DynamicType;
        else if (reference.Kind == "array")
            symbol = compilation.CreateArrayTypeSymbol(
                Resolve(reference.Arguments[0], compilation, model),
                reference.Rank);
        else if (reference.Kind is "named" or "tuple")
        {
            var facts = model.Declarations.Single(value => value.Id == reference.Name);
            var assembly = compilation
                    .SourceModule.ReferencedAssemblySymbols.Prepend(compilation.Assembly)
                    .SingleOrDefault(value => value.Identity.ToString() == facts.Assembly) ??
                throw new InvalidDataException("No referenced assembly matches declaration " + facts.Id);
            INamedTypeSymbol? named;
            if (reference.ContainingType is { } containing)
            {
                var parent = (INamedTypeSymbol)Resolve(containing, compilation, model);
                named = parent.GetTypeMembers(facts.Name, reference.Arguments.Length).SingleOrDefault();
            }
            else
                named = assembly.GetTypeByMetadataName(facts.MetadataName);

            if (named is null)
                throw new InvalidDataException("No compiled type matches declaration " + facts.Id);
            symbol = reference.Arguments.Length == 0
                ? named
                : named.Construct(reference.Arguments.Select(value => Resolve(value, compilation, model)).ToArray());
        }
        else
            throw new InvalidDataException(
                "No compilable Host type for declaration reference " + reference.Kind + ":" + reference.Name);

        return symbol.WithNullableAnnotation(
            reference.Nullability == "Annotated" ? NullableAnnotation.Annotated :
            reference.Nullability == "NotAnnotated" ? NullableAnnotation.NotAnnotated : NullableAnnotation.None);
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));

    private static string RuntimeTypeName(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string DefaultValue(DeclarationParameter parameter, ITypeSymbol type) =>
        parameter.Default is { } value
            ? value.Json == "null" ? "default(" + TypeName(type) + ")" : DeclarationConstantEmitter.Emit(value)
            : throw new InvalidDataException("Missing explicit Remote parameter constant " + parameter.Name);
}
