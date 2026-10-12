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
    registry.Register(owner, ReadonlyServiceTypert.Contribution("IndependentProjection.Provider"));
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
var clientArtifact = TypertArtifacts.GenerateClient(SharedRemoteTypert.Contribution("IndependentData.Provider"));
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
