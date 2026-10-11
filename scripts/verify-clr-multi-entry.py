"""Consume a local Cordis package batch through an independently packed multi-entry author bundle."""
import argparse
import json
import glob
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import sys
import xml.etree.ElementTree as ET


def verify_multi_assembly(options, root, directory, version, project, run):
    fixture = root / "tests/fixtures/ClrMultiAssembly"
    reference = f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>'

    def author(name, assembly, source, constants="", dependency=None):
        folder = directory / name
        references = reference if source not in ("Private.cs", "Bridge.cs") else ""
        if dependency:
            references += f'<ProjectReference Include="{dependency}"/>'
        path = project(folder, f'<AssemblyName>{assembly}</AssemblyName>' +
            f'<DefineConstants>{constants}</DefineConstants>', references)
        shutil.copyfile(fixture / source, folder / source)
        return path

    private_first = author("PrivateFirst", "BundlePrivate", "Private.cs")
    private_second = author("PrivateSecond", "BundlePrivate", "Private.cs", "SECOND")
    bridge = author("Bridge", "BundleBridge", "Bridge.cs", dependency=private_first)
    managed = [
        author("PlainA", "BundleA", "ManagedEntry.cs"),
        author("PrivateA", "BundleA", "ManagedEntry.cs", "PRIVATE", private_first),
        author("FirstB", "BundleB", "ManagedEntry.cs", "PRIVATE", private_first),
        author("SecondB", "BundleB", "ManagedEntry.cs", "PRIVATE", private_second),
        author("DelayedA", "BundleA", "ManagedEntry.cs", "PRIVATE;BRIDGE;DELAYED", bridge),
        author("BridgeB", "BundleB", "ManagedEntry.cs", "PRIVATE;BRIDGE", bridge),
    ]
    if os.name == "nt":
        constants = "WINDOWS"
        native_name = "CordisBundleNative.dll"
        system = Path(os.environ["SystemRoot"]) / "System32"
        native_files = [system / "version.dll", system / "winmm.dll"]
        native_bytes = [path.read_bytes() for path in native_files]
    elif sys.platform.startswith("linux"):
        constants = ""
        native_name = "libCordisBundleNative.so"
        matches = sorted(glob.glob("/lib/*/libm.so.6") + glob.glob("/usr/lib/*/libm.so.6") +
                         glob.glob("/lib/libm.so.6") + glob.glob("/usr/lib/libm.so.6"))
        if not matches:
            raise RuntimeError("The private native CLR fixture requires an installed libm.so.6.")
        native_bytes = [Path(matches[0]).read_bytes()]
        # ELF loading ignores trailing data; both variants must pass a real cos invocation.
        native_bytes.append(native_bytes[0] + b"Cordis native binary conflict fixture\n")
    else:
        raise RuntimeError("The private native CLR fixture supports Windows and Linux.")
    native = [
        author("NativeFirst", "NativeFirst", "NativeEntry.cs", constants),
        author("NativeSecond", "NativeSecond", "NativeEntry.cs", constants + ";SECOND"),
        author("NativeDelayed", "NativeFirst", "NativeEntry.cs", constants + ";DELAYED"),
    ]
    outputs = {}
    for path in [*managed, *native]:
        output = directory / (path.stem + "-output")
        run("multi-assembly-" + path.stem + "-build", [options.dotnet, "build", path, "-c", "Release", "-o", output])
        outputs[path.stem] = output
    bundles = directory / "multi-assembly-bundles"

    def copy_entry(bundle, subdirectory, source, assembly, private=True, native_index=None):
        target = bundles / bundle / subdirectory
        target.mkdir(parents=True)
        for extension in (".dll", ".deps.json"):
            shutil.copyfile(outputs[source] / (assembly + extension), target / (assembly + extension))
        private_file = outputs[source] / "BundlePrivate.dll"
        if private and private_file.exists():
            shutil.copyfile(private_file, target / private_file.name)
        bridge_file = outputs[source] / "BundleBridge.dll"
        if bridge_file.exists():
            shutil.copyfile(bridge_file, target / bridge_file.name)
        if native_index is not None:
            (target / native_name).write_bytes(native_bytes[native_index])
            deps_path = target / (assembly + ".deps.json")
            deps = json.loads(deps_path.read_text(encoding="utf-8"))
            libraries = deps["targets"][deps["runtimeTarget"]["name"]]
            entry = next(key for key in libraries if key.startswith(assembly + "/"))
            libraries[entry]["native"] = {native_name: {}}
            deps_path.write_text(json.dumps(deps), encoding="utf-8")

    for bundle, first, second in [("order", "PlainA", "FirstB"), ("same", "PrivateA", "FirstB"),
                                  ("conflict", "PrivateA", "SecondB"), ("missing", "PlainA", "FirstB")]:
        copy_entry(bundle, "a", first, "BundleA")
        copy_entry(bundle, "b", second, "BundleB", private=bundle != "missing")
    copy_entry("native-same", "a", "NativeFirst", "NativeFirst", native_index=0)
    copy_entry("native-same", "b", "NativeFirst", "NativeFirst", native_index=0)
    copy_entry("native-conflict", "a", "NativeFirst", "NativeFirst", native_index=0)
    copy_entry("native-conflict", "b", "NativeSecond", "NativeSecond", native_index=1)
    copy_entry("late-managed", "a", "DelayedA", "BundleA")
    copy_entry("late-managed", "b", "BridgeB", "BundleB")
    shutil.copyfile(outputs["SecondB"] / "BundlePrivate.dll", bundles / "late-managed/b/BundlePrivate.dll")
    copy_entry("late-native", "a", "NativeDelayed", "NativeFirst", native_index=0)
    copy_entry("late-native", "b", "NativeSecond", "NativeSecond", native_index=1)
    consumer = directory / "MultiAssemblyConsumer"
    consumer_project = project(consumer, '<OutputType>Exe</OutputType>', reference)
    shutil.copyfile(fixture / "Consumer.cs", consumer / "Program.cs")
    run("multi-assembly-consumer-run", [options.dotnet, "run", "--project", consumer_project,
        "-c", "Release", "--", bundles])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--node", default="node")
    parser.add_argument("--tsc", type=Path, help="Path to the existing TypeScript compiler CLI.")
    parser.add_argument("--multi-assembly-only", action="store_true",
                        help="Run only the nested CLR assembly/dependency package consumer.")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    tsc = options.tsc or root / "clients/modules/node_modules/typescript/bin/tsc"
    if not options.multi_assembly_only and not tsc.is_file():
        parser.error("TypeScript compiler not found; install the client module dependencies or provide --tsc.")
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
    directory = Path(tempfile.mkdtemp(prefix="cordis-multi-entry-consumer-"))
    author_packages = directory / "author-packages"
    author_packages.mkdir()
    options.output.mkdir(parents=True, exist_ok=True)
    outcomes = []
    (directory / "global.json").write_bytes((root / "global.json").read_bytes())
    (directory / "NuGet.Config").write_text(
        '<configuration><packageSources><clear/><add key="local" value="' +
        str(options.packages.resolve()) + '"/><add key="author" value="' + str(author_packages) +
        '"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>' +
        '</packageSources></configuration>', encoding="utf-8")

    def run(label, arguments):
        result = subprocess.run([str(value) for value in arguments], cwd=directory,
                                env=dict(os.environ, DOTNET_CLI_UI_LANGUAGE="en"),
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                encoding="utf-8", errors="replace")
        (options.output / (label + ".log")).write_text(result.stdout, encoding="utf-8")
        outcomes.append({"label": label, "exitCode": result.returncode})
        print(label, "exit", result.returncode, flush=True)
        if result.returncode:
            print(result.stdout[-8000:], flush=True)
            raise RuntimeError(label + " failed")

    def project(folder, output, references):
        folder.mkdir()
        path = folder / (folder.name + ".csproj")
        path.write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework>' +
            '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>' +
            '<RestorePackagesPath>' + str(directory / "cache") + '</RestorePackagesPath>' + output +
            '</PropertyGroup><ItemGroup>' + references + '</ItemGroup></Project>', encoding="utf-8")
        return path

    try:
        verify_multi_assembly(options, root, directory, version, project, run)
        if options.multi_assembly_only:
            return 0
        author = directory / "Author"
        author_project = project(author,
            '<AssemblyName>IndependentMultiEntry</AssemblyName><PackageId>IndependentMultiEntry</PackageId><Version>1.0.0-alpha</Version>',
            f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>')
        shutil.copyfile(root / "tests/fixtures/ClrMultiEntry/Plugin.cs", author / "Plugin.cs")
        metadata = {
            "assembly": "IndependentMultiEntry.dll", "entryType": "IndependentMultiEntry.First",
            "exports": {"./second": "IndependentMultiEntry.Second"},
            "patches": [{"insert": [
                {"id": "one", "name": "nuget:independentmultientry", "config": 7},
                {"id": "two", "name": "nuget:independentmultientry/second", "config": 11}]}],
        }
        (author / "cordis.plugin.json").write_text(json.dumps(metadata), encoding="utf-8")
        tree = ET.parse(author_project)
        ET.SubElement(tree.getroot().find("ItemGroup"), "None", Include="cordis.plugin.json", Pack="true", PackagePath="/")
        tree.write(author_project, encoding="utf-8")
        bundles = []
        for major in (1, 2, 3):
            package_version = f"{major}.0.0-alpha"
            run(f"author-{major}-pack", [options.dotnet, "pack", author_project, "-c", "Release", "-o", author_packages,
                "-p:Version=" + package_version])
            deployment = directory / f"Deployment{major}"
            deployment_project = project(deployment, '<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>',
                f'<PackageReference Include="IndependentMultiEntry" Version="{package_version}"/>')
            run(f"author-{major}-package-restore", [options.dotnet, "restore", deployment_project,
                "--configfile", directory / "NuGet.Config"])
            bundle = directory / f"bundle{major}"
            run(f"author-{major}-package-publish", [options.dotnet, "publish", deployment_project, "--no-restore", "-c", "Release", "-o", bundle])
            (bundle / "package.json").write_text('{"name":"multi-entry","version":"' + package_version + '"}\n', encoding="utf-8")
            bundles.append(bundle)
        consumer = directory / "Consumer"
        consumer_project = project(consumer, '<OutputType>Exe</OutputType>',
            f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>' +
            f'<PackageReference Include="Cordis.NET.Extensions" Version="{version}"/>' +
            f'<PackageReference Include="Cordis.NET.AspNetCore" Version="{version}"/>' +
            '<FrameworkReference Include="Microsoft.AspNetCore.App"/>')
        shutil.copyfile(root / "tests/fixtures/ClrMultiEntry/Consumer.cs", consumer / "Program.cs")
        run("consumer-run", [options.dotnet, "run", "--project", consumer_project, "-c", "Release", "--", *bundles,
            author_packages, options.packages.resolve(), options.dotnet, options.node,
            root / "scripts/multi-entry-client-consumer.mjs", tsc.resolve()])
    finally:
        (options.output / "results.json").write_text(json.dumps(outcomes, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
