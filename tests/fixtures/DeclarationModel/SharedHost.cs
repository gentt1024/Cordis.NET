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
await context.RunAsync(owner =>
{
#if CORDIS_READONLY_HOST
    owner.Provide("readonly", new ReadonlyService());
    registry.Register(owner, ReadonlyServiceTypert.Contribution("IndependentProjection.Provider"));
#else
    owner.Provide("sharedData", new SharedRemote());
    registry.Register(owner, SharedRemoteTypert.Contribution("IndependentData.Provider"));
#endif
    return Task.CompletedTask;
});
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
server.MapCordisRemote(
    "/remote",
    new TypertGateway(context, registry),
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
