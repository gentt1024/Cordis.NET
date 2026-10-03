"""Run actual external-plugin CLR deployments, including self-contained single-file JIT."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import shutil
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--rid", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--packages", type=Path,
                        help="Run the same fixtures as isolated consumers of a local package batch.")
    parser.add_argument("--allow-old-il3000", action="store_true",
                        help="Permit the known pre-fix Location warning so its runtime failure can be reproduced.")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    fixtures = root / "tests/fixtures/ClrDeployment"
    options.output.mkdir(parents=True, exist_ok=True)
    if options.packages:
        # Copy only authoring inputs. NuGet must import the shipped buildTransitive
        # target; the repository's ProjectReference import cannot help this consumer.
        source = options.output / "consumer-source"
        source.mkdir(exist_ok=False)
        for name in ("global.json", "Directory.Build.props"):
            shutil.copyfile(root / name, source / name)
        # Stop MSBuild from walking up into the checkout when output is under artifacts.
        for name in ("Directory.Build.targets", "Directory.Packages.props"):
            (source / name).write_text("<Project />\n", encoding="utf-8")
        copied = source / "fixtures"
        copied.mkdir()
        shutil.copyfile(fixtures / "HostProgram.cs", copied / "HostProgram.cs")
        version = ET.parse(root / "Directory.Build.props").findtext(".//Version")
        for name in ("Host", "AspNetHost", "Plugin", "AspNetPlugin"):
            folder = copied / name
            folder.mkdir()
            for file in (fixtures / name).glob("*.cs"):
                shutil.copyfile(file, folder / file.name)
            for file in (fixtures / name).glob("*.csproj"):
                tree = ET.parse(file)
                for group in tree.getroot().findall("ItemGroup"):
                    for reference in list(group.findall("ProjectReference")):
                        group.remove(reference)
                        ET.SubElement(group, "PackageReference", Include="Cordis.NET.Clr", Version=version)
                tree.write(folder / file.name, encoding="utf-8")
        config = ET.Element("configuration")
        sources = ET.SubElement(config, "packageSources")
        ET.SubElement(sources, "clear")
        ET.SubElement(sources, "add", key="local", value=str(options.packages.resolve()))
        ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        ET.ElementTree(config).write(source / "NuGet.Config", encoding="utf-8")
        fixtures = copied
    outcomes = []
    environment = dict(os.environ, DOTNET_ROOT=str(Path(options.dotnet).resolve().parent))

    def run(label, command):
        result = subprocess.run([str(part) for part in command], cwd=root, env=environment,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                encoding="utf-8", errors="replace")
        (options.output / (label + ".log")).write_text(result.stdout, encoding="utf-8")
        outcomes.append({"label": label, "command": [str(part) for part in command],
                         "exitCode": result.returncode})
        print(label, "exit", result.returncode, flush=True)
        return result.returncode

    def publish(label, project, self_contained=False, single=False):
        output = options.output / label
        command = [options.dotnet, "publish", fixtures / project, "-c", "Release", "-r", options.rid,
                   "--self-contained", str(self_contained).lower(), "-p:PublishSingleFile=" + str(single).lower(),
                   "-p:PublishTrimmed=false", "-p:PublishAot=false",
                   "-p:NuGetLockFilePath=obj/clr-deployment.packages.lock.json", "-o", output]
        if options.allow_old_il3000:
            command.append("-p:WarningsNotAsErrors=IL3000")
        if options.packages:
            command.append("-p:RestorePackagesPath=" + str((options.output / "cache").resolve()))
        return output, run(label + "-publish", command)

    basic, basic_error = publish("plugin", "Plugin/Plugin.csproj")
    aspnet, aspnet_error = publish("aspnet-plugin", "AspNetPlugin/AspNetPlugin.csproj")
    for label, project, bundle, error, kind, self_contained, single in [
        ("folder-host", "Host/Host.csproj", basic, basic_error, "json", False, False),
        ("single-host", "Host/Host.csproj", basic, basic_error, "json", True, True),
        ("aspnet-host", "AspNetHost/AspNetHost.csproj", aspnet, aspnet_error, "http", False, False),
        ("aspnet-scd-folder", "AspNetHost/AspNetHost.csproj", aspnet, aspnet_error, "http", True, False),
        ("aspnet-scd-single", "AspNetHost/AspNetHost.csproj", aspnet, aspnet_error, "http", True, True),
    ]:
        host, host_error = publish(label, project, self_contained, single)
        if error or host_error:
            continue
        name = "AspNetHost" if kind == "http" else "Host"
        if self_contained:
            command = [host / (name + (".exe" if options.rid.startswith("win-") else ""))]
        else:
            command = [options.dotnet, host / (name + ".dll")]
        run(label + "-run", [*command, bundle, kind, "single" if single else "folder"])
    (options.output / "results.json").write_text(json.dumps(outcomes, indent=2) + "\n", encoding="utf-8")
    return int(any(outcome["exitCode"] != 0 for outcome in outcomes))


if __name__ == "__main__":
    raise SystemExit(main())
