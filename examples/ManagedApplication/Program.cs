using System.Net;
using Cordis.AspNetCore;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Extensions;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length < 4)
    throw new ArgumentException("Usage: ManagedApplication <profile> <NuGet-feed> <client-runtime-directory> <client-package-directory>... (loopback development host)");
var profile = Path.GetFullPath(args[0]);
var feed = Path.GetFullPath(args[1]);
var runtime = Path.GetFullPath(args[2]);
var watchSources = args.Where(value => value.StartsWith("--watch=", StringComparison.Ordinal)).Select(value => Path.GetFullPath(value[8..])).ToArray();
var buildScript = args.FirstOrDefault(value => value.StartsWith("--client-build-script=", StringComparison.Ordinal))?[22..];
var node = args.FirstOrDefault(value => value.StartsWith("--node=", StringComparison.Ordinal))?[7..] ?? "node";
if (watchSources.Length > 0 && buildScript is null) throw new ArgumentException("--watch requires an explicitly approved --client-build-script path.");
var clientPackages = args[3..].Where(value => !value.StartsWith("--", StringComparison.Ordinal)).Select(Path.GetFullPath).ToDictionary(
    ReadName, StringComparer.Ordinal);
string ReadName(string directory) => PackageManifest.Read(Path.Combine(directory, "package.json")).Raw.GetValueOrDefault("name")
    is string name && !string.IsNullOrWhiteSpace(name) ? name : throw new FormatException("A supplied package requires a name.");
Directory.CreateDirectory(Path.Combine(profile, ".cordis"));
await using var ownership = new FileStream(Path.Combine(profile, ".cordis", "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
if (!File.Exists(Path.Combine(profile, "package.json"))) Profiles.Initialize(profile, clientPackages.Keys.ToArray());
var configuration = Path.Combine(profile, "cordis.yml");
if (!File.Exists(configuration)) await File.WriteAllTextAsync(configuration, "[]\n");
await using var resolver = new ClrModuleResolver(Path.Combine(profile, ".cordis", "shadow"), [typeof(ConfigObject<>).Assembly]);
using var packages = new DotnetPluginToolchain(profile, resolver, [feed]);
var launch = new ProfileLaunch(await Profiles.LoadAsync(profile, clientPackages, packages.Bundles),
    Path.Combine(profile, ".cordis", "home"), [], clientPackages, packages.Bundles);
await using var session = await ProfileSession.StartAsync(configuration, launch, resolver, enableHmr: true);

// This sample opts into local development authority. A deployed application supplies its own identity/policy.
var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
builder.WebHost.UseUrls("http://127.0.0.1:5187");
await using var app = builder.Build();
var clientArtifacts = new Dictionary<string, string>(clientPackages, StringComparer.Ordinal);
DeploymentPackageResolver ClientResolver()
{
    var generation = new DeploymentGeneration(Path.GetDirectoryName(profile)!, profile,
        clientArtifacts.Select(pair => new DeploymentEntry(pair.Key, pair.Value, "1.0.0", Path.Combine(pair.Value, "package.json"), DeploymentPackageScope.Installation)));
    return new(generation, native: (name, _) => clientArtifacts.GetValueOrDefault(name));
}
ClientModuleCatalog? catalog = null;
async Task CaptureAsync()
{
    var selected = session.LoadedBundles.Where(clientPackages.ContainsKey);
    var next = await ClientModuleCatalog.CaptureAsync(ClientResolver(), selected, new Uri(configuration), "/cordis/client/artifacts",
        withdrawUnavailableDependencies: true);
    catalog = next;
}
await CaptureAsync();
bool Authorized(HttpContext http) =>
    http.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)
    && http.Request.Host.Host == "127.0.0.1"
    && (!http.Request.Headers.ContainsKey("Origin") || http.Request.Headers.Origin == "http://127.0.0.1:5187");
var management = new CordisManagement(session, (http, _) => Task.FromResult(Authorized(http)),
    _ => new SettingsPolicy(["limit", "label", "credential"]), packages,
    _ => Task.FromResult(catalog!));
management.Map(app);
app.MapGet("/", () => Results.File(Path.Combine(app.Environment.ContentRootPath, "index.html"), "text/html"));
app.MapGet("/client-runtime/client.mjs", () => Results.File(Path.Combine(runtime, "client.mjs"), "text/javascript"));
app.MapGet("/client-runtime/LICENSES.txt", () => Results.File(Path.Combine(runtime, "LICENSES.txt"), "text/plain"));

// Refreshed runs inside the session queue. Capture is queued afterwards so catalog publication
// observes a settled selection without nesting lifecycle transactions.
var captures = new ConcurrentDictionary<Task, byte>();
void TrackCapture(Task pending, CancellationToken cancellation = default)
{
    async Task SettleAsync()
    {
        try { await pending; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException error) when (cancellation.IsCancellationRequested && error.ObjectName == nameof(HmrCoordinator)) { }
        catch (Exception error) { Console.Error.WriteLine("Client publication failed: " + error.Message); }
    }
    var settlement = SettleAsync();
    captures.TryAdd(settlement, 0);
    _ = settlement.ContinueWith(completed => { captures.TryRemove(completed, out _); }, TaskScheduler.Default);
}
void QueueCapture()
{
    using var flow = ExecutionContext.SuppressFlow();
    var refresh = Task.Run(async () => await session.ConfigurationOperations.RunExclusiveAsync!(async () =>
    {
        try { await CaptureAsync(); management.NotifyClientModulesChanged(); }
        catch (Exception error)
        {
            // Selection failure cannot retain contributions from a package the profile withdrew.
            catalog = await ClientModuleCatalog.CaptureAsync(ClientResolver(), [], new Uri(configuration), "/cordis/client/artifacts");
            management.NotifyClientModulesChanged();
            Console.Error.WriteLine("Client catalog withdrawn: " + error.Message);
        }
    }));
    TrackCapture(refresh);
}
session.Refreshed += QueueCapture;

var workers = new Dictionary<string, DevelopmentWorker>(StringComparer.Ordinal);
var builds = new ConcurrentDictionary<string, DevelopmentStatus>(StringComparer.Ordinal);
foreach (var author in watchSources)
{
    var name = ReadName(author);
    if (!clientPackages.ContainsKey(name) || workers.ContainsKey(name)) throw new ArgumentException("Watch must name one supplied client package, once: " + name);
    var work = Path.Combine(profile, ".cordis", "client-development", Guid.NewGuid().ToString("N"));
    var destination = Path.Combine(work, "artifacts");
    Directory.CreateDirectory(work);
    builds[name] = new(name, "starting", null, null);
    var worker = new DevelopmentWorker(app.Lifetime.ApplicationStopping, async cancellation =>
    {
        try
        {
            await DevelopmentBuildProcess.RunAsync(node,
                [Path.GetFullPath(buildScript!), Path.Combine(work, "runtime"), author, destination, "--watch"],
                author, Path.Combine(work, "process.json"), async line =>
                {
                    if (line.StandardError) { Console.Error.WriteLine(line.Text); return; }
                    if (!line.Text.StartsWith('{')) { Console.WriteLine(line.Text); return; }
                    using var frame = JsonDocument.Parse(line.Text);
                    var wire = frame.RootElement;
                    var kind = wire.GetProperty("kind").GetString();
                    if (kind == "build-error")
                    {
                        builds[name] = builds[name] with { Phase = "build-error", Diagnostic = wire.GetProperty("diagnostic").GetString() };
                        return;
                    }
                    if (kind != "client-built" || wire.GetProperty("name").GetString() != name) throw new FormatException("Unexpected development output identity.");
                    var revision = wire.GetProperty("revision").GetString() ?? throw new FormatException("Missing development revision.");
                    var directory = Path.GetFullPath(wire.GetProperty("directory").GetString() ?? throw new FormatException("Missing complete artifact directory."));
                    var parent = Path.GetFullPath(Path.Combine(destination, "generations"));
                    if (!string.Equals(Path.GetDirectoryName(directory), parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                        || Path.GetFileName(directory).StartsWith('.')) throw new FormatException("Development output must name a completed generation directory.");
                    using var artifactManifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "package.json"), cancellation));
                    var patch = artifactManifest.RootElement.GetProperty("dsh").GetProperty("bundle").GetProperty("patch");
                    if (patch.ValueKind != JsonValueKind.Array || patch.GetArrayLength() != 0)
                        throw new FormatException("This sample's frontend watch requires a client-only bundle with an empty patch array.");
                    var publication = session.ConfigurationOperations.RunExclusiveAsync!(async () =>
                    {
                        cancellation.ThrowIfCancellationRequested();
                        var prior = clientArtifacts[name];
                        clientArtifacts[name] = directory;
                        try
                        {
                            var artifact = await ClientArtifact.CaptureAsync(ClientResolver(), name, new Uri(configuration), cancellation);
                            if (artifact.Revision != revision) throw new FormatException("Development frame differs from captured artifact bytes.");
                            await CaptureAsync();
                        }
                        catch { clientArtifacts[name] = prior; throw; }
                        builds[name] = new(name, "running", revision, null);
                        management.NotifyClientModulesChanged();
                    });
                    // Stop escapes a queued publication while root disposal owns that queue.
                    // Its tracked settlement rejects publication when cancellation reaches the queue.
                    TrackCapture(publication, cancellation);
                    await publication.WaitAsync(cancellation);
                }, cancellation);
            builds[name] = builds[name] with { Phase = "exited" };
        }
        catch (PackageToolException error) when (error.Cancelled && cancellation.IsCancellationRequested)
        { builds[name] = builds[name] with { Phase = "stopped" }; }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { builds[name] = builds[name] with { Phase = "stopped" }; }
        catch (Exception error)
        {
            builds[name] = builds[name] with { Phase = "failed", Diagnostic = error.Message };
            throw;
        }
    });
    workers.Add(name, worker);
    try { await session.Context.RunAsync(context => { context.Effect(() => worker, "client development compiler: " + name); return Task.CompletedTask; }); }
    catch { await worker.DisposeAsync(); throw; }
}
app.MapGet("/development", (HttpContext http) => Authorized(http)
    ? Results.Json(builds.Values.OrderBy(value => value.Name, StringComparer.Ordinal).ToArray(), ExampleJsonContext.Default.DevelopmentStatusArray)
    : Results.StatusCode(StatusCodes.Status403Forbidden));
app.MapPost("/development/stop", async (HttpContext http) =>
{
    if (!Authorized(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
    if (!workers.TryGetValue(http.Request.Query["name"].ToString(), out var worker)) return Results.NotFound();
    await worker.DisposeAsync();
    return Results.NoContent();
});
try { await app.RunAsync(); }
finally
{
    session.Refreshed -= QueueCapture;
    await Task.WhenAll(workers.Values.Select(async worker => await worker.DisposeAsync()));
    await Task.WhenAll(captures.Keys);
}

sealed record DevelopmentStatus(string Name, string Phase, string? Revision, string? Diagnostic);
[JsonSerializable(typeof(DevelopmentStatus[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
sealed partial class ExampleJsonContext : JsonSerializerContext;

sealed class DevelopmentWorker : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly Task completion;
    private readonly object gate = new();
    private Task? disposal;
    public DevelopmentWorker(CancellationToken shutdown, Func<CancellationToken, Task> run)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        completion = run(stop.Token);
    }
    public ValueTask DisposeAsync()
    {
        lock (gate) return new(disposal ??= StopAsync());
    }
    private async Task StopAsync()
    {
        try { await stop.CancelAsync(); await completion; }
        finally { stop.Dispose(); }
    }
}



