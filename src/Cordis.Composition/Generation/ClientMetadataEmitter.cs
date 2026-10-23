using System.Text;
using System.Text.Json;

namespace Cordis.Typert;

// Metadata and the callable client consume the same directional schema facts.
internal static class ClientMetadataEmitter
{
    internal static ClientArtifact Generate(IReadOnlyList<ClientMethod> methods, string package)
    {
        var source = new StringBuilder();
        var definitions = new StringBuilder("export declare const info: RemoteInfo<{\n");
        var specs = new StringBuilder("export const info = createRemoteInfo({package:")
            .Append(Quote(package))
            .Append(", methods:{\n");
        var index = 0;
        foreach (var method in methods)
        {
            specs
                .Append("  ")
                .Append(Quote(method.Endpoint))
                .Append(": {service:")
                .Append(Quote(method.Service ?? method.Namespace));
            Text("id", method.Id);
            Text("implementation", method.Implementation);
            Text("description", method.Description);
            if (method.IsStream)
                specs.Append(", mode:'stream'");
            if (method.Cancellation)
                specs.Append(", cancellation:{parameter:'signal'}");
            specs.Append(", invocation:");
            if (method.Invocation is { } invocation)
            {
                specs
                    .Append("{kind:'context', context:")
                    .Append(Quote(invocation.Context))
                    .Append(", wire:")
                    .Append(Quote(invocation.Wire))
                    .Append(", codec:")
                    .Append(Codec(invocation.Codec, false))
                    .Append('}');
            }
            else
                specs.Append("{kind:'direct'}");

            if (method.Scope is { } scope)
                specs
                    .Append(", scope:{context:")
                    .Append(Quote(scope.Context))
                    .Append(", wire:")
                    .Append(Quote(scope.Wire))
                    .Append('}');
            specs.Append(", parameters:[");
            foreach (var parameter in method.Parameters)
            {
                specs
                    .Append("{name:")
                    .Append(Quote(parameter.Name ?? parameter.Wire))
                    .Append(", wire:")
                    .Append(Quote(parameter.Wire))
                    .Append(", source:")
                    .Append(Quote(parameter.Lookup is null ? "json" : "lookup"));
                Text("lookup", parameter.Lookup);
                Text("description", parameter.Description);
                if (parameter.AcceptsUndefined && parameter.Lookup is null)
                    specs.Append(", acceptsUndefined:true");
                specs
                    .Append(", codec:")
                    .Append(
                        Codec(
                            parameter.Codec,
                            false,
                            optionalInput: parameter.Lookup is null && parameter.AcceptsUndefined))
                    .Append(", strictCodec:")
                    .Append(
                        Codec(
                            parameter.Codec,
                            false,
                            true,
                            optionalInput: parameter.Lookup is null && parameter.AcceptsUndefined))
                    .Append("},");
            }

            specs.Append("], result:").Append(Codec(method.Result, !method.IsStream, result: true));
            if (method.Uplink is { } uplink)
                specs.Append(", uplink:{codec:").Append(Codec(uplink, false)).Append('}');
            specs.Append("},\n");
            if (method.Invocation is null)
            {
                var endpoint = Quote(method.Endpoint);
                definitions
                    .Append("  readonly ")
                    .Append(endpoint)
                    .Append(": RemoteMethod<Parameters<RemoteClient[")
                    .Append(endpoint)
                    .Append("]>[0], ReturnType<(typeof resultCodecs)[")
                    .Append(endpoint)
                    .Append("][\"parse\"]>, ReturnType<RemoteClient[")
                    .Append(endpoint)
                    .Append("]>, RemoteServices>;\n");
            }
        }

        return new(source.Append(specs).Append("}});\n").ToString(), definitions.Append("}>;\n").ToString());

        void Text(string name, string? value)
        {
            if (value is not null)
                specs.Append(", ").Append(name).Append(':').Append(Quote(value));
        }

        string Codec(
            ClientCodec codec,
            bool binary,
            bool strict = false,
            bool result = false,
            bool optionalInput = false)
        {
            var name = "metadataCodec" + index++;
            source.Append("function ").Append(name).Append("Factory() {\n");
            if (strict)
                source.Append(ClientInputCodecEmitter.Generate(codec, name));
            else
                source
                    .Append(ResultCodecEmitter.Generate(codec, name, binary).Source)
                    .Append("return ")
                    .Append(name)
                    .Append(";\n");
            source.Append("}\n");
            string projection;
            try
            {
                if (codec.IsVoid || codec.AcceptsUndefined && !optionalInput)
                    throw new ToolSchemaException("an absent value cannot be represented as JSON");
                if (result)
                    ToolSchemaProjection.RequireJsonResult(codec.ResultMetadata);
                var schema = ToolSchemaProjection.Project(codec.Schema, strict);
                projection = "{jsonSchema:JSON.parse(" + Quote(schema.GetRawText()) + ")}";
            }
            catch (ToolSchemaException error)
            {
                projection = "{jsonSchemaError:" + Quote(error.Message) + "}";
            }

            return "{typeSymbol:" + Quote(codec.TypeSymbol) +
                (codec.Description is null ? "" : ", description:" + Quote(codec.Description)) +
                ", create:" + name + "Factory, project:() => (" + projection + ")}";
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, ClientEmitterJson.Default.String);
}
