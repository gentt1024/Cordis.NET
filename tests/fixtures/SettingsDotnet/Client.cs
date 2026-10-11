using TypertConnection = Cordis.Composition.TypertConnection;
using System.Text;
using System.Text.Json;
using IndependentSettings.Client;
using RemoteError = Cordis.Composition.RemoteError;

if (args.Length != 1)
    throw new ArgumentException("Expected the independent Settings host address.");
if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("This consumer requires static JSON metadata.");
using var http = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(30)
};
var host = new Uri(args[0]);
await using var connection = new TypertConnection(http, new Uri(host, "/remote"));
await using var remote = new SettingsClient(connection);
var mirror = new DescribeMirror(remote);
await mirror.LoadAsync();
var initial = mirror.View ?? throw new InvalidOperationException("Describe did not publish a complete view.");
VerifyView(initial);
Assert(initial.Namespaces.All(row => row.Revision == 0), "New Settings owner did not start at revision zero.");

var serialized = JsonSerializer.Serialize(initial, SettingsClientJson.Default.SettingsDescribeValue);
using (var document = JsonDocument.Parse(serialized))
{
    var wire = document.RootElement;
    Assert(wire.GetProperty("namespaces").GetArrayLength() == 2, "Two real entries were not described together.");
    Assert(
        !serialized.Contains("root-default-secret", StringComparison.Ordinal) &&
        !serialized.Contains("nested-default-secret", StringComparison.Ordinal),
        "Secret values leaked.");
    Assert(!wire.TryGetProperty("diagnostics", out _), "Native diagnostics changed the fixed Remote result.");
}

var roundtrip = JsonSerializer.Deserialize(serialized, SettingsClientJson.Default.SettingsDescribeValue)!;
VerifyView(roundtrip);
var missingSecrets = serialized.Replace("\"secrets\":", "\"omittedSecrets\":", StringComparison.Ordinal);
ExpectJsonFailure(missingSecrets, "Missing required secrets was accepted.");
var missingApplies = serialized.Replace("\"applies\":", "\"omittedApplies\":", StringComparison.Ordinal);
ExpectJsonFailure(missingApplies, "Missing required applies was accepted.");
using (var bad = new StringContent("""{"type":"client-request","rpcId":"bad-argument","method":"settings/describe","payload":{"args":{"unexpected":1}}}""", Encoding.UTF8, "application/json"))
using (var response = await http.PostAsync(new Uri(host, "/remote/settings/describe"), bad))
using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
{
    Assert(!document.RootElement.GetProperty("result").GetProperty("ok").GetBoolean(), "Unexpected Remote argument was accepted.");
    Assert(
        document.RootElement.GetProperty("result").GetProperty("error").GetProperty("code").GetString() == "gateway/arguments-invalid",
        "Argument rejection lost its contract code.");
}

await Control("fail");
var refused = await Failure(() => remote.DescribeAsync(), "fixture/refused");
Assert(
    refused.Details!.Value.GetProperty("ns").GetString() == "one" &&
    refused.Details.Value.GetProperty("stage").GetString() == "provider",
    "Owner details were lost or disposed.");
await mirror.LoadAsync();
Assert(
    ReferenceEquals(initial, mirror.View) && mirror.Error is not null,
    "Failed describe discarded the held document.");
await Control("withdraw");
var missing = await Failure(() => remote.DescribeAsync(), "gateway/internal");
Assert(
    missing.Message.Contains("settings service is absent", StringComparison.Ordinal),
    "Absent provider is not actionable.");
await Control("reprovide");
await mirror.LoadAsync();
Assert(mirror.Error is null, "Successful retry retained the previous failure.");
VerifyView(mirror.View!);

// A describe accepted by the optional provider may settle after that provider retires.
await Control("hold");
var accepted = remote.DescribeAsync();
await Control("wait-held");
await Control("withdraw");
await Control("release");
VerifyView((await accepted).Value);
await Control("reprovide");

// Follow the pinned Settings mirror's held-view and invalidation oracle with the typed caller.
await Control("hold");
var beforeReads = mirror.Reads;
var inFlight = mirror.LoadAsync();
await Control("wait-held");
var invalidated = mirror.LoadAsync();
await Control("release");
await Task.WhenAll(inFlight, invalidated);
Assert(
    mirror.Reads == beforeReads + 2 && mirror.Error is null,
    "Invalidation during a describe did not rerun the read.");

await Control("hold");
var oldDefinition = remote.DescribeAsync();
await Control("wait-held");
await Control("definition-withdraw");
await Control("release");
await Failure(() => oldDefinition, "gateway/definition-unavailable");
await Control("reregister");
await Control("reprovide");
VerifyView((await remote.DescribeAsync()).Value);
await Control("suspend");
await Failure(() => remote.DescribeAsync(), "gateway/definition-unavailable");
await Control("resume");
VerifyView((await remote.DescribeAsync()).Value);

await Control("logger-fail");
VerifyView((await remote.DescribeAsync()).Value);
await Control("logger-restore");

await Control("hold");
await using var retiringClient = new SettingsClient(connection);
var lateClientCall = retiringClient.DescribeAsync();
await Control("wait-held");
var retiring = retiringClient.DisposeAsync().AsTask();
await Control("release");
await Failure(() => lateClientCall, "gateway/cancelled");
await retiring;
await Failure(() => retiringClient.DescribeAsync(), "gateway/internal");
await Control("reprovide");
VerifyView((await remote.DescribeAsync()).Value);
using (var stillBorrowed = await http.PostAsync(new Uri(host, "/control/status"), null))
    Assert(stillBorrowed.IsSuccessStatusCode, "Disposing one client disposed its borrowed HttpClient.");
var forms = (await remote.DescribeAsync()).Value;
var one = forms.Namespaces.Single(row => row.Ns == "one");
var two = forms.Namespaces.Single(row => row.Ns == "two");
var changed = (await remote.UpdateAsync("one", Json("""{"label":"edited"}"""), one.Revision)).Value;
Assert(
    changed.Revision == one.Revision + 1 && changed.Value.GetProperty("label").GetString() == "edited",
    "Numeric update did not publish the committed form revision.");
Assert(
    changed.Base!.Value.GetProperty("label").GetString() == "default-label" &&
    changed.User!.Value.GetProperty("label").GetString() == "edited",
    "Base/user override ownership changed.");
var saved = await Source();
Assert(saved.Contains("edited", StringComparison.Ordinal), "Successful update did not reach the profile document.");
var stale = await Failure(
    () => remote.UpdateAsync("one", Json("""{"label":"stale"}"""), one.Revision),
    "settings/conflict");
Assert(
    stale.Details!.Value.GetProperty("expected").GetDouble() == one.Revision &&
    stale.Details.Value.GetProperty("actual").GetDouble() == changed.Revision,
    "Numeric conflict details changed.");
Assert(await Source() == saved, "A stale form request was persisted.");
forms = (await remote.DescribeAsync()).Value;
Assert(
    forms.Namespaces.Single(row => row.Ns == "two").Revision == two.Revision,
    "Editing one entry advanced another entry.");
changed = (await remote.UpdateAsync("one", Json("""{"nested":{"visible":"without-prior-check"}}"""))).Value;
Assert(
    changed.Value.GetProperty("nested").GetProperty("visible").GetString() == "without-prior-check" &&
    changed.Secrets.All(secret => secret.Set),
    "Omitted expectedRevision bypassed or damaged form editing.");
changed = (await remote.MutateAsync(
    "one",
    new[]
    {
        new SettingsPathOp("set", new[] { "rootSecret" })
        {
            Value = Json("\"written-secret\"")
        }
    },
    changed.Revision)).Value;
Assert(
    !changed.Value.TryGetProperty("rootSecret", out _) && changed.Secrets.All(secret => secret.Set),
    "A writable secret was returned by the form.");
saved = await Source();
Assert(saved.Contains("written-secret", StringComparison.Ordinal), "Secret write did not persist.");
changed = (await remote.MutateAsync(
    "one",
    new[]
    {
        new SettingsPathOp("set", new[] { "nested", "visible" })
        {
            Value = Json("\"path-edit\"")
        },
        new SettingsPathOp("set", new[] { "genuineNull" })
        {
            Value = Json("null")
        }
    },
    changed.Revision)).Value;
Assert(
    changed.Value.GetProperty("nested").GetProperty("visible").GetString() == "path-edit" &&
    changed.Value.GetProperty("genuineNull").ValueKind == JsonValueKind.Null,
    "Ordered form edits or explicit null changed.");
Assert(
    (await Source()).Contains("written-secret", StringComparison.Ordinal),
    "An omitted secret was lost by a path edit.");
var beforeRefusal = await Source();
await Failure(() => remote.UpdateAsync("one", Json("""{"hidden":"must-not-save"}""")), "settings/rejected");
Assert(await Source() == beforeRefusal, "Omission of expectedRevision bypassed selection authorization.");
foreach (var (method, field) in new[] { ("update", "patch"), ("replace", "section"), ("mutate", "ops") })
{
    foreach (var revision in new[] { "null", "\"0\"", "\"sha256:old\"" })
    {
        var value = method == "mutate" ? "[]" : "{}";
        await RawFailure(
            method,
            "{\"ns\":\"one\",\"" + field + "\":" + value +
            ",\"expectedRevision\":" + revision + "}",
            "gateway/input-invalid");
    }
}

await RawFailure("mutate", """{"ns":"one","ops":[{"op":"set","path":["label"]}]}""", "settings/rejected");
await RawFailure(
    "mutate",
    """{"ns":"one","ops":[{"op":"unset","path":["label"],"value":null}]}""",
    "settings/rejected");
await RawFailure("mutate", """{"ns":"one","ops":[{"op":"set","path":[],"value":null}]}""", "settings/rejected");
Assert(await Source() == beforeRefusal, "Invalid wire input changed the profile document.");
changed = (await remote.MutateAsync(
    "one",
    new[] { new SettingsPathOp("unset", new[] { "rootSecret" }) },
    changed.Revision)).Value;
Assert(
    changed.Secrets.Single(secret => secret.Path.SequenceEqual(new[] { "rootSecret" })).Set &&
    !(await Source()).Contains("written-secret", StringComparison.Ordinal),
    "A generated unset did not omit its value or restore inherited secret presence.");
await Control("fail");
await Failure(
    () => remote.UpdateAsync("one", Json("""{"label":"committed-before-result-failure"}"""), changed.Revision),
    "fixture/refused");
Assert(
    (await Source()).Contains("committed-before-result-failure", StringComparison.Ordinal),
    "A post-commit describe failure was mistaken for a rejected write.");
await Control("reprovide");
changed = ((await remote.DescribeAsync()).Value).Namespaces.Single(row => row.Ns == "one");
changed = (await remote.ReplaceAsync("one", Json("{}"), changed.Revision)).Value;
Assert(
    changed.Value.GetProperty("label").GetString() == "default-label" &&
    changed.Value.GetProperty("nested").GetProperty("visible").GetString() == "nested-visible",
    "Replace did not restore inherited live fields.");
await Control("two-disable");
Assert(
    ((await remote.DescribeAsync()).Value).Namespaces.All(row => row.Ns != "two"),
    "A disappeared namespace remained available.");
await Control("two-enable");
var reentered = ((await remote.DescribeAsync()).Value).Namespaces.Single(row => row.Ns == "two");
Assert(
    reentered.Revision > two.Revision && !reentered.AutoGenerate,
    "Entry reentry lost its owner revision or presentation policy.");
await Control("settings-restart");
Assert(
    ((await remote.DescribeAsync()).Value).Namespaces.All(row => row.Revision == 0),
    "Form revisions persisted across Settings owner restart.");
await Control("readonly");
saved = await Source();
await Failure(() => remote.UpdateAsync("one", Json("""{"label":"read-only-refused"}""")), "settings/rejected");
Assert(await Source() == saved, "Omitted expectedRevision bypassed the read-only owner.");
await Control("settings-restart");
VerifyView((await remote.DescribeAsync()).Value);
Console.WriteLine(
    "Typed Settings passed: numeric update/replace/mutate; stale/omitted revision; null/string refusal; layered redaction; ordered secret/null edits; independent entries and owner lifecycle; static metadata and provider/definition/client retirement.");

async Task<string> Source()
{
    using var response = await http.PostAsync(new Uri(host, "/control/source"), null);
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return document.RootElement.GetProperty("document").GetString()!;
}

async Task RawFailure(string method, string arguments, string code)
{
    using var body = new StringContent("{\"type\":\"client-request\",\"rpcId\":\"raw-check\",\"method\":\"settings/" + method + "\",\"payload\":{\"args\":" + arguments + "}}", Encoding.UTF8, "application/json");
    using var response = await http.PostAsync(new Uri(host, "/remote/settings/" + method), body);
    response.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    Assert(
        !document.RootElement.GetProperty("result").GetProperty("ok").GetBoolean() &&
        document.RootElement.GetProperty("result").GetProperty("error").GetProperty("code").GetString() == code,
        "Invalid standard Settings input changed its refusal: " + method + " " + arguments);
}

static JsonElement Json(string text)
{
    using var document = JsonDocument.Parse(text);
    return document.RootElement.Clone();
}

static JsonElement SchemaNode(JsonElement schema, params string[] path)
{
    var refs = schema.GetProperty("refs");
    var node = refs.GetProperty(
        schema.GetProperty("uid").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
    foreach (var key in path)
        node = refs.GetProperty(
            node
                .GetProperty("dict")
                .GetProperty(key)
                .GetInt32()
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
    return node;
}

async Task Control(string command)
{
    using var response = await http.PostAsync(new Uri(host, "/control/" + command), null);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException(
            "Fixture control " + command + " failed: " + await response.Content.ReadAsStringAsync());
}

static async Task<RemoteError> Failure<T>(Func<Task<Cordis.Composition.RemoteResult<T>>> call, string code)
{
    var result = await call().WaitAsync(TimeSpan.FromSeconds(15));
    Assert(!result.Ok, "Expected Remote failure " + code);
    Assert(result.Error.Code == code, "Expected " + code + ", got " + result.Error.Code);
    return result.Error;
}

static void VerifyView(SettingsDescribeValue value)
{
    Assert(value.Writable && value.HasDocument, "Host deployment facts were inferred or changed.");
    Assert(value.Namespaces.Count == 2, "A selected entry was omitted.");
    foreach (var row in value.Namespaces)
    {
        Assert(
            row.Ns is "one" or "two" && row.Applies == "live" && double.IsFinite(row.Revision) && row.Revision >= 0,
            "Native namespace facts changed.");
        Assert(row.AutoGenerate == (row.Ns == "one"), "Host page selection was lost.");
        Assert(
            SchemaNode(row.Schema, "label").GetProperty("meta").GetProperty("default").GetString() == "default-label" &&
            SchemaNode(row.Schema, "defaulted").GetProperty("meta").GetProperty("default").GetInt32() == 7,
            "Public form defaults were lost by the generated client.");
        var secretMeta = SchemaNode(row.Schema, "rootSecret").GetProperty("meta");
        Assert(
            !secretMeta.TryGetProperty("default", out _) && !secretMeta.TryGetProperty("required", out _),
            "Secret schema metadata leaked.");
        var nestedDefault = SchemaNode(row.Schema, "nested").GetProperty("meta").GetProperty("default");
        Assert(
            nestedDefault.GetProperty("visible").GetString() == "nested-visible" &&
            !nestedDefault.TryGetProperty("secret", out _),
            "A composite default lost its public value or exposed a secret.");
        Assert(row.Base is not null && row.User is not null, "Available base/user layers were omitted.");
        Assert(
            !row.Base!.Value.TryGetProperty("rootSecret", out _) &&
            !row.User!.Value.TryGetProperty("rootSecret", out _),
            "A secret leaked through a configuration layer.");
        Assert(row.Value.GetProperty("label").GetString() == "default-label", "Ordinary configuration was lost.");
        Assert(row.Value.GetProperty("defaulted").GetInt32() == 7, "Resolved defaults were lost.");
        Assert(
            row.Value.GetProperty("genuineNull").ValueKind == JsonValueKind.Null,
            "JSON null was collapsed into absence.");
        Assert(
            !row.Value.TryGetProperty("rootSecret", out _) && !row.Value.TryGetProperty("hidden", out _),
            "Secret/hidden root was published.");
        var nested = row.Value.GetProperty("nested");
        Assert(
            nested.GetProperty("visible").GetString() == "nested-visible" && !nested.TryGetProperty("secret", out _),
            "Nested secret projection changed.");
        Assert(row.Secrets.Count == 2 && row.Secrets.All(secret => secret.Set), "Secret presence was not retained.");
        Assert(
            row.Secrets.Any(secret => secret.Path.SequenceEqual(["rootSecret"])) &&
            row.Secrets.Any(secret => secret.Path.SequenceEqual(["nested", "secret"])),
            "Secret paths were lost.");
    }
}

static void ExpectJsonFailure(string json, string reason)
{
    try
    {
        JsonSerializer.Deserialize(json, SettingsClientJson.Default.SettingsDescribeValue);
    }
    catch (JsonException)
    {
        return;
    }

    throw new InvalidOperationException(reason);
}

static void Assert(bool condition, string reason)
{
    if (!condition)
        throw new InvalidOperationException(reason);
}

// A bounded consumer oracle from ui-settings/settings-mirror.ts, separate from the RPC generator.
internal sealed class DescribeMirror(SettingsClient remote)
{
    private Task? inFlight;
    private bool rerun;
    private int generation;

    internal SettingsDescribeValue? View
    {
        get;
        private set;
    }

    internal string? Error
    {
        get;
        private set;
    }

    internal int Reads
    {
        get;
        private set;
    }

    internal Task LoadAsync()
    {
        if (inFlight is not null)
        {
            generation++;
            rerun = true;
            return inFlight;
        }

        // The first yield publishes the slot before a fast synchronous completion.
        return inFlight = RunAsync();
    }

    private async Task RunAsync()
    {
        await Task.Yield();
        try
        {
            do
            {
                rerun = false;
                var readGeneration = ++generation;
                Reads++;
                SettingsDescribeValue? result = null;
                string? error = null;
                var response = await remote.DescribeAsync();
                if (response.Ok) result = response.Value;
                else error = response.Error.Message;

                if (readGeneration != generation)
                    continue;
                if (result is not null)
                    View = result;
                Error = error;
            } while (rerun);
        }
        finally
        {
            inFlight = null;
        }
    }
}
