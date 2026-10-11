using System.Text.Json;
using Cordis.Composition;

namespace IndependentRemote;

public static class TupleContract
{
    private const string Closed =
        """{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}],"items":false,"minItems":2,"maxItems":2}""";

    private static readonly IReadOnlyDictionary<string, string> Variants = new Dictionary<string, string>
    {
        ["tuple-nested"] =
            """{"type":"array","prefixItems":[{"type":"string"},{"$ref":"#/$defs/pair"}],"items":false,"minItems":2,"maxItems":2,"$defs":{"pair":{"type":"array","prefixItems":[{"type":"integer"},{"type":"boolean"}],"items":false,"minItems":2,"maxItems":2}}}""",
        ["tuple-optional"] =
            """{"type":"array","prefixItems":[{"type":"string"},{"type":"integer"}],"items":false,"minItems":1,"maxItems":2}""",
        ["tuple-rest"] =
            """{"type":"array","prefixItems":[{"type":"string"}],"items":{"type":"boolean"},"minItems":1}""",
        ["tuple-open"] =
            """{"type":"array","prefixItems":[{"type":"string"}]}""",
    };

    public static TypertContribution Apply(TypertContribution contribution) => Replace(contribution, "closed", Closed);

    public static IReadOnlyDictionary<string, TypertClientArtifact> ClientContracts(TypertContribution contribution) =>
        Variants.ToDictionary(
            pair => pair.Key,
            pair => TypertArtifacts.GenerateClient(Replace(contribution, pair.Key, pair.Value)));

    public static void Verify(TypertContribution contribution)
    {
        Check(Codec(contribution), ["""["tuple",4]"""], ["""[4,"tuple"]""", """["tuple"]""", """["tuple",4,true]"""]);
        Check(Variant("tuple-nested"), ["""["tuple",[4,true]]"""], ["""["tuple",[true,4]]"""]);
        Check(
            Variant("tuple-optional"),
            ["""["tuple"]""", """["tuple",4]"""],
            ["[]", """["tuple","wrong"]""", """["tuple",4,true]"""]);
        Check(Variant("tuple-rest"), ["""["tuple"]""", """["tuple",true,false]"""], ["[]", """["tuple",4]"""]);
        Check(Variant("tuple-open"), ["[]", """["tuple",null,{}]"""], ["[4]"]);
        foreach (var schema in new[]
                 {
                     """{"type":"array","prefixItems":[{"type":"string"}],"items":{"type":"boolean"},"minItems":2}""",
                     """{"type":"array","prefixItems":[{"type":"string"}],"items":{"type":"boolean"},"maxItems":3}""",
                 })
        {
            try
            {
                TypertArtifacts.GenerateClient(Replace(contribution, "unsupported", schema));
            }
            catch (NotSupportedException)
            {
                continue;
            }

            throw new InvalidOperationException("An unsupported tuple length contract was silently projected.");
        }

        TypertCodec Variant(string name) => Codec(Replace(contribution, name, Variants[name]));
    }

    private static TypertContribution Replace(TypertContribution contribution, string name, string schema)
    {
        var codec = TypertCodec.Create(RemoteJson.Default.JsonElement, "IndependentRemote.Tuple." + name, Json(schema));
        var descriptors = contribution
            .Invocations.Select(descriptor => descriptor.Method == "Tuple"
                ? descriptor with
                {
                    Parameters =
                    [
                        descriptor.Parameters.Single() with
                        {
                            Codec = codec
                        }
                    ],
                    Result = codec,
                }
                : descriptor)
            .ToArray();
        return TypertArtifacts.Contribution(
            contribution.Package,
            contribution.Model.Services.Single().ExportName,
            descriptors);
    }

    private static TypertCodec Codec(TypertContribution contribution) =>
        contribution.Invocations.Single(descriptor => descriptor.Method == "Tuple").Parameters.Single().Codec;

    private static void Check(TypertCodec codec, string[] accepted, string[] refused)
    {
        foreach (var text in accepted)
        {
            var value = Json(text);
            if (codec.Decode(value) is not JsonElement decoded || !JsonElement.DeepEquals(value, decoded))
                throw new InvalidOperationException("Tuple decoding changed a valid value.");
        }

        foreach (var text in refused)
        {
            try
            {
                codec.Decode(Json(text));
            }
            catch (JsonException)
            {
                continue;
            }

            throw new InvalidOperationException("Tuple decoding accepted an invalid value: " + text);
        }
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
