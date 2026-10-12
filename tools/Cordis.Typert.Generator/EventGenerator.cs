using System.Text;
using Cordis.Typert.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cordis.Typert.Generator;

[Generator]
public sealed class EventGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor Unsupported = new(
        "CORDISEVENT001",
        "Unsupported Remote event",
        "{0}",
        "Cordis",
        DiagnosticSeverity.Error,
        true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var events = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Cordis.Composition.RemoteEventAttribute",
            static (node, _) => node is DelegateDeclarationSyntax,
            static (syntax, _) => (INamedTypeSymbol)syntax.TargetSymbol);
        var artifacts = context
            .AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".cordis.model.json", StringComparison.OrdinalIgnoreCase))
            .Select(static (file, token) => file.GetText(token)?.ToString() ?? "")
            .Collect();
        context.RegisterSourceOutput(
            events.Combine(context.CompilationProvider).Combine(artifacts),
            static (output, input) =>
            {
                var ((symbol, compilation), saved) = input;
                try
                {
                    var model = SourceContractReader.ReadDeclarations(
                        (CSharpCompilation)compilation,
                        Directory.GetCurrentDirectory(),
                        [symbol.ToDisplayString()]);
                    var contract = DeclarationEventContract.Read(model, model.Roots.Single());
                    foreach (var json in saved)
                    {
                        var artifact = DeclarationModelJson.Deserialize(json);
                        if (!artifact.Roots.Contains(contract.Facts.Id))
                            continue;
                        var differences = DeclarationModelConformance.Compare(
                            artifact,
                            model,
                            symbol.ToDisplayString());
                        if (differences.Length != 0)
                            throw new InvalidDataException(
                                "Published event declaration drift: " + string.Join("; ", differences));
                    }

                    output.AddSource(
                        symbol.ToDisplayString() + ".Events.g.cs",
                        Emit(symbol, compilation, model, contract));
                }
                catch (Exception error)
                {
                    output.ReportDiagnostic(Diagnostic.Create(Unsupported, symbol.Locations[0], error.Message));
                }
            });
    }

    private static string Emit(
        INamedTypeSymbol symbol,
        Compilation compilation,
        DeclarationModelDocument model,
        RemoteEventContract contract)
    {
        var nullabilityHelpers = new StringBuilder();
        var nullabilityIndex = 0;

        string Type(DeclarationReference reference) => RemoteGenerator
            .Resolve(reference, compilation, model)
            .ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
                    SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
                    SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
                    SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers));

        string Codec(DeclarationReference reference)
        {
            if (reference.Kind == "named" && reference.Arguments.Length == 1 && model.Declarations.Any(type =>
                    type.Id == reference.Name && type.MetadataName == "Cordis.Composition.RemoteOptional`1"))
                return "global::Cordis.Composition.TypertCodec.CreateOptional<" + Type(reference.Arguments[0]) + ">(" +
                    Codec(reference.Arguments[0]) + ")";
            var type = Type(reference);
            var native = RemoteGenerator.Resolve(reference, compilation, model);
            var query = "__CordisNullability" + nullabilityIndex++;
            nullabilityHelpers.Append(NullabilityProjection.Declaration(reference, model, contract.JsonContext, query));
            var runtimeType = native
                .WithNullableAnnotation(NullableAnnotation.NotAnnotated)
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var metadataType = native.IsReferenceType ? runtimeType : type;
            return "global::Cordis.Composition.TypertCodec.CreateDeclared" +
                "((global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" + metadataType + ">)" +
                Type(contract.JsonContext) + ".Default.GetTypeInfo(typeof(" + runtimeType + "))!, " + query + ")";
        }

        var name = SymbolDisplay.FormatLiteral(contract.Name, true);
        var output = new StringBuilder("// <auto-generated/>\n#nullable enable\n#pragma warning disable CS1591\n");
        if (!symbol.ContainingNamespace.IsGlobalNamespace)
            output.Append("namespace ").Append(symbol.ContainingNamespace.ToDisplayString()).Append(";\n");
        output.Append("public static class ").Append(symbol.Name).Append("Typert {\n");
        if (contract.Subject is null)
        {
            output
                .Append(
                    "public static global::Cordis.Composition.TypertRemoteEventDefinition Definition { get; } = new(")
                .Append(name)
                .Append(", [")
                .Append(string.Join(",", contract.Parameters.Select(p => Codec(p.Type))))
                .Append("]);\n")
                .Append(
                    "public static global::Cordis.EffectHandle On(global::Cordis.Composition.TypertRemoteEventsClient client, global::Cordis.Context owner, ")
                .Append(symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat))
                .Append(" listener) => client.On(owner, ")
                .Append(name)
                .Append(", (_, args) => { if(args.Length != ")
                .Append(contract.Parameters.Length)
                .Append(
                    ") throw new global::System.Text.Json.JsonException(\"Invalid event argument count.\"); listener(")
                .Append(
                    string.Join(
                        ",",
                        contract.Parameters.Select((p, i) =>
                            "(" + Type(p.Type) + ")Definition.Args[" + i +
                            "].Decode((global::System.Text.Json.JsonElement)args[" + i + "]!)!")))
                .Append("); return global::Cordis.Undefined.Value; });\n");
        }
        else
        {
            var subject = Type(contract.Subject);
            var request = Type(contract.Request!);
            var result = Type(contract.Result!);
            output
                .Append("private static readonly global::Cordis.Composition.TypertCodec Request = ")
                .Append(Codec(contract.Request!))
                .Append(";\n")
                .Append("private static readonly global::Cordis.Composition.TypertCodec Result = ")
                .Append(Codec(contract.Result!))
                .Append(";\n")
                .Append(
                    "public static global::Cordis.Composition.TypertRemoteEventDefinition Forward(global::System.Func<")
                .Append(subject)
                .Append(
                    ", global::Cordis.Composition.TypertRemoteEventTarget> project) => global::Cordis.Composition.TypertRemoteEvents.Waterfall<")
                .Append(subject)
                .Append(',')
                .Append(request)
                .Append(',')
                .Append(result)
                .Append(">(")
                .Append(name)
                .Append(", Request, Result, project);\n")
                .Append(
                    "public static global::Cordis.EffectHandle On(global::Cordis.Composition.TypertRemoteEventsClient client, global::Cordis.Context owner, global::System.Func<global::Cordis.Context,")
                .Append(request)
                .Append(
                    ",global::System.Func<global::System.Threading.Tasks.Task<global::Cordis.Composition.RemoteEventResult<")
                .Append(result)
                .Append(
                    ">>>,global::System.Threading.CancellationToken,global::System.Threading.Tasks.Task<global::Cordis.Composition.RemoteEventResult<")
                .Append(result)
                .Append(">>> listener) => global::Cordis.Composition.TypertEventListeners.On(client, owner, ")
                .Append(name)
                .Append(", Request, Result, listener);\n")
                .Append("public static global::System.Threading.Tasks.Task<")
                .Append(result)
                .Append("> InvokeAsync(global::Cordis.Context caller, ")
                .Append(subject)
                .Append(" agent, ")
                .Append(request)
                .Append(" request, global::System.Func<global::System.Threading.Tasks.Task<")
                .Append(result)
                .Append(
                    ">> next, global::System.Threading.CancellationToken signal = default) => global::Cordis.Composition.TypertRemoteEvents.AwaitResult<")
                .Append(result)
                .Append(">(caller.Events.WaterfallWith(global::Cordis.Composition.Scope.Target(agent, agent), ")
                .Append(name)
                .Append(", () => BoxNext(next), agent, request, signal));\n")
                .Append(
                    "private static async global::System.Threading.Tasks.Task<object?> BoxNext(global::System.Func<global::System.Threading.Tasks.Task<")
                .Append(result)
                .Append(">> next) => await next();\n");
        }

        return output.Append(nullabilityHelpers).Append("}\n").ToString();
    }
}
