using Cordis;
using Cordis.Composition;
using Cordis.Example.Probes;
using Cordis.Extensions;

await using var root = new Context();
await root.RunAsync(async ctx =>
{
    var modules = new StaticModuleResolver().Register("probes", ProbeModule.Create());
    var loader = new Loader(ctx, modules);
    await loader.Root.UpdateAsync(EntryPatches.Apply([], ProbeModule.ReadPatch()));
    await loader.WaitAsync();
    Check(loader.Resolve("probes").Fiber?.State == FiberState.Active, "provider activation");
    var events = new List<string>();
    ctx.On(ProbeContract.Changed, (_, change) => { events.Add(change.Value); });
    IAsyncDisposable? firstRegistration = null;
    var first = ctx.Plugin(new Plugin<object?>
    {
        Name = "connection-status", Inject = [ProbeContract.Name, ProbeContract.Formatter],
        Apply = (caller, _) =>
        {
            firstRegistration = caller.Probes.Register("connection", () => caller.ProbeFormatter.Format("connected"));
            caller.Emit(ProbeContract.Changed, new("connection", "connected"));
        },
    });
    var second = ctx.Plugin(new Plugin<object?>
    {
        Name = "worker-status", Inject = [ProbeContract.Name],
        Apply = (caller, _) => caller.Probes.Register("worker", static () => "ready"),
    });
    await Task.WhenAll(first.WaitAsync(), second.WaitAsync());
    Check(ctx.Probes.Snapshot()["connection"] == "status:connected", "plain query capability");
    Check(ctx.Probes.Snapshot().Count == 2, "two owned registrations");
    await firstRegistration!.DisposeAsync();
    Check(ctx.Probes.Snapshot().Keys.SequenceEqual(["worker"]), "early disposal leaves peer");
    await first.DisposeAsync();
    await loader.UpdateAsync("probes", new EntryOptions { Disabled = true });
    Check(second.State == FiberState.Pending, "provider disappearance stops consumer");
    await loader.UpdateAsync("probes", new EntryOptions { Disabled = false });
    await loader.WaitAsync();
    await second.WaitAsync();
    Check(ctx.Probes.Snapshot().Keys.SequenceEqual(["worker"]), "reactivation obtains new caller view");
    await second.DisposeAsync();
    Check(ctx.Probes.Snapshot().Count == 0, "consumer cleanup");
    Check(events.SequenceEqual(["connected"]), "typed event delivery");

    // A non-Timer, Action-based source can call from any thread. The adapter owns admission,
    // not the source itself, and does not silently turn subscription removal into task draining.
    var feed = new ProbeFeed();
    var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var external = ctx.SubscribeExternal<ProbeChanged>(feed.Subscribe, async change =>
    {
        await ctx.ParallelAsync(ProbeContract.Changed, change);
        delivered.TrySetResult();
    }, error => delivered.TrySetException(error));
    await Task.Run(() => feed.Publish(new("external", "online")));
    await delivered.Task;
    await external.DisposeAsync();
    feed.Publish(new("external", "late"));
    Check(events.SequenceEqual(["connected", "online"]), "external callback ownership");
});
Console.WriteLine("probe authoring scenario passed");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// A stand-in for an SDK's subscription interface; deliberately knows nothing about Cordis.
sealed class ProbeFeed
{
    private event Action<ProbeChanged>? Changed;
    public IDisposable Subscribe(Action<ProbeChanged> callback)
    {
        Changed += callback;
        return new Subscription(() => Changed -= callback);
    }
    public void Publish(ProbeChanged change) => Changed?.Invoke(change);
    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
