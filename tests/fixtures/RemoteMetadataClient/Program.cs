using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis.Composition;

var text = TypertCodec.Create(MetadataJson.Default.String, "string", Json("""{"type":"string"}"""));
var payload = TypertCodec.Create(MetadataJson.Default.MetadataPayload, "MetadataPayload");
var recursive = TypertCodec.Create(MetadataJson.Default.MetadataNode, "MetadataNode");
var dictionary = TypertCodec.Create(MetadataJson.Default.DictionaryStringString, "Dictionary<string,string>");
var overlap = JsonCodec("overlap", """{"oneOf":[{"type":"string"},{"type":"string","enum":["selected"]}]}""");
var tuple = JsonCodec(
    "tuple",
    """{"type":"array","prefixItems":[{"type":"string"}],"items":false,"minItems":1,"maxItems":1}""");
var binary = TypertCodec.Create(MetadataJson.Default.ByteArray, "byte[]");

TypertInvocationDescriptor Method(string name, TypertCodec codec) => new(
    "metadata-package#metadata/" + name,
    "metadata-service",
    "metadata",
    name,
    [new("value", "value", codec)],
    codec);

var methods = new[]
{
    Method("echo", payload) with
    {
        Description = "Echo the declared payload.",
        ResultDescription = "The returned payload.",
        Parameters =
        [
            new("payload", "value", payload)
            {
                Description = "The supplied payload."
            }
        ],
        Cancellation = true
    },
    Method("optional", text) with
    {
        Parameters =
        [
            new("label", "label", TypertCodec.CreateOptional<string>(text))
            {
                AcceptsUndefined = true
            }
        ]
    },
    Method("lookup", text) with
    {
        Parameters =
        [
            new("document", "documentId", text)
            {
                Lookup = "document",
                AcceptsUndefined = true
            }
        ],
        Scope = new("document", "documentId")
    },
    Method("context", text) with
    {
        Invocation = new("agent", "agentId", text)
    },
    Method("duplex", text) with
    {
        IsStream = true,
        Uplink = text
    },
    Method("optional-uplink", text) with
    {
        IsStream = true,
        Uplink = TypertCodec.CreateOptional<string>(text)
    },
    Method("required-optional", text) with
    {
        Parameters = [new("value", "value", TypertCodec.CreateOptional<string>(text))]
    },
    Method("json", JsonCodec("json", "{}")),
    Method("recursive", recursive) with
    {
        ResultDescription = "Recursive result documentation."
    },
    Method("dictionary", dictionary),
    Method("overlap", overlap),
    Method("tuple", tuple),
    Method(
        "composed",
        JsonCodec(
            "composed",
            """{"allOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]},{"type":"object","properties":{"b":{"type":"string"}},"required":["b"]}]}""")),
    Method(
        "ref-siblings",
        JsonCodec(
            "ref-siblings",
            """{"$ref":"#/$defs/a","type":"object","properties":{"b":{"type":"string"}},"required":["b"],"$defs":{"a":{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]}}}""")),
    Method(
        "composed-union",
        JsonCodec(
            "composed-union",
            """{"allOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]},{"anyOf":[{"type":"object","properties":{"b":{"type":"string"}},"required":["b"]},{"type":"object","properties":{"c":{"type":"string"}},"required":["c"]}]}]}""")),
    Method("optional-result", text) with
    {
        Result = TypertCodec.CreateOptional<string>(text)
    },
    Method("void", text) with
    {
        Result = TypertCodec.Void
    },
    Method(
        "prototype-field",
        JsonCodec(
            "prototype-field",
            """{"type":"object","properties":{"__proto__":{"type":"string"}},"required":["__proto__"]}""")),
    Method("binary", binary)
};
var artifact = TypertArtifacts.GenerateClient(TypertArtifacts.Contribution("metadata-package", "Metadata", methods));
Directory.CreateDirectory(args[0]);
foreach (var (name, content) in new[]
         {
             ("remote.mjs", artifact.Module),
             ("remote.d.mts", artifact.Declaration),
             ("cordis-remote.mjs", TypertClientRuntime.Module),
             ("cordis-remote.d.mts", TypertClientRuntime.Declaration)
         })
    await File.WriteAllTextAsync(Path.Combine(args[0], name), content);

TypertCodec JsonCodec(string name, string schema) =>
    TypertCodec.Create(MetadataJson.Default.JsonElement, name, Json(schema));

static JsonElement Json(string text)
{
    using var document = JsonDocument.Parse(text);
    return document.RootElement.Clone();
}

public sealed record MetadataPayload(string Name, MetadataNested Nested);

public sealed record MetadataNested(string Label);

public sealed record MetadataNode(string Name, MetadataNode? Next);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(MetadataPayload))]
[JsonSerializable(typeof(MetadataNode))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(byte[]))]
internal partial class MetadataJson : JsonSerializerContext;
