"""Exercise a model-only .NET caller against the real Settings describe provider, without Node."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import tempfile
import threading
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--aot", action="store_true")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = Path(tempfile.mkdtemp(prefix="cordis-settings-dotnet-"))
    assert not source.is_relative_to(root)
    shutil.copyfile(root / "global.json", source / "global.json")
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
    feed = source / "author-feed"
    feed.mkdir()
    config = ET.Element("configuration")
    feeds = ET.SubElement(config, "packageSources")
    ET.SubElement(feeds, "clear")
    for name, path in (("validated", options.packages.resolve()), ("author", feed), ("nuget.org", "https://api.nuget.org/v3/index.json")):
        ET.SubElement(feeds, "add", key=name, value=str(path))
    ET.ElementTree(config).write(source / "NuGet.Config", encoding="utf-8")
    dotnet = Path(shutil.which(options.dotnet) or options.dotnet).resolve()
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), NUGET_PACKAGES=str(source / "cache"), DOTNET_CLI_UI_LANGUAGE="en")
    env["PATH"] = str(dotnet.parent) + os.pathsep + os.pathsep.join(
        item for item in env.get("PATH", "").split(os.pathsep) if "node" not in item.lower() and "nvm" not in item.lower())
    steps = []
    status = "failed"
    provider_referenced = None

    def run(name, command, expected_failure=False):
        assert Path(str(command[0])).name.lower() not in {"node", "node.exe", "npm", "npx"}
        process = subprocess.run([str(item) for item in command], cwd=source, env=env, capture_output=True)
        (output / (name + ".log")).write_bytes(process.stdout + process.stderr)
        steps.append({"name": name, "exitCode": process.returncode, "expectedFailure": expected_failure})
        print(name, "exit", process.returncode, flush=True)
        if (process.returncode != 0) != expected_failure:
            print((process.stdout + process.stderr).decode("utf-8", "replace")[-10000:])
            raise RuntimeError(name + " failed")
        return process.stdout + process.stderr

    def project(name, properties, references, extra=""):
        folder = source / name
        folder.mkdir()
        path = folder / (name + ".csproj")
        path.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
            '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
            '<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>' + properties +
            '</PropertyGroup><ItemGroup>' + references + '</ItemGroup>' + extra + '</Project>', encoding="utf-8")
        return folder, path

    try:
        composition = f'<PackageReference Include="Cordis.NET.Composition" Version="{version}"/>'
        clr = f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>'
        author, author_project = project("Author",
            '<AssemblyName>IndependentSettings.Provider</AssemblyName><PackageId>IndependentSettings.Provider</PackageId>'
            '<Version>1.0.0-alpha</Version><CordisTypertService>settingsController</CordisTypertService>', clr,
            '<ItemGroup><None Include="cordis.plugin.json" Pack="true" PackagePath="/" CopyToOutputDirectory="PreserveNewest"/></ItemGroup>')
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/Author.cs", author / "Author.cs")
        entries = [{"id": "one", "name": "nuget:independentsettings.provider", "config": {}},
                   {"id": "two", "name": "nuget:independentsettings.provider/second", "config": {}}]
        metadata = {"assembly": "IndependentSettings.Provider.dll", "entryType": "IndependentSettings.First",
                    "exports": {"./second": "IndependentSettings.Second"}, "patches": [{"insert": entries}]}
        (author / "cordis.plugin.json").write_text(json.dumps(metadata), encoding="utf-8")
        run("author-pack", [dotnet, "pack", author_project, "-c", "Release", "-o", feed])
        model = author / "obj/Release/net10.0/cordis-typert/settingsController.cordis.typert.json"
        authored = json.loads(model.read_text(encoding="utf-8"))
        assert authored["formatVersion"] == 1 and authored["services"][0]["sourceType"] == "IndependentSettings.SettingsRemote"
        assert not authored["diagnostics"], authored["diagnostics"]
        assert authored["compilation"]["assemblyName"] == "IndependentSettings.Provider"
        assert authored["compilation"]["nullable"] == "Enable"
        assert {"RELEASE", "NET10_0"}.issubset(authored["compilation"]["defines"])
        (output / "authored-model.json").write_bytes(model.read_bytes())
        with zipfile.ZipFile(feed / "IndependentSettings.Provider.1.0.0-alpha.nupkg") as archive:
            assert archive.read("cordis/typert/settingsController.cordis.typert.json") == model.read_bytes()
        run("author-incremental", [dotnet, "build", author_project, "-c", "Release"])
        assert json.loads(model.read_text(encoding="utf-8"))["contractIdentity"] == authored["contractIdentity"]

        # A valid old artifact must fail on field-level drift, rather than merely a bad checksum.
        drift, drift_project = project("Drift", '<AssemblyName>IndependentSettings.Provider</AssemblyName>'
            '<CordisTypertService>settingsController</CordisTypertService>', clr,
            '<ItemGroup><AdditionalFiles Include="baseline.cordis.typert.json"/></ItemGroup>')
        (drift / "Author.cs").write_text((author / "Author.cs").read_text(encoding="utf-8").replace(
            '[RemoteMethod("describe")]', '[RemoteMethod("changedDescribe")]'), encoding="utf-8")
        shutil.copyfile(model, drift / "baseline.cordis.typert.json")
        rejected = run("valid-stale-model-rejected", [dotnet, "build", drift_project, "-c", "Release"], True)
        assert b"CORDISREMOTE002" in rejected and b"Published source model drift" in rejected, rejected[-5000:]

        contracts, contracts_project = project("Contracts", '<PackageId>IndependentSettings.Contracts</PackageId><Version>1.0.0-alpha</Version>'
            '<CordisTypertService>settingsController</CordisTypertService>'
            '<CordisTypertClientModel>settingsController.cordis.typert.json</CordisTypertClientModel>'
            '<CordisTypertClientNamespace>IndependentSettings.Client</CordisTypertClientNamespace>'
            '<CordisTypertClientName>SettingsClient</CordisTypertClientName>', composition,
            '<ItemGroup><None Include="settingsController.cordis.typert.json" Pack="true" PackagePath="cordis/typert/"/></ItemGroup>')
        shutil.copyfile(model, contracts / "settingsController.cordis.typert.json")
        run("model-only-contracts-pack", [dotnet, "pack", contracts_project, "-c", "Release", "-o", feed])
        generated = contracts / "obj/Release/net10.0/cordis-typert/SettingsClient.Generated.g.cs"
        generated_hash = hashlib.sha256(generated.read_bytes()).hexdigest()
        generated_mtime = generated.stat().st_mtime_ns
        run("contracts-incremental", [dotnet, "build", contracts_project, "-c", "Release"])
        assert generated.stat().st_mtime_ns == generated_mtime
        assert hashlib.sha256(generated.read_bytes()).hexdigest() == generated_hash
        run("contracts-clean", [dotnet, "clean", contracts_project, "-c", "Release"])
        assert not generated.exists(), "Owned generated source must participate in Clean"
        run("contracts-cold-rebuild", [dotnet, "build", contracts_project, "-c", "Release"])
        assert hashlib.sha256(generated.read_bytes()).hexdigest() == generated_hash

        # Projection regressions exercise real SDK analysis and shipped tools independently of Settings.
        projection_author, projection_project = project("ProjectionAuthor", '<CordisTypertService>primitives</CordisTypertService>', composition)
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/ProjectionAuthor.cs", projection_author / "Author.cs")
        run("projection-author-build", [dotnet, "build", projection_project, "-c", "Release"])
        sdk = run("selected-sdk", [dotnet, "--version"]).decode("utf-8").strip()
        compiler = source / "cache/cordis.net.composition" / version / "tools/net10.0/typert/Cordis.Typert.Compiler.dll"
        roslyn = dotnet.parent / "sdk" / sdk / "Roslyn/bincore"
        projection, projection_caller = project("ProjectionCaller", '<OutputType>Exe</OutputType>', composition)
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/ProjectionCaller.cs", projection / "Program.cs")
        response = projection_author / "obj/Release/net10.0/cordis-typert"

        def extract_projection(service, destination, source_response=None, defines=""):
            # Retain analysis facts for a shape rejected by the ordinary runtime generator.
            if service == "optional":
                defines = "CORDIS_MODEL_OPTIONAL"
            run(service + "-source-extract", [dotnet, compiler, "--roslyn-directory", roslyn, "extract",
                "--project-directory", projection_author, "--sources", source_response or response / "sources.rsp", "--references", response / "references.rsp",
                "--assembly-name", "ProjectionAuthor", "--language-version", "14.0", "--nullable", "enable", "--defines", defines,
                "--service", service, "--output", destination])

        if os.name != "nt":
            # Linux source identity is case sensitive; both partial declarations must survive.
            first_part, second_part = projection_author / "CaseParts.cs", projection_author / "caseparts.cs"
            first_part.write_text('using Cordis.Composition; using System.Text.Json.Serialization; '
                '[RemoteService("caseParts", typeof(CaseJson))] public sealed partial class CaseParts {'
                '[RemoteMethod("first")] public Task<int> First() => Task.FromResult(1); }'
                '[JsonSerializable(typeof(int))] public partial class CaseJson : JsonSerializerContext;', encoding="utf-8")
            second_part.write_text('public sealed partial class CaseParts {\n#if CORDIS_CASE\n'
                '[Cordis.Composition.RemoteMethod("selected")] public Task<int> Selected() => Task.FromResult(2);\n'
                '#else\n[Cordis.Composition.RemoteMethod("wrongBranch")] public Task<int> WrongBranch() => Task.FromResult(3);\n#endif\n}', encoding="utf-8")
            case_response = response / "case-sensitive-sources.rsp"
            case_response.write_text((response / "sources.rsp").read_text(encoding="utf-8-sig") +
                "\n" + str(first_part) + "\n" + str(second_part) + "\n", encoding="utf-8")
            case_model = projection_author / "case-parts.model.json"
            extract_projection("caseParts", case_model, case_response, "CORDIS_CASE")
            case_facts = json.loads(case_model.read_text(encoding="utf-8"))
            assert {method["name"] for method in case_facts["services"][0]["methods"]} == {"first", "selected"}
            assert case_facts["compilation"]["defines"] == ["CORDIS_CASE"]

        for service in ("primitives", "nullable", "readonly", "collision", "recordStruct", "optional"):
            # A source-root output must not filter out the explicitly supplied inputs.
            artifact = projection_author / (service + ".model.json")
            extract_projection(service, artifact)
            target = projection / (service + ".Generated.g.cs")
            refusal = service in {"readonly", "collision", "recordStruct", "optional"}
            if refusal:
                target.write_text("stale generated output", encoding="utf-8")
            emitted = run(service + "-model-project", [dotnet, compiler, "--roslyn-directory", roslyn, "emit-dotnet",
                "--model", artifact, "--namespace", "Caller.Edges", "--service", service, "--client", service.title() + "Client",
                "--output", target], refusal)
            if refusal:
                assert not target.exists(), "Refused projection retained a stale source"
                if service == "readonly":
                    retained = json.loads(artifact.read_text(encoding="utf-8"))
                    assert any(item["code"] == "TYPM112" for item in retained["diagnostics"])
                    member = next(item for item in retained["types"] if item["name"] == "ReadonlyValue")["members"][0]
                    assert member["writeAccess"] == "none" and member["getterConstant"]["json"] == '"live"'
                elif service == "collision":
                    assert b"collide" in emitted
                elif service == "optional":
                    facts = json.loads(artifact.read_text(encoding="utf-8"))
                    argument = facts["services"][0]["methods"][0]["parameters"][0]
                    assert argument["optional"] and argument["default"]["json"] == "7"
        optional_before = json.loads((projection_author / "optional.model.json").read_text(encoding="utf-8"))
        optional_source = (projection_author / "Author.cs").read_text(encoding="utf-8")
        (projection_author / "Author.cs").write_text(optional_source.replace("int limit = 7", "int limit = 8"), encoding="utf-8")
        extract_projection("optional", projection_author / "optional-altered.model.json")
        assert json.loads((projection_author / "optional-altered.model.json").read_text(encoding="utf-8"))["contractIdentity"] != optional_before["contractIdentity"]
        (projection_author / "Author.cs").write_text(optional_source, encoding="utf-8")
        nullable_before = json.loads((projection_author / "nullable.model.json").read_text(encoding="utf-8"))
        original_projection = (projection_author / "Author.cs").read_text(encoding="utf-8")
        (projection_author / "Author.cs").write_text(original_projection.replace("[DisallowNull, JsonPropertyName", "[JsonPropertyName"), encoding="utf-8")
        altered = projection_author / "nullable-altered.model.json"
        extract_projection("nullable", altered)
        assert json.loads(altered.read_text(encoding="utf-8"))["contractIdentity"] != nullable_before["contractIdentity"]
        (projection_author / "Author.cs").write_text(original_projection, encoding="utf-8")
        run("projection-caller-jit", [dotnet, "run", "--project", projection_caller, "-c", "Release"])
        if options.aot:
            run("projection-caller-aot-publish", [dotnet, "publish", projection_caller, "-c", "Release", "-p:PublishAot=true", "-o", source / "projection-native"])
            run("projection-caller-aot", [source / "projection-native" / ("ProjectionCaller.exe" if os.name == "nt" else "ProjectionCaller")])

        deployment, deployment_project = project("Deployment", "", '<PackageReference Include="IndependentSettings.Provider" Version="1.0.0-alpha"/>')
        bundle = source / "bundle"
        run("author-package-publish", [dotnet, "publish", deployment_project, "-c", "Release", "-o", bundle])
        shutil.copyfile(author / "cordis.plugin.json", bundle / "cordis.plugin.json")
        (bundle / "package.json").write_text(json.dumps({"name": "IndependentSettings.Provider", "version": "1.0.0-alpha",
            "dsh": {"bundle": {"patch": "cordis.patch.yml"}}}), encoding="utf-8")
        (bundle / "cordis.patch.yml").write_text(json.dumps([{"insert": entries}]), encoding="utf-8")
        host, host_project = project("Host", '<OutputType>Exe</OutputType>', clr +
            f'<PackageReference Include="Cordis.NET.AspNetCore" Version="{version}"/><FrameworkReference Include="Microsoft.AspNetCore.App"/>')
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/Host.cs", host / "Program.cs")
        run("host-build", [dotnet, "build", host_project, "-c", "Release"])
        caller, caller_project = project("Caller", '<OutputType>Exe</OutputType>',
            '<PackageReference Include="IndependentSettings.Contracts" Version="1.0.0-alpha"/>')
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/Client.cs", caller / "Program.cs")
        run("caller-build", [dotnet, "build", caller_project, "-c", "Release"])
        assets = json.loads((caller / "obj/project.assets.json").read_text(encoding="utf-8"))
        assert not any(name.startswith("IndependentSettings.Provider/") for name in assets["libraries"])
        assert not list((caller / "bin").rglob("IndependentSettings.Provider.dll"))
        provider_referenced = False
        for project_file in source.glob("*/*.csproj"):
            assert "ProjectReference" not in project_file.read_text(encoding="utf-8")

        if options.aot:
            run("caller-aot-publish", [dotnet, "publish", caller_project, "-c", "Release", "-p:PublishAot=true", "-o", source / "native"])
        for mode in ("jit", "aot") if options.aot else ("jit",):
            ready = queue.Queue()
            process = subprocess.Popen([str(dotnet), str(host / "bin/Release/net10.0/Host.dll"), str(source / ("profile-" + mode)), str(bundle)],
                cwd=source, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
            def wait_ready():
                for line in process.stdout:
                    if line.startswith("READY "):
                        ready.put(line.strip()[6:])
                        return
                ready.put(None)
            threading.Thread(target=wait_ready, daemon=True).start()
            try:
                address = ready.get(timeout=60)
                assert address and address.startswith("http://127.0.0.1:"), address
                command = [dotnet, caller / "bin/Release/net10.0/Caller.dll", address] if mode == "jit" else [source / "native" / ("Caller.exe" if os.name == "nt" else "Caller"), address]
                run("typed-settings-" + mode, command)
            finally:
                process.kill()
                process.wait(timeout=30)
                (output / ("host-" + mode + ".log")).write_text(process.stdout.read() + process.stderr.read(), encoding="utf-8")
        status = "requested-checks-passed"
    finally:
        (output / "results.json").write_text(json.dumps({"status": status, "steps": steps, "independentSourceDirectory": str(source),
            "providerImplementationReferencedByCaller": provider_referenced, "packageReferencesOnly": True,
            "nodeCommands": [], "aotRequested": options.aot}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
