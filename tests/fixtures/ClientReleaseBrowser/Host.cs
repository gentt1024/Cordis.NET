using System.Collections.Concurrent;
using System.Text.Json;
using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var workspace = Path.GetFullPath(args[0]);
var runtime = Path.GetFullPath(args[1]);
var page = Path.GetFullPath(args[2]);
Directory.CreateDirectory(workspace);
var profile = Path.Combine(workspace, "profile");
Profiles.Initialize(profile, []);
var configuration = Path.Combine(workspace, "cordis.yml");
await File.WriteAllTextAsync(configuration, "[]\n");
var launch = new ProfileLaunch(
    await Profiles.LoadAsync(profile, new Dictionary<string, string>()),
    workspace,
    [],
    new Dictionary<string, string>());
await using var session = await ProfileSession.StartAsync(configuration, launch, new StaticModuleResolver());
var catalogs = new Dictionary<string, ClientModuleCatalog>(StringComparer.Ordinal);
foreach (var stage in new[] { "a", "b", "a2" })
{
    var directory = Directory.CreateDirectory(Path.Combine(workspace, stage, "release-panel")).FullName;
    await File.WriteAllTextAsync(
        Path.Combine(directory, "package.json"),
        "{\"name\":\"release-panel\",\"dsh\":{\"client\":{\"platform\":\"web\",\"external\":[]}},\"exports\":{\"./client\":\"./client.js\"}}");
    await File.WriteAllTextAsync(
        Path.Combine(directory, "client.js"),
        $$$"""
           __ModuleLoader__.load({id:'release-panel',factory() {
             const style=document.createElement('style');style.id='release-style';
             style.textContent='body { --release: {{{stage}}}; }';document.head.append(style);
             return {apply(ctx) {
               const value={stage:'{{{stage}}}',draft:''};ctx.provide('release-panel',value);
               ctx.effect(() => {
                 globalThis.releaseStarts.push('{{{stage}}}');
                 const panel=document.createElement('div');panel.id='release-panel';panel.textContent='{{{stage}}}';
                 document.body.append(panel);
                 return () => {globalThis.releaseStops.push('{{{stage}}}');panel.remove()};
               });
             }};
           }});
           """);
    var packages = DeploymentPackageResolver.Native((name, _) => Path.Combine(workspace, stage, name));
    catalogs[stage] = await ClientModuleCatalog.CaptureAsync(
        packages,
        ["release-panel"],
        new Uri(workspace + Path.DirectorySeparatorChar),
        artifactBasePath: "/cordis/client/artifacts",
        release: stage == "b" ? new("application-2", "build-b") : new("application-1", "build-a"));
}

var current = catalogs["a"];
var management = new CordisManagement(
    session,
    (_, _) => Task.FromResult(true),
    clientModules: _ => Task.FromResult(Volatile.Read(ref current)));
var registry = new TypertRegistry(session.Context);
var gateway = new TypertGateway(session.Context, registry);
TypertHostConnection connection = null!;
await session.Context.RunAsync(owner =>
{
    gateway.RegisterRemoteEvents(
        owner,
        TypertRemoteEvents.FromCordis(owner, StandardRemoteEvents.Definitions),
        new TypertRemoteEventHost(""));
    connection = new(owner, gateway);
    owner.Provide("connection", connection);
    return Task.CompletedTask;
});
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
var sockets = new ConcurrentDictionary<HttpContext, byte>();
var artifactRequests = new ConcurrentQueue<string>();
var socketOpens = 0;
var graphReads = 0;
server.UseWebSockets();
server.Use(async (http, next) =>
{
    var mux = http.Request.Path == "/remote/remote.mux";
    if (mux)
    {
        Interlocked.Increment(ref socketOpens);
        sockets.TryAdd(http, 0);
    }

    if (http.Request.Path == "/cordis/client/graph")
        Interlocked.Increment(ref graphReads);
    if (http.Request.Path.StartsWithSegments("/cordis/client/artifacts"))
        artifactRequests.Enqueue(http.Request.Query["rev"].ToString());
    try
    {
        await next(http);
    }
    finally
    {
        if (mux)
            sockets.TryRemove(http, out _);
    }
});
management.Map(server);
server.MapCordisRemote(
    "/remote",
    connection,
    (_, endpoint) => Task.FromResult(endpoint is "remote.mux" or "$events" or "$events/result"));
var events = TypertArtifacts.GenerateEvents(StandardRemoteEvents.Definitions);
server.MapGet("/native/events.mjs", () => Results.Text(events.Module, "text/javascript"));
server.MapGet("/native/cordis-remote.mjs", () => Results.Text(TypertClientRuntime.Module, "text/javascript"));
server.MapGet("/", () => Results.File(page, "text/html"));
server.MapGet(
    "/runtime/{file}",
    (string file) =>
        file.EndsWith(".mjs", StringComparison.Ordinal) && Path.GetFileName(file) == file
            ? Results.File(Path.Combine(runtime, file), "text/javascript")
            : Results.NotFound());
server.MapPost(
    "/test/catalog/{stage}",
    async (string stage) =>
    {
        if (!catalogs.TryGetValue(stage, out var selected))
            return Results.NotFound();
        await session.Context.RunAsync(owner =>
        {
            Volatile.Write(ref current, selected);
            owner.Emit("client-modules/changed");
            return Task.CompletedTask;
        });
        return Results.Text("published");
    });
server.MapPost(
    "/test/loss",
    () =>
    {
        foreach (var http in sockets.Keys)
            http.Abort();
        return Results.Text("aborted");
    });
server.MapGet(
    "/test/observations",
    () => Results.Text(
        JsonSerializer.Serialize(
            new
            {
                socketOpens = Volatile.Read(ref socketOpens),
                graphReads = Volatile.Read(ref graphReads),
                artifactRequests = artifactRequests.ToArray(),
                revisions = catalogs.ToDictionary(pair => pair.Key, pair => pair.Value.Graph.Entries.Single().Revision)
            }),
        "application/json"));
server.MapPost(
    "/test/result",
    async (HttpContext http) =>
    {
        using var reader = new StreamReader(http.Request.Body);
        var result = await reader.ReadToEndAsync(http.RequestAborted);
        using var parsed = JsonDocument.Parse(result);
        if (parsed.RootElement.GetProperty("status").GetString() is not ("passed" or "failed"))
            return Results.BadRequest();
        var temporary = Path.Combine(workspace, "browser-result.pending");
        await File.WriteAllTextAsync(temporary, result, http.RequestAborted);
        File.Move(temporary, Path.Combine(workspace, "browser-result.json"), overwrite: true);
        return Results.Text("recorded");
    });
server.MapPost(
    "/test/stop",
    () =>
    {
        server.Lifetime.StopApplication();
        return Results.Text("stopping");
    });
await server.StartAsync();
Console.WriteLine("READY " + server.Urls.Single());
await server.WaitForShutdownAsync();
