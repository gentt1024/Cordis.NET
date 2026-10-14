using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Typert;
using Cordis.Typert.Generator.Model;

namespace Cordis.Typert.Compiler;

// Uses the same composed declarations as the native caller; no author assembly is loaded.
internal sealed class WebProjection(NativeTypeDeclaration[] declarations, NativeSerializerContext? serializer)
{
    private const string JsonPrefix = DeclarationAttributeProjection.JsonPrefix;
    private readonly Dictionary<string, NativeTypeDeclaration> types = declarations.ToDictionary(type => type.Id);

    internal static (ClientArtifact Client, string Schema) Emit(RemoteProjection model)
    {
        if (model.Diagnostics.Length != 0)
            throw new InvalidDataException(
                string.Join("; ", model.Diagnostics.Select(value => value.Code + ": " + value.Message)));
        var methods = new List<ClientMethod>();
        var schemas = new JsonObject();
        foreach (var service in model.Services)
        {
            var projection = new WebProjection(model.Types, service.Serializer);
            projection.ValidateOptions();
            foreach (var method in service.Methods)
            {
                var parameters = method
                    .Parameters.Select(parameter => new ClientParameter(
                        parameter.Wire,
                        projection.Codec(ParameterType(parameter)),
                        parameter.Optional && parameter.Lookup is null,
                        parameter.Lookup))
                    .ToArray();
                var result = method.Result.Name == "System_Void"
                    ? new ClientCodec(JsonSerializer.SerializeToElement(false), IsVoid: true)
                    : projection.Codec(method.Result, output: true);
                var receiver = method.Context is { } context
                    ? new ClientInvocation(context, method.ContextWire, projection.Codec(method.ContextWireType!))
                    : null;
                var scope = method.Scope is { } scoped
                    ? new ClientScope(scoped.Context, scoped.Wire)
                    : null;
                var uplink = method.Uplink is { } input
                    ? projection.Codec(input.Type) with
                    {
                        AcceptsUndefined = input.Optional
                    }
                    : null;
                var endpoint = service.Namespace + "/" + method.Name;
                methods.Add(
                    new(
                        endpoint,
                        service.Namespace,
                        method.Name,
                        parameters,
                        result,
                        method.Stream,
                        receiver,
                        scope,
                        uplink));
                var arguments = new JsonObject();
                var required = new JsonArray();
                foreach (var parameter in parameters)
                {
                    var key = endpoint + "/parameters/" + parameter.Wire;
                    AddSchema(key, parameter.Codec.Schema);
                    arguments.Add(
                        parameter.Wire,
                        new JsonObject
                        {
                            ["$ref"] = Pointer(key)
                        });
                    if (!parameter.AcceptsUndefined)
                        required.Add(parameter.Wire);
                }

                if (receiver is not null)
                {
                    var key = endpoint + "/parameters/" + receiver.Wire;
                    AddSchema(key, receiver.Codec.Schema);
                    arguments.Add(
                        receiver.Wire,
                        new JsonObject
                        {
                            ["$ref"] = Pointer(key)
                        });
                    required.Add(receiver.Wire);
                }

                schemas.Add(
                    endpoint + "/arguments",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = arguments,
                        ["required"] = required,
                        ["additionalProperties"] = false
                    });
                AddSchema(endpoint + "/result", result.Schema);
                if (uplink is not null)
                    AddSchema(endpoint + "/uplink", uplink.Schema);
            }
        }

        var catalog = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$defs"] = schemas
        };
        return (ClientEmitter.Generate(methods), catalog.ToJsonString(
            new JsonSerializerOptions
            {
                WriteIndented = true
            }) + "\n");

        void AddSchema(string key, JsonElement value)
        {
            var node = JsonNode.Parse(value.GetRawText())!;
            Rebase(node, Pointer(key));
            schemas.Add(key, node);
        }
    }

    internal static (ClientArtifact Client, string Schema) EmitEvent(
        DeclarationModelDocument model,
        RemoteEventContract contract,
        DeclarationModelDocument[] references)
    {
        var data = DeclarationRemoteProjection.EventData(model, contract, references);
        var projection = new WebProjection(data.Types, data.Serializer);
        projection.ValidateOptions();
        var schemas = new JsonObject();

        ClientCodec Codec(string path, NativeTypeReference type, bool output, bool optional = false)
        {
            var codec = projection.Codec(type, output) with
            {
                AcceptsUndefined = optional
            };
            var key = contract.Name + "/" + path;
            var node = JsonNode.Parse(codec.Schema.GetRawText())!;
            Rebase(node, Pointer(key));
            schemas.Add(key, node);
            return codec;
        }

        var args = data.Args.Select((type, index) => Codec("args/" + index, type, true)).ToArray();
        var waterfall = data.Request is { } request
            ? new ClientEventWaterfall(
                Codec("request", request, true),
                Codec("reply", data.Result!, false, data.Optional),
                Codec("next", data.Result!, true, data.Optional))
            : null;
        var catalog = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$defs"] = schemas
        };
        return (EventClientEmitter.Generate([new(contract.Name, args, waterfall)]), catalog.ToJsonString(
            new JsonSerializerOptions
            {
                WriteIndented = true
            }) + "\n");
    }

    private static string Pointer(string key) => "#/$defs/" +
        key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static void Rebase(JsonNode node, string prefix)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference)
                obj["$ref"] = prefix + reference.GetValue<string>()[1..];
            foreach (var child in obj.ToArray())
                if (child.Value is not null)
                    Rebase(child.Value, prefix);
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                if (child is not null)
                    Rebase(child, prefix);
    }

    private NativeAttribute? Options => serializer?.Attributes.SingleOrDefault(attribute =>
        attribute.Name == JsonPrefix + "JsonSourceGenerationOptionsAttribute");

    private string Option(string name, string fallback = "false") =>
        Options?.NamedArguments.SingleOrDefault(argument => argument.Name == name)?.Value.Json ?? fallback;

    private void ValidateOptions()
    {
        if (serializer is null)
            throw Refuse("An explicit source JSON context is required.");
        foreach (var option in Options?.NamedArguments ?? [])
        {
            var supported = option.Name switch
            {
                "PropertyNamingPolicy" or "WriteIndented" or "IncludeFields" or "RespectNullableAnnotations" or
                    "RespectRequiredConstructorParameters"
                    or "UnmappedMemberHandling" => true,
                "DefaultIgnoreCondition" => option.Value.Json is "0" or "2" or "3",
                "GenerationMode" => option.Value.Json is "0" or "1",
                "NumberHandling" or "PreferredObjectCreationHandling" => option.Value.Json == "0",
                _ => false
            };
            if (!supported)
                throw Refuse("Serializer option requires an explicit web projection: " + option.Name);
        }
    }

    private static NativeTypeReference ParameterType(NativeParameter parameter) =>
        parameter.Attributes.Any(attribute =>
            attribute.Name == "System.Diagnostics.CodeAnalysis.DisallowNullAttribute")
            ? NonNullable(parameter.Type)
            : parameter.Type;

    private static NativeTypeReference NonNullable(NativeTypeReference type) =>
        type.Kind == "nullable"
            ? type.Arguments.Single()
            : type with
            {
                Nullability = "NotAnnotated"
            };

    private ClientCodec Codec(NativeTypeReference root, bool output = false)
    {
        var definitions = new JsonObject();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var schema = Read(root);
        if (definitions.Count > 0)
            schema["$defs"] = definitions;
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return new(document.RootElement.Clone());

        JsonObject Read(NativeTypeReference reference)
        {
            if (reference.Kind == "nullable")
                return Nullable(Read(reference.Arguments.Single()));
            if (reference.Nullability == "None" && reference.Kind is "named" or "array" or "collection")
                throw Refuse("Oblivious reference annotation: " + reference.Name);
            JsonObject value;
            switch (reference.Kind)
            {
                case "primitive":
                    value = reference.Name switch
                    {
                        "System_String" => new()
                        {
                            ["type"] = "string"
                        },
                        "System_Char" => new()
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                            ["maxLength"] = 1
                        },
                        "System_Boolean" => new()
                        {
                            ["type"] = "boolean"
                        },
                        "System_Byte" or "System_SByte" or "System_Int16" or "System_UInt16" or
                            "System_Int32" or "System_UInt32" or "System_Int64" or "System_UInt64" => new()
                            {
                                ["type"] = "integer"
                            },
                        "System_Single" or "System_Double" or "System_Decimal" => new()
                        {
                            ["type"] = "number"
                        },
                        _ => throw Refuse("Primitive type: " + reference.Name)
                    };
                    break;
                case "json":
                    return new();
                case "array":
                case "collection":
                    if (reference.Rank > 1 || reference.Kind == "array" && reference.Arguments[0].Name == "System_Byte")
                        throw Refuse(
                            "Multidimensional arrays and binary result metadata require a further web projection.");
                    if (reference.Arguments.Length == 2)
                    {
                        if (reference.Arguments[0] is not
                            { Kind: "primitive", Name: "System_String", Nullability: "NotAnnotated" })
                            throw Refuse("Dictionary keys must be non-null strings.");
                        value = new()
                        {
                            ["type"] = "object",
                            ["additionalProperties"] = Read(reference.Arguments[1])
                        };
                    }
                    else
                        value = new()
                        {
                            ["type"] = "array",
                            ["items"] = Read(reference.Arguments.Single())
                        };

                    break;
                case "named":
                    if (!names.TryGetValue(reference.Name, out var name))
                    {
                        name = "Type" + names.Count;
                        names.Add(reference.Name, name);
                        definitions.Add(name, null);
                        definitions[name] = Object(types[reference.Name]);
                    }

                    value = new()
                    {
                        ["$ref"] = "#/$defs/" + name
                    };
                    break;
                default:
                    throw Refuse("Type kind: " + reference.Kind);
            }

            return reference.Nullability == "Annotated" ? Nullable(value) : value;
        }

        JsonObject Object(NativeTypeDeclaration type)
        {
            if (type.Constructors.Any(value => value.SetsRequiredMembers))
                throw Refuse(
                    "SetsRequiredMembers constructor semantics require a further schema projection: " + type.Id);
            var numberHandling = Attribute(type.Attributes, "JsonNumberHandlingAttribute");
            if (numberHandling is not null && numberHandling.Arguments.Single().Json != "0")
                throw Refuse("Non-default number handling: " + type.Id);
            var properties = new JsonObject();
            var required = new JsonArray();
            var constructor = type.Constructors.SingleOrDefault(value => value.JsonConstructor);
            if (constructor is null &&
                !type.Constructors.Any(value => value.Accessibility == "Public" && value.Parameters.Length == 0))
            {
                var candidates = type.Constructors.Where(value => value.Accessibility == "Public").ToArray();
                if (candidates.Length != 1)
                    throw Refuse("A unique data constructor is required: " + type.Id);
                constructor = candidates[0];
            }

            foreach (var member in type.Members)
            {
                var ignore = Attribute(member.Attributes, "JsonIgnoreAttribute");
                if (ignore is not null && Named(ignore, "Condition") is null or "1")
                    continue;
                if (ignore is not null && Named(ignore, "Condition") is not ("0" or "2" or "3"))
                    throw Refuse("Directional ignore semantics: " + type.Id + "." + member.Name);
                if (member.Kind == "field" && Option("IncludeFields") != "true" &&
                    Attribute(member.Attributes, "JsonIncludeAttribute") is null)
                    continue;
                if (output && !member.Readable)
                    continue;
                foreach (var attribute in member.Attributes)
                    if (attribute.Name == JsonPrefix + "JsonNumberHandlingAttribute" &&
                        attribute.Arguments.Single().Json != "0")
                        throw Refuse("Non-default number handling: " + type.Id + "." + member.Name);
                var reference = member.Type;
                if (member.Attributes.Any(value => value.Name == "System.Diagnostics.CodeAnalysis." +
                        (output ? "MaybeNullAttribute" : "AllowNullAttribute")))
                    reference = reference with
                    {
                        Nullability = "Annotated"
                    };
                if (member.Attributes.Any(value =>
                        value.Name == "System.Diagnostics.CodeAnalysis." +
                        (output ? "NotNullAttribute" : "DisallowNullAttribute")))
                    reference = NonNullable(reference);
                var property = Read(reference);
                properties.Add(member.WireName, property);
                var condition = ignore is null ? Option("DefaultIgnoreCondition", "0") : Named(ignore, "Condition");
                var canOmit = condition == "2" || condition == "3" &&
                    (member.Type.Kind is "nullable" or "named" or "array" or "collection" ||
                        member.Type.Name == "System_String");
                if (output
                        ? !canOmit
                        : member.Required || member.JsonRequired ||
                        constructor?.Parameters.Any(parameter =>
                            parameter.Member == member.Name && !parameter.Optional) == true)
                    required.Add(member.WireName);
            }

            var value = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties
            };
            if (required.Count != 0)
                value["required"] = required;
            var handling = Attribute(type.Attributes, "JsonUnmappedMemberHandlingAttribute")?.Arguments.Single().Json;
            if ((handling ?? Option("UnmappedMemberHandling", "0")) == "1")
                value["additionalProperties"] = false;
            return value;
        }
    }

    private static JsonObject Nullable(JsonObject value) => new()
    {
        ["anyOf"] = new JsonArray(
            value,
            new JsonObject
            {
                ["type"] = "null"
            })
    };

    private static NativeAttribute? Attribute(NativeAttribute[] attributes, string name) =>
        attributes.SingleOrDefault(attribute => attribute.Name == JsonPrefix + name);

    private static string? Named(NativeAttribute attribute, string name) =>
        attribute.NamedArguments.SingleOrDefault(argument => argument.Name == name)?.Value.Json;

    private static InvalidDataException Refuse(string reason) => new("WebProjectionUnsupported: " + reason);
}
