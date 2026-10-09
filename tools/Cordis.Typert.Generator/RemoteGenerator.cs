using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var services = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Cordis.Composition.RemoteServiceAttribute",
            static (node, _) => node is ClassDeclarationSyntax,
            static (syntax, _) => (Type: (INamedTypeSymbol)syntax.TargetSymbol, Attribute: syntax.Attributes[0]));
        context.RegisterSourceOutput(
            services,
            static (output, service) => Emit(output, service.Type, service.Attribute));
    }

    private static void Emit(SourceProductionContext output, INamedTypeSymbol type, AttributeData attribute)
    {
        void Refuse(string message) =>
            output.ReportDiagnostic(Diagnostic.Create(Unsupported, type.Locations[0], message));

        if (type.TypeParameters.Length != 0 || type.ContainingType is not null ||
            type.DeclaredAccessibility != Accessibility.Public ||
            !type.DeclaringSyntaxReferences.All(reference =>
                reference.GetSyntax() is ClassDeclarationSyntax declaration &&
                declaration.Modifiers.Any(SyntaxKind.PartialKeyword)))
        {
            Refuse("Remote services must be public, top-level, non-generic partial classes.");
            return;
        }

        if (attribute.ConstructorArguments.Length < 2 ||
            attribute.ConstructorArguments[1].Value is not INamedTypeSymbol metadata)
        {
            Refuse("RemoteService requires an explicit source-generated JsonSerializerContext type.");
            return;
        }

        var service = (string)attribute.ConstructorArguments[0].Value!;
        var space = Named<string>(attribute, "Namespace") ?? service;
        var methods = type
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Select(method => (Method: method, Marker: method
                .GetAttributes()
                .FirstOrDefault(value =>
                    value.AttributeClass?.ToDisplayString() == "Cordis.Composition.RemoteMethodAttribute")))
            .Where(value => value.Marker is not null)
            .ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new StringBuilder();
        var unary = new StringBuilder();
        var streams = new StringBuilder();
        var streamHelpers = new StringBuilder();
        foreach (var (method, markerValue) in methods)
        {
            var marker = markerValue!;
            var name = Named<string>(marker, "Name") ??
                marker.ConstructorArguments.FirstOrDefault().Value as string ?? method.Name;
            var stream = Named<bool>(marker, "Stream");
            var receiver = Named<string>(marker, "Context");
            var wire = Named<string>(marker, "Wire") ?? "contextId";
            var cancellation = method.Parameters.LastOrDefault()?.Type.ToDisplayString() ==
                "System.Threading.CancellationToken";
            var parameters = cancellation
                ? method.Parameters.Take(method.Parameters.Length - 1).ToArray()
                : method.Parameters.ToArray();
            if (!names.Add(name) || method.IsStatic || method.IsGenericMethod ||
                method.DeclaredAccessibility != Accessibility.Public ||
                method.Parameters.Any(parameter =>
                    parameter.RefKind != RefKind.None || parameter.IsParams || parameter.HasExplicitDefaultValue) ||
                method.ReturnType is not INamedTypeSymbol returnType || returnType.TypeArguments.Length != 1 ||
                returnType.OriginalDefinition.ToDisplayString() != (stream
                    ? "System.Collections.Generic.IAsyncEnumerable<T>"
                    : "System.Threading.Tasks.Task<TResult>") ||
                parameters.Any(parameter => parameter.Type.ToDisplayString() == "System.Threading.CancellationToken"))
            {
                Refuse(
                    $"{method.Name}: Remote methods require unique export names, ordinary required parameters, and Task<T> or explicitly marked IAsyncEnumerable<T>. CancellationToken may only be the final parameter.");
                return;
            }

            string Codec(ITypeSymbol symbol) =>
                "global::Cordis.Composition.TypertCodec." +
                (symbol.IsReferenceType && symbol.NullableAnnotation == NullableAnnotation.Annotated
                    ? "CreateNullable"
                    : "Create") +
                "((global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<" +
                TypeName(symbol) + ">)" +
                TypeName(metadata) + ".Default.GetTypeInfo(typeof(" + TypeName(symbol) + "))!, " +
                Literal(symbol.ToDisplayString()) + ")";

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
                var lookup = parameter
                    .GetAttributes()
                    .FirstOrDefault(value =>
                        value.AttributeClass?.ToDisplayString() == "Cordis.Composition.RemoteLookupAttribute");
                var parameterWire = lookup?.ConstructorArguments[1].Value as string ?? parameter.Name;
                var wireType = lookup?.ConstructorArguments[2].Value as ITypeSymbol ?? parameter.Type;
                definitions
                    .Append("new global::Cordis.Composition.TypertInvocationParameter(")
                    .Append(Literal(parameter.Name))
                    .Append(", ")
                    .Append(Literal(parameterWire))
                    .Append(", ")
                    .Append(Codec(wireType))
                    .Append(")");
                if (lookup is not null)
                    definitions
                        .Append(" { Lookup = ")
                        .Append(Literal((string)lookup.ConstructorArguments[0].Value!))
                        .Append(" }");
                definitions.Append(',');
            }

            definitions
                .Append("], ")
                .Append(Codec(returnType.TypeArguments[0]))
                .Append(") { Cancellation = ")
                .Append(cancellation ? "true" : "false")
                .Append(", IsStream = ")
                .Append(stream ? "true" : "false");
            if (name != method.Name)
                definitions.Append(", Implementation = ").Append(Literal(method.Name));
            if (receiver is not null)
                definitions
                    .Append(", Invocation = new global::Cordis.Composition.TypertContextInvocation(")
                    .Append(Literal(receiver))
                    .Append(", ")
                    .Append(Literal(wire))
                    .Append(", ")
                    .Append(
                        "global::Cordis.Composition.TypertCodec.Create((global::System.Text.Json.Serialization.Metadata.JsonTypeInfo<string>)")
                    .Append(TypeName(metadata))
                    .Append(".Default.GetTypeInfo(typeof(string))!, \"string\")")
                    .Append(")");
            definitions.Append(" },");
            var call = "((" + TypeName(type) + ")service).@" + method.Name + "(" +
                string.Join(
                    ", ",
                    parameters.Select((parameter, index) => "(" + TypeName(parameter.Type) + ")args[" + index + "]!")) +
                (cancellation ? (parameters.Length == 0 ? "" : ", ") + "token" : "") + ")";
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
                       public static class {{type.Name}}Typert
                       {
                           public static global::Cordis.Composition.TypertContribution Contribution(string package) => global::Cordis.Composition.TypertArtifacts.Contribution(package, {{Literal(type.ToDisplayString())}}, [{{definitions}}]);
                           internal static readonly global::Cordis.Composition.TypertRemoteBinding Binding = new({{Literal(service)}}, {{Literal(space)}},
                               new global::System.Collections.Generic.Dictionary<string, global::Cordis.Composition.TypertUnaryInvoker> { {{unary}} },
                               new global::System.Collections.Generic.Dictionary<string, global::Cordis.Composition.TypertStreamInvoker> { {{streams}} });
                           {{streamHelpers}}
                       }
                       """;
        output.AddSource(type.ToDisplayString().Replace('.', '_') + ".Remote.g.cs", source);
    }

    private static T? Named<T>(AttributeData attribute, string name) => attribute
        .NamedArguments
        .Where(value => value.Key == name)
        .Select(value => (T?)value.Value.Value)
        .FirstOrDefault();

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, true);
    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}
