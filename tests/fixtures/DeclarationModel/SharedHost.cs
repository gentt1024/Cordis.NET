using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
#if CORDIS_READONLY_HOST
using Author;
#else
using IndependentDeclarations;
#endif
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

await using var context = new Context();
var registry = new TypertRegistry(context);
#if !CORDIS_READONLY_HOST
EffectHandle documentLookup = null!;
EffectHandle receiverAdapter = null!;
#endif
await context.RunAsync(owner =>
{
#if CORDIS_READONLY_HOST
    owner.Provide("readonly", new ReadonlyService());
    var contribution = ReadonlyServiceTypert.Contribution("IndependentProjection.Provider");
    var resultCodec = contribution.Invocations.Single(method => method.Endpoint == "readonly/value").Result;
    var schemaPath = Path.Combine(AppContext.BaseDirectory, "result.schema.json");
    File.WriteAllText(schemaPath, resultCodec.ResultSchema.GetRawText());
    using (var exported = System.Text.Json.JsonDocument.Parse(File.ReadAllText(schemaPath)))
    {
        if (!exported.RootElement.GetProperty("properties").TryGetProperty("applies", out _))
            throw new Exception("Public result schema lost the computed read-only output member.");
        var output = resultCodec.Encode(new ReadonlyValue());
        var exportedCodec = TypertCodec.Create(ExportedSchemaJson.Default.JsonElement, schema: exported.RootElement);
        exportedCodec.Decode(output);
        if (output.GetProperty("applies").GetString() != "live" ||
            ((ReadonlyValue)resultCodec.DecodeOutput(output)!).Applies != "live")
            throw new Exception("Public result schema changed read-only serialization.");
    }
    registry.Register(owner, contribution);
#else
    owner.Metadata["prefix"] = "root";
    var receiver = owner.Extend();
    receiver.Metadata["prefix"] = "selected";
    documentLookup = registry.RegisterLookup(
        owner,
        "shared:document",
        new(
            "document",
            "documentId",
            "IndependentDeclarations.SharedDocument",
            "string",
            value => ValueTask.FromResult<object?>(
                value is "doc-1" or "doc-2" ? new SharedDocument((string)value) : null)));
    receiverAdapter = registry.RegisterHostContext(
        owner,
        "shared:receiver",
        new("receiverId", "int", value => ValueTask.FromResult<Context?>(value is 7 ? receiver : null)));
    owner.Provide("sharedData", new SharedRemote());
    registry.Register(owner, SharedRemoteTypert.Contribution("IndependentData.Provider"));
    var registeredInfo = registry.GetLocalInfo("IndependentData.Provider")!;
    if (!ReferenceEquals(registeredInfo, registry.GetLocalInfo("IndependentData.Provider")) ||
        registeredInfo.Methods.Values.Any(method => !ReferenceEquals(method, registry.GetLocal(method.Endpoint))))
        throw new Exception("Registered metadata lost its stored descriptor identity.");
#endif
    return Task.CompletedTask;
});
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
#if !CORDIS_READONLY_HOST
server.MapPost(
    "/control/withdraw-lookup",
    async () =>
    {
        await context.RunAsync(async _ => await documentLookup.DisposeAsync());
        return Results.Ok();
    });
server.MapPost(
    "/control/withdraw-context",
    async () =>
    {
        await context.RunAsync(async _ => await receiverAdapter.DisposeAsync());
        return Results.Ok();
    });
#endif
TypertHostConnection remoteConnection = null!;
await context.RunAsync(owner =>
{
    remoteConnection = new(owner, new TypertGateway(context, registry));
    owner.Provide("connection", remoteConnection);
    return Task.CompletedTask;
});
server.UseWebSockets();
#if !CORDIS_READONLY_HOST
var shared = SharedRemoteTypert.Contribution("IndependentData.Provider");
using var encodedString = System.Text.Json.JsonDocument.Parse("""{"type":"string","contentEncoding":"base64"}""");
using var binaryLiteral = System.Text.Json.JsonDocument.Parse("""{"enum":["AQID",null]}""");
using var composedBytes = System.Text.Json.JsonDocument.Parse(
    """
    {"allOf":[{"type":"string"},{"not":{"type":"null"}}]}
    """);
using var composedObject = System.Text.Json.JsonDocument.Parse(
    """
    {"allOf":[{"type":"object","properties":{"Payload":{"type":"string"}},"required":["Payload"]},{"not":{"type":"null"}},{"type":"object"}]}
    """);
var bytesResult = TypertCodec.Create(ResultJson.Default.ByteArray, schema: composedBytes.RootElement);
var objectResult = TypertCodec.Create(ResultJson.Default.ResultBytes, schema: composedObject.RootElement);
foreach (var bytes in new byte[][] { [1, 2, 3], [] })
{
    if (!((byte[])bytesResult.DecodeOutput(bytesResult.Encode(bytes))!).SequenceEqual(bytes) ||
        !((ResultBytes)objectResult.DecodeOutput(objectResult.Encode(new ResultBytes(bytes)))!).Payload.SequenceEqual(
            bytes))
        throw new Exception("Composed native output contract rejected valid bytes.");
}

var clientArtifact = TypertArtifacts.GenerateClient(
    new(
        "IndependentData.Provider",
        [
            .. shared.Invocations.Select(method => method.Endpoint == "sharedData/bytes"
                ? method with
                {
                    Result = bytesResult
                }
                : method),
            new(
                "result#plainBytes",
                "result",
                "result",
                "plainBytes",
                [],
                TypertCodec.Create(ResultJson.Default.ByteArray)),
            new("result#composedObject", "result", "result", "composedObject", [], objectResult),
            new(
                "result#optional",
                "result",
                "result",
                "optional",
                [],
                TypertCodec.CreateOptional<string?>(TypertCodec.CreateNullable(ResultJson.Default.String))),
            new(
                "result#encodedText",
                "result",
                "result",
                "encodedText",
                [],
                TypertCodec.Create(ResultJson.Default.String, schema: encodedString.RootElement)),
            new(
                "result#literal",
                "result",
                "result",
                "literal",
                [],
                TypertCodec.Create(ResultJson.Default.ByteArray, schema: binaryLiteral.RootElement))
        ]));
server.MapGet("/client-runtime/shared.mjs", () => Results.Text(clientArtifact.Module, "text/javascript"));
server.MapGet("/client-runtime/shared.d.mts", () => Results.Text(clientArtifact.Declaration, "text/plain"));
server.MapGet("/client-runtime/cordis-remote.mjs", () => Results.Text(TypertClientRuntime.Module, "text/javascript"));
server.MapGet("/client-runtime/cordis-remote.d.mts", () => Results.Text(TypertClientRuntime.Declaration, "text/plain"));
#endif
server.MapCordisRemote(
    "/remote",
    remoteConnection,
    (http, _) => Task.FromResult(http.Request.Headers.Authorization == "consumer-policy"));
server.MapPost(
    "/control/stop",
    () =>
    {
        server.Lifetime.StopApplication();
        return Results.Ok();
    });
await server.StartAsync();
Console.WriteLine("READY " + server.Urls.Single());
Console.Out.Flush();
await server.WaitForShutdownAsync();

#if CORDIS_READONLY_HOST
[System.Text.Json.Serialization.JsonSerializable(typeof(System.Text.Json.JsonElement))]
internal partial class ExportedSchemaJson : System.Text.Json.Serialization.JsonSerializerContext;
#else
[System.Text.Json.Serialization.JsonSerializable(typeof(byte[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ResultBytes))]
internal partial class ResultJson : System.Text.Json.Serialization.JsonSerializerContext;

internal sealed record ResultBytes(byte[] Payload);
#endif
