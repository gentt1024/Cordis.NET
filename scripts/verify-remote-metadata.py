"""Verify Remote metadata from an isolated NuGet consumer and its installed TypeScript artifact."""
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
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = options.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("Output must be empty; preserve previous evidence and select a new directory.")
    output.mkdir(parents=True, exist_ok=True)
    version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
    assert version
    source = Path(tempfile.mkdtemp(prefix="cordis-remote-metadata-consumer-"))
    assert not source.is_relative_to(root)
    shutil.copyfile(root / "global.json", source / "global.json")
    shutil.copyfile(root / "tests/fixtures/RemoteMetadataClient/Program.cs", source / "Program.cs")
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in {
        "TargetFramework": "net10.0", "OutputType": "Exe", "ImplicitUsings": "enable",
        "Nullable": "enable", "TreatWarningsAsErrors": "true",
        "JsonSerializerIsReflectionEnabledByDefault": "false",
    }.items():
        ET.SubElement(properties, name).text = value
    ET.SubElement(ET.SubElement(project, "ItemGroup"), "PackageReference",
                  Include="Cordis.NET.Composition", Version=version)
    project_path = source / "Consumer.csproj"
    ET.ElementTree(project).write(project_path, encoding="utf-8")
    config = ET.Element("configuration")
    feeds = ET.SubElement(config, "packageSources")
    ET.SubElement(feeds, "clear")
    ET.SubElement(feeds, "add", key="candidate", value=str(options.packages.resolve()))
    ET.SubElement(feeds, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
    mapping = ET.SubElement(config, "packageSourceMapping")
    for name, pattern in (("candidate", "Cordis.NET.*"), ("nuget.org", "*")):
        ET.SubElement(ET.SubElement(mapping, "packageSource", key=name), "package", pattern=pattern)
    ET.ElementTree(config).write(source / "NuGet.Config", encoding="utf-8")
    dotnet = Path(shutil.which(options.dotnet) or options.dotnet).resolve()
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), NUGET_PACKAGES=str(source / "cache"),
               DOTNET_CLI_UI_LANGUAGE="en")
    env["PATH"] = str(dotnet.parent) + os.pathsep + env.get("PATH", "")
    result = {"status": "failed", "packageVersion": version, "packageReferencesOnly": True,
              "independentSourceDirectory": str(source), "steps": []}

    def run(name, arguments):
        process = subprocess.run([str(part) for part in arguments], cwd=source, env=env,
                                 stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        (output / (name + ".log")).write_bytes(process.stdout)
        result["steps"].append({"name": name, "exitCode": process.returncode})
        print(name, "exit", process.returncode, flush=True)
        if process.returncode:
            print(process.stdout.decode("utf-8", "replace")[-8000:], flush=True)
            raise RuntimeError(name + " failed")

    try:
        artifacts = output / "generated"
        run("restore", [dotnet, "restore", project_path])
        run("native-metadata-artifacts", [dotnet, "run", "--project", project_path, "-c", "Release",
                                          "--no-restore", "--", artifacts])
        run("installed-typescript-metadata", ["node", root / "scripts/verify-remote-metadata.mjs",
                                               artifacts, root / "clients/modules/package.json"])
        result["status"] = "passed"
    finally:
        (output / "results.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
