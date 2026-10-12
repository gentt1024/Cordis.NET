using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>Generated TypeScript module and declaration, produced from the same Host descriptors.</summary>
public sealed record TypertClientArtifact(string Module, string Declaration);

/// <summary>Native emitters consuming compiler-independent Typert metadata.</summary>
public static class TypertArtifacts
{
    /// <summary>Build package schemas and reflected method signatures from compiler-generated descriptors.</summary>
    public static TypertContribution Contribution(
        string package,
        string exportName,
        IReadOnlyList<TypertInvocationDescriptor> descriptors)
    {
        var schemas = descriptors
            .SelectMany(descriptor => descriptor
                .Parameters.Select(parameter => parameter.Codec)
                .Append(descriptor.Result)
                .Concat(descriptor.Invocation is { } receiver ? [receiver.Codec] : []))
            .DistinctBy(codec => codec.TypeSymbol)
            .Select(codec => new TypertSchemaFactory(codec.TypeSymbol, () => codec.Schema))
            .ToArray();
        var services = descriptors
            .GroupBy(descriptor => descriptor.Service)
            .Select(group => new TypertServiceModel(
                group.Key,
                exportName,
                group
                    .Select(descriptor => new TypertMemberModel(
                        "method",
                        descriptor.Method,
                        "(" + string.Join(
                            ", ",
                            descriptor.Parameters.Select(parameter =>
                                parameter.Name + (parameter.AcceptsUndefined ? "?" : "") + ": " +
                                parameter.Codec.TypeSymbol)) +
                        "): " + (descriptor.IsStream ? "RemoteStream<" : "Promise<") + descriptor.Result.TypeSymbol +
                        ">"))
                    .ToArray(),
                []))
            .ToArray();
        return new(package, "host", schemas, new(services, [], []), descriptors);
    }

    /// <summary>Emit typed Client methods borrowing the shared Connection runtime. Unsupported schema constructs fail generation.</summary>
    public static TypertClientArtifact GenerateClient(TypertContribution contribution)
    {
        ArgumentNullException.ThrowIfNull(contribution);
        if (contribution
            .Invocations.GroupBy(descriptor => descriptor.Endpoint, StringComparer.Ordinal)
            .Any(group => group.Skip(1).Any()))
            throw new ArgumentException(
                "A Client contribution cannot contain duplicate Remote endpoints.",
                nameof(contribution));
        var definitions = new StringBuilder(
            "import type { TypertConnection, RemoteResult, RemoteStreamHandle, RemoteDuplexHandle, RemoteOwner } from './cordis-remote.mjs';\n" +
            "export { TypertConnection, RemoteError, RemoteStreamCarrierError } from './cordis-remote.mjs';\n" +
            "export type { RemoteResult, RemoteStreamHandle, RemoteDuplexHandle, RemoteOwner } from './cordis-remote.mjs';\n");
        var methods = new StringBuilder("export interface RemoteClient {\n  dispose(): Promise<void>;\n");
        var runtime = new StringBuilder(
            "import { createClient, mountClient } from './cordis-remote.mjs';\n" +
            "export { TypertConnection, RemoteError, RemoteStreamCarrierError } from './cordis-remote.mjs';\n" +
            "export function createRemote(connection) { return createClient(connection, [\n");
        var index = 0;
        foreach (var descriptor in contribution.Invocations)
        {
            var fields = new List<string>();
            foreach (var parameter in descriptor.Parameters)
            {
                var type = "Boundary" + index++;
                AppendType(definitions, type, parameter.Codec.Schema);
                fields.Add(Quote(parameter.Wire) + (parameter.AcceptsUndefined ? "?" : "") + ": " + type);
            }

            if (descriptor.Invocation is { } receiver)
            {
                var type = "Boundary" + index++;
                AppendType(definitions, type, receiver.Codec.Schema);
                fields.Add(Quote(receiver.Wire) + ": " + type);
            }

            var result = "Boundary" + index++;
            if (ReferenceEquals(descriptor.Result, TypertCodec.Void))
                definitions.Append("export type ").Append(result).Append(" = undefined;\n");
            else
                AppendType(
                    definitions,
                    result,
                    descriptor.Result.Schema,
                    descriptor.IsStream ? null : descriptor.Result.ResultMetadata);
            string? input = null;
            if (descriptor.Uplink is { } uplink)
            {
                input = "Boundary" + index++;
                AppendType(definitions, input, uplink.Schema);
                if (uplink.AcceptsUndefined)
                    input += " | undefined";
            }

            methods
                .Append("  ")
                .Append(Quote(descriptor.Endpoint))
                .Append("(args: { ")
                .Append(string.Join("; ", fields))
                .Append(" }, signal?: AbortSignal): ")
                .Append(
                    descriptor.IsStream
                        ? input is null ? "RemoteStreamHandle<" : "RemoteDuplexHandle<"
                        : "Promise<RemoteResult<")
                .Append(result)
                .Append(input is null ? "" : ", " + input)
                .Append(descriptor.IsStream ? ">;\n" : ">>;\n");
            runtime
                .Append("  [")
                .Append(Quote(descriptor.Endpoint))
                .Append(", ")
                .Append(descriptor.IsStream ? "true" : "false")
                .Append(", ")
                .Append(input is null ? "false" : "true")
                .Append("],\n");
        }

        methods.Append("}\nexport declare function createRemote(connection: TypertConnection): RemoteClient;\n");
        runtime.Append("]); }\n");
        runtime.Append("const namespaceSpecs = [\n");
        foreach (var group in contribution.Invocations.GroupBy(descriptor => descriptor.Namespace))
            runtime
                .Append("  [")
                .Append(Quote("remote." + group.Key))
                .Append(", [")
                .Append(
                    string.Join(
                        ", ",
                        group.Select(descriptor =>
                            "[" + Quote(descriptor.Method) + ", " + Quote(descriptor.Endpoint) + "]")))
                .Append("]],\n");
        runtime.Append(
            "];\nexport function mountRemote(ctx, connection) { return mountClient(ctx, createRemote(connection), namespaceSpecs); }\n");
        methods.Append(
            "export declare function mountRemote(ctx: RemoteOwner, connection: TypertConnection): Promise<{remote: RemoteClient; dispose: () => Promise<void>}>;\n");
        return new(runtime.ToString(), definitions.Append(methods).ToString());
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, TypertJson.Default.String);

    private static void AppendType(StringBuilder output, string name, JsonElement schema, JsonTypeInfo? metadata = null)
    {
        var refs = new Dictionary<(string, JsonTypeInfo?), string>()
        {
            [("#", metadata)] = name
        };
        var pending = new Queue<(string Name, JsonElement Schema, JsonTypeInfo? Metadata)>();
        pending.Enqueue((name, schema, metadata));
        while (pending.TryDequeue(out var type))
            output
                .Append("export type ")
                .Append(type.Name)
                .Append(" = ")
                .Append(Render(type.Schema, type.Metadata))
                .Append(";\n");

        string Render(JsonElement node, JsonTypeInfo? native)
        {
            if (node.ValueKind == JsonValueKind.True)
                return "unknown";
            if (node.ValueKind == JsonValueKind.False)
                return "never";
            if (node.ValueKind != JsonValueKind.Object)
                throw new NotSupportedException("Invalid JSON Schema type model.");
            if (node.TryGetProperty("$ref", out var reference))
            {
                var path = reference.GetString()!;
                if (refs.TryGetValue((path, native), out var known))
                    return known;
                if (!path.StartsWith("#/", StringComparison.Ordinal))
                    throw new NotSupportedException("Only local schema references are supported.");
                var target = schema;
                foreach (var segment in path[2..].Split('/'))
                {
                    var key = segment
                        .Replace("~1", "/", StringComparison.Ordinal)
                        .Replace("~0", "~", StringComparison.Ordinal);
                    target = target.ValueKind == JsonValueKind.Array
                        ? target[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)]
                        : target.GetProperty(key);
                }

                var alias = name + "Ref" + refs.Count;
                refs.Add((path, native), alias);
                pending.Enqueue((alias, target, native));
                return alias;
            }

            if (node.TryGetProperty("enum", out var values))
                return string.Join(" | ", values.EnumerateArray().Select(value => value.GetRawText()));
            if (node.TryGetProperty("const", out var constant))
                return constant.GetRawText();
            foreach (var union in new[] { "anyOf", "oneOf" })
                if (node.TryGetProperty(union, out var choices))
                    return string.Join(" | ", choices.EnumerateArray().Select(child => Render(child, native)));
            if (node.TryGetProperty("allOf", out var requirements))
                return string.Join(" & ", requirements.EnumerateArray().Select(child => Render(child, native)));
            if (TypertBinaryResult.IsBinary(native) &&
                !(node.TryGetProperty("type", out var nullKind) && nullKind.ValueKind == JsonValueKind.String &&
                    nullKind.GetString() == "null"))
                return node.TryGetProperty("type", out var binaryKind) && binaryKind.ValueKind == JsonValueKind.Array &&
                    binaryKind.EnumerateArray().Any(value => value.GetString() == "null")
                        ? "Uint8Array | null"
                        : "Uint8Array";
            if (!node.TryGetProperty("type", out var kind))
            {
                if (node.TryGetProperty("properties", out _))
                    return ObjectType(node, native);
                if (node
                    .EnumerateObject()
                    .All(property => property.Name is "description" or "title" or "$schema" or "$id"))
                    return "unknown";
                throw new NotSupportedException("The generated schema has no supported type projection.");
            }

            var types = kind.ValueKind == JsonValueKind.Array
                ? kind.EnumerateArray().Select(value => value.GetString()!)
                : [kind.GetString()!];
            return string.Join(
                " | ",
                types.Select(value => value switch
                {
                    "string" => "string",
                    "integer" or "number" => "number",
                    "boolean" => "boolean",
                    "null" => "null",
                    "array" => ArrayType(node, native),
                    "object" => ObjectType(node, native),
                    _ => throw new NotSupportedException("Unsupported schema type " + value),
                }));
        }

        string ArrayType(JsonElement node, JsonTypeInfo? native)
        {
            var element = native?.Kind == JsonTypeInfoKind.Enumerable && native.ElementType is { } elementType
                ? native.Options.GetTypeInfo(elementType)
                : null;
            var hasItems = node.TryGetProperty("items", out var items);
            if (!node.TryGetProperty("prefixItems", out var prefix))
                return "ReadonlyArray<" + (hasItems ? Render(items, element) : "unknown") + ">";
            var count = prefix.GetArrayLength();
            var minimum = node.TryGetProperty("minItems", out var minItems) ? minItems.GetInt32() : 0;
            int? maximum = node.TryGetProperty("maxItems", out var maxItems) ? maxItems.GetInt32() : null;
            if (hasItems && items.ValueKind == JsonValueKind.False)
                maximum = Math.Min(maximum ?? count, count);
            if (minimum < 0 || minimum > count || maximum is < 0 || maximum < minimum || maximum > count)
                throw new NotSupportedException(
                    "Tuple projection requires a minimum within its prefix and no finite tail length limit.");
            var length = maximum ?? count;
            var elements = Enumerable
                .Range(0, length)
                .Select(index =>
                    index < minimum ? Render(prefix[index], element) : "(" + Render(prefix[index], element) + ")?")
                .ToList();
            if (maximum is null)
                elements.Add("...Array<" + (hasItems ? Render(items, element) : "unknown") + ">");
            return "readonly [" + string.Join(", ", elements) + "]";
        }

        string ObjectType(JsonElement node, JsonTypeInfo? native)
        {
            var required = node.TryGetProperty("required", out var requiredFields)
                ? requiredFields.EnumerateArray().Select(field => field.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
            var fields = node.TryGetProperty("properties", out var properties)
                ? properties
                    .EnumerateObject()
                    .Select(field =>
                        "readonly " + Quote(field.Name) + (required.Contains(field.Name) ? "" : "?") + ": " +
                        Render(field.Value, TypertBinaryResult.Property(native, field.Name)))
                    .ToList()
                : [];
            if (node.TryGetProperty("additionalProperties", out var additional) &&
                additional.ValueKind != JsonValueKind.False)
                fields.Add(
                    "readonly [key: string]: " + Render(
                        additional,
                        native?.Kind == JsonTypeInfoKind.Dictionary && native.ElementType is { } elementType
                            ? native.Options.GetTypeInfo(elementType)
                            : null));
            return "{ " + string.Join("; ", fields) + " }";
        }
    }
}
