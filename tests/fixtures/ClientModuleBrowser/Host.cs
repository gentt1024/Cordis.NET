using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

await using var root = new Context();
var registry = new TypertRegistry(root);
var gateway = new TypertGateway(root, registry);
TypertHostConnection connection = null!;
await root.RunAsync(owner =>
{
    owner.Provide("typert", registry);
    gateway.RegisterRemoteEvents(
        owner,
        TypertRemoteEvents.FromCordis(owner, [ClientModulesChangedEventTypert.Definition]),
        new TypertRemoteEventHost(""));
    connection = new(owner, gateway);
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
    (_, endpoint) => Task.FromResult(endpoint is "remote.mux" or "$events" or "$events/result"));
server.MapPost(
    "/changed",
    () => root.RunAsync(owner =>
    {
        owner.Emit("client-modules/changed");
        return Task.CompletedTask;
    }));
server.MapPost(
    "/stop",
    () =>
    {
        server.Lifetime.StopApplication();
    });
await server.StartAsync();
Console.WriteLine("READY " + server.Urls.Single());
await server.WaitForShutdownAsync();
