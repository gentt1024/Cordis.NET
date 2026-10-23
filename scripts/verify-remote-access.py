"""Exercise Remote access policy APIs through an independent NuGet consumer."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET


PROGRAM = r'''using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.Composition;

var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition || checks.Contains(name))
        throw new InvalidOperationException(name);
    checks.Add(name);
}
void Refused(TypertRemoteResult result, string reason, string name) => Check(
    !result.Ok && result.Error?.Code == "remote-access/denied" &&
    result.Error.Details?.GetProperty("reason").GetString() == reason, name);

await using var root = new Context();
var registry = new TypertRegistry(root);
var access = new TypertRemoteAccess(root);
var gateway = new TypertGateway(root, registry);
var room = TypertAccessResource.Create("room", "one");
var otherRoom = TypertAccessResource.Create("room", "two");
var document = TypertAccessResource.Create("document", "doc-1");
var agent = TypertAccessResource.Create("agent", "agent-1");
TypertPeer guest = null!;
TypertPeer owner = null!;
TypertPeer stranger = null!;
TypertPeer broken = null!;
TypertPeer brokenCondition = null!;
TypertPeer brokenExplanation = null!;
TypertPeer scoped = null!;
EffectHandle accessMount = null!;
Fiber policyOwner = null!;
var providers = 0;
var contextProviders = 0;
var business = 0;
var bindingsCalls = 0;
var explainCalls = 0;
var readerCalls = 0;
object? policyInput = null;
object? readerInput = null;
IReadOnlyList<string>? explicitIds = null;
var readerThrows = false;
Action? afterFirstListCheck = null;
IReadOnlyList<TypertAccessBinding> held = [];
var diagnostics = new List<LogMessage>();
var stringCodec = TypertCodec.Create(GateJson.Default.String, "System.String");
var inputCodec = TypertCodec.Create(GateJson.Default.AccessInput);
var intCodec = TypertCodec.Create(GateJson.Default.Int32);

TypertAccessBinding Bound(string action, TypertAccessResource resource,
    Func<TypertAccessCondition, TypertAccessDecision>? condition = null) =>
    new(new("reader", resource.Kind, [action]), resource, condition);
TypertAccessDecision ObserveCall(TypertAccessCondition condition)
{
    policyInput = condition.Arguments!["request"];
    var call = (AccessInput)policyInput!;
    Check(call.Text == "decoded" && call.RoomIds!.SequenceEqual(["one", "two"]), "policy-sees-decoded-ordinary-value");
    Check(condition.Arguments["document"] is JsonElement wire && wire.GetString() == "doc-1" &&
        condition.Arguments["agent"] is JsonElement receiver && receiver.GetString() == "agent-1",
        "policy-sees-wire-provider-identities");
    Check(!condition.Arguments.ContainsKey("optional"), "policy-preserves-argument-absence");
    return TypertAccessDecision.Allowed;
}

var remote = new BoundService(
    new("access-service", "gate", new Dictionary<string, TypertUnaryInvoker>
    {
        ["read"] = (_, arguments, _) =>
        {
            business++;
            Check(ReferenceEquals(policyInput, arguments[0]) && ReferenceEquals(readerInput, arguments[0]),
                "policy-reader-business-share-decoded-instance");
            Check(Equals(arguments[1], "resolved:doc-1") && arguments[2] is Undefined,
                "business-receives-resolved-lookup-and-absence");
            Check(ReferenceEquals(TypertInvocation.Current?.Peer, guest) &&
                ReferenceEquals(TypertPrincipal.PrincipalOf(TypertInvocation.Current), guest.Principal),
                "business-receives-admitted-principal");
            return Task.FromResult<object?>(23);
        },
        ["ping"] = (_, _, _) =>
        {
            business++;
            return Task.FromResult<object?>(19);
        },
        ["explicit"] = (_, _, _) =>
        {
            business++;
            return Task.FromResult<object?>(29);
        },
        ["list"] = (_, _, _) =>
        {
            business++;
            var selected = new List<string>();
            if (access.Allows(room))
                selected.Add("one");
            afterFirstListCheck?.Invoke();
            if (access.Allows(otherRoom))
                selected.Add("two");
            return Task.FromResult<object?>(string.Join(",", selected));
        }
    }, new Dictionary<string, TypertStreamInvoker>()),
    new Dictionary<string, IReadOnlyDictionary<string, TypertResourceReader>>
    {
        ["read"] = new Dictionary<string, TypertResourceReader>
        {
            ["room"] = (arguments, _) =>
            {
                readerCalls++;
                readerInput = arguments[0];
                return ((AccessInput)arguments[0]!).RoomIds;
            }
        },
        ["explicit"] = new Dictionary<string, TypertResourceReader>
        {
            ["room"] = (_, _) => readerThrows ? throw new InvalidOperationException("reader failed") : explicitIds
        }
    });

await root.RunAsync(ctx =>
{
    ctx.Logger.Exporter(new DelegateLogExporter(diagnostics.Add, (int)LogLevel.Warn));
    guest = new(ctx, new(Principal("guest")));
    owner = new(ctx, new(TypertPrincipal.Owner));
    stranger = new(ctx, new(Principal("stranger")));
    broken = new(ctx, new(Principal("broken")));
    brokenCondition = new(ctx, new(Principal("broken-condition")));
    brokenExplanation = new(ctx, new(Principal("broken-explanation")));
    scoped = new(ctx, new(Principal("scoped")));
    accessMount = ctx.Provide("remoteAccess", access);
    ctx.Provide("access-service", remote);
    access.RegisterPolicy(ctx, "guest", new(_ =>
    {
        bindingsCalls++;
        return held;
    }, _ =>
    {
        explainCalls++;
        return null;
    }));
    access.RegisterPolicy(ctx, "broken", new(_ => throw new InvalidOperationException("broken bindings")));
    access.RegisterPolicy(ctx, "broken-condition", new(_ =>
        [Bound("gate/ping", TypertAccessResource.Global, _ => throw new InvalidOperationException("broken condition")),
            Bound("gate/ping", TypertAccessResource.Global)]));
    access.RegisterPolicy(ctx, "broken-explanation", new(_ => [],
        _ => throw new InvalidOperationException("broken explanation")));
    registry.RegisterResource(ctx, "room", new("roomId"));
    registry.RegisterLookup(ctx, "document", new("document", "document", "System.String", "System.String", identity =>
    {
        providers++;
        return ValueTask.FromResult<object?>("resolved:" + identity);
    }) { Resource = "document" });
    registry.RegisterHostContext(ctx, "agent", new("agent", "System.String", _ =>
    {
        contextProviders++;
        return ValueTask.FromResult<Context?>(root);
    }) { Resource = "agent" });
    registry.Register(ctx, new("independent-access", [
        new("access#read", "access-service", "gate", "read", [
            new("request", "request", inputCodec),
            new("document", "document", stringCodec) { Lookup = "document" },
            new("optional", "optional", stringCodec) { AcceptsUndefined = true }
        ], intCodec) { Invocation = new("agent", "agent", stringCodec) },
        new("access#ping", "access-service", "gate", "ping", [], intCodec),
        new("access#explicit", "access-service", "gate", "explicit", [], intCodec),
        new("access#list", "access-service", "gate", "list", [], stringCodec)
    ]));
    return Task.CompletedTask;
});
var readArgs = Json("""{"request":{"text":"decoded","roomIds":["one","two"]},"document":"doc-1","agent":"agent-1"}""");
Refused(await gateway.InvokeAsync("gate/read", readArgs, guest), "no-allow", "ungranted-call-refused");
Check(providers == 0 && contextProviders == 0 && business == 0, "denied-call-has-no-provider-or-business-effects");
await root.RunAsync(_ =>
{
    held = [Bound("gate/read", room, ObserveCall), Bound("gate/read", otherRoom),
        Bound("gate/read", document), Bound("gate/read", agent)];
    return Task.CompletedTask;
});
var beforeBindings = bindingsCalls;
var allowed = await gateway.InvokeAsync("gate/read", readArgs, guest);
Check(allowed.Ok && allowed.Value?.GetInt32() == 23 && providers == 1 && contextProviders == 1 && business == 1,
    "granted-call-reaches-providers-and-business");
Check(bindingsCalls == beforeBindings + 1 && readerCalls == 2, "one-bindings-read-per-decision");
Check(TypertInvocation.Current is null, "invocation-scope-restored");
await root.RunAsync(_ =>
{
    held = [Bound("gate/read", room), Bound("gate/read", document), Bound("gate/read", agent)];
    return Task.CompletedTask;
});
Refused(await gateway.InvokeAsync("gate/read", readArgs, guest), "no-allow", "every-resource-is-required");
Check(providers == 1 && contextProviders == 1 && business == 1, "resource-intersection-refusal-precedes-providers");
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), stranger), "no-allow", "missing-principal-policy-refused");

var listConditions = 0;
TypertAccessDecision ObserveList(TypertAccessCondition condition)
{
    if (condition.Arguments is not null || condition.Action != "gate/list")
        throw new InvalidOperationException("list condition received call arguments");
    listConditions++;
    return TypertAccessDecision.Allowed;
}
await root.RunAsync(_ =>
{
    held = [Bound("gate/list", TypertAccessResource.Global), Bound("gate/list", room, ObserveList)];
    afterFirstListCheck = () => held = [Bound("gate/list", TypertAccessResource.Global), Bound("gate/list", otherRoom, ObserveList)];
    return Task.CompletedTask;
});
beforeBindings = bindingsCalls;
var beforeExplain = explainCalls;
var listed = await gateway.InvokeAsync("gate/list", Json("{}"), guest);
Check(listed.Ok && listed.Value?.GetString() == "one,two" && listConditions == 2,
    "list-checks-read-dynamic-bindings-without-arguments");
Check(bindingsCalls == beforeBindings + 3 && explainCalls == beforeExplain,
    "list-checks-use-current-invocation-and-do-not-explain");
await root.RunAsync(_ =>
{
    held = [Bound("gate/list", TypertAccessResource.Global),
        Bound("gate/list", room, _ => throw new InvalidOperationException("item condition failed"))];
    afterFirstListCheck = null;
    return Task.CompletedTask;
});
var filtered = await gateway.InvokeAsync("gate/list", Json("{}"), guest);
Check(filtered.Ok && filtered.Value?.GetString() == "", "list-policy-failures-filter-items");

await root.RunAsync(_ =>
{
    held = [Bound("gate/explicit", TypertAccessResource.Global)];
    return Task.CompletedTask;
});
var absent = await gateway.InvokeAsync("gate/explicit", Json("{}"), guest);
Check(absent.Ok && absent.Value?.GetInt32() == 29, "null-resource-reader-falls-back-to-global");
explicitIds = [];
Refused(await gateway.InvokeAsync("gate/explicit", Json("{}"), guest), "resource-invalid", "empty-explicit-resource-list-refused");
explicitIds = [""];
Refused(await gateway.InvokeAsync("gate/explicit", Json("{}"), guest), "resource-invalid", "empty-resource-identity-refused");
readerThrows = true;
Refused(await gateway.InvokeAsync("gate/explicit", Json("{}"), guest), "resource-invalid", "resource-reader-exception-contained");
readerThrows = false;
explicitIds = null;

var beforeFailure = business;
var beforeLogs = diagnostics.Count;
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), broken), "policy-failed", "policy-exception-refuses-call");
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), broken), "policy-failed", "repeated-policy-exception-refuses-call");
Check(business == beforeFailure && diagnostics.Count == beforeLogs + 2, "policy-failure-diagnosed-once");
await root.RunAsync(_ =>
{
    access.Invalidate();
    return Task.CompletedTask;
});
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), broken), "policy-failed", "invalidated-policy-still-fails-closed");
Check(diagnostics.Count == beforeLogs + 4, "invalidation-reenables-policy-diagnostic");
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), brokenCondition), "policy-failed",
    "condition-exception-prevents-later-binding-allow");
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), brokenExplanation), "policy-failed",
    "explanation-exception-fails-closed");
Check(business == beforeFailure, "policy-exceptions-never-reach-business");

await root.RunAsync(ctx =>
{
    held = [Bound("gate/ping", TypertAccessResource.Global)];
    policyOwner = ctx.Plugin(new Plugin<object?>
    {
        Apply = (child, _) => access.RegisterPolicy(child, "scoped", new(_ => [Bound("gate/ping", TypertAccessResource.Global)]))
    });
    return Task.CompletedTask;
});
await policyOwner.WaitAsync();
var scopedAllowed = await gateway.InvokeAsync("gate/ping", Json("{}"), scoped);
Check(scopedAllowed.Ok && scopedAllowed.Value?.GetInt32() == 19, "fiber-owned-policy-allows-call");
await policyOwner.DisposeAsync();
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), scoped), "no-allow", "fiber-disposal-withdraws-policy");
await accessMount.DisposeAsync();
Refused(await gateway.InvokeAsync("gate/ping", Json("{}"), guest), "no-allow", "missing-access-service-refuses-guest");
var ownerAllowed = await gateway.InvokeAsync("gate/ping", Json("{}"), owner);
Check(ownerAllowed.Ok && ownerAllowed.Value?.GetInt32() == 19, "owner-bypasses-access-service");
await root.RunAsync(ctx =>
{
    accessMount = ctx.Provide("remoteAccess", access);
    held = [Bound("$events", TypertAccessResource.Global), Bound("$events/result", TypertAccessResource.Global)];
    return Task.CompletedTask;
});
await using (var events = gateway.StreamAsync("$events", Json("{}"), guest).GetAsyncEnumerator())
{
    Check(await events.MoveNextAsync(), "nonowner-event-stream-returns-refusal");
    Refused(events.Current, "non-owner-unavailable", "nonowner-event-stream-remains-closed");
    Check(!await events.MoveNextAsync(), "refused-event-stream-ends");
}
Refused(await gateway.InvokeAsync("$events/result", Json("{}"), guest), "non-owner-unavailable",
    "nonowner-event-answer-remains-closed");
await guest.DisposeAsync();
await owner.DisposeAsync();
await stranger.DisposeAsync();
await broken.DisposeAsync();
await brokenCondition.DisposeAsync();
await brokenExplanation.DisposeAsync();
await scoped.DisposeAsync();
Console.WriteLine("REMOTE_ACCESS_RESULT " + JsonSerializer.Serialize(checks, GateJson.Default.ListString));

static TypertPrincipal Principal(string kind) => new(Json("{\"kind\":\"" + kind + "\"}"));
static JsonElement Json(string text)
{
    using var source = JsonDocument.Parse(text);
    return source.RootElement.Clone();
}

sealed class BoundService(TypertRemoteBinding binding,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypertResourceReader>> readers) :
    ITypertRemoteService, ITypertRemoteResourceSource
{
    public TypertRemoteBinding TypertRemote { get; } = binding;
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypertResourceReader>> ResourceReaders { get; } = readers;
}

sealed class AccessInput
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
    [JsonPropertyName("roomIds")]
    public string[]? RoomIds { get; set; }
}

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(AccessInput))]
[JsonSerializable(typeof(List<string>))]
internal partial class GateJson : JsonSerializerContext;
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    packages, output = options.packages.resolve(), options.output.resolve()
    if not packages.is_dir():
        parser.error("--packages must name an existing local package feed.")
    if output.exists() and any(output.iterdir()):
        parser.error("--output must be empty so previous evidence is preserved.")
    output.mkdir(parents=True, exist_ok=True)
    source = Path(tempfile.mkdtemp(prefix="cordis-remote-access-"))
    if source.is_relative_to(root):
        raise RuntimeError("The package consumer must be outside the repository.")
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
    dotnet = Path(shutil.which(options.dotnet) or options.dotnet).resolve()
    cache = source / "cache"
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), NUGET_PACKAGES=str(cache),
        DOTNET_CLI_UI_LANGUAGE="en")
    env["PATH"] = str(dotnet.parent) + os.pathsep + env.get("PATH", "")
    result = {"status": "failed", "packageVersion": version, "packageReferencesOnly": False,
        "independentSourceDirectory": str(source), "steps": [], "checks": []}

    def run(name, command):
        process = subprocess.run([str(part) for part in command], cwd=source, env=env, capture_output=True)
        stdout, stderr = name + ".stdout.log", name + ".stderr.log"
        (output / stdout).write_bytes(process.stdout)
        (output / stderr).write_bytes(process.stderr)
        result["steps"].append({"name": name, "exitCode": process.returncode,
            "stdout": stdout, "stderr": stderr})
        print(name, "exit", process.returncode, flush=True)
        if process.returncode != 0:
            raise RuntimeError(name + " failed; inspect the logs in " + str(output))
        return process.stdout.decode("utf-8", "replace")

    try:
        shutil.copyfile(root / "global.json", source / "global.json")
        config = ET.Element("configuration")
        feeds = ET.SubElement(config, "packageSources")
        ET.SubElement(feeds, "clear")
        for key, value in (("local", str(packages)), ("nuget.org", "https://api.nuget.org/v3/index.json")):
            ET.SubElement(feeds, "add", key=key, value=value)
        ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
        mapping = ET.SubElement(config, "packageSourceMapping")
        for key, pattern in (("local", "Cordis.NET.*"), ("nuget.org", "*")):
            ET.SubElement(ET.SubElement(mapping, "packageSource", key=key), "package", pattern=pattern)
        ET.ElementTree(config).write(source / "NuGet.Config", encoding="utf-8")
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        properties = ET.SubElement(project, "PropertyGroup")
        for name, value in (("OutputType", "Exe"), ("TargetFramework", "net10.0"),
                ("ImplicitUsings", "enable"), ("Nullable", "enable"), ("TreatWarningsAsErrors", "true"),
                ("JsonSerializerIsReflectionEnabledByDefault", "false")):
            ET.SubElement(properties, name).text = value
        ET.SubElement(ET.SubElement(project, "ItemGroup"), "PackageReference",
            Include="Cordis.NET.Composition", Version=version)
        project_path = source / "Consumer.csproj"
        ET.ElementTree(project).write(project_path, encoding="utf-8")
        (source / "Program.cs").write_text(PROGRAM, encoding="utf-8")
        generated = output / "source"
        generated.mkdir()
        for name in ("global.json", "NuGet.Config", "Consumer.csproj", "Program.cs"):
            shutil.copyfile(source / name, generated / name)
        run("restore", [dotnet, "restore", project_path, "--configfile", source / "NuGet.Config",
            "--packages", cache, "--no-cache"])
        assets = json.loads((source / "obj/project.assets.json").read_text(encoding="utf-8"))
        if {Path(folder).resolve() for folder in assets["packageFolders"]} != {cache.resolve()}:
            raise RuntimeError("Consumer used a shared or fallback package cache.")
        libraries = assets["libraries"]
        if f"Cordis.NET.Composition/{version}" not in libraries or any(
                item.get("type") == "project" or name.split("/", 1)[1] != version
                for name, item in libraries.items() if name.startswith("Cordis.NET.")):
            raise RuntimeError("Consumer did not resolve the selected Cordis package batch.")
        if any(item.get("type") == "project" for item in libraries.values()):
            raise RuntimeError("Consumer resolved a project reference.")
        result["packageReferencesOnly"] = True
        log = run("run", [dotnet, "run", "--project", project_path, "-c", "Release", "--no-restore"])
        reports = [line.removeprefix("REMOTE_ACCESS_RESULT ") for line in log.splitlines()
            if line.startswith("REMOTE_ACCESS_RESULT ")]
        if len(reports) != 1:
            raise RuntimeError("Consumer did not emit exactly one access result.")
        result["checks"] = json.loads(reports[0])
        expected_checks = [
            'business-receives-admitted-principal',
            'business-receives-resolved-lookup-and-absence',
            'condition-exception-prevents-later-binding-allow',
            'denied-call-has-no-provider-or-business-effects',
            'empty-explicit-resource-list-refused',
            'empty-resource-identity-refused',
            'every-resource-is-required',
            'explanation-exception-fails-closed',
            'fiber-disposal-withdraws-policy',
            'fiber-owned-policy-allows-call',
            'granted-call-reaches-providers-and-business',
            'invalidated-policy-still-fails-closed',
            'invalidation-reenables-policy-diagnostic',
            'invocation-scope-restored',
            'list-checks-read-dynamic-bindings-without-arguments',
            'list-checks-use-current-invocation-and-do-not-explain',
            'list-policy-failures-filter-items',
            'missing-access-service-refuses-guest',
            'missing-principal-policy-refused',
            'nonowner-event-answer-remains-closed',
            'nonowner-event-stream-remains-closed',
            'nonowner-event-stream-returns-refusal',
            'null-resource-reader-falls-back-to-global',
            'one-bindings-read-per-decision',
            'owner-bypasses-access-service',
            'policy-exception-refuses-call',
            'policy-exceptions-never-reach-business',
            'policy-failure-diagnosed-once',
            'policy-preserves-argument-absence',
            'policy-reader-business-share-decoded-instance',
            'policy-sees-decoded-ordinary-value',
            'policy-sees-wire-provider-identities',
            'refused-event-stream-ends',
            'repeated-policy-exception-refuses-call',
            'resource-intersection-refusal-precedes-providers',
            'resource-reader-exception-contained',
            'ungranted-call-refused',
        ]
        if sorted(result["checks"]) != expected_checks:
            raise RuntimeError("Consumer did not finish every access check exactly once.")
        result["status"] = "passed"
    except Exception as error:
        result["error"] = str(error)
        raise
    finally:
        (output / "results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
