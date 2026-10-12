using PackageProgress = Cordis.Composition.PackageProgress;
using PluginManagerJson = Cordis.Composition.PluginManagerJson;
using PluginInstallLogEventTypert = Cordis.Composition.PluginInstallLogEventTypert;
using PluginInstallStateEventTypert = Cordis.Composition.PluginInstallStateEventTypert;
using TypertRegistry = Cordis.Composition.TypertRegistry;
using TypertRemoteEventsClient = Cordis.Composition.TypertRemoteEventsClient;
using Cordis;
using System.Threading.Channels;
using RemoteStreamCarrierException = Cordis.Composition.RemoteStreamCarrierException;
using TypertConnection = Cordis.Composition.TypertConnection;
using System.Text.Json;
using IndependentManagement.Manager;
using IndependentManagement.Notifications;
using IndependentManagement.Collections;
using RemoteError = Cordis.Composition.RemoteError;

using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};
http.DefaultRequestHeaders.Authorization = new("consumer-policy");
var address = new Uri(args.Single());
await using var connection = new TypertConnection(http, new Uri(address, "/remote"));
await using var manager = new PluginManagerClient(connection);
await using var notifications = new EventObservation(connection);
using var observation = new CancellationTokenSource();
await notifications.StartAsync(observation.Token);
using (var emitted = await http.PostAsync(new Uri(address, "/control/collection-events"), null))
    emitted.EnsureSuccessStatusCode();
await notifications.Collections.Task.WaitAsync(TimeSpan.FromSeconds(10));
await using var events = notifications.Read().GetAsyncEnumerator();
var plugins = (await manager.ListPluginsAsync()).Value;
var worker = plugins.Single(row => row.EntryId == "root:worker");
Assert(
    worker.Enabled && worker.PatchId == "worker" && worker.ReadOnlyReason is null,
    "Typed inventory lost the writable profile entry.");
Assert(
    plugins.Single(row => row.EntryId == "root").ReadOnlyReason == "management-required",
    "Typed inventory lost the protected root.");
Assert(
    ((await manager.ListBundlesAsync()).Value).Any(row => row.Name == "existing" && row.Version == "1.0.0"),
    "Typed bundle inventory lost its installed version.");
var disabled = (await manager.SetPluginEnabledAsync("root:worker", false)).Value;
Assert(
    disabled.Changed && disabled.Application == "applied" && disabled.Error is null,
    "Typed enablement lost separate persistence/application facts.");
Assert(
    !((await manager.ListPluginsAsync()).Value).Single(row => row.EntryId == "root:worker").Enabled,
    "Plugin disable was not applied.");
_ = (await manager.SetPluginEnabledAsync("root:worker", true)).Value;
await Event(events, frame => frame.Event == "plugin-manager/changed");
var bundleDisabled = (await manager.SetBundleEnabledAsync("existing", false)).Value;
Assert(
    bundleDisabled.Changed && bundleDisabled.Application == "applied" &&
    !((await manager.ListBundlesAsync()).Value).Single(row => row.Name == "existing").Enabled,
    "Typed bundle selection was not saved.");
_ = (await manager.SetBundleEnabledAsync("existing", true)).Value;
Assert(
    ((await manager.ListBundlesAsync()).Value).Single(row => row.Name == "existing").Enabled,
    "Typed bundle selection was not restored.");
var missingRemoval = (await manager.RemoveBundleAsync("missing")).Value;
Assert(
    missingRemoval.Application == "failed" && missingRemoval.Error == "not-removable",
    "Typed removal lost the actual refusal result.");
var source = ((await manager.SourcesAsync()).Value).Single();
Assert(
    ((await manager.VersionsAsync("existing", source)).Value).SequenceEqual(["1.0.0", "2.0.0"]),
    "Typed versions lost the selected platform source.");
Assert((await manager.WaitForInstallAsync("never-admitted")).Value is null, "Unknown wait was presented as success.");
Assert(
    ((await manager.CancelInstallAsync("never-admitted")).Value).Status == "not-running",
    "Unknown cancellation lost its status.");

var request = new PackageRequest("existing", "2.0.0", source);
var inspected = (await manager.InspectAsync(request)).Value;
Assert(
    inspected.Request.Name == request.Name && inspected.RequiresBuildApproval && inspected.ContentHash.Length == 64,
    "Typed inspection lost approval identity.");
await Deny("install", true);
await Failure(
    () => manager.InstallBundleAsync(request, new("resource-denied", inspected.ContentHash)),
    "plugin-manager/forbidden");
await Deny("install", false);
Assert(
    (await manager.WaitForInstallAsync("resource-denied")).Value is null,
    "Resource refusal admitted an installation.");
var stale = (await manager.InstallBundleAsync(request, new("stale", "wrong-hash"))).Value;
Assert(
    stale.Stage == "inspect" && stale.Application == "failed" && stale.Error == "inspection-changed" && stale.Installed,
    "Admitted stale inspection lost its installation outcome.");
await Deny("build", true);
var deniedBuild = (await manager.InstallBundleAsync(request, new("build-denied", inspected.ContentHash))).Value;
Assert(
    deniedBuild.Stage == "inspect" && deniedBuild.Application == "failed" &&
    deniedBuild.Error == "build-not-approved" && deniedBuild.Installed,
    "Admitted build refusal lost its installation outcome.");
using (var probe = await Probe())
    Assert(
        probe.RootElement.GetProperty("preparations").GetInt32() == 0,
        "Inspect authority silently granted build execution.");
await Deny("build", false);

var incompatible = new PackageRequest("incompatible", "1.0.0", source);
var incompatibleInspection = (await manager.InspectAsync(incompatible)).Value;
var refusedVersion = (await manager.InstallBundleAsync(
    incompatible,
    new("version-refused", incompatibleInspection.ContentHash))).Value;
Assert(
    refusedVersion.Application == "failed" && !refusedVersion.Installed && refusedVersion.Stage == "inspect",
    "Build approval silently granted a DSH version exemption.");
using (var probe = await Probe())
    Assert(
        probe.RootElement.GetProperty("preparations").GetInt32() == 0,
        "Incompatible known declarations reached preparation.");
await Deny("compatibility-grant", true);
await Deny("compatibility-write", true);
await Failure(
    () => manager.SetVersionExemptionAsync("incompatible@1.0.0", "0.2.0-rc.2", true, true),
    "plugin-manager/forbidden");
await Deny("compatibility-grant", false);
await Deny("compatibility-write", false);
var grant = (await manager.SetVersionExemptionAsync("incompatible@1.0.0", "0.2.0-rc.2", true, true)).Value;
Assert(
    grant.Changed && grant.Error is null && grant.Application == "applied",
    $"Typed exact-version grant failed: {grant.Application}/{grant.Error}: {grant.Diagnostic}");
var grants = (await manager.ListVersionExemptionsAsync()).Value;
Assert(
    grants.Exemptions["incompatible@1.0.0"].Contains("0.2.0-rc.2"),
    "Typed dictionary projection lost the exact grant.");
await Deny("build", true);
var deniedAfterGrant = (await manager.InstallBundleAsync(
    incompatible,
    new("grant-does-not-build", incompatibleInspection.ContentHash))).Value;
Assert(
    deniedAfterGrant.Error == "build-not-approved" && !deniedAfterGrant.Installed,
    "A version grant authorized build execution.");
await Deny("build", false);

var inspectingRequest = new PackageRequest("inspection-cancelled", "1.0.0", source);
var inspectingFacts = (await manager.InspectAsync(inspectingRequest)).Value;
await Control("hold", inspectingRequest.Name, inspectingRequest.Version, "inspect");
var inspecting = manager.InstallBundleAsync(inspectingRequest, new("request-inspecting", inspectingFacts.ContentHash));
await Control("wait-preparing", inspectingRequest.Name, inspectingRequest.Version);
var inspectingWait = manager.WaitForInstallAsync("request-inspecting");
await Control("wait-observed", "request-inspecting");
var inspectionCancellation = (await manager.CancelInstallAsync("request-inspecting")).Value;
var inspectedCancellation = (await inspecting).Value;
Assert(
    inspectionCancellation.Status == "cancelled" && inspectedCancellation.Stage == "inspect" &&
    inspectedCancellation.Application == "cancelled" &&
    ((await inspectingWait).Value)?.RequestId == "request-inspecting",
    "Blocked inspection did not belong to the request task owner.");
using (var probe = await Probe())
{
    Assert(
        probe.RootElement.GetProperty("preparations").GetInt32() == 0,
        "Cancelled inspection reached SDK preparation.");
    Assert(
        !probe
            .RootElement.GetProperty("permissions")
            .EnumerateArray()
            .Any(item => item.GetString() == "build:inspection-cancelled"),
        "Cancelled inspection reached build approval.");
}

var policyRequest = new PackageRequest("policy-cancelled", "1.0.0", source);
var policyFacts = (await manager.InspectAsync(policyRequest)).Value;
await Control("hold-policy", policyRequest.Name);
var policyInstall = manager.InstallBundleAsync(policyRequest, new("request-policy", policyFacts.ContentHash));
await Control("wait-policy", policyRequest.Name);
var policyWait = manager.WaitForInstallAsync("request-policy");
await Control("wait-observed", "request-policy");
var policyCancel = manager.CancelInstallAsync("request-policy");
await Control("wait-cancelling", "request-policy");
var responseWindow = Task.Delay(TimeSpan.FromMilliseconds(350));
Assert(
    await Task.WhenAny(policyCancel, responseWindow) == responseWindow && !policyInstall.IsCompleted &&
    !policyWait.IsCompleted,
    "Explicit cancellation abandoned the host's pending build decision.");
await Control("release-policy", policyRequest.Name);
Assert(
    ((await policyCancel).Value).Status == "cancelled" && ((await policyInstall).Value).Application == "cancelled" &&
    ((await policyWait).Value)?.Application == "cancelled",
    "Settled build decision lost the admitted cancellation outcome.");
using (var probe = await Probe())
    Assert(
        probe.RootElement.GetProperty("preparations").GetInt32() == 0,
        "Cancelled build approval reached platform preparation.");

await Control("hold", "existing", "2.0.0");
using var responseLifetime = new CancellationTokenSource();
var installing = manager.InstallBundleAsync(
    request,
    new("disconnected-update", inspected.ContentHash),
    responseLifetime.Token);
await Control("wait-preparing", "existing", "2.0.0");
await Event(
    events,
    frame => frame.Event == "plugin-manager/install-log" &&
        frame.Args![0].GetProperty("requestId").GetString() == "disconnected-update");
var nextBeforeDispose = events.MoveNextAsync().AsTask();
await notifications.DisposeAsync();
await IgnoreWithdrawal(nextBeforeDispose);
responseLifetime.Cancel();
await Failure(() => installing, "gateway/cancelled");
using (var probe = await Probe())
    Assert(
        probe.RootElement.GetProperty("cancellations").GetInt32() == 1,
        "Disconnect cancelled installation preparation.");
await using var reconnected = new EventObservation(connection);
await reconnected.StartAsync();
await using var resumed = reconnected.Read().GetAsyncEnumerator();
Assert(
    ((await manager.ListBundlesAsync()).Value).Any(row => row.Name == "existing" && row.Version == "1.0.0"),
    "Reconnect inferred installation success without querying.");
var waiting = manager.WaitForInstallAsync("disconnected-update");
await Control("wait-observed", "disconnected-update");
await Control("release", "existing", "2.0.0");
var updated = (await waiting).Value ??
    throw new InvalidOperationException("The original active request was not observed.");
Assert(
    updated.RequestId == "disconnected-update" && updated.Target == "existing" && updated.Installed &&
    updated.Selected &&
    updated.Application == "restart-required" && updated.Error is null && updated.ToolExitCode is null,
    $"Wait lost the real installed update/restart outcome: {updated.Stage}/{updated.Application}/{updated.Error}: {updated.Diagnostic}");
Assert(
    ((await manager.ListBundlesAsync()).Value).Single(row => row.Name == "existing").Version == "1.0.0",
    "Install rewrote the live package mapping before restart.");
await Event(resumed, frame => frame.Event == "plugin-manager/changed");
using (var probe = await Probe())
{
    Assert(
        probe.RootElement.GetProperty("cancellations").GetInt32() == 1,
        "Observer withdrawal became installation cancellation.");
    using var saved = JsonDocument.Parse(probe.RootElement.GetProperty("manifest").GetString()!);
    Assert(
        saved.RootElement.GetProperty("dependencies").GetProperty("existing").GetString() == "2.0.0",
        "Wait result did not match durable profile selection.");
}

Assert(
    (await manager.WaitForInstallAsync("disconnected-update")).Value is null,
    "Completed requests became a fabricated durable task history.");

foreach (var mode in new[] { "cancelled", "failed" })
{
    var name = "cleanup-" + mode;
    var requestId = "request-" + name;
    var candidate = new PackageRequest(name, "1.0.0", source);
    var inspection = (await manager.InspectAsync(candidate)).Value;
    await Control("hold", name, "1.0.0", mode);
    var operation = manager.InstallBundleAsync(candidate, new(requestId, inspection.ContentHash));
    await Control("wait-preparing", name, "1.0.0");
    var wait = manager.WaitForInstallAsync(requestId);
    await Control("wait-observed", requestId);
    var cancel = (await manager.CancelInstallAsync(requestId)).Value;
    var result = (await operation).Value;
    var observed = (await wait).Value;
    Assert(
        cancel.Status == mode && result.Application == mode && observed?.RequestId == requestId,
        "Explicit cancel lost cleanup failure distinction.");
    Assert(
        !result.Installed && !result.Selected && result.Stage == "prepare" && result.Residuals?.Count == 1 &&
        Directory.Exists(result.Residuals.Single()) && result.ToolExitCode == (mode == "failed" ? 42 : 130),
        "Typed cancellation discarded actual residual files or exit status.");
}

await using (var cancelledNotifications = new EventObservation(connection))
using (var lifetime = new CancellationTokenSource())
{
    await cancelledNotifications.StartAsync(lifetime.Token);
    await using var reader = cancelledNotifications.Read().GetAsyncEnumerator();
    var reading = ReadUntilEnd(reader);
    lifetime.Cancel();
    await reading.WaitAsync(TimeSpan.FromSeconds(10));
}

foreach (var control in new[] { "terminal", "incomplete", "malformed" })
{
    await using var invalidConnection = new TypertConnection(http, new Uri(address, "/" + control));
    await using var invalid = new EventObservation(invalidConnection);
    if (control == "terminal")
    {
        var error = await StreamFailure(() => invalid.StartAsync(), "fixture/stream");
        Assert(
            error.Details!.Value.GetProperty("stage").GetString() == "source",
            "Terminal stream error lost its details.");
    }
    else
    {
        try
        {
            await invalid.StartAsync();
            throw new InvalidOperationException("Expected a carrier failure.");
        }
        catch (RemoteStreamCarrierException)
        {
        }
    }
}

await Control("remove-source");
try
{
    await ReadUntilEnd(resumed);
    throw new InvalidOperationException("Expected source withdrawal failure.");
}
catch (RemoteStreamCarrierException)
{
}

await Control("restore-source");
await using var restored = new EventObservation(connection);
await restored.StartAsync();
await using var restoredEvents = restored.Read().GetAsyncEnumerator();
Assert(
    ((await manager.ListPluginsAsync()).Value).Single(row => row.EntryId == "root:worker").Enabled,
    "Notification errors changed the managed plugin outcome.");
var shutdownRequest = new PackageRequest("inspection-shutdown", "1.0.0", source);
var shutdownFacts = (await manager.InspectAsync(shutdownRequest)).Value;
await Control("hold", shutdownRequest.Name, shutdownRequest.Version, "inspect");
var shutdownInstall = manager.InstallBundleAsync(shutdownRequest, new("request-shutdown", shutdownFacts.ContentHash));
await Control("wait-preparing", shutdownRequest.Name, shutdownRequest.Version);
using var beforeShutdown = await Probe();
await Control("shutdown-session");
var shutdownResult = await shutdownInstall;
if (shutdownResult.Ok)
    Assert(shutdownResult.Value.Application == "cancelled", "Session shutdown reported successful inspection install.");
else
    Assert(
        shutdownResult.Error.Code is "gateway/internal" or "gateway/cancelled",
        "Retired installation observation lost its generation failure.");

using (var probe = await Probe())
{
    Assert(
        probe.RootElement.GetProperty("preparations").GetInt32() ==
        beforeShutdown.RootElement.GetProperty("preparations").GetInt32(),
        "Shutdown inspection entered SDK preparation.");
    Assert(
        probe.RootElement.GetProperty("cancellations").GetInt32() ==
        beforeShutdown.RootElement.GetProperty("cancellations").GetInt32() + 1,
        "Session shutdown returned before inspection cancellation drained.");
    Assert(
        !probe
            .RootElement.GetProperty("permissions")
            .EnumerateArray()
            .Any(item => item.GetString() == "build:inspection-shutdown"),
        "Shutdown inspection entered build approval.");
}

Console.WriteLine("PASS actual generated .NET PluginManager and RemoteNotifications package clients");

async Task Control(string control, string? name = null, string? version = null, string? mode = null)
{
    var query = name is null ? "" : "?name=" + Uri.EscapeDataString(name) + "&version=" + version + "&mode=" + mode;
    using var response = await http.PostAsync(new Uri(address, "/control/" + control + query), null);
    response.EnsureSuccessStatusCode();
}

async Task Deny(string operation, bool enabled)
{
    using var response = await http.PostAsync(
        new Uri(address, "/control/deny?operation=" + operation + "&enabled=" + enabled.ToString().ToLowerInvariant()),
        null);
    response.EnsureSuccessStatusCode();
}

async Task<JsonDocument> Probe() => JsonDocument.Parse(await http.GetStringAsync(new Uri(address, "/control/probe")));

static async Task<bool> Next(IAsyncEnumerator<ObservedEvent> reader) =>
    await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20));

static async Task Event(IAsyncEnumerator<ObservedEvent> reader, Func<ObservedEvent, bool> matches)
{
    while (await Next(reader))
        if (matches(reader.Current))
            return;
    throw new InvalidOperationException("Expected notification was not delivered.");
}

static async Task ReadUntilEnd(IAsyncEnumerator<ObservedEvent> reader)
{
    while (await Next(reader))
    {
    }
}

static async Task<RemoteError> Failure<T>(Func<Task<Cordis.Composition.RemoteResult<T>>> call, string code)
{
    var result = await call().WaitAsync(TimeSpan.FromSeconds(20));
    Assert(!result.Ok, "Expected Remote failure: " + code);
    Assert(result.Error.Code == code, "Wrong Remote failure: " + result.Error.Code + ", expected " + code);
    return result.Error;
}

static async Task<RemoteError> StreamFailure(Func<Task> read, string code)
{
    try
    {
        await read().WaitAsync(TimeSpan.FromSeconds(20));
    }
    catch (RemoteError error)
    {
        Assert(error.Code == code, "Wrong stream failure: " + error.Code);
        return error;
    }

    throw new InvalidOperationException("Expected remote stream failure: " + code);
}

static async Task IgnoreWithdrawal(Task<bool> pending)
{
    // Buffered progress may already have completed this read; otherwise withdrawal must fail it.
    try
    {
        await pending.WaitAsync(TimeSpan.FromSeconds(20));
    }
    catch (OperationCanceledException)
    {
    }
    catch (InvalidOperationException error) when (error.Message.Contains("withdrawn", StringComparison.Ordinal))
    {
    }
}

static void Assert(bool value, string reason)
{
    if (!value)
        throw new InvalidOperationException(reason);
}

sealed record ObservedEvent(string Event, IReadOnlyList<JsonElement> Args);

sealed class EventObservation : IAsyncDisposable
{
    private readonly Context owner = new();
    private readonly TypertRemoteEventsClient client;
    private readonly Channel<ObservedEvent> queue = Channel.CreateUnbounded<ObservedEvent>();
    private Task? pump;
    private int collectionCount;

    public TaskCompletionSource Collections
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public EventObservation(TypertConnection connection) => client = new(owner, new TypertRegistry(owner), connection);

    public async Task StartAsync(CancellationToken token = default)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await owner.RunAsync(ctx =>
        {
            CollectionEvent.On(
                client,
                ctx,
                (values, labels, required) =>
                {
                    if (collectionCount == 0 && (values?.Count != 2 || values[0] != "text" || values[1] is not null ||
                            !labels.ContainsKey("empty") || labels["empty"] is not null ||
                            required.Single() != "required"))
                        Collections.TrySetException(new Exception("Saved event listener lost nullable elements."));
                    else if (collectionCount == 1 && (values is not null || labels.Count != 0 || required.Count != 0))
                        Collections.TrySetException(new Exception("Saved event listener lost nullable root."));
                    else if (++collectionCount == 2)
                        Collections.TrySetResult();
                });
            ChangedEvent.On(client, ctx, _ => queue.Writer.TryWrite(new("plugin-manager/changed", [])));
            PluginInstallStateEventTypert.On(client, ctx, value => Publish("plugin-manager/install-state", value));
            PluginInstallLogEventTypert.On(client, ctx, value => Publish("plugin-manager/install-log", value));
            return Task.CompletedTask;
        });
        pump = Run();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));

        async Task Run()
        {
            try
            {
                await client.RunAsync(_ => ready.TrySetResult(), token);
                queue.Writer.TryComplete();
            }
            catch (Exception error)
            {
                ready.TrySetException(error);
                queue.Writer.TryComplete(error);
            }
        }
    }

    public IAsyncEnumerable<ObservedEvent> Read() => queue.Reader.ReadAllAsync();

    private void Publish(string name, PackageProgress progress) => queue.Writer.TryWrite(
        new(
            name,
            [JsonSerializer.SerializeToElement(progress, PluginManagerJson.Default.PackageProgress)]));

    public async ValueTask DisposeAsync()
    {
        await client.DisposeAsync();
        if (pump is not null)
            await pump;
        await owner.DisposeAsync();
    }
}
