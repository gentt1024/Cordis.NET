"""Exercise Remote identity and resource APIs through an independent NuGet consumer."""
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
    if (!condition)
        throw new InvalidOperationException(name);
    checks.Add(name);
}

TypertPrincipal guestPrincipal;
using (var identity = JsonDocument.Parse("""{"kind":"guest","claims":{"id":"guest-1"}}"""))
    guestPrincipal = new(identity.RootElement);
Check(guestPrincipal.Kind == "guest" &&
    guestPrincipal.Identity.GetProperty("claims").GetProperty("id").GetString() == "guest-1",
    "principal-survives-source-disposal");
Check(TypertPrincipal.PrincipalOf(null) is null, "principal-outside-call");

await using var root = new Context();
var registry = new TypertRegistry(root);
var gateway = new TypertGateway(root, registry);
TypertPeer owner = null!;
TypertPeer guest = null!;
EffectHandle lookupPolicy = null!;
EffectHandle hostPolicy = null!;
var invocations = 0;
var changes = new List<string>();
await root.RunAsync(async ctx =>
{
    owner = new(ctx, new(TypertPrincipal.Owner));
    guest = new(ctx, new(guestPrincipal, TypertPeerInteractions.None));
    Check(TypertPrincipal.PrincipalOf(new(ctx, "identity/read", default) { Peer = owner }) is null,
        "owner-principal-is-null");
    Check(ReferenceEquals(guestPrincipal,
        TypertPrincipal.PrincipalOf(new(ctx, "identity/read", default) { Peer = guest })),
        "nonowner-principal-retained");
    registry.Subscribe(ctx, change =>
    {
        if (change.Kind == "resource")
            changes.Add(change.Key);
    });
    registry.RegisterResource(owner.Context, "document", new("documentId"));
    Check(registry.GetResource("document")?.Field == "documentId" &&
        registry.ListResources().SequenceEqual([new TypertResourceEntry("document", "documentId")]),
        "resource-registration");
    lookupPolicy = registry.ConfigureLookup(ctx, "document",
        _ => ValueTask.FromResult<object?>("configured"));
    registry.RegisterLookup(ctx, "document", new("document", "documentId", "Independent#Document", "System.String",
        _ => ValueTask.FromResult<object?>("default")) { Resource = "document" });
    registry.RegisterHostContext(ctx, "document", new("documentId", "System.String",
        _ => ValueTask.FromResult<Context?>(null)) { Resource = "document" });
    hostPolicy = registry.ConfigureHostContext(ctx, "document", _ => ValueTask.FromResult<Context?>(root));
    Check(registry.GetLookup("document")?.Resource == "document" &&
        Equals(await registry.GetLookup("document")!.Resolve("one"), "configured"),
        "configured-lookup-resource");
    Check(registry.GetHostContext("document")?.Resource == "document" &&
        ReferenceEquals(await registry.GetHostContext("document")!.Resolve("one"), root),
        "configured-host-resource");
    var binding = new TypertRemoteBinding("identity-service", "identity",
        new Dictionary<string, TypertUnaryInvoker>
        {
            ["read"] = (_, _, _) =>
            {
                invocations++;
                var invocation = TypertInvocation.Current;
                Check(invocation is not null && ReferenceEquals(invocation.Peer, owner) &&
                    TypertPrincipal.PrincipalOf(invocation) is null, "business-owner-identity");
                return Task.FromResult<object?>(17);
            }
        }, new Dictionary<string, TypertStreamInvoker>());
    ctx.Provide("identity-service", new BoundService(binding));
    registry.Register(ctx, new("independent-identity",
        [new("independent-identity#identity/read", "identity-service", "identity", "read", [],
            TypertCodec.Create(GateJson.Default.Int32))]));
});

var denied = await gateway.InvokeAsync("identity/read", Json("""{"principal":{"kind":"owner"}}"""), guest);
Check(!denied.Ok && denied.Error?.Code == "remote-access/denied" && invocations == 0,
    "guest-denied-before-business");
var allowed = await gateway.InvokeAsync("identity/read", Json("{}"), owner);
Check(allowed.Ok && allowed.Value?.GetInt32() == 17 && invocations == 1, "owner-business-result");
Check(TypertInvocation.Current is null, "invocation-scope-restored");
await lookupPolicy.DisposeAsync();
await hostPolicy.DisposeAsync();
await root.RunAsync(async _ =>
{
    Check(registry.GetLookup("document")?.Resource == "document" &&
        Equals(await registry.GetLookup("document")!.Resolve("one"), "default"),
        "withdrawn-lookup-policy-resource");
    Check(registry.GetHostContext("document")?.Resource == "document" &&
        await registry.GetHostContext("document")!.Resolve("one") is null,
        "withdrawn-host-policy-resource");
});
await owner.DisposeAsync();
var closed = await gateway.InvokeAsync("identity/read", Json("{}"), owner);
Check(!closed.Ok && closed.Error?.Code == "gateway/peer-disposed" && invocations == 1,
    "disposed-peer-denied-before-business");
await root.RunAsync(_ =>
{
    Check(registry.GetResource("document") is null && registry.ListResources().Count == 0 &&
        changes.SequenceEqual(["document", "document"]), "peer-owned-resource-withdrawal");
    return Task.CompletedTask;
});
await guest.DisposeAsync();
Console.WriteLine("REMOTE_IDENTITY_RESULT " + JsonSerializer.Serialize(checks, GateJson.Default.ListString));

static JsonElement Json(string text)
{
    using var document = JsonDocument.Parse(text);
    return document.RootElement.Clone();
}

sealed class BoundService(TypertRemoteBinding binding) : ITypertRemoteService
{
    public TypertRemoteBinding TypertRemote { get; } = binding;
}

[JsonSerializable(typeof(int))]
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
    source = Path(tempfile.mkdtemp(prefix="cordis-remote-identity-"))
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
        reports = [line.removeprefix("REMOTE_IDENTITY_RESULT ") for line in log.splitlines()
            if line.startswith("REMOTE_IDENTITY_RESULT ")]
        if len(reports) != 1:
            raise RuntimeError("Consumer did not emit exactly one identity result.")
        result["checks"] = json.loads(reports[0])
        if len(result["checks"]) != 15 or len(set(result["checks"])) != 15:
            raise RuntimeError("Consumer did not finish every identity check exactly once.")
        result["status"] = "passed"
    except Exception as error:
        result["error"] = str(error)
        raise
    finally:
        (output / "results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
