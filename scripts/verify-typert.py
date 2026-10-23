"""Verify a generated Remote author NuGet package with isolated JIT/AOT and TypeScript consumers."""
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

from package_inspection import inspect_restored_packages


def hashes(directory):
    return {path.relative_to(directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*")) if path.is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--package-version", help="Use the isolated candidate package version.")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--runtime", type=Path, help="Consume an existing browser runtime without rebuilding it")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--aot", action="store_true")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    runtime = options.runtime.resolve() if options.runtime else root / "artifacts/client-modules"
    if options.runtime and not (runtime / "client.mjs").is_file():
        parser.error("The supplied runtime must contain client.mjs")
    options.output = options.output.resolve()
    if options.output.exists() and any(options.output.iterdir()):
        parser.error("Output must be empty; preserve prior evidence and choose a new directory")
    options.output.mkdir(parents=True, exist_ok=True)
    source = Path(tempfile.mkdtemp(prefix="cordis-remote-consumer-")).resolve()
    if source.is_relative_to(root):
        raise RuntimeError("The independent consumer source must be outside the checkout")
    (source / "global.json").write_bytes((root / "global.json").read_bytes())
    version = options.package_version or ET.parse(root / "Directory.Build.props").findtext(".//Version")
    author_feed = source / "author-feed"
    author_feed.mkdir()
    config = ET.Element("configuration")
    feeds = ET.SubElement(config, "packageSources")
    ET.SubElement(feeds, "clear")
    for key, value in (("local", str(options.packages.resolve())), ("author", str(author_feed)), ("nuget.org", "https://api.nuget.org/v3/index.json")):
        ET.SubElement(feeds, "add", key=key, value=value)
    ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
    mapping = ET.SubElement(config, "packageSourceMapping")
    for key, pattern in (("local", "Cordis.NET.*"), ("author", "IndependentRemote"), ("nuget.org", "*")):
        ET.SubElement(ET.SubElement(mapping, "packageSource", key=key), "package", pattern=pattern)
    ET.ElementTree(config).write(source / "NuGet.Config", encoding="utf-8")
    env = dict(os.environ, DOTNET_ROOT=str(Path(shutil.which(options.dotnet) or options.dotnet).resolve().parent),
               NUGET_PACKAGES=str(source / "cache"), NUGET_FALLBACK_PACKAGES="", DOTNET_CLI_UI_LANGUAGE="en")
    env["PATH"] = env["DOTNET_ROOT"] + os.pathsep + env["PATH"]
    steps = []
    runtime_hashes = hashes(runtime) if options.runtime else None
    restored_packages = {}
    status = "failed"

    def run(label, command, cwd=source, expected=0):
        process = subprocess.run([str(part) for part in command], cwd=cwd, env=env, capture_output=True)
        (options.output / (label + ".log")).write_bytes(process.stdout + process.stderr)
        steps.append({"name": label, "exitCode": process.returncode, "expectedExitCode": expected})
        print(label, "exit", process.returncode, flush=True)
        if (expected == 0 and process.returncode != 0) or (expected != 0 and process.returncode == 0):
            print((process.stdout + process.stderr).decode("utf-8", "replace")[-8000:])
            raise RuntimeError(label + " failed")
        return process.stdout + process.stderr

    def project(name, properties, references):
        directory = source / name
        directory.mkdir()
        path = directory / (name + ".csproj")
        path.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>'
                        '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>' +
                        '<RestorePackagesPath>' + str(source / 'cache') + '</RestorePackagesPath>' +
                        properties + '</PropertyGroup><ItemGroup>' + references + '</ItemGroup></Project>', encoding="utf-8")
        return directory, path

    try:
        author, author_project = project("Author", "<PackageId>IndependentRemote</PackageId><Version>1.0.0-alpha</Version>",
            f'<PackageReference Include="Cordis.NET.Composition" Version="{version}"/>')
        shutil.copyfile(root / "tests/fixtures/TypertConsumer/Author.cs", author / "Author.cs")
        shutil.copyfile(root / "tests/fixtures/TypertConsumer/TupleContract.cs", author / "TupleContract.cs")
        run("author-pack", [options.dotnet, "pack", author_project, "-c", "Release", "-o", author_feed])
        # A compiler failure guards the source contract, using the shipped analyzer.
        (author / "Invalid.cs").write_text('using Cordis.Composition;\n'
            '[RemoteService("invalid", typeof(IndependentRemote.RemoteJson))] public partial class Invalid { [RemoteMethod] public int Call() => 1; }\n', encoding="utf-8")
        rejected = run("invalid-author-rejected", [options.dotnet, "build", author_project, "-c", "Release"], expected=1)
        assert b"CORDISREMOTE001" in rejected, "Invalid source failed for an unrelated reason"
        (author / "Invalid.cs").unlink()
        consumer, consumer_project = project("Consumer", "<OutputType>Exe</OutputType>",
            f'<PackageReference Include="IndependentRemote" Version="1.0.0-alpha"/><PackageReference Include="Cordis.NET.AspNetCore" Version="{version}"/>'
            '<FrameworkReference Include="Microsoft.AspNetCore.App"/>')
        shutil.copyfile(root / "tests/fixtures/TypertConsumer/Consumer.cs", consumer / "Program.cs")
        shutil.copyfile(root / "tests/fixtures/TypertConsumer/LifetimeCases.cs", consumer / "LifetimeCases.cs")
        generated = options.output / "generated"
        run("consumer-jit", [options.dotnet, "run", "--project", consumer_project, "-c", "Release", "--", generated])
        if options.runtime is None:
            run("client-runtime-build", ["node", root / "scripts/build-client-modules.mjs", runtime], root)
            runtime_hashes = hashes(runtime)
        readiness = queue.Queue()
        process = subprocess.Popen([options.dotnet, str(consumer / "bin/Release/net10.0/Consumer.dll"), str(generated), "--serve"],
            cwd=source, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        threading.Thread(target=lambda: readiness.put(process.stdout.readline()), daemon=True).start()
        try:
            url = readiness.get(timeout=45).strip()
            if not url.startswith("http://127.0.0.1:"):
                raise RuntimeError("Remote HTTP host did not start: " + url)
            run("typescript-and-http-client", ["node", root / "scripts/typert-client-consumer.mjs", url + "/remote", generated, runtime], root)
        finally:
            process.kill()
            process.wait(timeout=30)
            (options.output / "http-host.log").write_text(process.stderr.read(), encoding="utf-8")
        if options.aot:
            native = source / "native"
            run("consumer-aot-publish", [options.dotnet, "publish", consumer_project, "-c", "Release", "-p:PublishAot=true", "-o", native])
            run("consumer-aot-run", [native / ("Consumer.exe" if os.name == "nt" else "Consumer"), options.output / "generated-aot"])
        for directory in (author, consumer):
            assets = json.loads((directory / "obj/project.assets.json").read_text(encoding="utf-8"))
            if any(value.get("type") == "project" for value in assets["libraries"].values()):
                raise RuntimeError("The package consumer resolved a project dependency")
            if {Path(path).resolve() for path in assets["packageFolders"]} != {source / "cache"}:
                raise RuntimeError("The package consumer used a shared or fallback cache")
        restored_packages = inspect_restored_packages(source / "cache", options.packages.resolve())
        if hashes(runtime) != runtime_hashes:
            raise RuntimeError("The consumed browser runtime changed during verification")
        status = "requested-checks-passed"
    finally:
        (options.output / "results.json").write_text(json.dumps({"status": status, "steps": steps, "independentSourceDirectory": str(source),
            "packageReferencesOnly": True, "aotRequested": options.aot,
            "runtimeDirectory": str(runtime), "runtimeSupplied": options.runtime is not None,
            "runtimeSha256": runtime_hashes, "restoredPackages": restored_packages}, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
