using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Cordis.Platform.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[assembly: TypertContext("agent", typeof(string))]

var directory = Path.GetFullPath(args.Single());
Directory.CreateDirectory(directory);
var profile = Path.Combine(directory, "profile");
var home = Path.Combine(directory, "home");
Directory.CreateDirectory(home);
Profiles.Initialize(profile, ["existing"]);
await File.WriteAllTextAsync(Path.Combine(profile, "cordis.patch.yml"), "- insert: [{ id: worker, name: worker }]\n");
var packages = new FixtureToolchain(directory);
packages.CreateInstalled("existing", "1.0.0");
var manifest = PackageManifest.Read(Path.Combine(profile, "package.json"));
manifest.Raw["dependencies"] = new EntryOptions
{
    ["existing"] = "1.0.0"
};
manifest.Write(Path.Combine(profile, "package.json"));
var configuration = Path.Combine(directory, "cordis.yml");
await File.WriteAllTextAsync(configuration, "[]\n");
var installation = new Dictionary<string, string>();
var launch = new ProfileLaunch(
    await Profiles.LoadAsync(profile, installation, packages.Bundles),
    home,
    [],
    installation,
    packages.Bundles)
{
    RuntimeIdentity = new("0.2.0-rc.2")
};
await using var session = await ProfileSession.StartAsync(
    configuration,
    launch,
    new StaticModuleResolver().Register(
        "worker",
        new Plugin<object?>
        {
            Apply = (_, _) =>
            {
            }
        }));
var managerContracts = PluginManagerTypert.Contribution("Cordis.Independent.Management");
var settingsContracts = SettingsControllerTypert.Contribution("Cordis.Independent.Settings");
var openedDocuments = 0;
var registry = new TypertRegistry(session.Context);
var gateway = new TypertGateway(session.Context, registry);
var denied = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
var permissions = new ConcurrentQueue<string>();
var observations = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
var cancellationObservations = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
var buildDecisions = new ConcurrentDictionary<string, BuildDecision>(StringComparer.Ordinal);
EffectHandle events = null!;
TypertRemoteEventDefinition[] selectedEvents =
[
    Cordis.Platform.Tests.EventAlpha.ChangedTypert.Definition,
    Cordis.Platform.Tests.EventAlpha.NestedTypert.Definition,
    Cordis.Platform.Tests.EventBeta.ChangedTypert.Definition,
    PluginManagerChangedEventTypert.Definition,
    PluginInstallStateEventTypert.Definition,
    PluginInstallLogEventTypert.Definition,
    DecisionRequestedTypert.Forward(agent => new(agent.Context, agent.Id))
];
await session.Context.RunAsync(context =>
{
    new SettingsForms(context, session.ConfigurationOperations);
    new SettingsController(
        context,
        (path, signal) =>
        {
            signal.ThrowIfCancellationRequested();
            if (path != Path.Combine(profile, "cordis.patch.yml"))
                throw new InvalidOperationException("The Settings document left its configuration authority.");
            Interlocked.Increment(ref openedDocuments);
            return Task.CompletedTask;
        });
    registry.Register(context, settingsContracts);
    new PluginManager(
        context,
        session.ConfigurationOperations,
        async (_, permission) =>
        {
            permissions.Enqueue(permission.Operation + ":" + permission.Target);
            if (permission.Operation == "install" && permission.Target is { } target)
            {
                observations
                    .GetOrAdd(target, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                    .TrySetResult();
                if (permission.Mutation)
                    cancellationObservations
                        .GetOrAdd(target, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
                        .TrySetResult();
            }

            if (permission.Operation == "build" && permission.Target is { } name &&
                buildDecisions.TryGetValue(name, out var decision))
            {
                decision.Started.TrySetResult();
                await decision.Released.Task;
            }

            return !denied.ContainsKey(permission.Operation);
        },
        packages);
    registry.Register(context, managerContracts);
    events = RegisterEvents(context);
    return Task.CompletedTask;
});

EffectHandle RegisterEvents(Context context) => gateway.RegisterRemoteEvents(
    context,
    TypertRemoteEvents.FromCordis(context, selectedEvents),
    new TypertRemoteEventHost(launch.Home));

TaskCompletionSource? eventReplyGate = null;
var eventReplyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
server.MapGet("/client-runtime/cordis-remote.mjs", () => Results.Text(TypertClientRuntime.Module, "text/javascript"));
server.MapGet("/client-runtime/cordis-remote.d.mts", () => Results.Text(TypertClientRuntime.Declaration, "text/plain"));
var eventArtifact = TypertArtifacts.GenerateEvents(selectedEvents);
server.MapGet("/client-runtime/events.mjs", () => Results.Text(eventArtifact.Module, "text/javascript"));
server.MapGet("/client-runtime/events.d.ts", () => Results.Text(eventArtifact.Declaration, "text/plain"));
TypertHostConnection remoteConnection = null!;
await session.Context.RunAsync(owner =>
{
    remoteConnection = new(owner, gateway);
    owner.Provide("connection", remoteConnection);
    return Task.CompletedTask;
});
server.MapPost(
    "/control/collection-events",
    async () =>
    {
        await session.Context.RunAsync(context =>
        {
            context.Emit(
                "alpha/changed",
                new List<string?>
                {
                    "text",
                    null
                },
                new Dictionary<string, string?>
                {
                    ["empty"] = null
                },
                new List<string>
                {
                    "required"
                });
            context.Emit("alpha/changed", null, new Dictionary<string, string?>(), new List<string>());
            context.Emit(
                "alpha/nested",
                new Cordis.Platform.Tests.LooseFirst
                {
                    Id = Guid.Parse("f031a626-7695-4b15-a915-15aa56e0cf94"),
                    Loose = [[null]],
                    Strict = [["required"]]
                },
                new Cordis.Platform.Tests.StrictFirst
                {
                    Loose = [[null]],
                    Strict = [["required"]]
                });
            return Task.CompletedTask;
        });
        return Results.Ok();
    });
server.UseWebSockets();
server.Use(async (http, next) =>
{
    if (http.Request.Path.Value!.EndsWith("/$events/result", StringComparison.Ordinal) && eventReplyGate is { } gate)
    {
        eventReplyStarted.TrySetResult();
        await gate.Task;
    }

    await next(http);
});
server.MapPost(
    "/control/hold-event-reply",
    () =>
    {
        eventReplyGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        return Results.Ok();
    });
server.MapPost(
    "/control/wait-event-reply",
    async () =>
    {
        await eventReplyStarted.Task;
        return Results.Ok();
    });
server.MapPost(
    "/control/release-event-reply",
    () =>
    {
        eventReplyGate?.TrySetResult();
        eventReplyGate = null;
        return Results.Ok();
    });
server.MapPost(
    "/control/decision",
    async (HttpContext http) =>
    {
        Task<RemoteOptional<DecisionValue?>> invocation = null!;
        await session.Context.RunAsync(ctx =>
        {
            invocation = DecisionRequestedTypert.InvokeAsync(
                ctx,
                new EventSubject(ctx, "agent-1"),
                new DecisionRequest(http.Request.Query["mode"].ToString())
                {
                    Id = Guid.Parse("f031a626-7695-4b15-a915-15aa56e0cf94")
                },
                () => Task.FromResult(new RemoteOptional<DecisionValue?>(new DecisionValue("fallback"))),
                http.RequestAborted);
            return Task.CompletedTask;
        });
        var result = await invocation;
        if (http.Request.Query["mode"] == "value" &&
            (!result.HasValue || result.Value?.Id != Guid.Parse("f031a626-7695-4b15-a915-15aa56e0cf94")))
            throw new InvalidOperationException("Generated event reply changed its Guid.");
        return Results.Text(
            JsonSerializer.Serialize(
                new DecisionReply(result.HasValue, result.HasValue ? result.Value?.Value : null),
                DecisionJson.Default.DecisionReply),
            "application/json");
    });
server.MapCordisRemote(
    "/remote",
    remoteConnection,
    (http, _) => Task.FromResult(
        (http.Request.Headers.Authorization == "consumer-policy" ||
            http.Request.Query["consumer"] == "consumer-policy")));
foreach (var pair in new[]
         {
             (Name: "management", Contract: managerContracts), (Name: "settings", Contract: settingsContracts)
         })
{
    var artifact = TypertArtifacts.GenerateClient(pair.Contract);
    server.MapGet("/client-runtime/" + pair.Name + ".mjs", () => Results.Text(artifact.Module, "text/javascript"));
    server.MapGet("/client-runtime/" + pair.Name + ".d.ts", () => Results.Text(artifact.Declaration, "text/plain"));
}

server.MapGet(
    "/control/opened-documents",
    () => Results.Text(
        Volatile.Read(ref openedDocuments).ToString(System.Globalization.CultureInfo.InvariantCulture),
        "application/json"));

server.MapPost(
    "/control/deny",
    (HttpContext http) =>
    {
        var operation = http.Request.Query["operation"].ToString();
        if (http.Request.Query["enabled"] == "true")
            denied[operation] = true;
        else
            denied.TryRemove(operation, out _);
        return Results.Text("ok");
    });
server.MapPost(
    "/control/hold",
    (HttpContext http) =>
    {
        packages.Hold(
            http.Request.Query["name"].ToString(),
            http.Request.Query["version"].ToString(),
            http.Request.Query["mode"].ToString());
        return Results.Text("ok");
    });
server.MapPost(
    "/control/wait-preparing",
    async (HttpContext http) =>
    {
        await packages
            .WaitPreparing(http.Request.Query["name"].ToString(), http.Request.Query["version"].ToString())
            .WaitAsync(TimeSpan.FromSeconds(20));
        return Results.Text("ok");
    });
server.MapPost(
    "/control/release",
    (HttpContext http) =>
    {
        packages.Release(http.Request.Query["name"].ToString(), http.Request.Query["version"].ToString());
        return Results.Text("ok");
    });
server.MapPost(
    "/control/wait-observed",
    async (HttpContext http) =>
    {
        var id = http.Request.Query["name"].ToString();
        await observations
            .GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(TimeSpan.FromSeconds(20));
        return Results.Text("ok");
    });
server.MapPost(
    "/control/hold-policy",
    (HttpContext http) =>
    {
        buildDecisions[http.Request.Query["name"].ToString()] = new();
        return Results.Text("ok");
    });
server.MapPost(
    "/control/wait-policy",
    async (HttpContext http) =>
    {
        await buildDecisions[http.Request.Query["name"].ToString()].Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        return Results.Text("ok");
    });
server.MapPost(
    "/control/wait-cancelling",
    async (HttpContext http) =>
    {
        var id = http.Request.Query["name"].ToString();
        await cancellationObservations
            .GetOrAdd(id, _ => new(TaskCreationOptions.RunContinuationsAsynchronously))
            .Task.WaitAsync(TimeSpan.FromSeconds(20));
        return Results.Text("ok");
    });
server.MapPost(
    "/control/release-policy",
    (HttpContext http) =>
    {
        buildDecisions[http.Request.Query["name"].ToString()].Released.TrySetResult();
        return Results.Text("ok");
    });
server.MapPost(
    "/control/remove-source",
    async () =>
    {
        await session.Context.RunAsync(async _ => await events.DisposeAsync());
        return Results.Text("ok");
    });
server.MapPost(
    "/control/restore-source",
    async () =>
    {
        await session.Context.RunAsync(context =>
        {
            events = RegisterEvents(context);
            return Task.CompletedTask;
        });
        return Results.Text("ok");
    });
server.MapGet(
    "/control/probe",
    () => Results.Json(
        new Probe(
            packages.Preparations,
            packages.Cancellations,
            packages.Publications,
            File.ReadAllText(Path.Combine(profile, "package.json")),
            permissions.ToArray()),
        ProbeJson.Default.Probe));
server.MapPost(
    "/control/shutdown-session",
    async () =>
    {
        await session.DisposeAsync();
        return Results.Text("ok");
    });
foreach (var kind in new[] { "terminal", "incomplete", "malformed" })
    server.MapGet(
        "/" + kind + "/remote.mux",
        async (HttpContext http) =>
        {
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            using var message = new MemoryStream();
            var buffer = new byte[4096];
            ValueWebSocketReceiveResult read;
            do
            {
                read = await socket.ReceiveAsync(buffer.AsMemory(), http.RequestAborted);
                message.Write(buffer, 0, read.Count);
            } while (!read.EndOfMessage);

            using var open = JsonDocument.Parse(message.ToArray());
            var id = open.RootElement.GetProperty("streamId").GetRawText();
            var frame = kind switch
            {
                "terminal" => "{\"type\":\"error\",\"streamId\":" + id +
                    ",\"error\":{\"code\":\"fixture/stream\",\"message\":\"notification source failed\",\"details\":{\"stage\":\"source\"}}}",
                "incomplete" => "{\"type\":",
                _ => "not-json"
            };
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(frame).AsMemory(),
                WebSocketMessageType.Text,
                true,
                http.RequestAborted);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", http.RequestAborted);
        });
await server.StartAsync();
Console.WriteLine("READY " + server.Urls.Single());
await server.WaitForShutdownAsync();

record Probe(int Preparations, int Cancellations, int Publications, string Manifest, IReadOnlyList<string> Permissions);

sealed class BuildDecision
{
    internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

[JsonSerializable(typeof(Probe))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class ProbeJson : JsonSerializerContext;

// A host-selected platform adapter with real prepared files. Controls only schedule preparation
// and tool cleanup; ProfileSession and PluginConfigurationOperations produce every outcome.
sealed class FixtureToolchain(string directory) : IProfilePackageToolchain
{
    private sealed class Attempt(string mode)
    {
        internal readonly string Mode = mode;
        internal readonly TaskCompletionSource Preparing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly ConcurrentDictionary<string, Attempt> attempts = new(StringComparer.Ordinal);
    private int preparations, cancellations, publications;
    public int Preparations => Volatile.Read(ref preparations);
    public int Cancellations => Volatile.Read(ref cancellations);
    public int Publications => Volatile.Read(ref publications);

    public Dictionary<string, string> Bundles
    {
        get;
    } = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Sources => ["selected-local-source"];
    private static string Key(string name, string version) => name + "@" + version;
    public void Hold(string name, string version, string mode) => attempts[Key(name, version)] = new(mode);
    public Task WaitPreparing(string name, string version) => attempts[Key(name, version)].Preparing.Task;
    public void Release(string name, string version) => attempts[Key(name, version)].Released.TrySetResult();

    public void CreateInstalled(string name, string version)
    {
        var path = WritePrepared(name, version, Manifest(name, version));
        Bundles[name] = path;
    }

    private static string Manifest(string name, string version) =>
        "{\"name\":\"" + name + "\",\"version\":\"" + version + "\",\"dsh\":{\"bundle\":{\"patch\":\"patch.yml\"}}" +
        (name == "incompatible" ? ",\"peerDependencies\":{\"@deepseek-ai/dsh\":\"999.0.0\"}" : "") + "}";

    private string WritePrepared(string name, string version, string manifest)
    {
        var path = Path.Combine(directory, "prepared", Key(name, version));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "package.json"), manifest);
        File.WriteAllText(Path.Combine(path, "patch.yml"), "[]\n");
        return path;
    }

    public Task<IReadOnlyList<string>> VersionsAsync(
        string name,
        string source,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Sources.Contains(source))
            throw new InvalidOperationException("Host source was not selected.");
        return Task.FromResult<IReadOnlyList<string>>(["1.0.0", "2.0.0"]);
    }

    public async Task<PackageInspection> InspectAsync(
        PackageRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Sources.Contains(request.Source))
            throw new InvalidOperationException("Host source was not selected.");
        var manifest = Manifest(request.Name, request.Version);
        var bytes = System.Text.Encoding.UTF8.GetBytes(manifest);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (attempts.TryGetValue(Key(request.Name, request.Version), out var attempt) && attempt.Mode == "inspect")
        {
            attempt.Preparing.TrySetResult();
            try
            {
                await attempt.Released.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancellations);
                throw;
            }
        }

        return new PackageInspection(request, hash, "Local prepared package", true)
        {
            ManifestJson = manifest
        };
    }

    public async Task<PreparedPackage> PrepareAsync(
        PackageInspection inspection,
        bool buildApproved,
        Action<string> output,
        CancellationToken cancellationToken = default)
    {
        if (!buildApproved)
            throw new InvalidOperationException("Build approval was not supplied to the platform adapter.");
        Interlocked.Increment(ref preparations);
        var request = inspection.Request;
        var path = WritePrepared(request.Name, request.Version, inspection.ManifestJson!);
        output("preparation started");
        if (attempts.TryGetValue(Key(request.Name, request.Version), out var attempt))
        {
            attempt.Preparing.TrySetResult();
            try
            {
                await attempt.Released.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancellations);
                throw new PackageToolException(
                    "Platform cleanup " + attempt.Mode,
                    path,
                    exitCode: attempt.Mode == "failed" ? 42 : 130,
                    cancelled: attempt.Mode != "failed");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        output("preparation completed");
        return new(request.Name, request.Version, path);
    }

    public Task PublishAsync(PreparedPackage package, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Bundles.TryAdd(package.Name, package.Directory);
        Interlocked.Increment(ref publications);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Bundles.Remove(name);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetRetainedDirectories(string name) =>
        Directory.Exists(Path.Combine(directory, "prepared"))
            ? Directory.GetDirectories(Path.Combine(directory, "prepared"), name + "@*")
            : [];
}

public sealed record EventSubject(Context Context, string Id) : IContextualService
{
    public object ForContext(Context caller) => new EventSubject(caller, Id);
}


public sealed record DecisionReply(bool HasValue, string? Value);

[RemoteEvent("fixture/decision", typeof(DecisionJson))]
public delegate Task<RemoteOptional<DecisionValue?>> DecisionRequested(
    [RemoteLookup("agent", "agentId", typeof(string))]
    EventSubject agent,
    DecisionRequest request,
    Func<Task<RemoteOptional<DecisionValue?>>> next,
    CancellationToken signal);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DecisionRequest))]
[JsonSerializable(typeof(DecisionReply))]
[JsonSerializable(typeof(DecisionValue))]
[JsonSerializable(typeof(string))]
public partial class DecisionJson : JsonSerializerContext;
