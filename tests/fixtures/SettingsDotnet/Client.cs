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
using var remote = new SettingsClient(http, new Uri(host, "/remote"));
var mirror = new DescribeMirror(remote);
await mirror.LoadAsync();
var initial = mirror.View ?? throw new InvalidOperationException("Describe did not publish a complete view.");
VerifyView(initial);

var serialized = JsonSerializer.Serialize(initial, SettingsClientJson.Default.SettingsDescribeValue);
using (var document = JsonDocument.Parse(serialized))
{
    var wire = document.RootElement;
    Assert(wire.GetProperty("namespaces").GetArrayLength() == 2, "Two real entries were not described together.");
    Assert(
        !serialized.Contains("root-default-secret", StringComparison.Ordinal) &&
        !serialized.Contains("nested-default-secret", StringComparison.Ordinal),
        "Secret values leaked.");
    Assert(!serialized.Contains("\"default\":", StringComparison.Ordinal), "Schema defaults leaked.");
    Assert(!wire.TryGetProperty("diagnostics", out _), "Native diagnostics changed the fixed Remote result.");
}

var roundtrip = JsonSerializer.Deserialize(serialized, SettingsClientJson.Default.SettingsDescribeValue)!;
VerifyView(roundtrip);
var missingSecrets = serialized.Replace("\"secrets\":", "\"omittedSecrets\":", StringComparison.Ordinal);
ExpectJsonFailure(missingSecrets, "Missing required secrets was accepted.");
var missingApplies = serialized.Replace("\"applies\":", "\"omittedApplies\":", StringComparison.Ordinal);
ExpectJsonFailure(missingApplies, "Missing required applies was accepted.");
using (var bad = new StringContent("""{"args":{"unexpected":1}}""", Encoding.UTF8, "application/json"))
using (var response = await http.PostAsync(new Uri(host, "/remote/settings/describe"), bad))
using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
{
    Assert(!document.RootElement.GetProperty("ok").GetBoolean(), "Unexpected Remote argument was accepted.");
    Assert(
        document.RootElement.GetProperty("error").GetProperty("code").GetString() == "gateway/arguments-invalid",
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
VerifyView(await accepted);
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
VerifyView(await remote.DescribeAsync());
await Control("suspend");
await Failure(() => remote.DescribeAsync(), "gateway/definition-unavailable");
await Control("resume");
VerifyView(await remote.DescribeAsync());

await Control("logger-fail");
VerifyView(await remote.DescribeAsync());
await Control("logger-restore");

await Control("hold");
using var retiringClient = new SettingsClient(http, new Uri(host, "/remote"));
var lateClientCall = retiringClient.DescribeAsync();
await Control("wait-held");
retiringClient.Dispose();
await Control("release");
await Failure(() => lateClientCall, "gateway/internal");
await Failure(() => retiringClient.DescribeAsync(), "gateway/internal");
await Control("reprovide");
VerifyView(await remote.DescribeAsync());
using (var stillBorrowed = await http.PostAsync(new Uri(host, "/control/status"), null))
    Assert(stillBorrowed.IsSuccessStatusCode, "Disposing one client disposed its borrowed HttpClient.");
Console.WriteLine(
    "Typed Settings describe passed: real profile, two entries, redaction, static metadata, retry, invalidation and separate provider/definition/client retirement.");

async Task Control(string command)
{
    using var response = await http.PostAsync(new Uri(host, "/control/" + command), null);
    if (!response.IsSuccessStatusCode)
        throw new InvalidOperationException(
            "Fixture control " + command + " failed: " + await response.Content.ReadAsStringAsync());
}

static async Task<RemoteError> Failure(Func<Task<SettingsDescribeValue>> call, string code)
{
    try
    {
        await call().WaitAsync(TimeSpan.FromSeconds(15));
    }
    catch (RemoteError failure)
    {
        Assert(failure.Code == code, "Expected " + code + ", got " + failure.Code + ": " + failure.Message);
        return failure;
    }

    throw new InvalidOperationException("Expected Remote failure " + code + ".");
}

static void VerifyView(SettingsDescribeValue value)
{
    Assert(!value.Writable && value.HasDocument, "Host deployment facts were inferred or changed.");
    Assert(value.Namespaces.Count == 2, "A selected entry was omitted.");
    foreach (var row in value.Namespaces)
    {
        Assert(
            row.Ns is "one" or "two" && row.Applies == "live" && row.Revision.Length > 0,
            "Native namespace facts changed.");
        Assert(row.AutoGenerate == (row.Ns == "one"), "Host page selection was lost.");
        Assert(row.Base is null && row.User is null, "Unavailable base/user data was fabricated.");
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
                try
                {
                    result = await remote.DescribeAsync();
                }
                catch (RemoteError failure)
                {
                    error = failure.Message;
                }

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
