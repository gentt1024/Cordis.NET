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
    registry.Register(owner, ReferencedRemoteTypert.Contribution("referenced-provider"));
    connection = new(owner, new TypertGateway(owner, registry));
    owner.Provide("connection", connection);
    return Task.CompletedTask;
});
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
server.UseWebSockets();
server.MapCordisRemote("/remote", connection, (_, endpoint) => Task.FromResult(endpoint == "referencedData/echo"));
server.MapPost(
    "/stop",
    () =>
    {
        server.Lifetime.StopApplication();
    });
await server.StartAsync();
Console.WriteLine("READY " + server.Urls.Single());
await server.WaitForShutdownAsync();
