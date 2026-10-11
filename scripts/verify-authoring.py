"""Run the actual authoring example, compiler contracts, and optional deployment paths.

Use a frozen checkout: source drift makes the run fail even when all commands pass.
Outputs stay under artifacts/authoring; package consumers and compiler fixtures are
physical copies in temporary directories outside the checkout, retained for inspection.
This gate adds .NET authoring evidence; it does not upgrade upstream assertion coverage.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import runpy
import shutil
import subprocess
import socket
import sys
import tempfile
import time
import traceback
import urllib.request
import xml.etree.ElementTree as ET

from package_inspection import inspect_packages
from verify import archive_manifest, git_checkout, source_hashes


ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts" / "authoring"
REQUIRED_SUITES = (
    "Cordis.Core.Tests.TypedEventTests",
    "Cordis.Composition.Tests.ServiceAuthoringTests",
    "Cordis.Composition.Tests.ConfigBindingTests",
    "Cordis.Extensions.Tests.ExternalCallbackTests",
    "Cordis.Composition.Tests.AuthoringBootTests",
    "Cordis.Platform.Tests.BootCollectionTests",
    "Cordis.Platform.Tests.ProbeDeploymentTests",
    "Cordis.Composition.Tests.ConfigurationEditTests",
    "Cordis.Composition.Tests.ClientArtifactTests",
    "Cordis.Extensions.Tests.ConfigurationSessionTests",
)
PACKAGE_IDS = ("Cordis.NET.Core", "Cordis.NET.Composition", "Cordis.NET.Extensions")
SUCCESS_MESSAGE = b"probe authoring scenario passed"


class Gate:
    def __init__(self, directory: Path):
        self.directory = directory
        self.steps: list[dict] = []
        self.env = dict(os.environ, DOTNET_CLI_UI_LANGUAGE="en", VSLANG="1033")
        self.env.setdefault("DOTNET_ROOT", str(Path(shutil.which("dotnet")).resolve().parent))

    def record(self, label: str, **detail):
        self.steps.append({"name": label, "status": "passed", **detail})
        print(f"PASS {label}", flush=True)

    def run(self, label: str, command, cwd=ROOT, env=None, diagnostic_failure=False, runtime_failure=None):
        command = [str(argument) for argument in command]
        print(f"RUN {label}", flush=True)
        started = time.monotonic()
        stdout_path = self.directory / f"{label}.stdout.log"
        stderr_path = self.directory / f"{label}.stderr.log"
        step = {
            "name": label, "command": command, "cwd": str(cwd),
            "stdout": stdout_path.relative_to(OUT).as_posix(),
            "stderr": stderr_path.relative_to(OUT).as_posix(),
            "expected": "compiler CS0411/CS1503 rejection" if diagnostic_failure else
                f"runtime rejection containing {runtime_failure}" if runtime_failure else "exit 0",
            "status": "failed",
        }
        self.steps.append(step)
        stdout, stderr = b"", b""
        try:
            process = subprocess.run(command, cwd=cwd, env=env or self.env,
                                     capture_output=True, timeout=900)
            stdout, stderr = process.stdout, process.stderr
            step["exitCode"] = process.returncode
            if diagnostic_failure:
                diagnostics = sorted(set(re.findall(rb"error (CS\d{4})\b", stdout + stderr)))
                step["compilerErrors"] = [code.decode("ascii") for code in diagnostics]
                if process.returncode == 0 or not diagnostics or not set(diagnostics) <= {b"CS0411", b"CS1503"}:
                    raise RuntimeError(f"{label}: expected only CS0411/CS1503 compiler failures")
                # A restore failure cannot count as a successful negative compilation check.
                if re.search(rb"\berror (?:NU|MSB)\d+\b", stdout + stderr):
                    raise RuntimeError(f"{label}: restore/MSBuild failed before the intended compile check")
            elif runtime_failure:
                if process.returncode == 0 or runtime_failure.encode() not in stdout + stderr:
                    raise RuntimeError(f"{label}: did not reject the targeted runtime defect")
            elif process.returncode != 0:
                raise RuntimeError(f"{label}: command exited {process.returncode}")
            step["status"] = "passed"
        except subprocess.TimeoutExpired as error:
            stdout, stderr = error.stdout or b"", error.stderr or b""
            step["timedOut"] = True
            raise RuntimeError(f"{label}: command exceeded 900 seconds") from error
        finally:
            step["seconds"] = round(time.monotonic() - started, 3)
            stdout_path.write_bytes(stdout)
            stderr_path.write_bytes(stderr)
            print(f"{'PASS' if step['status'] == 'passed' else 'FAIL'} {label} ({step['seconds']}s)", flush=True)
            if step["status"] != "passed":
                print((stdout + stderr).decode("utf-8", "replace")[-6000:], file=sys.stderr)
        return stdout


def standalone_directory(prefix: str) -> Path:
    directory = Path(tempfile.mkdtemp(prefix=prefix)).resolve()
    if directory.is_relative_to(ROOT):
        raise RuntimeError("Temporary consumers must live outside the repository; select an external TEMP/TMPDIR")
    # Prevent unrelated ancestor props, targets, SDK selection or NuGet sources from affecting this consumer.
    (directory / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    (directory / "Directory.Build.targets").write_text("<Project />\n", encoding="utf-8")
    shutil.copyfile(ROOT / "global.json", directory / "global.json")
    return directory


def write_config(directory: Path, package_dir: Path | None = None):
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    fallback = ET.SubElement(config, "fallbackPackageFolders")
    ET.SubElement(fallback, "clear")
    if package_dir is not None:
        ET.SubElement(sources, "add", key="local", value=str(package_dir))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        mapping = ET.SubElement(config, "packageSourceMapping")
        local = ET.SubElement(mapping, "packageSource", key="local")
        ET.SubElement(local, "package", pattern="Cordis.NET.*")
        upstream = ET.SubElement(mapping, "packageSource", key="nuget.org")
        ET.SubElement(upstream, "package", pattern="*")
    ET.indent(config)
    ET.ElementTree(config).write(directory / "NuGet.Config", encoding="utf-8", xml_declaration=True)


def project(directory: Path, *, executable=False, packages: str | None = None) -> Path:
    settings = ET.parse(ROOT / "Directory.Build.props")
    document = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    group = ET.SubElement(document, "PropertyGroup")
    values = {
        "TargetFramework": settings.findtext(".//TargetFramework"),
        "LangVersion": settings.findtext(".//LangVersion"),
        "ImplicitUsings": "enable", "Nullable": "enable", "TreatWarningsAsErrors": "true",
        "RestorePackagesWithLockFile": "true", "NuGetAudit": "false",
    }
    if executable:
        values.update(OutputType="Exe", AssemblyName="AuthoringConsumer", IsAotCompatible="true")
    for name, value in values.items():
        if not value:
            raise RuntimeError(f"Directory.Build.props does not specify {name}")
        ET.SubElement(group, name).text = value
    items = ET.SubElement(document, "ItemGroup")
    if packages:
        for name in PACKAGE_IDS:
            ET.SubElement(items, "PackageReference", Include=name, Version=packages)
        ET.SubElement(items, "EmbeddedResource", Include="Probes.Plugin/cordis.patch.yml", LogicalName="Probes.cordis.patch.yml")
    else:
        reference = ET.SubElement(items, "Reference", Include="Cordis.Core")
        ET.SubElement(reference, "HintPath").text = str(ROOT / "src/Cordis.Core/bin/Release/net10.0/Cordis.Core.dll")
    ET.indent(document)
    path = directory / ("AuthoringConsumer.csproj" if executable else "CompileContract.csproj")
    ET.ElementTree(document).write(path, encoding="utf-8", xml_declaration=True)
    return path


POSITIVE = """using Cordis;
public static class CompilationContract
{
    public static void Check(Context ctx)
    {
        var key = new EventKey<int>("number");
        ctx.Emit(key, 42);
        ctx.Emit(new EventKey<string?>("nullable"), null);
        ctx.Emit(new EventKey<bool>("boolean"), false);
        ctx.On(key, (_, _) => null);
        ctx.On(key, (_, _) => false);
        ctx.On(key, Observe);
        ctx.On(key, (_, _) => { });
        ctx.On(key, Result);
        ctx.On(key, ObserveAsync);
        ctx.On(key, ResultAsync);
        ctx.On(key, async (_, _) => { await Task.Yield(); });
        ctx.On(key, async (_, _) => { await Task.Yield(); return (object?)false; });
        ctx.On(key, (evt, _) => evt.Next());
        ctx.Once(key, (_, _) => null);
        ctx.Once(key, (_, _) => false);
        ctx.Once(key, Observe);
        ctx.Once(key, (_, _) => { });
        ctx.Once(key, Result);
        ctx.Once(key, ObserveAsync);
        ctx.Once(key, ResultAsync);
        ctx.Once(key, async (_, _) => { await Task.Yield(); });
        ctx.Once(key, async (_, _) => { await Task.Yield(); return (object?)false; });
        ctx.Once(key, (evt, _) => evt.Next());
        _ = ctx.ParallelAsync(key, 42);
        _ = ctx.SerialAsync(key, 42);
        _ = ctx.Bail(key, 42);
        _ = ctx.Waterfall(key, () => Undefined.Value, 42);
    }
    private static void Observe(EventContext evt, int value) { }
    private static object? Result(EventContext evt, int value) => false;
    private static Task ObserveAsync(EventContext evt, int value) => Task.CompletedTask;
    private static Task<object?> ResultAsync(EventContext evt, int value) => Task.FromResult<object?>(false);
}
"""

NEGATIVE_PAYLOAD = """using Cordis;
public static class CompilationContract
{
    public static void Check(Context ctx) => ctx.Emit(new EventKey<int>("number"), "wrong payload");
}
"""

NEGATIVE_LISTENER = """using Cordis;
public static class CompilationContract
{
    public static void Check(Context ctx) => ctx.On<int>(new EventKey<int>("number"),
        (Action<EventContext, string>)((evt, value) => { }));
}
"""


def compilation_contracts(gate: Gate):
    directory = standalone_directory("cordis-authoring-compile-")
    write_config(directory)
    gate.record("compiler-fixture-location", path=str(directory))
    for name, source, fails in (
        ("positive", POSITIVE, False),
        ("wrong-payload", NEGATIVE_PAYLOAD, True),
        ("wrong-listener", NEGATIVE_LISTENER, True),
    ):
        fixture = directory / name
        fixture.mkdir()
        csproj = project(fixture)
        (fixture / "Contract.cs").write_text(source, encoding="utf-8")
        gate.run(f"compile-{name}-restore", ["dotnet", "restore", csproj, "--configfile", directory / "NuGet.Config"], fixture)
        gate.run(f"compile-{name}", ["dotnet", "build", csproj, "-c", "Release", "--no-restore"], fixture,
                 diagnostic_failure=fails)


def reuse_test_results(report_path: Path, current_hashes: dict, rid: str, sdk_version: str) -> Path:
    """Reuse the preceding verification in this checkout, never an arbitrary green TRX."""
    report = json.loads(report_path.read_text(encoding="utf-8"))
    expected = {
        "status": "requested-checks-passed", "sourceUnchanged": True,
        "checkoutRoot": str(ROOT.resolve()), "rid": rid, "sdkVersion": sdk_version,
        "initialSourceSha256": current_hashes, "sourceSha256": current_hashes,
    }
    for field, value in expected.items():
        if report.get(field) != value:
            raise ValueError(f"Cannot reuse verification: {field} differs or is incomplete")
    for name in ("test", "test-map"):
        steps = [step for step in report.get("steps", []) if step.get("name") == name]
        if len(steps) != 1 or steps[0].get("exitCode") != 0:
            raise ValueError(f"Cannot reuse verification: {name} did not pass")
    recorded = report.get("testResults") or {}
    directory = Path(recorded.get("directory", "")).resolve()
    if not directory.is_relative_to((ROOT / "artifacts" / "verification").resolve()):
        raise ValueError("Cannot reuse verification: test results belong to another checkout")
    actual = {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
              for path in sorted(directory.glob("*.trx"))}
    if not actual or actual != recorded.get("sha256"):
        raise ValueError("Cannot reuse verification: TRX reports are missing or changed")
    return directory


def verify_test_suites(gate: Gate, directory: Path):
    audit = runpy.run_path(str(ROOT / "scripts/test-map.py"))["audit_native_results"](directory)
    gate.record("native-platform-results", **{key: value for key, value in audit.items() if key != "results"})
    suites: dict[str, list[dict]] = {name: [] for name in REQUIRED_SUITES}
    reports = list(directory.glob("*.trx"))
    if not reports:
        raise RuntimeError("The solution test run did not produce TRX reports")
    for report in reports:
        tree = ET.parse(report)
        classes = {}
        for definition in tree.findall(".//{*}UnitTest"):
            method = definition.find("{*}TestMethod")
            if method is not None:
                classes[definition.attrib["id"]] = method.attrib.get("className", "")
        for result in tree.findall(".//{*}UnitTestResult"):
            suite = classes.get(result.attrib.get("testId"))
            if suite in suites:
                suites[suite].append({"test": result.attrib.get("testName"), "outcome": result.attrib.get("outcome")})
    if any(not cases or any(case["outcome"] != "Passed" for case in cases) for cases in suites.values()):
        gate.steps.append({"name": "required-authoring-suites", "status": "failed", "suites": suites})
        raise RuntimeError("Required authoring suites were absent, skipped or failed; inspect TRX and results.json")
    gate.record("required-authoring-suites", suites=suites)


def publish_and_run(gate: Gate, label: str, csproj: Path, assembly: str, rid: str,
                    *, aot=False, cwd=ROOT, env=None, arguments=(), success_message=SUCCESS_MESSAGE):
    output = gate.directory / label
    command = ["dotnet", "publish", csproj, "-c", "Release", "-r", rid,
               "--self-contained", "true", "-o", output,
               "-p:NuGetLockFilePath=obj/aot.packages.lock.json" if aot else
               "-p:NuGetLockFilePath=obj/authoring-jit.packages.lock.json"]
    if aot:
        command.append("-p:PublishAot=true")
    gate.run(label + "-publish", command, cwd, env)
    executable = output / (assembly + (".exe" if os.name == "nt" else ""))
    if aot and (output / (assembly + ".dll")).exists():
        raise RuntimeError(f"{label}: managed entry assembly remained in the fresh Native AOT output")
    stdout = gate.run(label + "-run", [executable, *arguments], output, env)
    if success_message not in stdout:
        raise RuntimeError(f"{label}: example did not report successful completion")
    gate.record(label + "-scenario", output=output.relative_to(OUT).as_posix(), nativeAot=aot,
                executableSha256=hashlib.sha256(executable.read_bytes()).hexdigest())
    return stdout


def clr_example(gate: Gate, rid: str):
    # Build the fixtures through the solution, then deploy physical DLL bundles.
    # The console has no reference to either unloadable provider implementation.
    bundles = []
    framework = ET.parse(ROOT / "Directory.Build.props").findtext(".//TargetFramework")
    for version in ("V1", "V2"):
        source = ROOT / "tests" / "fixtures" / ("Probe" + version) / "bin" / "Release" / framework
        bundle = gate.directory / "clr-bundles" / version
        shutil.copytree(source, bundle)
        if not (bundle / "ProbePlugin.dll").is_file():
            raise RuntimeError(f"Missing real provider fixture: {bundle}")
        bundles.append(bundle)
    publish_and_run(gate, "clr-jit", ROOT / "examples/Probes.Clr/Probes.Clr.csproj",
                    "Probes.Clr", rid, arguments=bundles,
                    success_message=b"CLR probe authoring scenario passed")
    output = gate.directory / "clr-jit"
    dependencies = json.loads((output / "Probes.Clr.deps.json").read_text(encoding="utf-8"))
    forbidden = {"ProbePlugin", "ProbeV1", "ProbeV2", "Probes.Plugin"}
    if any(name.split("/")[0] in forbidden for name in dependencies["libraries"]) or any(
            (output / (name + ".dll")).exists() for name in forbidden):
        raise RuntimeError("CLR host deployment contains a static provider implementation dependency")
    gate.record("clr-deployment-boundaries", nativeAot=False, staticProviderDependencies=0,
                bundles={bundle.name: {path.relative_to(bundle).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
                                      for path in sorted(bundle.rglob("*")) if path.is_file()}
                         for bundle in bundles},
                unloadEvidence="Lifecycle cleanup and unload requests; collection is observed without forcing GC")


def package_consumer(gate: Gate, package_dir: Path, version: str, rid: str, aot: bool):
    inspected = inspect_packages(package_dir, version)
    gate.record("existing-package-content-boundaries", packages=inspected,
                sha256={path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(package_dir.glob("*.nupkg"))})
    directory = standalone_directory("cordis-authoring-packages-")
    write_config(directory, package_dir)
    copied = {}
    for example in ("Probes.Contracts", "Probes.Plugin", "Probes"):
        source_root = ROOT / "examples" / example
        for source in sorted(source_root.rglob("*")):
            relative = source.relative_to(source_root)
            if not source.is_file() or set(relative.parts) & {"bin", "obj"}:
                continue
            if source.suffix != ".cs" and relative.as_posix() != "cordis.patch.yml":
                continue
            destination = directory / example / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
            original_digest = hashlib.sha256(source.read_bytes()).hexdigest()
            if hashlib.sha256(destination.read_bytes()).hexdigest() != original_digest:
                raise RuntimeError(f"Source changed while copying {source}")
            copied[source.relative_to(ROOT).as_posix()] = original_digest
    if not (directory / "Probes" / "Program.cs").is_file() or not (directory / "Probes.Plugin" / "cordis.patch.yml").is_file():
        raise RuntimeError("The real probe example program and embedded patch must be copied")
    csproj = project(directory, executable=True, packages=version)
    gate.record("independent-package-consumer", path=str(directory), copiedSourceSha256=copied,
                projectReferences=[], packageReferences=list(PACKAGE_IDS), version=version)
    env = dict(gate.env, NUGET_PACKAGES=str(directory / "packages"),
               NUGET_HTTP_CACHE_PATH=str(directory / "http-cache"))
    dotnet = shutil.which("dotnet")
    if dotnet:
        env.setdefault("DOTNET_ROOT", str(Path(dotnet).resolve().parent))
    gate.run("package-consumer-restore", ["dotnet", "restore", csproj, "--packages", directory / "packages",
                                         "--configfile", directory / "NuGet.Config"], directory, env)
    assets = json.loads((directory / "obj/project.assets.json").read_text(encoding="utf-8-sig"))
    if any(library.get("type") == "project" for library in assets["libraries"].values()):
        raise RuntimeError("The package consumer resolved a project dependency")
    if {Path(folder).resolve() for folder in assets["packageFolders"]} != {directory / "packages"}:
        raise RuntimeError("The package consumer used a shared/fallback package cache")
    for name in PACKAGE_IDS:
        if f"{name}/{version}" not in assets["libraries"]:
            raise RuntimeError(f"The package consumer did not resolve {name} {version}")
    gate.record("package-consumer-isolation", packageFolders=list(assets["packageFolders"]), projectDependencies=0)
    jit = publish_and_run(gate, "package-jit", csproj, "AuthoringConsumer", rid, cwd=directory, env=env)
    application_client(gate, "package-jit", gate.directory / "package-jit" / ("AuthoringConsumer.exe" if os.name == "nt" else "AuthoringConsumer"))
    if aot:
        native = publish_and_run(gate, "package-aot", csproj, "AuthoringConsumer", rid, aot=True, cwd=directory, env=env)
        application_client(gate, "package-aot", gate.directory / "package-aot" / ("AuthoringConsumer.exe" if os.name == "nt" else "AuthoringConsumer"))
        if jit != native:
            raise RuntimeError("Package JIT and AOT scenario output differs")
        gate.record("package-jit-aot-output-parity")


def configuration_mutations(gate: Gate):
    # Run the unchanged public consumer against deliberately broken production helpers.
    # Each copy is outside the checkout; the original source and contract assertions stay frozen.
    mutations = (
        ("live-binding", "ConfigObject.cs", "descriptor.IsVolatile ? schema.WithVolatile(name, project) : schema", "schema", "requires an explicit typed projection"),
        ("ordinary-equality", "ConfigObject.cs", "!field.Descriptor.IsVolatile &&\n                !ConfigDescriptor.StrictEquals(field.Project(left), field.Project(right))", "field.Name.Length < 0", "ordinary effective change refuses live commit"),
        ("persistence", "ConfigObject.cs", "result.Add(field.Name, field.Project(value));", "if (field.Name != \"category\")\n                result.Add(field.Name, field.Project(value));", "complete persistence"),
        ("revision-fence", "PluginConfigurationOperations.Edit.cs", "if (revision != expectedRevision)\n                            throw new Refusal(\"conflict\");", "if (revision.Length < 0)\n                            throw new Refusal(\"conflict\");", "profile field edit fences stale revisions"),
        ("field-validation", "PluginConfigurationOperations.Edit.cs", "else\n                            entry.Fiber!.ValidateConfiguration(next);", "else\n                        {\n                        }", "profile field edit validates before persistence"),
    )
    files = source_hashes()
    for name, production_file, before, after, expected in mutations:
        directory = standalone_directory("cordis-configuration-mutant-")
        for relative in files:
            # Only copy the production libraries, actual consumer, and their build inputs.
            if not (relative.startswith(("src/", "tools/Cordis.Typert.Compiler/", "tools/Cordis.Typert.Generator/", "examples/Probes/", "examples/Probes.Plugin/", "examples/Probes.Contracts/"))
                    or "/" not in relative):
                continue
            source = ROOT / relative
            target = directory / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
        path = directory / "src/Cordis.Composition" / production_file
        text = path.read_text(encoding="utf-8")
        if text.count(before) != 1:
            raise RuntimeError(f"Mutation {name} no longer identifies exactly one production expression")
        path.write_text(text.replace(before, after), encoding="utf-8")
        consumer = directory / "examples/Probes/Probes.csproj"
        gate.run(f"configuration-mutant-{name}-restore", ["dotnet", "restore", consumer, "--locked-mode"], directory)
        gate.run(f"configuration-mutant-{name}-build", ["dotnet", "build", consumer, "-c", "Release", "--no-restore"], directory)
        gate.run(f"configuration-mutant-{name}-rejected", ["dotnet", "run", "--project", consumer, "-c", "Release", "--no-build", "--no-restore"],
                 directory, runtime_failure=expected)
        gate.record(f"configuration-mutant-{name}", sourceExpression=before, replacement=after,
                    expectedRejection=expected, consumerAssertionsChanged=False)


def application_client(gate: Gate, label: str, executable: Path, *, expected_failure=None, client_source=None):
    package = standalone_directory("cordis-client-package-")
    gate.run(label + "-client-build", ["node", "scripts/build-application-client.mjs", package,
                                      client_source or ROOT / "examples/Probes/client"])
    with socket.socket() as endpoint:
        endpoint.bind(("127.0.0.1", 0))
        port = endpoint.getsockname()[1]
    origin = f"http://127.0.0.1:{port}"
    stdout = gate.directory / (label + "-host.stdout.log")
    stderr = gate.directory / (label + "-host.stderr.log")
    with stdout.open("wb") as out, stderr.open("wb") as err:
        host = subprocess.Popen([str(executable), "--application-host", origin + "/", str(package)],
                                cwd=ROOT, env=gate.env, stdout=out, stderr=err)
        try:
            deadline = time.monotonic() + 30
            while True:
                if host.poll() is not None:
                    raise RuntimeError(f"{label}: host exited before ready; inspect {stderr}")
                try:
                    with urllib.request.urlopen(origin + "/client", timeout=1) as response:
                        json.load(response)
                    break
                except OSError:
                    if time.monotonic() >= deadline:
                        raise RuntimeError(f"{label}: native HTTP host did not become ready")
                    time.sleep(0.1)
            gate.run(label + "-real-client", ["node", "scripts/application-client-consumer.mjs", origin, package],
                     runtime_failure=expected_failure)
            gate.record(label + "-delivery", executableSha256=hashlib.sha256(executable.read_bytes()).hexdigest(),
                        hostStdout=stdout.relative_to(OUT).as_posix(), hostStderr=stderr.relative_to(OUT).as_posix(),
                        protocol="explicit primitive live SET and immutable self-contained ESM subset",
                        execution="actual Node ESM client; browser UI rendering not asserted")
        finally:
            try:
                with urllib.request.urlopen(urllib.request.Request(origin + "/stop", data=b"", method="POST"), timeout=3):
                    pass
                host.wait(timeout=10)
            except (OSError, subprocess.TimeoutExpired):
                host.kill()
                host.wait(timeout=10)


def application_contracts(gate: Gate):
    provenance = json.loads((ROOT / "examples/Probes/client/vendor/provenance.json").read_text(encoding="utf-8-sig"))
    lock = json.loads((ROOT / "upstream.lock.json").read_text(encoding="utf-8-sig"))
    # The retained reference source is pinned independently of the build tools.
    if provenance["commit"] not in json.dumps(lock):
        raise RuntimeError("Client provenance does not match the formal fixed DSH commit")
    for source in provenance["files"]:
        path = ROOT / "examples/Probes/client" / source["file"]
        if hashlib.sha256(path.read_bytes()).hexdigest() != source["sha256"]:
            raise RuntimeError(f"Fixed client source identity changed: {source['file']}")
    gate.record("fixed-client-source-identity", **provenance)
    gate.run("client-types", ["node", "reference/node_modules/typescript/bin/tsc", "-p", "examples/Probes/client/tsconfig.json"])
    files = source_hashes()
    for name, production, before, after, expected in (
        ("redaction", "PluginConfigurationOperations.Settings.cs", "if (!policy.Allows(name))", "if (name.Length < 0)", "live view excludes ordinary and secret values"),
        ("artifact-identity", "ClientArtifact.cs", "Revision = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();", "Revision = \"fixed\";", "client content hash binds captured bytes"),
    ):
        directory = standalone_directory("cordis-application-mutant-")
        for relative in files:
            if not (relative.startswith(("src/", "tools/Cordis.Typert.Compiler/", "tools/Cordis.Typert.Generator/", "examples/Probes/", "examples/Probes.Plugin/", "examples/Probes.Contracts/")) or "/" not in relative):
                continue
            target = directory / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / relative, target)
        path = directory / "src/Cordis.Composition" / production
        text = path.read_text(encoding="utf-8")
        if text.count(before) != 1:
            raise RuntimeError(f"Application mutation {name} no longer identifies one production expression")
        path.write_text(text.replace(before, after), encoding="utf-8")
        output = directory / "published"
        gate.run(name + "-mutant-publish", ["dotnet", "publish", directory / "examples/Probes/Probes.csproj", "-c", "Release", "-o", output], directory)
        application_client(gate, name + "-mutant", output / ("Probes.exe" if os.name == "nt" else "Probes"), expected_failure=expected)
    # Corrupt the actual adapter while retaining the independently written end-to-end consumer.
    directory = standalone_directory("cordis-client-fence-mutant-")
    shutil.copytree(ROOT / "examples/Probes/client", directory / "client")
    path = directory / "client/application.ts"
    text = path.read_text(encoding="utf-8")
    before = "this.revisions.get(expectedRevision)"
    if text.count(before) != 1:
        raise RuntimeError("Client fence mutation no longer identifies one lookup")
    path.write_text(text.replace(before, "this.revisions.get(this.nextRevision)"), encoding="utf-8")
    executable = ROOT / "examples/Probes/bin/Release/net10.0" / ("Probes.exe" if os.name == "nt" else "Probes")
    application_client(gate, "client-fence-mutant", executable, client_source=directory / "client",
                       expected_failure="draft keeps original revision fence after refresh")

    directory = standalone_directory("cordis-client-boundary-mutant-")
    for relative in files:
        if not (relative.startswith(("src/", "tools/Cordis.Typert.Compiler/", "tools/Cordis.Typert.Generator/", "examples/Probes/", "examples/Probes.Plugin/", "examples/Probes.Contracts/", "tests/Cordis.Composition.Tests/")) or "/" not in relative):
            continue
        target = directory / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(ROOT / relative, target)
    path = directory / "src/Cordis.Composition/ClientArtifact.cs"
    text = path.read_text(encoding="utf-8")
    before = "if (!Within(lexical, root) || !Within(canonical, root))"
    if text.count(before) != 1:
        raise RuntimeError("Client boundary mutation no longer identifies one guard")
    path.write_text(text.replace(before, "if (lexical.Length < 0)"), encoding="utf-8")
    project = directory / "tests/Cordis.Composition.Tests/Cordis.Composition.Tests.csproj"
    gate.run("client-boundary-mutant-build", ["dotnet", "build", project, "-c", "Release"], directory)
    gate.run("client-boundary-mutant-rejected", ["dotnet", "test", project, "-c", "Release", "--no-build", "--no-restore",
                                               "--filter", "FullyQualifiedName~Declared_artifact_cannot_escape"], directory,
             runtime_failure="No exception was thrown")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--aot", action="store_true", help="Publish and run the actual probe scenario with Native AOT too")
    parser.add_argument("--packages", type=Path, help="Consume an existing local package batch from verify.py --package; never publishes packages")
    parser.add_argument("--verification", type=Path,
                        help="Reuse the immediately preceding verify.py report and bound TRX in this checkout")
    options = parser.parse_args()
    OUT.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="run-", dir=OUT))
    gate = Gate(directory)
    rid = ("win" if os.name == "nt" else "linux" if sys.platform.startswith("linux") else "osx") + (
        "-arm64" if platform.machine().lower() in ("aarch64", "arm64") else "-x64")
    initial = source_hashes()
    status, error = "failed", None
    try:
        gate.run("sdk", ["dotnet", "--info"])
        if options.verification:
            sdk_version = gate.run("sdk-version", ["dotnet", "--version"]).decode().strip()
            results = reuse_test_results(options.verification, initial, rid, sdk_version)
            gate.record("reused-solution-tests", verification=str(options.verification.resolve()),
                        sha256=hashlib.sha256(options.verification.read_bytes()).hexdigest(),
                        results=str(results))
        else:
            gate.run("solution-restore", ["dotnet", "restore", "Cordis.slnx", "--locked-mode"])
            gate.run("solution-build", ["dotnet", "build", "Cordis.slnx", "-c", "Release", "--no-restore"])
            results = directory / "tests"
            gate.run("solution-tests", ["dotnet", "test", "Cordis.slnx", "-c", "Release", "--no-build", "--no-restore",
                                        "--logger", "trx", "--results-directory", results])
        verify_test_suites(gate, results)
        compilation_contracts(gate)
        configuration_mutations(gate)
        application_contracts(gate)
        clr_example(gate, rid)
        jit = publish_and_run(gate, "example-jit", ROOT / "examples/Probes/Probes.csproj", "Probes", rid)
        application_client(gate, "example-jit", gate.directory / "example-jit" / ("Probes.exe" if os.name == "nt" else "Probes"))
        if options.aot:
            native = publish_and_run(gate, "example-aot", ROOT / "examples/Probes/Probes.csproj", "Probes", rid, aot=True)
            application_client(gate, "example-aot", gate.directory / "example-aot" / ("Probes.exe" if os.name == "nt" else "Probes"))
            if jit != native:
                raise RuntimeError("Example JIT and AOT scenario output differs")
            gate.record("example-jit-aot-output-parity")
        if options.packages:
            version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
            if not version:
                raise RuntimeError("Directory.Build.props does not specify Version")
            package_consumer(gate, options.packages.resolve(), version, rid, options.aot)
        status = "requested-checks-passed"
    except Exception as caught:
        error = f"{type(caught).__name__}: {caught}"
        (directory / "failure.log").write_text(traceback.format_exc(), encoding="utf-8")
        print(error, file=sys.stderr)
    finally:
        final = source_hashes()
        changed = sorted(name for name in initial.keys() | final.keys() if initial.get(name) != final.get(name))
        if changed:
            status = "failed"
            drift = "Source tree changed during verification; rerun against a frozen checkpoint"
            error = f"{error}; {drift}" if error else drift
            print(drift + ": " + ", ".join(changed), file=sys.stderr)
        if git_checkout():
            commit = gate.run("git-head", ["git", "rev-parse", "HEAD"]).decode().strip()
            working_tree = gate.run("git-status", ["git", "status", "--porcelain"]).decode("utf-8", "replace")
        else:
            commit, working_tree = archive_manifest()["commit"], "source archive"
        report = {
            "status": status, "error": error, "commit": commit, "workingTree": working_tree,
            "platform": platform.platform(), "rid": rid,
            "requested": {"aot": options.aot, "packages": str(options.packages.resolve()) if options.packages else None,
                          "verification": str(options.verification.resolve()) if options.verification else None},
            "evidenceScope": ".NET authoring, deployment and package checks; not upstream assertion closure",
            "steps": gate.steps, "sourceUnchanged": not changed, "changedSources": changed,
            "initialSourceSha256": initial, "sourceSha256": final,
        }
        encoded = json.dumps(report, indent=2) + "\n"
        (directory / "results.json").write_text(encoded, encoding="utf-8")
        (OUT / "results.json").write_text(encoded, encoding="utf-8")
    print(status, flush=True)
    return 0 if status == "requested-checks-passed" else 1


if __name__ == "__main__":
    raise SystemExit(main())
