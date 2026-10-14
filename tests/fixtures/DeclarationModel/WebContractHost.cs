using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using IndependentDeclarations;
using IndependentDeclarations.External;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var directory = args[0];
var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "remote.schema.json")))!;
var contribution = WebContractRemoteTypert.Contribution("web-contract-provider");
var runtime = TypertArtifacts.GenerateClient(contribution);
Directory.CreateDirectory(Path.Combine(directory, "runtime"));
File.WriteAllText(Path.Combine(directory, "runtime/remote.mjs"), runtime.Module);
File.WriteAllText(Path.Combine(directory, "runtime/remote.d.mts"), runtime.Declaration);
var schemas = new JsonObject();
foreach (var method in contribution.Invocations)
    foreach (var parameter in method.Parameters)
        schemas[method.Endpoint + "/" + parameter.Wire] = JsonNode.Parse(parameter.Codec.Schema.GetRawText());
schemas["stj/required"] = WebContractJson.Default.RequiredConstructor.GetJsonSchemaAsNode();
schemas["stj/optional"] = WebContractJson.Default.OptionalConstructor.GetJsonSchemaAsNode();
schemas["stj/selected"] = WebContractJson.Default.SelectedConstructor.GetJsonSchemaAsNode();
var encoded = contribution
    .Invocations.Single(method => method.Endpoint == "webContract/direction")
    .Result.Encode(
        new DirectionalData
        {
            OutputNullable = null!
        });
schemas["authorAccessorMetadata"] = new JsonObject();
foreach (var property in WebContractJson.Default.DirectionalData.Properties)
    schemas["authorAccessorMetadata"]![property.Name] = new JsonObject
    {
        ["getterNullable"] = property.IsGetNullable,
        ["setterNullable"] = property.IsSetNullable
    };
var resultSchema = catalog.DeepClone();
resultSchema.AsObject().Remove("$schema");
resultSchema.AsObject().Remove("$comment");
resultSchema["$ref"] = "#/$defs/webContract~1direction~1result";
using var schemaDocument = JsonDocument.Parse(resultSchema.ToJsonString());
TypertCodec.Create(WebContractJson.Default.JsonElement, schema: schemaDocument.RootElement).Decode(encoded);
var directionCodec = contribution.Invocations.Single(method => method.Endpoint == "webContract/direction").Result;
var exportedSchemaPath = Path.Combine(directory, "result.schema.json");
File.WriteAllText(exportedSchemaPath, directionCodec.ResultSchema.GetRawText());
using (var exportedSchema = JsonDocument.Parse(File.ReadAllText(exportedSchemaPath)))
{
    var outputCodec = TypertCodec.Create(WebContractJson.Default.JsonElement, schema: exportedSchema.RootElement);
    outputCodec.Decode(encoded);
    var decoded = (DirectionalData)directionCodec.DecodeOutput(encoded)!;
    if (decoded.Count != 0 || decoded.OutputNullable is not null || decoded.InputNullable != "initial")
        throw new Exception("Public output schema changed actual output decoding.");
    if (encoded.TryGetProperty("Count", out _))
        throw new Exception("Result serialization lost conditional output omission.");
    var invalid = JsonNode.Parse(encoded.GetRawText())!;
    invalid["InputNullable"] = null;
    try
    {
        using var invalidDocument = JsonDocument.Parse(invalid.ToJsonString());
        outputCodec.Decode(invalidDocument.RootElement);
        throw new Exception("Public output schema weakened getter nullability.");
    }
    catch (JsonException)
    {
    }

    try
    {
        directionCodec.Decode(encoded);
        throw new Exception("Input codec accepted an output-only shape.");
    }
    catch (JsonException)
    {
    }
}

using (var unsupported = JsonDocument.Parse("""{"type":"string","pattern":"^x$"}"""))
{
    var unsupportedCodec = TypertCodec.Create(WebContractJson.Default.String, schema: unsupported.RootElement);
    try
    {
        _ = unsupportedCodec.ResultSchema;
        throw new Exception("Public output schema bypassed lazy unsupported-keyword refusal.");
    }
    catch (NotSupportedException error) when (error.Message.Contains("pattern", StringComparison.Ordinal))
    {
    }
}

schemas["publicResultSchema"] = JsonNode.Parse(directionCodec.ResultSchema.GetRawText());
schemas["serializedResult"] = JsonNode.Parse(encoded.GetRawText());
File.WriteAllText(Path.Combine(directory, "host-schemas.json"), schemas.ToJsonString());

await using var root = new Context();
var registry = new TypertRegistry(root);
TypertHostConnection connection = null!;
await root.RunAsync(owner =>
{
    owner.Provide("typert", registry);
    owner.Provide("webContract", new WebContractRemote());
    registry.Register(owner, contribution);
    connection = new(owner, new TypertGateway(owner, registry));
    owner.Provide("connection", connection);
    return Task.CompletedTask;
});
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
server.MapCordisRemote(
    "/remote",
    connection,
    (_, endpoint) => Task.FromResult(endpoint.StartsWith("webContract/", StringComparison.Ordinal)));
server.MapPost("/stop", () => server.Lifetime.StopApplication());
await server.StartAsync();
Console.WriteLine("READY " + server.Urls.Single());
await server.WaitForShutdownAsync();
