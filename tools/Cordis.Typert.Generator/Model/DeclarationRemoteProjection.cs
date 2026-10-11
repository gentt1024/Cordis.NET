using System.Text.Json;
using static Cordis.Typert.Generator.Model.DeclarationAttributeProjection;

namespace Cordis.Typert.Generator.Model;

/// <summary>Interprets author facts once for source and saved-declaration Remote projections.</summary>
internal sealed class DeclarationRemoteProjection
{
    private readonly DeclarationModelDocument model;
    private readonly Dictionary<string, DeclarationType> declarations;
    private readonly Dictionary<string, NativeTypeDeclaration> types = new(StringComparer.Ordinal);
    private readonly HashSet<string> visiting = new(StringComparer.Ordinal);
    private readonly HashSet<string> relevant = new(StringComparer.Ordinal);
    private readonly HashSet<string> generatedContexts = new(StringComparer.Ordinal);
    private readonly List<NativeDiagnostic> diagnostics = [];
    private string namingPolicy = "0";

    private DeclarationRemoteProjection(DeclarationModelDocument model)
    {
        this.model = model;
        declarations = model.Declarations.ToDictionary(value => value.Id, StringComparer.Ordinal);
    }

    internal static RemoteProjection Project(DeclarationModelDocument model, string? service) =>
        new DeclarationRemoteProjection(model).ProjectServices(service);

    private RemoteProjection ProjectServices(string? service)
    {
        var selected = model.Roots.Select(Resolve)
            .Where(type => Attribute(type.Attributes, "Cordis.Composition.RemoteServiceAttribute") is not null)
            .Where(type => service is null or "*" || type.MetadataName.Replace('+', '.') == service ||
                String(Attribute(type.Attributes, "Cordis.Composition.RemoteServiceAttribute")!.Arguments.FirstOrDefault()) == service)
            .OrderBy(type => type.Id, StringComparer.Ordinal).ToArray();
        if (service is not null and not "*" && selected.Length != 1)
            throw Refuse(service, "Exactly one explicitly selected authored Remote service must match.");
        var services = selected.Select(Service).ToArray();
        foreach (var diagnostic in model.Diagnostics)
        {
            if (diagnostic.Symbol is not null && !relevant.Contains(diagnostic.Symbol) &&
                !relevant.Any(id => diagnostic.Symbol.StartsWith(id + ".", StringComparison.Ordinal)))
                continue;
            if (diagnostic.Category == "PreGeneratorDeclaration" && diagnostic.Code == "CS0534" &&
                diagnostic.Symbol is not null && generatedContexts.Contains(diagnostic.Symbol))
                continue;
            diagnostics.Add(diagnostic);
        }
        var result = new RemoteProjection(model.ContractIdentity, model.Compilation,
            services, types.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
            diagnostics.OrderBy(value => value.Symbol, StringComparer.Ordinal).ThenBy(value => value.Code, StringComparer.Ordinal).ToArray());
        return result;
    }

    private NativeService Service(DeclarationType type)
    {
        relevant.Add(type.Id);
        var marker = Attribute(type.Attributes, "Cordis.Composition.RemoteServiceAttribute")!;
        var key = String(marker.Arguments.FirstOrDefault()) ?? "";
        var space = String(Named(marker, "Namespace")) ?? key;
        var contextReference = marker.Arguments.ElementAtOrDefault(1)?.Reference;
        NativeSerializerContext? serializer = null;
        namingPolicy = "0";
        if (contextReference is null || contextReference.Kind != "named")
            Diagnose("ProjectionUnsupported", "TYPM101", "An explicit source JSON context is required for native wire projection.", type.Id);
        else
        {
            var context = Resolve(contextReference.Name);
            relevant.Add(context.Id);
            var options = Attribute(context.Attributes, JsonPrefix + "JsonSourceGenerationOptionsAttribute");
            namingPolicy = Named(options, "PropertyNamingPolicy")?.Json ?? "0";
            if (Named(options, "Converters") is { Elements.Length: > 0 })
                Diagnose("ProjectionUnsupported", "TYPM102", "Context converter policies cannot be copied into a standalone caller.", context.Id);
            var authored = context.Members.Any(member => member.Source &&
                member.Name is "Default" or "GetTypeInfo" or "GeneratedSerializerOptions");
            if (authored)
                Diagnose("ProjectionUnsupported", "TYPM103", "Authored JSON metadata implementations cannot be replayed as source JSON options.", context.Id);
            var annotationContext = context.Provenance == "source" && context.Kind == "class" &&
                context.TypeParameters.Length == 0 && context.Interfaces.Length == 0 &&
                context.Attributes.Any(value => value.Name == JsonPrefix + "JsonSerializableAttribute" &&
                    value.Arguments.FirstOrDefault()?.Reference is not null) &&
                context.Modifiers.Contains("partial", StringComparer.Ordinal) &&
                context.Modifiers.All(value => value is "partial" or "sealed") && PartialContainers(context) &&
                context.BaseType is { Kind: "named" } parent &&
                Resolve(parent.Name) is { } contextBase && DeclarationWireProjection.Framework(contextBase) &&
                contextBase.MetadataName == "System.Text.Json.Serialization.JsonSerializerContext" && !authored;
            if (annotationContext)
                generatedContexts.Add(context.Id);
            else if (context.Provenance == "source")
                Diagnose("ProjectionUnsupported", "TYPM115", "Remote projection requires an annotation-only source JSON context with JSON registrations directly deriving from JsonSerializerContext.", context.Id);
            serializer = new(context.Id, Wire(context.Attributes),
                context.Attributes.Where(value => value.Name == JsonPrefix + "JsonSerializableAttribute")
                    .Select(value => value.Arguments.FirstOrDefault()?.Reference ??
                        throw Refuse(context.Id, "JSON registration lacks a structured type reference; re-extract the model."))
                    .Select(reference => Reference(reference)).ToArray(), authored);
        }
        var methods = type.Members.Where(method => Attribute(method.Attributes, "Cordis.Composition.RemoteMethodAttribute") is not null)
            .OrderBy(method => method.Location?.Line ?? 0).ThenBy(method => method.Location?.Column ?? 0)
            .Select(Method).ToArray();
        if (methods.Select(method => method.Name).Distinct(StringComparer.Ordinal).Count() != methods.Length)
            Diagnose("AnalysisUnsupported", "TYPM001", "Duplicate exported method names are not a unique invocation contract.", type.Id);
        return new(key, space, type.MetadataName.Replace('+', '.'), serializer, methods, type.Documentation, type.Location);
    }

    private NativeMethod Method(DeclarationMember method)
    {
        relevant.Add(method.Id);
        var marker = Attribute(method.Attributes, "Cordis.Composition.RemoteMethodAttribute")!;
        var name = String(Named(marker, "Name")) ?? String(marker.Arguments.FirstOrDefault()) ?? method.Name;
        var stream = Boolean(Named(marker, "Stream"));
        var context = String(Named(marker, "Context"));
        var cancellation = method.Parameters.LastOrDefault() is { } last && Is(last.Type, "System.Threading.CancellationToken");
        var parameters = cancellation ? method.Parameters[..^1] : method.Parameters;
        var expected = stream ? "System.Collections.Generic.IAsyncEnumerable`1" : "System.Threading.Tasks.Task`1";
        var wrapper = stream ? "System.Collections.Generic.IAsyncEnumerable<T>" : "System.Threading.Tasks.Task<TResult>";
        if (method.Type is null || !Is(method.Type, expected) || method.Type.Arguments.Length != 1)
            Diagnose("AnalysisUnsupported", "TYPM002", "Remote return wrapper is not the declared Task<T>/IAsyncEnumerable<T> boundary.", method.Id);
        if (method.Modifiers.Contains("static", StringComparer.Ordinal) || method.TypeParameters.Length > 0 || method.Accessibility != "Public")
            Diagnose("AnalysisUnsupported", "TYPM003", "Static, generic, or non-public Remote methods are retained but unsupported.", method.Id);
        if (method.Modifiers.Contains("abstract", StringComparer.Ordinal) || method.Implementation != "body")
            Diagnose("AnalysisUnsupported", "TYPM007", "An authored Remote method must have a source implementation body.", method.Id);
        var result = method.Type is { Arguments.Length: 1 } ? method.Type.Arguments[0] : method.Type ?? new("unsupported", "missing return", "None", []);
        return new(name, method.Name, wrapper, Reference(result), stream, cancellation, context,
            String(Named(marker, "Wire")) ?? "contextId", parameters.Select(parameter =>
            {
                var lookup = Attribute(parameter.Attributes, "Cordis.Composition.RemoteLookupAttribute");
                return new NativeParameter(parameter.Name, String(lookup?.Arguments.ElementAtOrDefault(1)) ?? parameter.Name,
                    Reference(parameter.Type), String(lookup?.Arguments.FirstOrDefault()),
                    lookup?.Arguments.ElementAtOrDefault(2)?.Reference is { } wire ? Reference(wire) : null,
                    parameter.RefKind, parameter.Optional, parameter.Default, parameter.Params, Wire(parameter.Attributes), parameter.Documentation);
            }).ToArray(), method.Documentation, method.Location);
    }

    private NativeTypeReference Reference(DeclarationReference reference, int depth = 0)
    {
        if (depth > 64)
            throw Refuse(reference.Name, "Type nesting exceeds 64.");
        if (reference.Kind == "array")
            return new("array", "", reference.Nullability, [Reference(reference.Arguments[0], depth + 1)], reference.Rank);
        if (reference.Kind == "primitive")
        {
            if (reference.Name == "System_Object")
            {
                Diagnose("ProjectionUnsupported", "TYPM104", "System.Object wire data has no exact standalone source DTO contract.", reference.Name);
                return new("object", "System.Object", reference.Nullability, []);
            }
            return new("primitive", reference.Name, reference.Nullability, []);
        }
        if (reference.Kind != "named")
        {
            Diagnose("AnalysisUnsupported", "TYPM005", "Type cannot be resolved to a supported authored native declaration.", reference.Name);
            return new("unsupported", reference.Name, reference.Nullability, []);
        }
        var type = Resolve(reference.Name);
        if (DeclarationWireProjection.FrameworkReference(type, reference, value => Reference(value, depth + 1)) is { } framework)
            return framework;
        if (type.Provenance == "metadata-reference")
        {
            Diagnose("ProjectionUnsupported", "TYPM114", "Opaque metadata references do not provide a complete DTO signature.", type.Id);
            return new("unsupported", type.Id, reference.Nullability, []);
        }
        relevant.Add(type.Id);
        ValidateCachedWireNames(new("named", type.Id, reference.Nullability, []), new(StringComparer.Ordinal));
        if (!types.ContainsKey(type.Id) && visiting.Add(type.Id))
        {
            if (types.Count + visiting.Count > 256)
                throw Refuse(type.Id, "Reachable named types exceed 256.");
            Declaration(type, depth);
            visiting.Remove(type.Id);
        }
        return new("named", type.Id, reference.Nullability, reference.Arguments.Select(value => Reference(value, depth + 1)).ToArray());
    }

    private void Declaration(DeclarationType type, int depth)
    {
        if (type.Kind is not ("class" or "record") || type.Modifiers.Contains("refLike", StringComparer.Ordinal) || type.TypeParameters.Length > 0)
            Diagnose("ProjectionUnsupported", "TYPM105", "Only closed ordinary data records/classes are currently projected.", type.Id);
        if (type.ContainingType is not null)
            Diagnose("ProjectionUnsupported", "TYPM106", "Nested named declarations are retained but standalone projection is not supported.", type.Id);
        var baseType = type.BaseType is { Kind: "primitive", Name: "System_Object" or "System_ValueType" } or null
            ? null : Reference(type.BaseType, depth + 1);
        if (baseType is not null)
            Diagnose("ProjectionUnsupported", "TYPM107", "Data inheritance is retained but standalone projection requires a declaration without a data base type.", type.Id);
        var members = type.Members.Where(member => !member.Modifiers.Contains("static", StringComparer.Ordinal) &&
            (member.Accessibility == "Public" || Attribute(member.Attributes, JsonPrefix + "JsonIncludeAttribute") is not null) &&
            (member.Kind == "property" || member.Kind == "field" && !member.Implicit)).Select(member => Member(member, type.Id)).ToArray();
        if (members.Select(member => member.WireName).Distinct(StringComparer.Ordinal).Count() != members.Length)
            Diagnose("ProjectionUnsupported", "TYPM108", "Serialized member names collide.", type.Id);
        var constructors = type.Members.Where(member => member.Kind == "Constructor" &&
            (!member.Implicit || member.Parameters.Length == 0) && !(type.Kind == "record" && member.Parameters.Length == 1 && member.Parameters[0].Type.Name == type.Id))
            .Select(member => new NativeConstructor(member.Accessibility,
                Attribute(member.Attributes, JsonPrefix + "JsonConstructorAttribute") is not null,
                Attribute(member.Attributes, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") is not null,
                member.Parameters.Select(parameter => new NativeConstructorParameter(parameter.Name, Reference(parameter.Type, depth + 1),
                    members.FirstOrDefault(value => value.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))?.Name,
                    parameter.Optional, parameter.Default, Wire(parameter.Attributes))).ToArray())).ToArray();
        var attributes = Wire(type.Attributes);
        if (attributes.Any(value => value.Name is JsonPrefix + "JsonConverterAttribute" or JsonPrefix + "JsonPolymorphicAttribute" or JsonPrefix + "JsonDerivedTypeAttribute"))
            Diagnose("ProjectionUnsupported", "TYPM109", "Custom converter/polymorphic data semantics cannot be safely projected.", type.Id);
        var separator = type.MetadataName.LastIndexOf('.');
        types[type.Id] = new(type.Id, type.Name, separator < 0 ? "" : type.MetadataName[..separator], type.Kind,
            type.Provenance != "source", baseType, [], members, constructors, attributes, type.Documentation, type.Location);
    }

    private NativeMember Member(DeclarationMember facts, string owner)
    {
        var attributes = Wire(facts.Attributes);
        var write = facts.Setter == "Public" ? facts.InitOnly ? "init" : "set" :
            facts.Kind == "field" && !facts.Modifiers.Contains("readonly", StringComparer.Ordinal) ? "field" : "none";
        var initializer = facts.Initializer;
        if (initializer is { Kind: "expression" })
        {
            if (JsonSerializer.Deserialize<string>(initializer.Json) == "[]")
                initializer = new("emptyCollection", "", "[]");
            else
            {
                Diagnose("ProjectionUnsupported", "TYPM111", "Non-constant member initializer is an unsupported projection fact.", owner + "." + facts.Name);
                initializer = new("unsupported", initializer.Type, "null");
            }
        }
        if (write == "none")
            Diagnose("ProjectionUnsupported", "TYPM112", "Read-only wire members require an explicit standalone caller projection.", owner + "." + facts.Name);
        if (attributes.Any(value => value.Name is JsonPrefix + "JsonConverterAttribute" or JsonPrefix + "JsonExtensionDataAttribute") || facts.Accessibility != "Public")
            Diagnose("ProjectionUnsupported", "TYPM110", "Custom converters, extension data and non-public inclusion require an explicit standalone projection.", owner + "." + facts.Name);
        return DeclarationWireProjection.Member(facts, value => Reference(value), attributes, namingPolicy, initializer, facts.GetterConstant);
    }

    private void ValidateCachedWireNames(NativeTypeReference reference, HashSet<string> checkedTypes)
    {
        foreach (var argument in reference.Arguments)
            ValidateCachedWireNames(argument, checkedTypes);
        if (reference.Kind != "named" || !checkedTypes.Add(reference.Name) || !types.TryGetValue(reference.Name, out var type))
            return;
        if (type.Members.Any(member => member.WireName != DeclarationWireProjection.MemberName(member.Name, member.Attributes, namingPolicy)))
            Diagnose("ProjectionUnsupported", "TYPM113", "Shared Remote data has conflicting serializer member names; select each service separately for wire projection.", type.Id);
        foreach (var member in type.Members)
            ValidateCachedWireNames(member.Type, checkedTypes);
        foreach (var parameter in type.Constructors.SelectMany(value => value.Parameters))
            ValidateCachedWireNames(parameter.Type, checkedTypes);
        if (type.BaseType is not null)
            ValidateCachedWireNames(type.BaseType, checkedTypes);
    }

    private bool Is(DeclarationReference reference, string metadataName) =>
        reference.Kind == "named" && Resolve(reference.Name) is { } type &&
        DeclarationWireProjection.Framework(type) && type.MetadataName == metadataName;

    private bool PartialContainers(DeclarationType type)
    {
        while (type.ContainingType is { } id)
        {
            type = Resolve(id);
            if (type.Provenance != "source" || type.TypeParameters.Length != 0 ||
                !type.Modifiers.Contains("partial", StringComparer.Ordinal))
                return false;
        }
        return true;
    }

    internal static bool OwnsServiceDiagnostic(RemoteProjection model, NativeService service, string symbol)
    {
        var separator = symbol.IndexOf(':');
        if (separator >= 0)
        {
            var assembly = symbol[..separator];
            if (assembly != model.Compilation.AssemblyName && !assembly.StartsWith(model.Compilation.AssemblyName + ",", StringComparison.Ordinal))
                return false;
            symbol = symbol[(separator + 1)..];
            if (symbol.Length > 2 && symbol[1] == ':')
                symbol = symbol[2..];
        }
        symbol = symbol.Replace('+', '.');
        return symbol == service.SourceType || symbol.StartsWith(service.SourceType + ".", StringComparison.Ordinal);
    }

    private DeclarationType Resolve(string id) => declarations.TryGetValue(id, out var type) && type.Id == type.Assembly + ":" + type.MetadataName
        ? type : throw Refuse(id, "Missing or inconsistent assembly-qualified declaration identity.");

    private void Diagnose(string category, string code, string message, string symbol) => diagnostics.Add(new(category, code, message, symbol));

    private static InvalidDataException Refuse(string symbol, string message) => new("RemoteProjectionUnsupported " + symbol + ": " + message);
}
