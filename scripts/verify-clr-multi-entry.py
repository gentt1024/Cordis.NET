"""Consume a local Cordis package batch through an independently packed multi-entry author bundle."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--packages", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--node", default="node")
    parser.add_argument("--tsc", type=Path, help="Path to the existing TypeScript compiler CLI.")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    tsc = options.tsc or root / "clients/modules/node_modules/typescript/bin/tsc"
    if not tsc.is_file():
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
