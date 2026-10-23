"""Exercise ordinary declaration catalogs and a model-only Settings caller using shipped .NET packages."""
import argparse
from contextlib import contextmanager
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

from typert_declaration_consumer import verify_declaration_consumer, verify_referenced_data_consumer, verify_data_consumer, pack_external_data, verify_web_contracts
from typert_management_consumer import verify_management_consumer


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--package-version", help="Use the unique local candidate package identity instead of the repository version.")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--aot", action="store_true")
    selectors = parser.add_mutually_exclusive_group()
    selectors.add_argument("--declarations-only", action="store_true",
        help="Run only the ordinary declaration package and catalog checks.")
    selectors.add_argument("--management-only", action="store_true",
        help="Run only standard generated management and notification package consumers.")
    selectors.add_argument("--data-references-only", action="store_true",
        help="Run only ordinary DTO projections composed from explicit referenced models.")
    selectors.add_argument("--web-contracts-only", action="store_true",
        help="Run only directional Web contracts and the runtime numeric metadata consumer.")
    selectors.add_argument("--saved-remote-only", action="store_true",
        help="Run only the saved declaration model and real unary Remote package consumer.")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = options.output.resolve()
    if not options.saved_remote_only:
        dependencies = root / "clients/modules/node_modules"
        if shutil.which("node") is None or not all((dependencies / name / "package.json").is_file()
                for name in ("typescript", "esbuild")):
            parser.error("This consumer check needs Node and the locked client tools; run npm ci --prefix clients/modules first.")
    if output.exists() and any(output.iterdir()):
        parser.error("Output must be empty. Preserve the previous evidence and select a new --output directory.")
    output.mkdir(parents=True, exist_ok=True)
    source = Path(tempfile.mkdtemp(prefix="cordis-settings-dotnet-"))
    assert not source.is_relative_to(root)
    shutil.copyfile(root / "global.json", source / "global.json")
    version = options.package_version or ET.parse(root / "Directory.Build.props").findtext(".//Version")
    feed = source / "author-feed"
    feed.mkdir()
    config = ET.Element("configuration")
    feeds = ET.SubElement(config, "packageSources")
    ET.SubElement(feeds, "clear")
    for name, path in (("validated", options.packages.resolve()), ("author", feed), ("nuget.org", "https://api.nuget.org/v3/index.json")):
        ET.SubElement(feeds, "add", key=name, value=str(path))
    ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
    mapping = ET.SubElement(config, "packageSourceMapping")
    for name, pattern in (("validated", "Cordis.NET.*"), ("author", "Independent*"), ("nuget.org", "*")):
        ET.SubElement(ET.SubElement(mapping, "packageSource", key=name), "package", pattern=pattern)
    ET.ElementTree(config).write(source / "NuGet.Config", encoding="utf-8")
    dotnet = Path(shutil.which(options.dotnet) or options.dotnet).resolve()
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), NUGET_PACKAGES=str(source / "cache"), DOTNET_CLI_UI_LANGUAGE="en")
    env["PATH"] = str(dotnet.parent) + os.pathsep + os.pathsep.join(
        item for item in env.get("PATH", "").split(os.pathsep) if "node" not in item.lower() and "nvm" not in item.lower())
    steps = []
    status = "failed"
    provider_referenced = None
    management_node_commands = []

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

    @contextmanager
    def hosted(name, command, expect_stop=False):
        ready = queue.Queue()
        lines = []
        with (output / (name + ".log")).open("w", encoding="utf-8") as log:
            process = subprocess.Popen([str(item) for item in command], cwd=source, env=env,
                stdout=subprocess.PIPE, stderr=log, text=True, encoding="utf-8")
            def wait_ready():
                announced = False
                for line in process.stdout:
                    lines.append(line)
                    if not announced and line.startswith("READY "):
                        ready.put(line.strip()[6:])
                        announced = True
                if not announced:
                    ready.put(None)
            reader = threading.Thread(target=wait_ready, daemon=True)
            reader.start()
            try:
                address = ready.get(timeout=60)
                assert address and address.startswith("http://127.0.0.1:"), address
                yield address
                if expect_stop:
                    assert process.wait(timeout=30) == 0, "Host failed during shutdown."
            finally:
                if process.poll() is None:
                    process.kill()
                process.wait(timeout=30)
                reader.join(timeout=30)
                assert not reader.is_alive(), "Host output reader did not retire."
                log.write("".join(lines))

    try:
        composition = f'<PackageReference Include="Cordis.NET.Composition" Version="{version}"/>'
        clr = f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>'
        if options.web_contracts_only:
            external = pack_external_data(root / "tests/fixtures/DeclarationModel", feed, dotnet, composition, run, project)
            verify_web_contracts(root, feed, output, version, dotnet, composition, run, project, hosted, external[2])
            provider_referenced = False
            status = "requested-checks-passed"
            return
        if options.saved_remote_only:
            sdk = run("saved-remote-selected-sdk", [dotnet, "--version"]).decode("utf-8").strip()
            compiler = source / "cache/cordis.net.composition" / version / "tools/net10.0/typert/Cordis.Typert.Compiler.dll"
            roslyn = dotnet.parent / "sdk" / sdk / "Roslyn/bincore"
            verify_data_consumer(root / "tests/fixtures/DeclarationModel", source, feed, output,
                dotnet, composition, compiler, roslyn, run, project, hosted, saved_remote_only=True)
            provider_referenced = False
            status = "requested-checks-passed"
            return
        if options.data_references_only:
            verify_referenced_data_consumer(root, source, feed, output, version, dotnet, composition, run, project, hosted, aot=options.aot)
            provider_referenced = False
            status = "requested-checks-passed"
            return
        if not options.management_only:
            verify_declaration_consumer(root, source, feed, output, version, dotnet, composition, run, project, hosted, aot=options.aot)
        if options.declarations_only:
            provider_referenced = False
            status = "requested-checks-passed"
            return
        management_node_commands = verify_management_consumer(root, source, options.packages.resolve(), output,
            version, dotnet, composition, run, project, options.aot)
        if options.management_only:
            provider_referenced = False
            status = "requested-checks-passed"
            return
        author, author_project = project("Author",
            '<AssemblyName>IndependentSettings.Provider</AssemblyName><PackageId>IndependentSettings.Provider</PackageId>'
            '<Version>1.0.0-alpha</Version><CordisTypertService>settingsController</CordisTypertService>', clr,
            '<ItemGroup><CordisTypertModelRoot Include="IndependentSettings.Unrelated"/>'
            '<None Include="cordis.plugin.json" Pack="true" PackagePath="/" CopyToOutputDirectory="PreserveNewest"/></ItemGroup>')
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/Author.cs", author / "Author.cs")
        unrelated_source = 'namespace IndependentSettings; public sealed class Unrelated { public int Count { get; set; } = 7; }'
        (author / "Unrelated.cs").write_text(unrelated_source, encoding="utf-8")
        entries = [{"id": "one", "name": "nuget:independentsettings.provider", "config": {}},
                   {"id": "two", "name": "nuget:independentsettings.provider/second", "config": {}}]
        metadata = {"assembly": "IndependentSettings.Provider.dll", "entryType": "IndependentSettings.First",
                    "exports": {"./second": "IndependentSettings.Second"}, "patches": [{"insert": entries}]}
        (author / "cordis.plugin.json").write_text(json.dumps(metadata), encoding="utf-8")
        run("author-pack", [dotnet, "pack", author_project, "-c", "Release", "-o", feed])
        model = author / "obj/Release/net10.0/cordis-typert/IndependentSettings.Provider.cordis.model.json"
        authored = json.loads(model.read_text(encoding="utf-8"))
        settings_remote = next(value for value in authored["declarations"]
            if value["metadataName"] == "IndependentSettings.SettingsRemote")
        assert settings_remote["id"] in authored["roots"]
        assert {value["name"] for value in settings_remote["members"] if value["kind"] == "Ordinary"} == \
            {"DescribeAsync", "UpdateAsync", "ReplaceAsync", "MutateAsync", "OpenSettingsDocumentAsync"}
        assert authored["compilation"]["assemblyName"] == "IndependentSettings.Provider"
        assert authored["compilation"]["nullable"] == "Enable"
        assert {"RELEASE", "NET10_0"}.issubset(authored["compilation"]["defines"])
        (output / "authored-model.json").write_bytes(model.read_bytes())
        with zipfile.ZipFile(feed / "IndependentSettings.Provider.1.0.0-alpha.nupkg") as archive:
            assert archive.read("cordis/typert/" + model.name) == model.read_bytes()
        run("author-incremental", [dotnet, "build", author_project, "-c", "Release"])
        assert json.loads(model.read_text(encoding="utf-8"))["contractIdentity"] == authored["contractIdentity"]

        # Source drift must refuse a valid saved artifact, without corrupting its identity.
        drift, drift_project = project("Drift", '<AssemblyName>IndependentSettings.Provider</AssemblyName>'
            '<CordisTypertService>settingsController</CordisTypertService>', clr,
            '<ItemGroup><AdditionalFiles Include="baseline.cordis.model.json"/></ItemGroup>')
        shutil.copyfile(author / "Author.cs", drift / "Author.cs")
        (drift / "Unrelated.cs").write_text(unrelated_source.replace("= 7", "= 8"), encoding="utf-8")
        shutil.copyfile(model, drift / "baseline.cordis.model.json")
        run("unrelated-declaration-change-accepted", [dotnet, "build", drift_project, "-c", "Release"])
        original_remote = (author / "Author.cs").read_text(encoding="utf-8")
        for label, changed in (
                ("method-name", original_remote.replace('[RemoteMethod("describe")]', '[RemoteMethod("changedDescribe")]')),
                ("service-type-name", original_remote.replace("SettingsRemote", "RenamedSettingsRemote"))):
            assert changed != original_remote
            (drift / "Author.cs").write_text(changed, encoding="utf-8")
            rejected = run("valid-stale-model-" + label + "-rejected", [dotnet, "build", drift_project, "-c", "Release"], True)
            assert b"CORDISREMOTE002" in rejected and b"Published declaration model drift" in rejected, rejected[-5000:]

        contracts, contracts_project = project("Contracts", '<PackageId>IndependentSettings.Contracts</PackageId><Version>1.0.0-alpha</Version>'
            '<CordisTypertService>settingsController</CordisTypertService>'
            '<CordisTypertClientModel>' + model.name + '</CordisTypertClientModel>'
            '<CordisTypertClientNamespace>IndependentSettings.Client</CordisTypertClientNamespace>'
            '<CordisTypertClientName>SettingsClient</CordisTypertClientName>', composition,
            '<ItemGroup><None Include="' + model.name + '" Pack="true" PackagePath="cordis/typert/"/></ItemGroup>')
        shutil.copyfile(model, contracts / model.name)
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
        projection_author, projection_project = project("ProjectionAuthor",
            '<PackageId>IndependentProjection.Provider</PackageId><Version>1.0.0-alpha</Version>'
            '<CordisTypertService>numericDefaults</CordisTypertService>', composition)
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/ProjectionAuthor.cs", projection_author / "Author.cs")
        run("projection-author-pack", [dotnet, "pack", projection_project, "-c", "Release", "-o", feed])
        sdk = run("selected-sdk", [dotnet, "--version"]).decode("utf-8").strip()
        compiler = source / "cache/cordis.net.composition" / version / "tools/net10.0/typert/Cordis.Typert.Compiler.dll"
        roslyn = dotnet.parent / "sdk" / sdk / "Roslyn/bincore"
        response = projection_author / "obj/Release/net10.0/cordis-typert"
        numeric_model = response / "ProjectionAuthor.cordis.model.json"
        shutil.copyfile(numeric_model, output / "numeric-authored-model.json")
        with zipfile.ZipFile(feed / "IndependentProjection.Provider.1.0.0-alpha.nupkg") as archive:
            assert archive.read("cordis/typert/" + numeric_model.name) == numeric_model.read_bytes()
        numeric_contracts, numeric_contracts_project = project("NumericContracts",
            '<PackageId>IndependentProjection.Contracts</PackageId><Version>1.0.0-alpha</Version>'
            '<CordisTypertService>numericDefaults</CordisTypertService>'
            '<CordisTypertClientModel>' + numeric_model.name + '</CordisTypertClientModel>'
            '<CordisTypertClientNamespace>Caller.Edges</CordisTypertClientNamespace>'
            '<CordisTypertClientName>NumericDefaultsClient</CordisTypertClientName>', composition,
            '<ItemGroup><None Include="' + numeric_model.name + '" Pack="true" PackagePath="cordis/typert/"/></ItemGroup>')
        shutil.copyfile(numeric_model, numeric_contracts / numeric_model.name)
        run("numeric-model-only-contracts-pack", [dotnet, "pack", numeric_contracts_project, "-c", "Release", "-o", feed])
        shutil.copyfile(numeric_contracts / "obj/Release/net10.0/cordis-typert/NumericDefaultsClient.Generated.g.cs",
            output / "numeric-precompile-generated.g.cs")
        projection, projection_caller = project("ProjectionCaller", '<OutputType>Exe</OutputType>', composition +
            '<PackageReference Include="IndependentProjection.Contracts" Version="1.0.0-alpha"/>')
        shutil.copyfile(root / "tests/fixtures/SettingsDotnet/ProjectionCaller.cs", projection / "Program.cs")

        def extract_projection(service, destination, source_response=None, defines=""):
            # Include the optional declaration in this source-analysis control.
            if service == "optional":
                defines = "CORDIS_MODEL_OPTIONAL"
            run(service + "-source-extract", [dotnet, compiler, "--roslyn-directory", roslyn, "extract-model",
                "--project-directory", projection_author, "--sources", source_response or response / "model-sources.rsp", "--references", response / "model-references.rsp",
                "--assembly-name", "ProjectionAuthor", "--language-version", "14.0", "--nullable", "enable", "--defines", defines,
                "--service", service, "--output", destination])

        if os.name != "nt":
            # Case-sensitive source identity must retain both partial declarations.
            first_part, second_part = projection_author / "CaseParts.cs", projection_author / "caseparts.cs"
            first_part.write_text('using Cordis.Composition; using System.Text.Json.Serialization; '
                '[RemoteService("caseParts", typeof(CaseJson))] public sealed partial class CaseParts {'
                '[RemoteMethod("first")] public Task<int> First() => Task.FromResult(1); }'
                '[JsonSerializable(typeof(int))] public partial class CaseJson : JsonSerializerContext;', encoding="utf-8")
            if second_part.exists() and first_part.samefile(second_part):
                raise RuntimeError("The case-only source fixture requires a case-sensitive TMPDIR; "
                                   "refusing to overwrite CaseParts.cs through caseparts.cs")
            second_part.write_text('public sealed partial class CaseParts {\n#if CORDIS_CASE\n'
                '[Cordis.Composition.RemoteMethod("selected")] public Task<int> Selected() => Task.FromResult(2);\n'
                '#else\n[Cordis.Composition.RemoteMethod("wrongBranch")] public Task<int> WrongBranch() => Task.FromResult(3);\n#endif\n}', encoding="utf-8")
            case_response = response / "case-sensitive-sources.rsp"
            case_response.write_text((response / "model-sources.rsp").read_text(encoding="utf-8-sig") +
                "\n" + str(first_part) + "\n" + str(second_part) + "\n", encoding="utf-8")
            case_model = projection_author / "case-parts.model.json"
            extract_projection("caseParts", case_model, case_response, "CORDIS_CASE")
            case_facts = json.loads(case_model.read_text(encoding="utf-8"))
            case_type = next(value for value in case_facts["declarations"] if value["metadataName"] == "CaseParts")
            assert {method["name"] for method in case_type["members"] if method["kind"] == "Ordinary"} == {"First", "Selected"}
            assert case_facts["compilation"]["defines"] == ["CORDIS_CASE"]

        for service in ("primitives", "nullable", "readonly", "collision", "recordStruct", "optional"):
            # A source-root output must not filter out the explicitly supplied inputs.
            artifact = projection_author / (service + ".model.json")
            extract_projection(service, artifact)
            target = projection / (service + ".Generated.g.cs")
            refusal = service in {"readonly", "collision", "recordStruct"}
            if refusal:
                target.write_text("stale generated output", encoding="utf-8")
            emitted = run(service + "-model-project", [dotnet, compiler, "--roslyn-directory", roslyn, "emit-dotnet",
                "--model", artifact, "--namespace", "Caller.Edges", "--service", service, "--client", service.title() + "Client",
                "--output", target], refusal)
            if refusal:
                assert not target.exists(), "Refused projection retained a stale source"
                if service == "readonly":
                    retained = json.loads(artifact.read_text(encoding="utf-8"))
                    assert b"TYPM112" in emitted, emitted
                    readonly_type = next(item for item in retained["declarations"] if item["metadataName"] == "Author.ReadonlyValue")
                    member = next(item for item in readonly_type["members"] if item["name"] == "Applies" and item["kind"] == "property")
                    assert member["getter"] == "Public" and member["setter"] is None and member["getterConstant"]["json"] == '"live"'
                    assert not any(item["code"] == "TYPM112" for item in retained["diagnostics"]), \
                        "A client projection refusal contaminated declaration analysis"
                elif service == "collision":
                    assert b"collide" in emitted
                elif service == "recordStruct":
                    assert b"CORDISTYPERT: Caller projection currently supports only non-generic ordinary data records/classes without data inheritance:" in emitted \
                        and b"Author.ValueRecord" in emitted, emitted
            elif service == "optional":
                facts = json.loads(artifact.read_text(encoding="utf-8"))
                optional_type = next(value for value in facts["declarations"] if value["metadataName"] == "Author.OptionalService")
                argument = next(value for value in optional_type["members"] if value["name"] == "Count")["parameters"][0]
                assert argument["optional"] and argument["default"]["json"] == "7"
        readonly_host, readonly_host_project = project("ReadonlyRemoteHost",
            '<OutputType>Exe</OutputType><DefineConstants>$(DefineConstants);CORDIS_READONLY_HOST</DefineConstants>',
            f'<PackageReference Include="Cordis.NET.AspNetCore" Version="{version}"/>'
            '<PackageReference Include="IndependentProjection.Provider" Version="1.0.0-alpha"/>'
            '<FrameworkReference Include="Microsoft.AspNetCore.App"/>')
        shutil.copyfile(root / "tests/fixtures/DeclarationModel/SharedHost.cs", readonly_host / "Program.cs")
        run("readonly-host-build", [dotnet, "build", readonly_host_project, "-c", "Release"])
        readonly_caller, readonly_caller_project = project("ReadonlyHostConsumer", '<OutputType>Exe</OutputType>', "")
        shutil.copyfile(root / "tests/fixtures/DeclarationModel/ReadonlyHostConsumer.cs", readonly_caller / "Program.cs")
        run("readonly-host-caller-build", [dotnet, "build", readonly_caller_project, "-c", "Release"])
        with hosted("readonly-host", [dotnet, readonly_host / "bin/Release/net10.0/ReadonlyRemoteHost.dll"], expect_stop=True) as address:
            result = run("readonly-host-caller", [dotnet, readonly_caller / "bin/Release/net10.0/ReadonlyHostConsumer.dll", address])
            assert b"read-only Host succeeds while standalone client projection refuses" in result
        (output / "backend-boundary-evidence.json").write_text(json.dumps({
            "authorScenario": "ReadonlyService/ReadonlyValue", "authoredGetterObservedOverHttp": "live",
            "standaloneProjectionDiagnostic": "TYPM112", "declarationAnalysisRejected": False,
            "hostRuntimeBindingRejected": False, "providerImplementationReferencedByCaller": False
        }, indent=2) + "\n", encoding="utf-8")
        helper_model = projection_author / "helper-collision.model.json"
        extract_projection("helperCollision", helper_model)
        shutil.copyfile(helper_model, output / "helper-collision-model.json")
        helper_contracts, helper_contracts_project = project("HelperContracts",
            '<CordisTypertService>helperCollision</CordisTypertService>'
            '<CordisTypertClientModel>helper-collision.model.json</CordisTypertClientModel>'
            '<CordisTypertClientNamespace>Caller.Edges</CordisTypertClientNamespace>'
            '<CordisTypertClientName>ClientCollision</CordisTypertClientName>', composition)
        shutil.copyfile(helper_model, helper_contracts / "helper-collision.model.json")
        stale_helper = helper_contracts / "obj/Release/net10.0/cordis-typert/ClientCollision.Generated.g.cs"
        stale_helper.parent.mkdir(parents=True)
        stale_helper.write_text("stale generated output", encoding="utf-8")
        helper_rejection = run("helper-model-only-contracts-rejected",
            [dotnet, "pack", helper_contracts_project, "-c", "Release", "-o", feed], True)
        assert not stale_helper.exists(), "Rejected contract build retained a stale source"
        named_diagnostic = b"Caller type names collide at 'ClientCollision'" in helper_rejection
        csharp_collision = b"CS0101" in helper_rejection
        assert named_diagnostic, helper_rejection
        assert not csharp_collision, "Collision reached C# compilation instead of projection refusal"
        helper_rejections = [{"entryPoint": "pre-CoreCompile", "clientName": "ClientCollision",
            "collisionName": "ClientCollision", "exitCode": steps[-1]["exitCode"], "expectedFailure": True,
            "staleOutputRemoved": not stale_helper.exists(), "namedDiagnosticObserved": named_diagnostic,
            "csharpCollisionDiagnosticObserved": csharp_collision}]
        for client_name, collision_name in (
                ("JsonCollision", "JsonCollisionJson"),
                ("CarrierCollision", "CarrierCollisionCarrierJson"),
                ("ClientCollision", "ClientCollision"),
                ("ArgsCollision", "ArgsCollisionValueArgs")):
            target = projection / (client_name + ".Generated.g.cs")
            target.write_text("stale generated output", encoding="utf-8")
            rejected = run("helper-" + client_name + "-rejected",
                [dotnet, compiler, "--roslyn-directory", roslyn, "emit-dotnet", "--model", helper_model,
                 "--namespace", "Caller.Edges", "--service", "helperCollision", "--client", client_name,
                 "--output", target], True)
            assert not target.exists(), "Type-name collision retained a stale source"
            named_diagnostic = ("Caller type names collide at '" + collision_name + "'").encode() in rejected
            assert named_diagnostic, rejected
            assert b"data type " in rejected and b"generated " in rejected, rejected
            helper_rejections.append({"entryPoint": "emit-dotnet", "clientName": client_name,
                "collisionName": collision_name, "exitCode": steps[-1]["exitCode"], "expectedFailure": True,
                "staleOutputRemoved": not target.exists(), "namedDiagnosticObserved": named_diagnostic})
        (output / "helper-collision-rejections.json").write_text(
            json.dumps(helper_rejections, indent=2) + "\n", encoding="utf-8")
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
        projection_assets = json.loads((projection / "obj/project.assets.json").read_text(encoding="utf-8"))
        assert {Path(folder).resolve() for folder in projection_assets["packageFolders"]} == {(source / "cache").resolve()}, \
            "Projection caller used a shared/fallback package cache"
        assert f"Cordis.NET.Composition/{version}" in projection_assets["libraries"] and all(
            name.split("/", 1)[1] == version for name in projection_assets["libraries"] if name.startswith("Cordis.NET.")), \
            "Projection caller did not resolve the selected candidate version"
        assert not any(name.startswith("IndependentProjection.Provider/") for name in projection_assets["libraries"])
        assert not list((projection / "bin").rglob("ProjectionAuthor.dll"))
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
        assert {Path(folder).resolve() for folder in assets["packageFolders"]} == {(source / "cache").resolve()}, \
            "Settings caller used a shared/fallback package cache"
        assert f"Cordis.NET.Composition/{version}" in assets["libraries"] and all(
            name.split("/", 1)[1] == version for name in assets["libraries"] if name.startswith("Cordis.NET.")), \
            "Settings caller did not resolve the selected candidate version"
        assert not any(name.startswith("IndependentSettings.Provider/") for name in assets["libraries"])
        assert not list((caller / "bin").rglob("IndependentSettings.Provider.dll"))
        provider_referenced = False
        for project_file in source.glob("*/*.csproj"):
            assert "ProjectReference" not in project_file.read_text(encoding="utf-8")

        if options.aot:
            run("caller-aot-publish", [dotnet, "publish", caller_project, "-c", "Release", "-p:PublishAot=true", "-o", source / "native"])
        for mode in ("jit", "aot") if options.aot else ("jit",):
            with hosted("host-" + mode, [dotnet, host / "bin/Release/net10.0/Host.dll", source / ("profile-" + mode), bundle]) as address:
                command = [dotnet, caller / "bin/Release/net10.0/Caller.dll", address] if mode == "jit" else [source / "native" / ("Caller.exe" if os.name == "nt" else "Caller"), address]
                run("typed-settings-" + mode, command)
        status = "requested-checks-passed"
    finally:
        (output / "results.json").write_text(json.dumps({"status": status, "steps": steps, "independentSourceDirectory": str(source),
            "providerImplementationReferencedByCaller": provider_referenced, "packageReferencesOnly": True,
            "dotnetGenerationRequiresNode": False, "managementTypeScriptCommands": management_node_commands,
            "referencedWebEvidence": "referenced-web-evidence.json" if (output / "referenced-web-evidence.json").exists() else None,
            "aotRequested": options.aot, "declarationsOnly": options.declarations_only,
            "managementOnly": options.management_only, "dataReferencesOnly": options.data_references_only,
            "savedRemoteOnly": options.saved_remote_only, "webContractsOnly": options.web_contracts_only}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
