using Cordis.Typert;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Cordis.Composition;

/// <summary>Generated TypeScript module and declaration, produced from the same Host descriptors.</summary>
public sealed record TypertClientArtifact(string Module, string Declaration);

/// <summary>Native emitters consuming compiler-independent Typert metadata.</summary>
public static partial class TypertArtifacts
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

        static ClientCodec Codec(TypertCodec codec, bool output = false) => new(
            output ? codec.ResultSchema : codec.Schema,
            ReferenceEquals(codec, TypertCodec.Void),
            codec.AcceptsUndefined,
            codec.ResultMetadata,
            codec.TypeSymbol);

        var artifact = ClientEmitter.Generate(
            contribution
                .Invocations.Select(descriptor => new ClientMethod(
                    descriptor.Endpoint,
                    descriptor.Namespace,
                    descriptor.Method,
                    descriptor
                        .Parameters.Select(parameter => new ClientParameter(
                            parameter.Wire,
                            Codec(parameter.Codec),
                            parameter.AcceptsUndefined,
                            parameter.Lookup,
                            parameter.Name,
                            parameter.Description))
                        .ToArray(),
                    Codec(descriptor.Result, output: true) with
                    {
                        Description = descriptor.ResultDescription
                    },
                    descriptor.IsStream,
                    descriptor.Invocation is { } receiver
                        ? new(receiver.Context, receiver.Wire, Codec(receiver.Codec))
                        : null,
                    descriptor.Scope is { } scope ? new(scope.Context, scope.Wire) : null,
                    descriptor.Uplink is { } uplink ? Codec(uplink) : null,
                    descriptor.Service,
                    descriptor.Id,
                    descriptor.Implementation,
                    descriptor.Description,
                    descriptor.Cancellation))
                .ToArray(),
            contribution.Package);
        return new(artifact.Module, artifact.Declaration);
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, TypertJson.Default.String);

    private static void AppendType(
        StringBuilder output,
        string name,
        JsonElement schema,
        JsonTypeInfo? metadata = null) =>
        ClientEmitter.AppendType(output, name, schema, metadata);
}
