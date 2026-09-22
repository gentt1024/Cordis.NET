using Cordis;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Example.Probes;

if (args.Length == 1 && args[0] is "--help" or "-h" or "-?")
{
    Usage();
    return 0;
}
if (args.Length != 2)
{
    Usage();
    return 2;
}

var shadowRoot = Path.Combine(Path.GetTempPath(), "cordis-probes-" + Guid.NewGuid().ToString("N"));
var observations = await RunAsync(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), shadowRoot);
Check(observations.Length == 2 && observations.All(item => item.UnloadRequested), "both unload requests");
for (var index = 0; index < observations.Length; index++)
{
    // These observations contain weak references. Collection is cooperative; this host never forces GC.
    var observation = observations[index];
    var deleted = observation.TryDeleteShadow();
    Console.WriteLine($"v{index + 1}: unload requested={observation.UnloadRequested}, " +
        $"collected={observation.IsCollected}, shadow deleted={deleted}");
    if (!deleted) Console.WriteLine($"pending shadow cleanup: {observation.ShadowDirectory}");
}
if (observations.All(item => item.ShadowDeleted)) Directory.Delete(shadowRoot);
Console.WriteLine("CLR probe authoring scenario passed (lifecycle cleanup and unload requests; GC completion is independent)");
return 0;

static async Task<ClrUnloadObservation[]> RunAsync(string firstBundle, string secondBundle, string shadowRoot)
{
    // The host references shared contracts and the CLR adapter, never the provider implementation.
    await using var resolver = new ClrModuleResolver(shadowRoot, [typeof(IProbeRegistry).Assembly]);
    resolver.Register("probes", Definition(firstBundle));
    await using (var root = new Context())
    {
        Loader? loader = null;
        Fiber? connection = null;
        Fiber? worker = null;
        var connectionVersions = new List<string>();
        var workerVersions = new List<string>();
        await root.RunAsync(async ctx =>
        {
            loader = new Loader(ctx, resolver);
            await loader.Root.UpdateAsync([new EntryOptions
            {
                Id = "provider", Name = "probes",
                Config = new Dictionary<string, object?> { ["Prefix"] = "live" },
            }]);
            await loader.WaitAsync();
            Check(loader.Resolve("provider").Fiber?.State == FiberState.Active, "v1 provider activation");
            connection = ctx.Plugin(Consumer("connection", "connected", connectionVersions));
            worker = ctx.Plugin(Consumer("worker", "ready", workerVersions));
            await Task.WhenAll(connection.WaitAsync(), worker.WaitAsync());
            ShowAndCheck(ctx, "v1", ["connection", "worker"]);
        });

        await resolver.ReplaceAsync("probes", Definition(secondBundle),
            async (old, next) => await loader!.ReplacePluginAsync(old, next));
        Console.WriteLine("v1 provider replaced; unload requested=" + resolver.Unloads[0].UnloadRequested);
        await root.RunAsync(async ctx =>
        {
            await Task.WhenAll(connection!.WaitAsync(), worker!.WaitAsync());
            Check(connectionVersions.SequenceEqual(["v1", "v2"]), "connection reactivation with new view");
            Check(workerVersions.SequenceEqual(["v1", "v2"]), "worker reactivation with new view");
            ShowAndCheck(ctx, "v2", ["connection", "worker"]);
            await connection.DisposeAsync();
            ShowAndCheck(ctx, "v2", ["worker"]);
            await worker.DisposeAsync();
            ShowAndCheck(ctx, "v2", []);
            Console.WriteLine("consumer cleanup complete; provider remains active");
        });
    }
    Console.WriteLine("root lifecycle cleanup complete");
    // Stop fibers before retiring the resolver's last implementation.
    await resolver.DisposeAsync();
    return resolver.Unloads.ToArray();
}

static IPlugin Consumer(string name, string value, List<string> versions) => new Plugin<object?>
{
    Name = name, Inject = [ProbeContract.Name, ProbeContract.Formatter],
    Apply = (caller, _) =>
    {
        // Obtain a fresh caller-bound view on every activation. Keep only version strings for reporting.
        var probes = caller.Probes;
        var formatter = caller.ProbeFormatter;
        var version = probes.Version;
        versions.Add(version);
        probes.Register(name, () => formatter.Format(value));
        caller.Effect(() => (Action)(() => Console.WriteLine($"{name}: {version} activation ended")));
        Console.WriteLine($"{name}: activated with {version} caller view");
    },
};

static void ShowAndCheck(Context context, string version, string[] names)
{
    var probes = context.Probes;
    var snapshot = probes.Snapshot();
    Check(probes.Version == version, version + " provider version");
    Check(snapshot.Keys.Order(StringComparer.Ordinal).SequenceEqual(names), "caller-owned contributions");
    foreach (var name in names)
        Check(snapshot[name] == "live:" + (name == "connection" ? "connected" : "ready"), "shared formatter");
    Console.WriteLine($"{version} contributions: " + (snapshot.Count == 0 ? "(none)" :
        string.Join(", ", snapshot.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value))));
}

static ClrModuleDefinition Definition(string bundle) => new(bundle, "ProbePlugin.dll", "Cordis.ProbeFixture.Entry");
static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static void Usage() => Console.WriteLine("Usage: Probes.Clr <v1-bundle-directory> <v2-bundle-directory>");
