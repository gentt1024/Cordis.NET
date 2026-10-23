"""Consume a local Cordis package batch through an independently packed multi-entry author bundle."""
import argparse
import json
import glob
import hashlib
import os
import platform
from pathlib import Path
import shutil
import subprocess
import tempfile
import sys
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape, quoteattr

from package_inspection import inspect_restored_packages


def package_feed_snapshot(directory):
    return {path.relative_to(directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*")) if path.is_file()}


def seal_dependency_feed(cache, batch, feed):
    restored = inspect_restored_packages(cache, batch)
    feed.mkdir()
    # Inner Prepare owns another cache. Give it only copied third-party
    # archives and the exact Cordis batch, without a public-source fallback.
    archives = [path for path in cache.rglob("*.nupkg")
                if not path.name.casefold().startswith(("cordis.net.", "independentmultientry."))]
    archives.extend(batch.resolve().glob("*.nupkg"))
    copied = {}
    for source in sorted(archives):
        name = source.name.casefold()
        digest = hashlib.sha256(source.read_bytes()).hexdigest()
        if name in copied:
            if copied[name] != digest:
                raise ValueError("Conflicting archives in the sealed dependency feed: " + source.name)
            continue
        shutil.copyfile(source, feed / name)
        copied[name] = digest
    snapshot = package_feed_snapshot(feed)
    if snapshot != copied:
        raise ValueError("The dependency feed differs from the verified archive copies")
    if not restored.keys() <= snapshot.keys():
        raise ValueError("The sealed dependency feed omitted a restored Cordis archive")
    for name, digest in restored.items():
        if snapshot[name] != digest:
            raise ValueError("The sealed feed contains a different Cordis package: " + name)
    return {
        "meaning": "Inner Prepare/Publish uses only this unchanged local dependency feed and the independent author feed.",
        "restoredCordisSha256": restored, "packagesSha256": snapshot,
    }


def verify_multi_assembly(options, root, directory, version, project, run):
    fixture = root / "tests/fixtures/ClrMultiAssembly"
    reference = f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>'

    def author(name, assembly, source, constants="", dependency=None):
        folder = directory / name
        references = reference if source not in ("Private.cs", "Bridge.cs") else ""
        if dependency:
            references += f'<ProjectReference Include={quoteattr(str(dependency))}/>'
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
    parser.add_argument("--package-version", help="Use the isolated candidate package version.")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--node", default="node")
    parser.add_argument("--tsc", type=Path, help="Path to the existing TypeScript compiler CLI.")
    parser.add_argument("--multi-assembly-only", action="store_true",
                        help="Run only the nested CLR assembly/dependency package consumer.")
    parser.add_argument("--stable-publication-only", action="store_true",
                        help="Prepare and publish one real author package, then verify repeated loading and retained worker/native/resource lifetimes.")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    tsc = options.tsc or root / "clients/modules/node_modules/typescript/bin/tsc"
    if options.multi_assembly_only and options.stable_publication_only:
        parser.error("Choose only one focused consumer.")
    if not (options.multi_assembly_only or options.stable_publication_only) and not tsc.is_file():
        parser.error("TypeScript compiler not found; install the client module dependencies or provide --tsc.")
    if options.output.exists() and any(options.output.iterdir()):
        parser.error("Output must be empty. Preserve the previous evidence and select a new --output directory.")
    version = options.package_version or ET.parse(root / "Directory.Build.props").findtext(".//Version")
    directory = Path(tempfile.mkdtemp(prefix="cordis-multi-entry-consumer-"))
    author_packages = directory / "author-packages"
    author_packages.mkdir()
    options.output.mkdir(parents=True, exist_ok=True)
    outcomes = []
    dependency_feed = directory / "sealed-dependency-feed"
    sealed_packages = None
    sealed_authors = None
    (directory / "global.json").write_bytes((root / "global.json").read_bytes())
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    for key, value in (("local", str(options.packages.resolve())), ("author", str(author_packages)),
                       ("nuget.org", "https://api.nuget.org/v3/index.json")):
        ET.SubElement(sources, "add", key=key, value=value)
    ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
    mapping = ET.SubElement(config, "packageSourceMapping")
    for key, pattern in (("local", "Cordis.NET.*"), ("author", "IndependentMultiEntry"), ("nuget.org", "*")):
        ET.SubElement(ET.SubElement(mapping, "packageSource", key=key), "package", pattern=pattern)
    ET.ElementTree(config).write(directory / "NuGet.Config", encoding="utf-8")

    def verify_package_origin():
        restored = inspect_restored_packages(directory / "cache", options.packages)
        (options.output / "package-origin.json").write_text(json.dumps(restored, indent=2) + "\n", encoding="utf-8")

    def verify_sealed_feed():
        if sealed_packages is not None:
            if package_feed_snapshot(dependency_feed) != sealed_packages:
                raise ValueError("The sealed dependency feed changed during the independent package consumer")
            if package_feed_snapshot(author_packages) != sealed_authors:
                raise ValueError("The independent author feed changed during Prepare/Publish")

    def run(label, arguments):
        verify_sealed_feed()
        try:
            result = subprocess.run([str(value) for value in arguments], cwd=directory,
                                    env=dict(os.environ, DOTNET_CLI_UI_LANGUAGE="en", NUGET_PACKAGES=str(directory / "cache"),
                                             NUGET_FALLBACK_PACKAGES=""),
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                    encoding="utf-8", errors="replace")
        finally:
            verify_sealed_feed()
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
            '<RestorePackagesPath>' + escape(str(directory / "cache")) + '</RestorePackagesPath>' + output +
            '</PropertyGroup><ItemGroup>' + references + '</ItemGroup></Project>', encoding="utf-8")
        return path

    try:
        if not options.stable_publication_only:
            verify_multi_assembly(options, root, directory, version, project, run)
        if options.multi_assembly_only:
            verify_package_origin()
            return 0
        author = directory / "Author"
        author_project = project(author,
            '<AssemblyName>IndependentMultiEntry</AssemblyName><PackageId>IndependentMultiEntry</PackageId><Version>1.0.0-alpha</Version>' +
            ('<DefineConstants>WINDOWS</DefineConstants>' if os.name == 'nt' else ''),
            f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>')
        shutil.copyfile(root / "tests/fixtures/ClrMultiEntry/Plugin.cs", author / "Plugin.cs")
        shutil.copyfile(root / "tests/fixtures/ClrMultiAssembly/NativeEntry.cs", author / "NativeEntry.cs")
        architecture = {'amd64': 'x64', 'x86_64': 'x64', 'aarch64': 'arm64', 'arm64': 'arm64'}[platform.machine().lower()]
        if os.name == 'nt':
            rid, native_name = 'win-' + architecture, 'CordisBundleNative.dll'
            native_source = Path(os.environ['SystemRoot']) / 'System32/version.dll'
        else:
            rid, native_name = 'linux-' + architecture, 'libCordisBundleNative.so'
            native_source = Path(sorted(glob.glob('/lib/*/libm.so.6') + glob.glob('/usr/lib/*/libm.so.6'))[0])
        shutil.copyfile(native_source, author / native_name)
        (author / 'retained-data.txt').write_text('published resource retained for worker\n', encoding='utf8')
        metadata = {
            "assembly": "IndependentMultiEntry.dll", "entryType": "IndependentMultiEntry.First",
            "exports": {"./second": "IndependentMultiEntry.Second", "./native": "IndependentBundle.NativeEntry"},
            "patches": [{"insert": [
                {"id": "one", "name": "nuget:independentmultientry", "config": 7},
                {"id": "two", "name": "nuget:independentmultientry/second", "config": 11}]}],
        }
        (author / "cordis.plugin.json").write_text(json.dumps(metadata), encoding="utf-8")
        tree = ET.parse(author_project)
        ET.SubElement(tree.getroot().find("ItemGroup"), "None", Include="cordis.plugin.json", Pack="true", PackagePath="/")
        ET.SubElement(tree.getroot().find("ItemGroup"), "None", Include=native_name, Pack="true", PackagePath=f"runtimes/{rid}/native/")
        ET.SubElement(tree.getroot().find("ItemGroup"), "None", Include="retained-data.txt", Pack="true",
            PackagePath="contentFiles/any/any/", PackageCopyToOutput="true")
        tree.write(author_project, encoding="utf-8")
        bundles = []
        for major in ((1,) if options.stable_publication_only else (1, 2, 3)):
            package_version = f"{major}.0.0-alpha"
            run(f"author-{major}-pack", [options.dotnet, "pack", author_project, "-c", "Release", "-o", author_packages,
                "-p:Version=" + package_version])
            if options.stable_publication_only:
                continue
            deployment = directory / f"Deployment{major}"
            deployment_project = project(deployment, '<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>',
                f'<PackageReference Include="IndependentMultiEntry" Version="{package_version}"/>')
            run(f"author-{major}-package-restore", [options.dotnet, "restore", deployment_project,
                "--configfile", directory / "NuGet.Config"])
            bundle = directory / f"bundle{major}"
            run(f"author-{major}-package-publish", [options.dotnet, "publish", deployment_project, "--no-restore", "-c", "Release", "-o", bundle])
            shutil.copyfile(bundle / f"Deployment{major}.deps.json", bundle / "IndependentMultiEntry.deps.json")
            (bundle / "package.json").write_text('{"name":"multi-entry","version":"' + package_version + '"}\n', encoding="utf-8")
            bundles.append(bundle)
        if not options.stable_publication_only:
            consumer = directory / "Consumer"
            consumer_project = project(consumer, '<OutputType>Exe</OutputType>',
                f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>' +
                f'<PackageReference Include="Cordis.NET.Extensions" Version="{version}"/>' +
                f'<PackageReference Include="Cordis.NET.AspNetCore" Version="{version}"/>' +
                '<FrameworkReference Include="Microsoft.AspNetCore.App"/>')
            shutil.copyfile(root / "tests/fixtures/ClrMultiEntry/Consumer.cs", consumer / "Program.cs")
            run("consumer-build", [options.dotnet, "build", consumer_project, "-c", "Release"])
        stable = directory / "StableConsumer"
        stable_project = project(stable, '<OutputType>Exe</OutputType>',
            f'<PackageReference Include="Cordis.NET.Clr" Version="{version}"/>')
        shutil.copyfile(root / "tests/fixtures/ClrMultiEntry/StableConsumer.cs", stable / "Program.cs")
        run("stable-consumer-build", [options.dotnet, "build", stable_project, "-c", "Release"])
        feed_receipt = seal_dependency_feed(directory / "cache", options.packages, dependency_feed)
        sealed_packages = feed_receipt["packagesSha256"]
        sealed_authors = package_feed_snapshot(author_packages)
        feed_receipt["authorPackagesSha256"] = sealed_authors
        (options.output / "sealed-feed.json").write_text(json.dumps(feed_receipt, indent=2) + "\n", encoding="utf-8")
        if not options.stable_publication_only:
            run("consumer-run", [options.dotnet, "run", "--project", consumer_project, "-c", "Release",
                "--no-build", "--no-restore", "--", *bundles, author_packages, dependency_feed, options.dotnet, options.node,
                root / "scripts/multi-entry-client-consumer.mjs", tsc.resolve()])
        run("stable-publication-worker", [options.dotnet, "run", "--project", stable_project, "-c", "Release",
            "--no-build", "--no-restore", "--", author_packages, dependency_feed, options.dotnet])
        verify_sealed_feed()
        feed_receipt["packagesAfterSha256"] = package_feed_snapshot(dependency_feed)
        feed_receipt["authorPackagesAfterSha256"] = package_feed_snapshot(author_packages)
        (options.output / "sealed-feed.json").write_text(json.dumps(feed_receipt, indent=2) + "\n", encoding="utf-8")
        verify_package_origin()
    finally:
        (options.output / "results.json").write_text(json.dumps(outcomes, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
