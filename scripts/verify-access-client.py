"""Exercise native access snapshots and permission state through an independent NuGet consumer."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET


PROGRAM = r'''using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cordis;
using Cordis.Composition;
using Cordis.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition || checks.Contains(name)) throw new InvalidOperationException(name);
    checks.Add(name);
}
TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
var limit = TimeSpan.FromSeconds(10);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
await using var host = new Context();
await using var clientOwner = new Context();
var registry = new TypertRegistry(host);
var gateway = new TypertGateway(host, registry);
var room = TypertAccessResource.Create("room", "one");
using var identity = JsonDocument.Parse("{\"kind\":\"guest\",\"id\":\"a\"}");
var principal = new TypertPrincipal(identity.RootElement);
TypertRemoteAccess access = null!;
Fiber mounted = null!;
TypertHostConnection hostConnection = null!;
IReadOnlyList<TypertAccessBinding> held = [new(new("reader", "room", ["notes/read", "event:changed"]), room)];
async Task MountAsync()
{
    await host.RunAsync(ctx =>
    {
        mounted = ctx.Plugin(new Plugin<object?> { Apply = (child, _) =>
        {
            access = new(child);
            child.Provide("remoteAccess", access);
            access.RegisterRemote(child, registry);
            access.RegisterPolicy(child, "guest", new(_ => held));
        }});
        return Task.CompletedTask;
    });
    await mounted.WaitAsync();
}
await MountAsync();
await host.RunAsync(ctx =>
{
    hostConnection = new(ctx, gateway, new(principal));
    return Task.CompletedTask;
});
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
app.UseWebSockets();
app.MapCordisRemote("/real", hostConnection, (_, _) => Task.FromResult(true));
var generation = 0;
var release = Signal();
app.Map("/script/remote.mux", async http =>
{
    using var socket = await http.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[8192];
    while (socket.State == WebSocketState.Open)
    {
    using var message = new MemoryStream();
    ValueWebSocketReceiveResult received;
    do
    {
        received = await socket.ReceiveAsync(buffer.AsMemory(), http.RequestAborted);
        if (received.MessageType == WebSocketMessageType.Close) return;
        message.Write(buffer, 0, received.Count);
    } while (!received.EndOfMessage);
    using var opened = JsonDocument.Parse(message.ToArray());
    if (opened.RootElement.GetProperty("type").GetString() != "open") continue;
    var id = opened.RootElement.GetProperty("streamId").GetString()!;
    var current = Interlocked.Increment(ref generation);
    async Task Send(string value) => await socket.SendAsync(
        Encoding.UTF8.GetBytes("{\"type\":\"item\",\"streamId\":\"" + id + "\",\"value\":" + value + "}"),
        WebSocketMessageType.Text, true, http.RequestAborted);
    const string granted = "{\"type\":\"baseline\",\"owner\":false,\"grants\":[{\"resource\":{\"kind\":\"room\",\"id\":\"one\"},\"actions\":[\"notes/read\"]}]}";
    if (current == 1)
    {
        await Send(granted);
        await release.Task.WaitAsync(http.RequestAborted);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "retry", http.RequestAborted);
        return;
    }
    else if (current == 2)
        await Send("{\"type\":\"remove\",\"resource\":{\"kind\":\"room\",\"id\":\"one\"}}");
    else if (current == 3)
    {
        await Send(granted);
        await Send(granted);
    }
    else
    {
        await Send("{\"type\":\"baseline\",\"owner\":true}");
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"error\",\"streamId\":\"" + id + "\",\"error\":{\"code\":\"sample/closed\",\"message\":\"terminal\",\"details\":{}}}"),
            WebSocketMessageType.Text, true, http.RequestAborted);
    }
    }
});
await app.StartAsync(timeout.Token);
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri(app.Urls.Single() + "/real"));
await using var client = new TypertRemoteAccessClient(clientOwner, connection);
var source = client.State.Resource("room", "one", "notes/read");
Check(ReferenceEquals(source, client.State.Resource("room", "one", "notes/read")) && !source.Value, "stable-initially-closed-source");
var changed = Signal();
using var subscription = source.Subscribe(() => changed.TrySetResult());
var running = client.RunAsync(timeout.Token);
await changed.Task.WaitAsync(limit);
Check(source.Value && !client.State.Resource("room", "one", "event:changed").Value, "registered-guest-follow-baseline");
changed = Signal();
await host.RunAsync(_ => { held = []; access.Invalidate(principal); return Task.CompletedTask; });
await changed.Task.WaitAsync(limit);
Check(!source.Value, "targeted-revocation-reaches-native-client");
changed = Signal();
await host.RunAsync(_ => { held = [new(new("reader", "room", ["notes/read"]), room)]; access.Invalidate(); return Task.CompletedTask; });
await changed.Task.WaitAsync(limit);
Check(source.Value, "grant-upsert-reaches-native-client");
await mounted.DisposeAsync();
try { await running.WaitAsync(limit); throw new Exception("Expected definition withdrawal."); }
catch (RemoteError error) when (error.Code == "gateway/definition-unavailable") { }
Check(!source.Value, "service-withdrawal-closes-stale-grants");
held = [];
await MountAsync();
changed = Signal();
running = client.RunAsync(timeout.Token);
await changed.Task.WaitAsync(limit);
Check(!source.Value, "service-remount-replaces-stale-grants");
await client.DisposeAsync();
await running.WaitAsync(limit);
await using var newMount = new TypertRemoteAccessClient(clientOwner, connection);
Check(!newMount.State.Resource("room", "one", "notes/read").Value, "client-remount-has-no-stale-grants");
await using var scriptedConnection = new TypertConnection(http, new Uri(app.Urls.Single() + "/script"));
await using var scripted = new TypertRemoteAccessClient(clientOwner, scriptedConnection);
var scriptedSource = scripted.State.Resource("room", "one", "notes/read");
var scriptReady = Signal();
using var scriptSubscription = scriptedSource.Subscribe(() => scriptReady.TrySetResult());
var first = scripted.RunAsync(timeout.Token);
await scriptReady.Task.WaitAsync(limit);
release.TrySetResult();
try { await first.WaitAsync(limit); throw new Exception("Expected carrier loss."); }
catch (RemoteStreamCarrierException) { }
Check(scriptedSource.Value, "carrier-loss-retains-snapshot");
try { await scripted.RunAsync(timeout.Token).WaitAsync(limit); throw new Exception("Expected baseline fence."); }
catch (JsonException) { }
Check(!scriptedSource.Value, "new-generation-rejects-delta-before-baseline");
try { await scripted.RunAsync(timeout.Token).WaitAsync(limit); throw new Exception("Expected duplicate fence."); }
catch (JsonException) { }
Check(!scriptedSource.Value, "duplicate-baseline-fails-closed");
try { await scripted.RunAsync(timeout.Token).WaitAsync(limit); throw new Exception("Expected terminal business error."); }
catch (RemoteError error) when (error.Code == "sample/closed") { }
Check(scriptedSource.Value && scripted.State.Owner, "terminal-failure-preserves-owner");
await scripted.DisposeAsync();
Check(scriptedSource.Value, "disposed-client-remains-quiescent");
await mounted.DisposeAsync();
Console.WriteLine("ACCESS_CLIENT_RESULT " + JsonSerializer.Serialize(checks, ConsumerJson.Default.ListString));

[System.Text.Json.Serialization.JsonSerializable(typeof(List<string>))]
internal partial class ConsumerJson : System.Text.Json.Serialization.JsonSerializerContext;
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
    source = Path(tempfile.mkdtemp(prefix="cordis-access-client-"))
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
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk.Web")
        properties = ET.SubElement(project, "PropertyGroup")
        for name, value in (("OutputType", "Exe"), ("TargetFramework", "net10.0"),
                ("ImplicitUsings", "enable"), ("Nullable", "enable"), ("TreatWarningsAsErrors", "true"),
                ("JsonSerializerIsReflectionEnabledByDefault", "false")):
            ET.SubElement(properties, name).text = value
        ET.SubElement(ET.SubElement(project, "ItemGroup"), "PackageReference",
            Include="Cordis.NET.AspNetCore", Version=version)
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
        reports = [line.removeprefix("ACCESS_CLIENT_RESULT ") for line in log.splitlines()
            if line.startswith("ACCESS_CLIENT_RESULT ")]
        if len(reports) != 1:
            raise RuntimeError("Consumer did not emit exactly one access result.")
        result["checks"] = json.loads(reports[0])
        expected_checks = ['carrier-loss-retains-snapshot', 'client-remount-has-no-stale-grants', 'disposed-client-remains-quiescent', 'duplicate-baseline-fails-closed', 'grant-upsert-reaches-native-client', 'new-generation-rejects-delta-before-baseline', 'registered-guest-follow-baseline', 'service-remount-replaces-stale-grants', 'service-withdrawal-closes-stale-grants', 'stable-initially-closed-source', 'targeted-revocation-reaches-native-client', 'terminal-failure-preserves-owner']
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
