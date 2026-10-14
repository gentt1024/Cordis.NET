using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using IndependentDeclarations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await using var root = new Context();
var registry = new TypertRegistry(root);
TypertHostConnection connection = null!;
await root.RunAsync(owner =>
{
    owner.Provide("typert", registry);
    owner.Provide("referencedData", new ReferencedRemote());
    var contribution = ReferencedRemoteTypert.Contribution("referenced-provider");
    var resultCodec = contribution.Invocations.Single(method => method.Endpoint == "referencedData/echo").Result;
    var schemaPath = Path.Combine(AppContext.BaseDirectory, "echo-result.schema.json");
    File.WriteAllText(schemaPath, resultCodec.ResultSchema.GetRawText());
    using (var exported = System.Text.Json.JsonDocument.Parse(File.ReadAllText(schemaPath)))
    {
        var outputCodec = TypertCodec.Create(ExportedSchemaJson.Default.JsonElement, schema: exported.RootElement);
        var value = new ReferencedData
        {
            External = new IndependentDeclarations.External.ImportedData
            {
                Name = "outer",
                Code = null,
                Next = new IndependentDeclarations.External.ImportedData
                {
                    Name = "inner",
                    Code = "nested"
                }
            }
        };
        var encoded = resultCodec.Encode(value);
        outputCodec.Decode(encoded);
        var decoded = (ReferencedData)resultCodec.DecodeOutput(encoded)!;
        if (decoded.External.Next?.Name != "inner")
            throw new Exception("Exported result schema lost a recursive referenced DTO.");
    }

    registry.Register(owner, contribution);
    connection = new(owner, new TypertGateway(owner, registry));
    owner.Provide("connection", connection);
    return Task.CompletedTask;
});
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
server.UseWebSockets();
server.MapCordisRemote(
    "/remote",
    connection,
    (_, endpoint) => Task.FromResult(
        endpoint is "referencedData/echo" or "referencedData/guid" or "referencedData/nullableGuid"
            or "referencedData/item"));
server.MapPost(
    "/stop",
    () =>
    {
        server.Lifetime.StopApplication();
    });
await server.StartAsync();
Console.WriteLine("READY " + server.Urls.Single());
await server.WaitForShutdownAsync();

[System.Text.Json.Serialization.JsonSerializable(typeof(System.Text.Json.JsonElement))]
internal partial class ExportedSchemaJson : System.Text.Json.Serialization.JsonSerializerContext;
