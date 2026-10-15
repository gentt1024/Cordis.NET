namespace Cordis.Typert.Generator.Model;

internal sealed partial class DotnetProjectionEmitter
{
    internal static string EmitEvent(
        DeclarationModelDocument model,
        RemoteEventContract contract,
        DeclarationModelDocument[] references,
        string targetNamespace,
        string client)
    {
        var data = DeclarationRemoteProjection.EventData(model, contract, references);
        var roots = data
            .Args.Concat(data.Request is { } request ? [request] : [])
            .Concat(data.Result is { } resultRoot ? [resultRoot] : [])
            .ToArray();
        var emitter = new DotnetProjectionEmitter(data.Types, client);
        var context = client + "Json";
        var source = EmitData(data.Types, roots, model.ContractIdentity, targetNamespace, context, data.Serializer);

        string Codec(NativeTypeReference type) => emitter.UplinkCodec(type);

        var output = new System.Text.StringBuilder(source)
            .Append("\n#pragma warning disable CS1591\npublic static class ")
            .Append(Identifier(client))
            .Append(" {\n");
        if (data.Request is null)
        {
            var parameters = string.Join(",", data.Args.Select(emitter.TypeName));
            output
                .Append(
                    "public static global::Cordis.EffectHandle On(global::Cordis.Composition.TypertRemoteEventsClient client, global::Cordis.Context owner, global::System.Action")
                .Append(data.Args.Length == 0 ? "" : "<" + parameters + ">")
                .Append(" listener) => client.On(owner, ")
                .Append(Literal(contract.Name))
                .Append(", (_, args) => { if(args.Length != ")
                .Append(data.Args.Length)
                .Append(
                    ") throw new global::System.Text.Json.JsonException(\"Invalid event argument count.\"); listener(")
                .Append(
                    string.Join(
                        ",",
                        data.Args.Select((type, index) =>
                            "(" + emitter.TypeName(type) + ")" + Codec(type) +
                            ".Decode((global::System.Text.Json.JsonElement)args[" + index + "]!)!")))
                .Append("); return global::Cordis.Undefined.Value; });\n");
        }
        else
        {
            var result = emitter.TypeName(data.Result!);
            var codec = Codec(data.Result!);
            if (data.Optional)
            {
                codec = "global::Cordis.Composition.TypertCodec.CreateOptional<" + result + ">(" + codec + ")";
                result = "global::Cordis.Composition.RemoteOptional<" + result + ">";
            }

            output
                .Append(
                    "public static global::Cordis.EffectHandle On(global::Cordis.Composition.TypertRemoteEventsClient client, global::Cordis.Context owner, global::System.Func<global::Cordis.Context,")
                .Append(emitter.TypeName(data.Request))
                .Append(
                    ",global::System.Func<global::System.Threading.Tasks.Task<global::Cordis.Composition.RemoteEventResult<")
                .Append(result)
                .Append(
                    ">>>,global::System.Threading.CancellationToken,global::System.Threading.Tasks.Task<global::Cordis.Composition.RemoteEventResult<")
                .Append(result)
                .Append(">>> listener) => global::Cordis.Composition.TypertEventListeners.On(client, owner, ")
                .Append(Literal(contract.Name))
                .Append(',')
                .Append(Codec(data.Request))
                .Append(',')
                .Append(codec)
                .Append(", listener);\n");
        }

        return output.Append(emitter.nullabilityHelpers).Append("}\n").ToString();
    }
}
