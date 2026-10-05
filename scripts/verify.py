"""Local verification entry point. Every external command has a log and checked exit code."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
import xml.etree.ElementTree as ET

from package_inspection import inspect_packages

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "artifacts" / "verification"
OUT.mkdir(parents=True, exist_ok=True)
steps = []


def git_checkout():
    if not shutil.which("git"):
        return False
    result = subprocess.run(["git", "rev-parse", "--show-toplevel"], cwd=ROOT, capture_output=True, text=True)
    return result.returncode == 0 and Path(result.stdout.strip()).resolve() == ROOT


def archive_manifest():
    return json.loads((ROOT / "SOURCE_SHA256.json").read_text(encoding="utf-8"))


def source_hashes():
    if git_checkout():
        files = subprocess.check_output(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT).decode().split("\0")[:-1]
    else:
        # Source ZIPs deliberately contain no .git directory. Their generated inventory
        # provides the same source boundary without including build caches.
        manifest = archive_manifest()
        recorded = manifest["files"] | manifest.get("generatedFiles", {})
        files = list(recorded)
        for name in files:
            path = (ROOT / name).resolve()
            assert path.is_relative_to(ROOT) and path.is_file(), name
    return {name: hashlib.sha256((ROOT / name).read_bytes()).hexdigest() for name in sorted(set(files))
            if (ROOT / name).is_file() and not name.startswith("verification/")}


def run(label, command, cwd=ROOT, env=None, expected_exit=0):
    print(f"RUN {label}", flush=True)
    started = time.time()
    process = subprocess.run([str(a) for a in command], cwd=cwd, env=env, capture_output=True)
    (OUT / f"{label}.stdout.log").write_bytes(process.stdout)
    (OUT / f"{label}.stderr.log").write_bytes(process.stderr)
    steps.append({"name": label, "command": [str(a) for a in command], "exitCode": process.returncode,
                  "seconds": round(time.time() - started, 3), "stdout": f"{label}.stdout.log", "stderr": f"{label}.stderr.log"})
    steps[-1]["expectedExitCode"] = expected_exit
    print(f"{'PASS' if process.returncode == expected_exit else 'FAIL'} {label} ({steps[-1]['seconds']}s)", flush=True)
    if process.returncode != expected_exit:
        print(process.stdout.decode("utf-8", "replace")[-12000:])
        print(process.stderr.decode("utf-8", "replace")[-12000:], file=sys.stderr)
        raise RuntimeError(f"{label} exited {process.returncode}")
    return process.stdout


def compare(label, first, second):
    a = json.loads(first.decode("utf-8-sig"))
    b = json.loads(second.decode("utf-8-sig"))
    if a != b:
        raise AssertionError(f"{label}: ordered trace difference; inspect saved logs")
    steps.append({"name": label, "exitCode": 0})
    print(f"PASS {label}", flush=True)


def record_source_results(reports):
    inventory = json.loads((ROOT / "docs/upstream-tests.json").read_text(encoding="utf-8"))["tests"]
    target = json.loads((ROOT / "upstream.lock.json").read_text(encoding="utf-8"))["harness"]["commit"]
    evidence = []
    for report, repository in reports:
        results = json.loads(report.read_text(encoding="utf-8"))
        assert results["success"] and results["numFailedTests"] == 0, f"Failed upstream report: {report}"
        report_skipped = 0
        for file in results["testResults"]:
            assert file["status"] == "passed"
            source_file = Path(file["name"]).relative_to(repository).as_posix()
            skipped = []
            for test in file["assertionResults"]:
                if test["status"] == "passed":
                    continue
                # This pinned test detects the filesystem capability itself and calls
                # context.skip(). Keep that outcome visible; never count it as a pass.
                filesystem_skip = (
                    target == "639ed015397290b3745d163aafe02ffee4aa3f84"
                    and source_file == "packages/boot/app-boot/tests/package-meta.spec.ts"
                    and test["fullName"] == "plugin locale display metadata rejects case-equivalent language files on case-sensitive filesystems"
                    and test["status"] == "skipped"
                )
                assert filesystem_skip, f"Unexpected upstream outcome: {source_file}: {test['fullName']} [{test['status']}]"
                skipped.append({"assertion": test["fullName"],
                                "reason": "Pinned upstream calls context.skip() on case-insensitive filesystems that cannot store both en.json and EN.json."})
                print(f"SOURCE SKIP {source_file}: {test['fullName']} (case-insensitive filesystem)", flush=True)
            report_skipped += len(skipped)
            instances = [test for test in inventory if test["file"] == source_file]
            if target == "639ed015397290b3745d163aafe02ffee4aa3f84" and source_file.startswith(("scripts/", "packages/boot/")):
                evidence.append({"file": source_file, "sourceCommit": target, "report": report.name,
                                 "allOriginalInstancesPassed": not skipped, "instanceCount": len(file["assertionResults"]),
                                 "passedInstanceCount": len(file["assertionResults"]) - len(skipped), "skippedInstances": skipped,
                                 "assertions": [test["fullName"] for test in file["assertionResults"]],
                                 "meaning": "Current fixed-source execution only; historical inventory and native assertion-review dispositions are unchanged."})
                continue
            assert len(instances) == len(file["assertionResults"]), (source_file, len(instances), len(file["assertionResults"]))
            evidence.append({"file": source_file, "inventoryIds": [test["id"] for test in instances],
                             "sourceCommit": instances[0]["sourceCommit"], "report": report.name,
                             "allOriginalInstancesPassed": True, "instanceCount": len(instances)})
        assert results["numPendingTests"] == report_skipped, f"Unrecorded upstream skips: {report}"
        assert results["numTotalTests"] == results["numPassedTests"] + report_skipped, f"Inconsistent upstream totals: {report}"
    (OUT / "source-test-evidence.json").write_text(json.dumps({"meaning": "Original case outcomes are recorded per executed file. Historical files match frozen inventory cohort counts; current target files record their fixed commit, assertion names and the explicit filesystem-capability skip separately. Skipped cases are not passes. This is reference-side execution, not .NET adaptation status.", "files": evidence}, indent=2) + "\n", encoding="utf-8")


def consume_packages(rid, package_dir, version, aot):
    # Outside the repository: no inherited props, project references or developer package cache.
    directory = Path(tempfile.mkdtemp(prefix="cordis-consumer-"))
    env = dict(os.environ, NUGET_PACKAGES=str(directory / "cache"))
    # The tool shim needs the runtime location when the SDK is installed privately
    # (for example dotnet-install under a user's home rather than /usr/share/dotnet).
    env.setdefault("DOTNET_ROOT", str(Path(shutil.which("dotnet")).resolve().parent))
    (directory / "NuGet.Config").write_text(
        f'<configuration><packageSources><clear/><add key="local" value="{package_dir.as_posix()}"/>'
        '<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>'
        '<packageSourceMapping><packageSource key="local"><package pattern="Cordis.*"/></packageSource>'
        '<packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>', encoding="utf-8")
    (directory / "Consumer.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup><PackageReference Include="Cordis.Example.Greeting" Version="{version}"/></ItemGroup>
</Project>''', encoding="utf-8")
    (directory / "Program.cs").write_text('''using Cordis;
using Cordis.Composition;
using Cordis.Example.Greeting;
await using var context = new Context();
await context.RunAsync(async ctx => {
    var modules = new StaticModuleResolver().Register("greeting", GreetingModule.Plugin);
    var loader = new Loader(ctx, modules);
    var patch = ConfigurationFile.ParseEntries(GreetingModule.ReadPatch());
    await loader.Root.UpdateAsync(EntryPatches.Apply([], patch));
    await loader.WaitAsync();
    if (ctx.Get<string>("greeting") != "Hello from a deployed NuGet resource") throw new Exception("Package composition failed");
    await loader.UpdateAsync("greeting", new EntryOptions { Disabled = true });
    if (ctx.Get<string>("greeting") != null) throw new Exception("Disable failed");
    await loader.UpdateAsync("greeting", new EntryOptions { Disabled = false });
    await loader.WaitAsync();
    if (ctx.Get<string>("greeting") == null) throw new Exception("Reactivation failed");
    int applies = 0;
    var schema = new ConfigSchema<ConsumerSettings>(raw => raw is ConsumerSettings value && value.Limit > 0
        ? ConfigResult<ConsumerSettings>.Success(value) : ConfigResult<ConsumerSettings>.Failure("positive limit required"),
        ConfigDescriptor.Object(("limit", ConfigDescriptor.Number().Volatile()), ("label", ConfigDescriptor.String())))
        .WithVolatile("limit", value => value.Limit)
        .WithOrdinaryEquality((left, right) => left.Label == right.Label)
        .WithSimplify(value => new Dictionary<string, object?> { ["limit"] = value.Limit, ["label"] = value.Label });
    var fiber = ctx.Plugin(new Plugin<ConsumerSettings> { Configuration = schema, Apply = (_, _) => applies++ }, new ConsumerSettings(1, "same"));
    await fiber.WaitAsync();
    var reference = fiber.GetConfigReference<int>("limit"); var effective = fiber.Config;
    if (!fiber.TryPrepareConfigurationUpdate(new ConsumerSettings(2, "same"), out var update) || update is null || reference.Value != 1 || !update.Commit())
        throw new Exception("Package configuration preparation/commit failed");
    if (reference.Value != 2 || !ReferenceEquals(effective, fiber.Config) || applies != 1)
        throw new Exception("Package stable configuration identity failed");
    fiber.Update(new ConsumerSettings(3, "changed")); await fiber.WaitAsync();
    if (reference.Value != 2 || ReferenceEquals(reference, fiber.GetConfigReference<int>("limit")) || fiber.GetConfigReference<int>("limit").Value != 3)
        throw new Exception("Package activation references were not isolated");
    if ((int)((IReadOnlyDictionary<string, object?>)fiber.SimplifyConfiguration(fiber.Config)!)["limit"]! != 3)
        throw new Exception("Package typed simplification failed");
});
Console.WriteLine("independent package consumer passed");
record ConsumerSettings(int Limit, string Label);
''', encoding="utf-8")
    run("consumer-restore", ["dotnet", "restore", "Consumer.csproj", "--packages", directory / "cache"], directory, env)
    run("consumer-publish", ["dotnet", "publish", "Consumer.csproj", "-c", "Release", "-r", rid, "--self-contained", "true", "-o", directory / "publish"], directory, env)
    run("consumer-run", [directory / "publish" / ("Consumer.exe" if os.name == "nt" else "Consumer")], directory / "publish", env)
    if aot:
        run("consumer-aot-publish", ["dotnet", "publish", "Consumer.csproj", "-c", "Release", "-r", rid,
                                    "-p:PublishAot=true", "-o", directory / "native"], directory, env)
        run("consumer-aot-run", [directory / "native" / ("Consumer.exe" if os.name == "nt" else "Consumer")], directory / "native", env)
    adapters = directory / "adapters"
    adapters.mkdir()
    (adapters / "Adapters.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>{''.join(f'<PackageReference Include="{name}" Version="{version}"/>' for name in ['Cordis.NET.Extensions', 'Cordis.NET.Clr', 'Cordis.NET.Hosting', 'Cordis.NET.JavaScript'])}</ItemGroup>
</Project>''', encoding="utf-8")
    (adapters / "Program.cs").write_text('''using Cordis;
using Cordis.Clr;
using Cordis.Extensions;
using Cordis.Hosting;
using Cordis.JavaScript;
using Microsoft.Extensions.DependencyInjection;
var services = new ServiceCollection();
services.AddCordis();
await using var container = services.BuildServiceProvider();
var root = container.GetRequiredService<Context>();
await root.RunAsync(async ctx => {
    ctx.Provide("answer", (Func<int>)(() => 41));
    var actual = new JintExpressionEvaluator().Evaluate("answer() + 1", ctx);
    if (Convert.ToDouble(actual) != 42) throw new Exception("JS package bridge failed");
    await new TimerService(ctx).TimeoutAsync(TimeSpan.Zero);
});
await using var resolver = new ClrModuleResolver(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
try { await resolver.LocateAsync("unknown", new Uri("file:///")); throw new Exception("Expected unknown module rejection"); }
catch (KeyNotFoundException) { }
Console.WriteLine("independent optional adapter packages passed");
''', encoding="utf-8")
    run("adapters-restore", ["dotnet", "restore", "Adapters.csproj", "--packages", directory / "cache"], adapters, env)
    run("adapters-run", ["dotnet", "run", "--project", "Adapters.csproj", "-c", "Release", "--no-restore"], adapters, env)
    run("tool-install", ["dotnet", "tool", "install", "Cordis.NET.Tool", "--version", version,
                         "--tool-path", directory / "tools", "--configfile", directory / "NuGet.Config"], directory, env)
    run("tool-package-preview", [directory / "tools" / ("cordis.exe" if os.name == "nt" else "cordis"),
                                "preview", OUT / "cli-input.yml", "--json"], directory, env)
    consume_http_package(directory, rid, version, aot, env)
    steps.append({"name": "independent-consumer-location", "path": str(directory), "exitCode": 0})


def consume_http_package(directory, rid, version, aot, env):
    """Exercise the shipped HTTP adapter from an isolated Web SDK PackageReference consumer."""
    consumer = directory / "http"
    consumer.mkdir()
    (consumer / "HttpConsumer.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault></PropertyGroup>
  <ItemGroup><PackageReference Include="Cordis.NET.AspNetCore" Version="{version}"/></ItemGroup>
</Project>''', encoding="utf-8")
    source = (ROOT / "scripts/package_http_consumer.cs").read_text(encoding="utf-8")
    (consumer / "Program.cs").write_text(source, encoding="utf-8")
    run("http-consumer-restore", ["dotnet", "restore", "HttpConsumer.csproj", "--packages", directory / "cache"], consumer, env)
    executable = "HttpConsumer.exe" if os.name == "nt" else "HttpConsumer"
    modes = [("jit", consumer / "publish", [])]
    if aot:
        modes.append(("aot", consumer / "native", ["-p:PublishAot=true"]))
    for mode, output, arguments in modes:
        run(f"http-consumer-{mode}-publish", ["dotnet", "publish", "HttpConsumer.csproj", "-c", "Release", "-r", rid,
            "--self-contained", "true", *arguments, "-o", output], consumer, env)
        run(f"http-consumer-{mode}-run", [output / executable, "one"], output, env)
    # Editing author code cannot change a deployed static binary. Verify the retained
    # artifacts still serve v1, then publish and run v2 through the same contract.
    updated = source.replace('const string implementation = "one";', 'const string implementation = "two";')
    assert updated != source
    (consumer / "Program.cs").write_text(updated, encoding="utf-8")
    for mode, output, arguments in modes:
        run(f"http-consumer-{mode}-retained-run", [output / executable, "one"], output, env)
        replacement = consumer / ("republished-" + mode)
        run(f"http-consumer-{mode}-republish", ["dotnet", "publish", "HttpConsumer.csproj", "-c", "Release", "-r", rid,
            "--self-contained", "true", *arguments, "-o", replacement], consumer, env)
        run(f"http-consumer-{mode}-republished-run", [replacement / executable, "two"], replacement, env)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dsh", type=Path)
    parser.add_argument("--origin", type=Path, help="Pinned Cordis source checkout containing the original Core/Loader tests")
    parser.add_argument("--upstream-test", action="append", default=[], help="Select a relevant original DSH test file; repeat for multiple files. Default retains the original suite.")
    parser.add_argument("--aot", action="store_true")
    parser.add_argument("--package", action="store_true")
    parser.add_argument("--package-output", type=Path, help="Write and validate the exact package batch in this directory")
    options = parser.parse_args()
    rid = ("win" if os.name == "nt" else "linux" if sys.platform.startswith("linux") else "osx") + ("-arm64" if platform.machine().lower() in ("aarch64", "arm64") else "-x64")
    status = "failed"
    initial_hashes = source_hashes()
    try:
        if not git_checkout():
            manifest = archive_manifest()
            recorded = manifest["files"] | manifest.get("generatedFiles", {})
            expected = {name: digest for name, digest in recorded.items() if not name.startswith("verification/")}
            assert initial_hashes == expected, "Source archive bytes differ from the recorded checkpoint"
        run("sdk", ["dotnet", "--info"])
        run("restore", ["dotnet", "restore", "Cordis.slnx", "--locked-mode"])
        run("build", ["dotnet", "build", "Cordis.slnx", "-c", "Release", "--no-restore"])
        test_results = Path(tempfile.mkdtemp(prefix="tests-", dir=OUT))
        run("test", ["dotnet", "test", "Cordis.slnx", "-c", "Release", "--no-build", "--logger", "trx", "--results-directory", test_results])
        run("test-map", [sys.executable, "scripts/test-map.py", "--results", test_results, "--output", OUT / "test-map-evidence.json"])
        command = ["dotnet", ROOT / "tests/Cordis.Conformance/bin/Release/net10.0/Cordis.Conformance.dll"]
        jit = run("jit-1", command)
        for index in (2, 3): compare(f"jit-repeat-{index}", jit, run(f"jit-{index}", command))
        upgrade_jit = run("upgrade-jit", [*command, "--upgrade"])
        run("example", ["dotnet", ROOT / "examples/Composition/bin/Release/net10.0/Composition.dll"])
        client_artifacts = ROOT / "artifacts/client-modules"
        run("client-modules-build", ["node", "scripts/build-client-modules.mjs", client_artifacts])
        run("client-modules-types", ["node", "scripts/verify-client-module-types.mjs", client_artifacts])
        run("client-lifecycle", ["node", "scripts/verify-client-lifecycle.mjs", client_artifacts])
        run("client-shared-modules", ["node", "scripts/verify-client-shared-modules.mjs", client_artifacts])
        run("client-author-watch", ["node", "scripts/verify-client-author-watch.mjs"])
        cli = ["dotnet", ROOT / "tools/Cordis.Cli/bin/Release/net10.0/Cordis.Cli.dll"]
        fixture = OUT / "cli-input.yml"
        fixture.write_text(
            "- id: greeting\n"
            "  name: greeting\n"
            "  config:\n"
            "    positive: .inf\n"
            "    negative: -.inf\n"
            "    nan: .nan\n"
            "    values: [.inf, 1.25, unchanged, null]\n",
            encoding="utf-8")
        run("cli-validate", [*cli, "validate", fixture])
        preview = run("cli-preview", [*cli, "preview", fixture, "--json"])
        assert json.loads(preview) == [{
            "id": "greeting",
            "name": "greeting",
            "config": {
                "positive": None,
                "negative": None,
                "nan": None,
                "values": [None, 1.25, "unchanged", None],
            },
        }]
        run("cli-invocation", [sys.executable, "scripts/verify-cli-invocation.py"])
        run("cli-usage", cli, expected_exit=2)
        run("cli-invalid-option", [*cli, "validate", fixture, "--unknown"], expected_exit=1)
        run("api", ["dotnet", "run", "--project", "tools/Cordis.ApiCheck", "-c", "Release", "--no-build", "--", "--check"])
        if options.dsh:
            target = json.loads((ROOT / "upstream.lock.json").read_text(encoding="utf-8"))["harness"]["commit"]
            upgrade = target == "639ed015397290b3745d163aafe02ffee4aa3f84"
            runner = "reference/run-upgrade-reference.mjs" if upgrade else "reference/run-reference.mjs"
            reference = run("dsh-core", ["node", "--experimental-transform-types", runner, *(["--established"] if upgrade else []), f"--dsh={options.dsh.resolve()}"])
            compare("dsh-core-differential", reference, jit)
            if upgrade:
                reference = run("dsh-upgrade", ["node", "--experimental-transform-types", runner, f"--dsh={options.dsh.resolve()}"])
                compare("dsh-upgrade-differential", reference, upgrade_jit)
            reference_temp = ROOT / "artifacts/reference-temp"
            reference_temp.mkdir(exist_ok=True)
            reference_env = dict(os.environ, CORDIS_DSH_REFERENCE=str(options.dsh.resolve()),
                                 TEMP=str(reference_temp), TMP=str(reference_temp), TMPDIR=str(reference_temp))
            if not upgrade:
                run("source-excerpt-probes", ["node", "--test", "reference/probes/effects.test.mjs",
                                               "reference/probes/acceptance-contracts.test.mjs"], env=reference_env)
            run("original-composition", ["node", "reference/node_modules/vitest/vitest.mjs", "run", *options.upstream_test, "--config", "reference/vitest-composition.config.mjs"], env=reference_env)
            source_reports = [(OUT / "original-composition-reference.json", options.dsh.resolve())]
            origin = options.origin or ROOT.parent / "upstream-cordis"
            if origin.is_dir():
                env = dict(os.environ, CORDIS_DSH_REFERENCE=str(options.dsh.resolve()), CORDIS_ORIGIN_REFERENCE=str(origin.resolve()))
                run("original-core-loader", ["node", "reference/node_modules/vitest/vitest.mjs", "run", "--config", "reference/vitest.config.mjs"], env=env)
                source_reports.append((OUT / "original-core-reference.json", origin.resolve()))
            elif options.origin:
                raise FileNotFoundError(origin)
            else:
                print("NOT RUN: original Core/Loader tests; supply --origin with the pinned test-source checkout.")
            record_source_results(source_reports)
        else:
            print("LOCAL-ONLY: DSH differential not requested; no upstream-conformance claim.")
        if options.aot:
            run("aot-publish", ["dotnet", "publish", "tests/Cordis.Conformance", "-c", "Release", "-r", rid,
                                "-p:PublishAot=true", "-p:NuGetLockFilePath=obj/aot.packages.lock.json", "-o", ROOT / "artifacts/aot"])
            native = run("aot-run", [ROOT / "artifacts/aot" / ("Cordis.Conformance.exe" if os.name == "nt" else "Cordis.Conformance"), "--require-aot"])
            compare("jit-aot-differential", jit, native)
            upgrade_native = run("upgrade-aot-run", [ROOT / "artifacts/aot" / ("Cordis.Conformance.exe" if os.name == "nt" else "Cordis.Conformance"), "--upgrade", "--require-aot"])
            compare("upgrade-jit-aot-differential", upgrade_jit, upgrade_native)
        if options.package:
            version = ET.parse(ROOT / "Directory.Build.props").findtext(".//Version")
            assert version
            if options.package_output:
                package_dir = options.package_output.resolve()
                package_dir.mkdir(parents=True, exist_ok=True)
                assert not any(package_dir.iterdir()), f"Package output must be empty: {package_dir}"
            else:
                package_dir = Path(tempfile.mkdtemp(prefix="packages-", dir=ROOT / "artifacts"))
            pack_command = ["dotnet", "pack", "Cordis.slnx", "-c", "Release", "--no-build", "-o", package_dir]
            if not git_checkout():
                # Exported source has no Git metadata; retain the verified manifest's
                # native checkpoint commit in the package repository metadata.
                pack_command.append("-p:RepositoryCommit=" + archive_manifest()["commit"])
            run("pack", pack_command)
            inspected = inspect_packages(package_dir, version)
            run("package-symbols-and-debug-consumer", [sys.executable, "scripts/package_inspection.py",
                "--directory", package_dir, "--version", version, "--symbols", "--debug-consumer", "--source-root", ROOT])
            steps.append({"name": "package-content-boundaries", "exitCode": 0, "packages": inspected})
            consume_packages(rid, package_dir, version, options.aot)
            deployment_dir = Path(tempfile.mkdtemp(prefix="clr-deployment-", dir=ROOT / "artifacts"))
            run("clr-deployment", [sys.executable, "scripts/verify-clr-deployment.py", "--dotnet", shutil.which("dotnet"),
                "--rid", rid, "--packages", package_dir, "--output", deployment_dir])
        if git_checkout():
            run("whitespace", ["git", "diff", "--check"])
        else:
            steps.append({"name": "archive-source-inventory", "exitCode": 0, "files": len(initial_hashes)})
        assert initial_hashes == source_hashes(), "Source tree changed during verification; rerun against a frozen checkpoint"
        status = "requested-checks-passed"
    finally:
        if git_checkout():
            commit = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
            dirty = subprocess.run(["git", "status", "--porcelain"], cwd=ROOT, capture_output=True, text=True).stdout
        else:
            manifest = archive_manifest()
            commit = manifest["commit"]
            recorded = manifest["files"] | manifest.get("generatedFiles", {})
            dirty = "source archive; " + ("unchanged" if all(recorded[name] == digest for name, digest in source_hashes().items()) else "modified")
        hashes = source_hashes()
        (OUT / "verification.json").write_text(json.dumps({"status": status, "commit": commit, "workingTree": dirty,
            "platform": platform.platform(), "rid": rid, "upstreamInventoryClosure": "see docs/upstream-tests.json; passing gates do not imply inventory closure",
            "steps": steps, "sourceUnchanged": initial_hashes == hashes, "initialSourceSha256": initial_hashes,
            "sourceSha256": hashes}, indent=2) + "\n", encoding="utf-8")
    print(status)


if __name__ == "__main__":
    main()
