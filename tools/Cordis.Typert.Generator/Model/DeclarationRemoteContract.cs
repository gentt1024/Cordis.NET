using static Cordis.Typert.Generator.Model.DeclarationAttributeProjection;

namespace Cordis.Typert.Generator.Model;

internal sealed record RemoteParameterContract(
    DeclarationParameter Facts,
    string Wire,
    string? Lookup,
    DeclarationReference? LookupWireType,
    bool DisallowNull);

internal sealed record RemoteMethodContract(
    DeclarationMember Facts,
    string Name,
    string ReturnWrapper,
    DeclarationReference Result,
    bool Stream,
    bool Cancellation,
    string? Context,
    string ContextWire,
    RemoteParameterContract[] Parameters);

internal sealed record RemoteServiceContract(
    DeclarationType Facts,
    string Key,
    string Namespace,
    DeclarationReference? JsonContext,
    RemoteMethodContract[] Methods,
    NativeDiagnostic[] Diagnostics);

/// <summary>
/// Interprets invocation semantics from saved author facts. It does not reconstruct DTOs or JSON metadata;
/// Host binding and standalone callers apply their own backend admission after this interpretation.
/// </summary>
internal static class DeclarationRemoteContract
{
    internal static RemoteServiceContract[] Read(DeclarationModelDocument model, string? service)
    {
        var declarations = model.Declarations.ToDictionary(value => value.Id, StringComparer.Ordinal);
        var selected = model
            .Roots.Select(id => declarations[id])
            .Where(type => Attribute(type.Attributes, "Cordis.Composition.RemoteServiceAttribute") is not null)
            .Where(type => service is null or "*" || Matches(type, service))
            .OrderBy(type => type.Id, StringComparer.Ordinal)
            .ToArray();
        if (service is not null and not "*" && selected.Length != 1)
            throw new InvalidDataException(
                "Exactly one explicitly selected authored Remote service must match '" + service + "'.");
        return selected.Select(Service).ToArray();

        bool Is(DeclarationReference reference, string name) => reference.Kind == "named" &&
            declarations.TryGetValue(reference.Name, out var type) &&
            DeclarationWireProjection.Framework(type) && type.MetadataName == name;

        RemoteServiceContract Service(DeclarationType type)
        {
            var diagnostics = new List<NativeDiagnostic>();
            var marker = Attribute(type.Attributes, "Cordis.Composition.RemoteServiceAttribute")!;
            var key = String(marker.Arguments.FirstOrDefault()) ?? "";
            var methods = type
                .Members.Where(member =>
                    Attribute(member.Attributes, "Cordis.Composition.RemoteMethodAttribute") is not null)
                .OrderBy(member => member.Location?.Line ?? 0)
                .ThenBy(member => member.Location?.Column ?? 0)
                .ThenBy(member => member.Id, StringComparer.Ordinal)
                .Select(Method)
                .ToArray();
            if (methods.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count() != methods.Length)
                Diagnose("TYPM001", "Duplicate exported method names are not a unique invocation contract.", type.Id);
            return new(
                type,
                key,
                String(Named(marker, "Namespace")) ?? key,
                marker.Arguments.ElementAtOrDefault(1)?.Reference,
                methods,
                diagnostics.ToArray());

            RemoteMethodContract Method(DeclarationMember method)
            {
                var annotation = Attribute(method.Attributes, "Cordis.Composition.RemoteMethodAttribute")!;
                var stream = Boolean(Named(annotation, "Stream"));
                var cancellation = method.Parameters.LastOrDefault() is { } last && Is(
                    last.Type,
                    "System.Threading.CancellationToken");
                var parameters = cancellation ? method.Parameters[..^1] : method.Parameters;
                var expected =
                    stream ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Threading.Tasks.Task`1";
                if (method.Type is null || !Is(method.Type, expected) || method.Type.Arguments.Length != 1)
                    Diagnose(
                        "TYPM002",
                        "Remote return wrapper is not the declared Task<T>/IAsyncEnumerable<T> boundary.",
                        method.Id);
                if (method.Modifiers.Contains("static", StringComparer.Ordinal) || method.TypeParameters.Length > 0 ||
                    method.Accessibility != "Public")
                    Diagnose(
                        "TYPM003",
                        "Static, generic, or non-public Remote methods are retained but unsupported.",
                        method.Id);
                if (method.Modifiers.Contains("abstract", StringComparer.Ordinal) || method.Implementation != "body")
                    Diagnose("TYPM007", "An authored Remote method must have a source implementation body.", method.Id);
                if (method.Parameters.Any(parameter => parameter.RefKind != "None" || parameter.Params ||
                        parameter.Optional && parameter.Default is null || parameter.Default is not null &&
                        Attribute(parameter.Attributes, "Cordis.Composition.RemoteLookupAttribute") is not null) ||
                    parameters.Any(parameter => Is(parameter.Type, "System.Threading.CancellationToken")))
                    Diagnose(
                        "TYPM008",
                        "Remote parameters require ordinary values and explicit constant defaults when optional; lookups cannot be optional and CancellationToken must be final.",
                        method.Id);
                var result = method.Type is { Arguments.Length: 1 }
                    ? method.Type.Arguments[0]
                    : method.Type ?? new("unsupported", "missing return", "None", []);
                return new(
                    method,
                    String(Named(annotation, "Name")) ?? String(annotation.Arguments.FirstOrDefault()) ?? method.Name,
                    stream ? "System.Collections.Generic.IAsyncEnumerable<T>" : "System.Threading.Tasks.Task<TResult>",
                    result,
                    stream,
                    cancellation,
                    String(Named(annotation, "Context")),
                    String(Named(annotation, "Wire")) ?? "contextId",
                    parameters
                        .Select(parameter =>
                        {
                            var lookup = Attribute(parameter.Attributes, "Cordis.Composition.RemoteLookupAttribute");
                            return new RemoteParameterContract(
                                parameter,
                                String(lookup?.Arguments.ElementAtOrDefault(1)) ?? parameter.Name,
                                String(lookup?.Arguments.FirstOrDefault()),
                                lookup?.Arguments.ElementAtOrDefault(2)?.Reference,
                                Attribute(
                                    parameter.Attributes,
                                    "System.Diagnostics.CodeAnalysis.DisallowNullAttribute") is not null);
                        })
                        .ToArray());
            }

            void Diagnose(string code, string message, string symbol) =>
                diagnostics.Add(new("AnalysisUnsupported", code, message, symbol));
        }
    }

    internal static bool Matches(DeclarationType type, string service) =>
        type.MetadataName.Replace('+', '.') == service ||
        String(Attribute(type.Attributes, "Cordis.Composition.RemoteServiceAttribute")?.Arguments.FirstOrDefault()) ==
        service;

    internal static bool HasAuthoredMetadata(DeclarationType context) => context.Members.Any(member => member.Source &&
        member.Name is "Default" or "GetTypeInfo" or "GeneratedSerializerOptions");

    internal static bool IsGeneratedContext(DeclarationModelDocument model, DeclarationType context)
    {
        var declarations = model.Declarations.ToDictionary(type => type.Id, StringComparer.Ordinal);
        if (context.Provenance != "source" || context.Kind != "class" || context.TypeParameters.Length != 0 ||
            context.Interfaces.Length != 0 || !context.Modifiers.Contains("partial", StringComparer.Ordinal) ||
            context.Modifiers.Any(value => value is not ("partial" or "sealed")) || HasAuthoredMetadata(context) ||
            !context.Attributes.Any(value => value.Name == JsonPrefix + "JsonSerializableAttribute" &&
                value.Arguments.FirstOrDefault()?.Reference is not null) ||
            context.BaseType is not { Kind: "named" } parent ||
            !declarations.TryGetValue(parent.Name, out var baseType) ||
            !DeclarationWireProjection.Framework(baseType) ||
            baseType.MetadataName != JsonPrefix + "JsonSerializerContext")
            return false;
        while (context.ContainingType is { } id)
        {
            context = declarations[id];
            if (context.Provenance != "source" || context.TypeParameters.Length != 0 ||
                !context.Modifiers.Contains("partial", StringComparer.Ordinal))
                return false;
        }

        return true;
    }

    internal static NativeDiagnostic[] SourceDiagnostics(DeclarationModelDocument model, RemoteServiceContract service)
    {
        var selected = DeclarationModelConformance.Select(model, service.Facts.MetadataName.Replace('+', '.'));
        var context = selected.Declarations.FirstOrDefault(type => type.Id == service.JsonContext?.Name);
        return selected
            .Diagnostics.Where(diagnostic => !(diagnostic.Category == "PreGeneratorDeclaration" &&
                diagnostic.Code == "CS0534" && diagnostic.Symbol == context?.Id && context is not null &&
                IsGeneratedContext(model, context)))
            .ToArray();
    }
}
