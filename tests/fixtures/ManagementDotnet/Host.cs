using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
var notificationsContracts = RemoteNotificationsTypert.Contribution("Cordis.Independent.Notifications");
var registry = new TypertRegistry(session.Context);
var gateway = new TypertGateway(session.Context, registry);
var denied = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
var permissions = new ConcurrentQueue<string>();
var observations = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
var cancellationObservations = new ConcurrentDictionary<string, TaskCompletionSource>(StringComparer.Ordinal);
var buildDecisions = new ConcurrentDictionary<string, BuildDecision>(StringComparer.Ordinal);
EffectHandle events = null!;
await session.Context.RunAsync(context =>
{
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
    new RemoteNotifications(context, gateway);
    registry.Register(context, managerContracts);
    registry.Register(context, notificationsContracts);
    events = RegisterEvents(context);
    return Task.CompletedTask;
});

EffectHandle RegisterEvents(Context context) => gateway.RegisterRemoteEvents(
    context,
    TypertRemoteEvents.FromCordis(
        context,
        [
            new("plugin-manager/changed", [TypertCodec.Create(PluginManagerJson.Default.PluginManagerChanged)]),
            new("plugin-manager/install-state", [TypertCodec.Create(PluginManagerJson.Default.PackageProgress)]),
            new("plugin-manager/install-log", [TypertCodec.Create(PluginManagerJson.Default.PackageProgress)])
        ]));

var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
await using var server = builder.Build();
server.MapCordisRemote(
    "/remote",
    gateway,
    (http, _) => Task.FromResult(http.Request.Headers.Authorization == "consumer-policy"));
foreach (var pair in new[]
         {
             (Name: "management", Contract: managerContracts),
             (Name: "notifications", Contract: notificationsContracts)
         })
{
    var artifact = TypertArtifacts.GenerateClient(pair.Contract);
    server.MapGet("/client-runtime/" + pair.Name + ".mjs", () => Results.Text(artifact.Module, "text/javascript"));
    server.MapGet("/client-runtime/" + pair.Name + ".d.ts", () => Results.Text(artifact.Declaration, "text/plain"));
}

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
foreach (var pair in new[]
         {
             (Name: "terminal",
                 Frame:
                 "{\"ok\":false,\"error\":{\"code\":\"fixture/stream\",\"message\":\"notification source failed\",\"details\":{\"stage\":\"source\"}}}\n"),
             (Name: "incomplete", Frame: "{\"ok\":true,\"value\":{\"kind\":\"ready\"}}"),
             (Name: "malformed", Frame: "not-json\n")
         })
    server.MapPost(
        "/" + pair.Name + "/remoteNotifications/watch/stream",
        async (HttpContext http) =>
        {
            http.Response.ContentType = "application/x-ndjson";
            await http.Response.WriteAsync(pair.Frame);
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
