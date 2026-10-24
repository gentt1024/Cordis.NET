using System.Text.Json;
using static Cordis.Typert.Generator.Model.DeclarationAttributeProjection;

namespace Cordis.Typert.Generator.Model;

/// <summary>Projects the shared invocation contract and author data facts for independent callers.</summary>
internal sealed class DeclarationRemoteProjection
{
    private readonly DeclarationModelDocument model;
    private readonly DeclarationModelComposition declarations;
    private readonly Dictionary<string, NativeTypeDeclaration> types = new(StringComparer.Ordinal);
    private readonly HashSet<string> visiting = new(StringComparer.Ordinal);
    private readonly List<NativeDiagnostic> diagnostics = [];
    private string namingPolicy = "0";

    private DeclarationRemoteProjection(DeclarationModelDocument model, DeclarationModelDocument[] references)
    {
        this.model = model;
        declarations = new(model, references);
    }

    internal static RemoteProjection Project(
        DeclarationModelDocument model,
        string? service,
        DeclarationModelDocument[]? references = null) =>
        new DeclarationRemoteProjection(model, references ?? []).ProjectServices(service);

    private RemoteProjection ProjectServices(string? service)
    {
        var selected = DeclarationRemoteContract.Read(model, service);
        var services = selected.Select(Service).ToArray();
        diagnostics.AddRange(
            declarations.Diagnostics.Where(diagnostic => diagnostic.Symbol is null ||
                types.Keys.Any(id =>
                    diagnostic.Symbol == id || diagnostic.Symbol.StartsWith(id + ".", StringComparison.Ordinal))));
        foreach (var contract in selected)
        {
            diagnostics.AddRange(contract.Diagnostics);
            diagnostics.AddRange(DeclarationRemoteContract.SourceDiagnostics(model, contract));
        }

        var result = new RemoteProjection(
            model.ContractIdentity,
            model.Compilation,
            services,
            types.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(),
            diagnostics
                .OrderBy(value => value.Symbol, StringComparer.Ordinal)
                .ThenBy(value => value.Code, StringComparer.Ordinal)
                .ThenBy(value => value.Category, StringComparer.Ordinal)
                .ThenBy(value => value.Message, StringComparer.Ordinal)
                .ToArray());
        return result;
    }

    private NativeService Service(RemoteServiceContract contract)
    {
        var type = contract.Facts;
        var serializer = Serializer(contract.JsonContext, type);

        return new(
            contract.Key,
            contract.Namespace,
            type.MetadataName.Replace('+', '.'),
            serializer,
            contract.Methods.Select(Method).ToArray(),
            type.Documentation,
            type.Location);
    }

    private NativeSerializerContext? Serializer(DeclarationReference? contextReference, DeclarationType type)
    {
        NativeSerializerContext? serializer = null;
        namingPolicy = "0";
        if (contextReference is null || contextReference.Kind != "named")
            Diagnose(
                "ProjectionUnsupported",
                "TYPM101",
                "An explicit source JSON context is required for native wire projection.",
                type.Id);
        else
        {
            var context = Resolve(contextReference.Name);
            var options = Attribute(context.Attributes, JsonPrefix + "JsonSourceGenerationOptionsAttribute");
            namingPolicy = Named(options, "PropertyNamingPolicy")?.Json ?? "0";
            if (Named(options, "Converters") is { Elements.Length: > 0 })
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM102",
                    "Context converter policies cannot be copied into a standalone caller.",
                    context.Id);
            if (Named(options, "UseStringEnumConverter")?.Json == "true")
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM118",
                    "String enum context policies require an explicitly supported standalone projection.",
                    context.Id);
            var authored = DeclarationRemoteContract.HasAuthoredMetadata(context);
            if (authored)
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM103",
                    "Authored JSON metadata implementations cannot be replayed as source JSON options.",
                    context.Id);
            var annotationContext = DeclarationRemoteContract.IsGeneratedContext(model, context);
            if (!annotationContext)
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM115",
                    "Remote projection requires an annotation-only source JSON context with JSON registrations directly deriving from JsonSerializerContext.",
                    context.Id);
            serializer = new(
                context.Id,
                Wire(context.Attributes),
                context
                    .Attributes.Where(value => value.Name == JsonPrefix + "JsonSerializableAttribute")
                    .Select(value => value.Arguments.FirstOrDefault()?.Reference ??
                        throw Refuse(
                            context.Id,
                            "JSON registration lacks a structured type reference; re-extract the model."))
                    .Select(reference => Reference(reference))
                    .ToArray(),
                authored);
        }

        return serializer;
    }

    internal static (NativeTypeDeclaration[] Types, NativeTypeReference[] Args, NativeTypeReference? Request,
        NativeTypeReference? Result, bool Optional, NativeSerializerContext Serializer) EventData(
            DeclarationModelDocument model,
            RemoteEventContract contract,
            DeclarationModelDocument[] references)
    {
        var projection = new DeclarationRemoteProjection(model, references);
        var serializer = projection.Serializer(contract.JsonContext, contract.Facts)!;
        var args = contract.Subject is null
            ? contract.Parameters.Select(p => projection.Reference(p.Type)).ToArray()
            : [];
        var request = contract.Request is { } input ? projection.Reference(input) : null;
        var output = contract.Result;
        var optional = output is { Kind: "named", Arguments.Length: 1 } && model.Declarations.Any(type =>
            type.Id == output.Name && type.MetadataName == "Cordis.Composition.RemoteOptional`1");
        if (optional)
            output = output!.Arguments[0];
        var result = output is null ? null : projection.Reference(output);
        var errors = projection
            .diagnostics.Concat(
                projection.declarations.Diagnostics.Where(diagnostic => diagnostic.Symbol is null ||
                    projection.types.Keys.Any(id =>
                        diagnostic.Symbol == id || diagnostic.Symbol.StartsWith(id + ".", StringComparison.Ordinal))))
            .Concat(DeclarationRemoteContract.SourceDiagnostics(model, contract.Facts, contract.JsonContext))
            .ToArray();
        if (errors.Length != 0)
            throw new InvalidDataException(string.Join("; ", errors.Select(e => e.Code + ": " + e.Message)));
        return (projection.types.Values.OrderBy(type => type.Id, StringComparer.Ordinal).ToArray(), args, request,
            result, optional, serializer);
    }

    private NativeMethod Method(RemoteMethodContract method)
    {
        return new(
            method.Name,
            method.Facts.Name,
            method.ReturnWrapper,
            Reference(method.Result),
            method.Stream,
            method.Cancellation,
            method.Context,
            method.ContextWire,
            method.ContextWireType is { } contextType ? Reference(contextType) : null,
            method.Scope,
            method
                .Parameters.Select(parameter =>
                    new NativeParameter(
                        parameter.Facts.Name,
                        parameter.Wire,
                        Reference(parameter.LookupWireType ?? parameter.Facts.Type),
                        parameter.Lookup,
                        parameter.LookupWireType is { } wire ? Reference(wire) : null,
                        parameter.Facts.RefKind,
                        parameter.Facts.Optional,
                        parameter.Facts.Default,
                        parameter.Facts.Params,
                        Wire(parameter.Facts.Attributes),
                        parameter.Facts.Documentation))
                .ToArray(),
            method.Facts.Documentation,
            method.Facts.Location)
        {
            Uplink = method.Uplink is { } uplink ? new(Reference(uplink.Type), uplink.Optional) : null
        };
    }

    private NativeTypeReference Reference(DeclarationReference reference, int depth = 0, string? parameterOwner = null)
    {
        if (depth > 64)
            throw Refuse(reference.Name, "Type nesting exceeds 64.");
        if (reference.Kind == "array")
            return new(
                "array",
                "",
                reference.Nullability,
                [Reference(reference.Arguments[0], depth + 1, parameterOwner)],
                reference.Rank);
        if (reference.Kind == "primitive")
        {
            if (reference.Name == "System_Object")
            {
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM104",
                    "System.Object wire data has no exact standalone source DTO contract.",
                    reference.Name);
                return new("object", "System.Object", reference.Nullability, []);
            }

            return new("primitive", reference.Name, reference.Nullability, []);
        }

        if (reference.Kind == "parameter" && reference.Owner == parameterOwner && parameterOwner is not null &&
            reference.Ordinal is { } ordinal && Resolve(parameterOwner).TypeParameters.Any(parameter =>
                parameter.Ordinal == ordinal && parameter.Name == reference.Name))
            return new("parameter", reference.Name, reference.Nullability, []);
        if (reference.Kind != "named")
        {
            Diagnose(
                "AnalysisUnsupported",
                "TYPM005",
                "Type cannot be resolved to a supported authored native declaration.",
                reference.Name);
            return new("unsupported", reference.Name, reference.Nullability, []);
        }

        var type = Resolve(reference.Name);
        if (DeclarationWireProjection.FrameworkReference(type, reference, value => Reference(value, depth + 1, parameterOwner)) is
            { } framework)
            return framework;
        if (type.Provenance != "source")
            throw Refuse(
                type.Id,
                "TYPM114: External data declarations require an explicit referenced source model; metadata signatures do not retain all authored DTO facts.");

        ValidateCachedWireNames(new("named", type.Id, reference.Nullability, []), new(StringComparer.Ordinal));
        if (!types.ContainsKey(type.Id) && visiting.Add(type.Id))
        {
            if (types.Count + visiting.Count > 256)
                throw Refuse(type.Id, "Reachable named types exceed 256.");
            Declaration(type, depth);
            visiting.Remove(type.Id);
        }

        return new(
            "named",
            type.Id,
            reference.Nullability,
            reference.Arguments.Select(value => Reference(value, depth + 1, parameterOwner)).ToArray());
    }

    private void Declaration(DeclarationType type, int depth)
    {
        if (type.Kind == "enum")
        {
            types[type.Id] = DeclarationEnumProjection.Project(type);
            return;
        }

        if (type.Kind is not ("class" or "record" or "struct" or "recordStruct") ||
            type.Modifiers.Contains("refLike", StringComparer.Ordinal))
            Diagnose(
                "ProjectionUnsupported",
                "TYPM105",
                "Only ordinary non-ref-like data classes, records and structs are projected.",
                type.Id);
        if (type.TypeParameters.Any(parameter => parameter.ReferenceType || parameter.ValueType || parameter.Unmanaged ||
                parameter.NotNull || parameter.Constructor || parameter.AllowsRefLikeType || parameter.Constraints.Length != 0))
            Diagnose("ProjectionUnsupported", "TYPM119", "Constrained generic data declarations require a further projection.", type.Id);
        if (type.ContainingType is not null)
            Diagnose(
                "ProjectionUnsupported",
                "TYPM106",
                "Nested named declarations are retained but standalone projection is not supported.",
                type.Id);
        var baseType = type.BaseType is { Kind: "primitive", Name: "System_Object" or "System_ValueType" } or null
            ? null
            : Reference(type.BaseType, depth + 1, type.Id);
        if (baseType is not null)
            Diagnose(
                "ProjectionUnsupported",
                "TYPM107",
                "Data inheritance is retained but standalone projection requires a declaration without a data base type.",
                type.Id);
        var members = type
            .Members.Where(member => !member.Modifiers.Contains("static", StringComparer.Ordinal) &&
                (member.Accessibility == "Public" ||
                    Attribute(member.Attributes, JsonPrefix + "JsonIncludeAttribute") is not null) &&
                (member.Kind == "property" || member.Kind == "field" && !member.Implicit))
            .Select(member => Member(member, type.Id))
            .ToArray();
        if (members.Select(member => member.WireName).Distinct(StringComparer.Ordinal).Count() != members.Length)
            Diagnose("ProjectionUnsupported", "TYPM108", "Serialized member names collide.", type.Id);
        var constructors = type
            .Members.Where(member => member.Kind == "Constructor" &&
                (!member.Implicit || member.Parameters.Length == 0 && type.Kind is not ("struct" or "recordStruct")) && !(type.Kind == "record" &&
                    member.Parameters.Length == 1 && member.Parameters[0].Type.Name == type.Id))
            .Select(member => new NativeConstructor(
                member.Accessibility,
                Attribute(member.Attributes, JsonPrefix + "JsonConstructorAttribute") is not null,
                Attribute(
                    member.Attributes,
                    "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute") is not null,
                member
                    .Parameters.Select(parameter => new NativeConstructorParameter(
                        parameter.Name,
                        Reference(parameter.Type, depth + 1, type.Id),
                        members.FirstOrDefault(value => value.Name.Equals(
                                parameter.Name,
                                StringComparison.OrdinalIgnoreCase))
                            ?.Name,
                        parameter.Optional,
                        parameter.Default,
                        Wire(parameter.Attributes)))
                    .ToArray()))
            .ToArray();
        var attributes = Wire(type.Attributes);
        if (attributes.Any(value => value.Name is JsonPrefix + "JsonConverterAttribute"
                or JsonPrefix + "JsonPolymorphicAttribute" or JsonPrefix + "JsonDerivedTypeAttribute"))
            Diagnose(
                "ProjectionUnsupported",
                "TYPM109",
                "Custom converter/polymorphic data semantics cannot be safely projected.",
                type.Id);
        var separator = type.MetadataName.LastIndexOf('.');
        types[type.Id] = new(
            type.Id,
            type.Name,
            separator < 0 ? "" : type.MetadataName[..separator],
            type.Kind,
            type.Provenance != "source",
            baseType,
            type.TypeParameters.Select(parameter => new NativeTypeReference("parameter", parameter.Name, "None", [])).ToArray(),
            members,
            constructors,
            attributes,
            type.Documentation,
            type.Location)
        {
            ReadOnly = type.Modifiers.Contains("readonly", StringComparer.Ordinal)
        };
    }

    private NativeMember Member(DeclarationMember facts, string owner)
    {
        var attributes = DeclarationAttributeProjection.MemberWire(facts);
        var write = facts.Setter == "Public" ? facts.InitOnly ? "init" : "set" :
            facts.Kind == "field" && !facts.Modifiers.Contains("readonly", StringComparer.Ordinal) ? "field" : "none";
        var initializer = facts.Initializer;
        if (initializer is { Kind: "expression" })
        {
            if (JsonSerializer.Deserialize<string>(initializer.Json) == "[]")
            {
                var collection = Reference(facts.Type!, parameterOwner: owner);
                initializer = new("emptyCollection", collection.Kind == "array" && collection.Arguments[0].Name == "System_Byte"
                    ? "byte[]" : collection.Name, "[]");
            }
            else
            {
                Diagnose(
                    "ProjectionUnsupported",
                    "TYPM111",
                    "Non-constant member initializer is an unsupported projection fact.",
                    owner + "." + facts.Name);
                initializer = new("unsupported", initializer.Type, "null");
            }
        }

        if (attributes.Any(value =>
                value.Name is JsonPrefix + "JsonConverterAttribute" or JsonPrefix + "JsonExtensionDataAttribute") ||
            facts.Accessibility != "Public")
            Diagnose(
                "ProjectionUnsupported",
                "TYPM110",
                "Custom converters, extension data and non-public inclusion require an explicit standalone projection.",
                owner + "." + facts.Name);
        return DeclarationWireProjection.Member(
            facts,
            value => Reference(value, parameterOwner: owner),
            attributes,
            namingPolicy,
            initializer,
            facts.GetterConstant);
    }

    private void ValidateCachedWireNames(NativeTypeReference reference, HashSet<string> checkedTypes)
    {
        foreach (var argument in reference.Arguments)
            ValidateCachedWireNames(argument, checkedTypes);
        if (reference.Kind != "named" || !checkedTypes.Add(reference.Name) ||
            !types.TryGetValue(reference.Name, out var type))
            return;
        if (type.Members.Any(member =>
                member.WireName != DeclarationWireProjection.MemberName(member.Name, member.Attributes, namingPolicy)))
            Diagnose(
                "ProjectionUnsupported",
                "TYPM113",
                "Shared Remote data has conflicting serializer member names; select each service separately for wire projection.",
                type.Id);
        foreach (var member in type.Members)
            ValidateCachedWireNames(member.Type, checkedTypes);
        foreach (var parameter in type.Constructors.SelectMany(value => value.Parameters))
            ValidateCachedWireNames(parameter.Type, checkedTypes);
        if (type.BaseType is not null)
            ValidateCachedWireNames(type.BaseType, checkedTypes);
    }

    internal static bool OwnsServiceDiagnostic(RemoteProjection model, NativeService service, string symbol)
    {
        var separator = symbol.IndexOf(':');
        if (separator >= 0)
        {
            var assembly = symbol[..separator];
            if (assembly != model.Compilation.AssemblyName && !assembly.StartsWith(
                    model.Compilation.AssemblyName + ",",
                    StringComparison.Ordinal))
                return false;
            symbol = symbol[(separator + 1)..];
            if (symbol.Length > 2 && symbol[1] == ':')
                symbol = symbol[2..];
        }

        symbol = symbol.Replace('+', '.');
        return symbol == service.SourceType || symbol.StartsWith(service.SourceType + ".", StringComparison.Ordinal);
    }

    private DeclarationType Resolve(string id) => declarations.Resolve(id);

    private void Diagnose(string category, string code, string message, string symbol) =>
        diagnostics.Add(new(category, code, message, symbol));

    private static InvalidDataException Refuse(string symbol, string message) =>
        new("RemoteProjectionUnsupported " + symbol + ": " + message);
}
