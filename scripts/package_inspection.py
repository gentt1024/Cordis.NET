"""Checks the exact layout and metadata of Cordis.NET package artifacts."""

from __future__ import annotations

from pathlib import Path, PurePosixPath
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import posixpath
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET
import zipfile


PACKAGE_LAYOUTS = {
    "Cordis.NET.Core": ("lib/net10.0", "Cordis.Core", "T:Cordis.Context", set()),
    "Cordis.NET.Composition": ("lib/net10.0", "Cordis.Composition", "T:Cordis.Composition.Loader", {"Cordis.NET.Core"}),
    "Cordis.NET.Extensions": ("lib/net10.0", "Cordis.Extensions", "T:Cordis.Extensions.TimerService", {"Cordis.NET.Core", "Cordis.NET.Composition"}),
    "Cordis.NET.Clr": ("lib/net10.0", "Cordis.Clr", "T:Cordis.Clr.ClrModuleResolver", {"Cordis.NET.Composition"}),
    "Cordis.NET.Hosting": ("lib/net10.0", "Cordis.Hosting", "T:Cordis.Hosting.CordisHostExtensions", {"Cordis.NET.Core"}),
    "Cordis.NET.AspNetCore": ("lib/net10.0", "Cordis.AspNetCore", "T:Cordis.AspNetCore.CordisManagement", {"Cordis.NET.Extensions"}),
    "Cordis.NET.JavaScript": ("lib/net10.0", "Cordis.JavaScript", "T:Cordis.JavaScript.JintExpressionEvaluator", {"Cordis.NET.Composition"}),
    "Cordis.NET.Tool": ("tools/net10.0/any", "Cordis.Cli", None, set()),
    "Cordis.Example.Greeting": ("lib/net10.0", "Greeting.Plugin", "T:Cordis.Example.Greeting.GreetingModule", {"Cordis.NET.Composition"}),
}

PUBLISHABLE_PACKAGE_IDS = tuple(identity for identity in PACKAGE_LAYOUTS if identity.startswith("Cordis.NET."))


def inspect_restored_packages(cache: Path, batch: Path) -> dict[str, str]:
    """Require every restored Cordis archive to match the selected package batch."""
    expected = {path.name.casefold(): path for path in batch.glob("*.nupkg")}
    restored = sorted(path for path in cache.rglob("*.nupkg")
                      if path.name.casefold().startswith("cordis."))
    if not restored:
        raise ValueError("No restored Cordis packages found in the isolated cache")
    result = {}
    for path in restored:
        name = path.name.casefold()
        if name not in expected:
            raise ValueError(f"Restored Cordis package is absent from the selected batch: {path.name}")
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if digest != hashlib.sha256(expected[name].read_bytes()).hexdigest():
            raise ValueError(f"Restored Cordis package differs from the selected batch: {path.name}")
        result[name] = digest
    return result


def prepare_package_consumer(directory: Path, root: Path, batch: Path, dotnet: str) -> dict[str, str]:
    """Fence an external consumer from ancestor builds and developer package caches."""
    directory = directory.resolve()
    if directory.is_relative_to(root.resolve()):
        raise ValueError("The package consumer must be outside the repository")
    props = ET.Element("Project")
    ET.SubElement(ET.SubElement(props, "PropertyGroup"), "RestorePackagesPath").text = str(directory / "cache")
    ET.ElementTree(props).write(directory / "Directory.Build.props", encoding="utf-8")
    (directory / "Directory.Build.targets").write_text("<Project />\n", encoding="utf-8")
    shutil.copyfile(root / "global.json", directory / "global.json")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="local", value=str(batch.resolve()))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
    mapping = ET.SubElement(config, "packageSourceMapping")
    for name, pattern in (("local", "Cordis.*"), ("nuget.org", "*")):
        ET.SubElement(ET.SubElement(mapping, "packageSource", key=name), "package", pattern=pattern)
    ET.ElementTree(config).write(directory / "NuGet.Config", encoding="utf-8")
    executable = Path(shutil.which(dotnet) or dotnet).resolve()
    env = dict(os.environ, DOTNET_ROOT=str(executable.parent), NUGET_PACKAGES=str(directory / "cache"),
               NUGET_FALLBACK_PACKAGES="", DOTNET_CLI_UI_LANGUAGE="en")
    env["PATH"] = str(executable.parent) + os.pathsep + env.get("PATH", "")
    return env


def inspect_package_consumer(project: Path, cache: Path, batch: Path, version: str) -> dict:
    """Bind actual restored package inputs, rather than the intended project text."""
    assets = json.loads((project.parent / "obj/project.assets.json").read_text(encoding="utf-8-sig"))
    if any(library.get("type") == "project" for library in assets["libraries"].values()):
        raise ValueError("The package consumer resolved a project dependency")
    if {Path(folder).resolve() for folder in assets["packageFolders"]} != {cache.resolve()}:
        raise ValueError("The package consumer used a shared or fallback package cache")
    sources = assets["project"]["restore"]["sources"]
    if not any("://" not in source and Path(source).resolve() == batch.resolve() for source in sources):
        raise ValueError("The package consumer did not restore with the selected batch source")
    cordis = [name for name in assets["libraries"] if name.casefold().startswith("cordis.")]
    if not cordis or any(name.rsplit("/", 1)[1] != version or
                         assets["libraries"][name].get("type") != "package" for name in cordis):
        raise ValueError("The package consumer did not resolve the selected Cordis version")
    restored = inspect_restored_packages(cache, batch)
    for name in cordis:
        identity, selected_version = name.rsplit("/", 1)
        if (identity + "." + selected_version + ".nupkg").casefold() not in restored:
            raise ValueError("The package consumer lacks its restored Cordis archive: " + name)
    return {"packageFolders": list(assets["packageFolders"]), "resolvedPackages": cordis,
            "restoredArchiveSha256": restored, "packageReferencesOnly": True}


def inspect_readme_links(readme: str, entries: set[str]) -> None:
    """Resolve packaged README links from the package root, not the source project."""
    text = re.sub(r"^```[^\n]*\n.*?^```\s*$", "", readme, flags=re.MULTILINE | re.DOTALL)
    targets = re.findall(r"\[[^\]]*\]\(([^)]+)\)", text)
    targets += re.findall(r"^\s*\[[^\]]+\]:\s*(\S+)", text, flags=re.MULTILINE)
    for raw in targets:
        target = raw.strip().split(maxsplit=1)[0].strip("<>")
        url = urlsplit(target)
        if url.scheme:
            assert url.scheme == "https" and url.netloc, ("package README needs an HTTPS document URL", target)
            continue
        path = posixpath.normpath(unquote(url.path))
        if not url.path:
            continue
        assert not url.netloc and not path.startswith(("/", "../")) and path != "..", ("package README link escapes package root", target)
        assert path in entries, ("package README link target missing", target)


def inspect_packages(directory: Path, version: str) -> list[str]:
    """Validate every expected package and return its package ID in sorted order."""
    packages = sorted(directory.glob("*.nupkg"))
    found: set[str] = set()
    for package in packages:
        with zipfile.ZipFile(package) as archive:
            entries = set(archive.namelist())
            nuspecs = [name for name in entries if name.endswith(".nuspec")]
            assert len(nuspecs) == 1, (package, nuspecs)
            manifest = ET.fromstring(archive.read(nuspecs[0]))
            metadata = {node.tag.rsplit("}", 1)[-1]: node.text for node in manifest.iter()}
            identity = metadata["id"]
            actual_version = metadata["version"]
            assert identity in PACKAGE_LAYOUTS, (package, identity)
            assert actual_version == version and identity not in found, (identity, actual_version, version)
            assert package.name == f"{identity}.{version}.nupkg", (package.name, identity, version)
            found.add(identity)

            assert "README.md" in entries and "NOTICE.md" in entries and "LICENSE" in entries, package
            assert metadata.get("readme") == "README.md", (identity, metadata.get("readme"))
            assert metadata.get("license") == "MIT", (identity, metadata.get("license"))
            assert metadata.get("description") and metadata.get("tags"), identity
            assert metadata.get("projectUrl") == "https://github.com/gentt1024/Cordis.NET", (identity, metadata.get("projectUrl"))
            repository = next(node for node in manifest.iter() if node.tag.endswith("}repository"))
            assert repository.attrib.get("url") == "https://github.com/gentt1024/Cordis.NET.git", (identity, repository.attrib)
            assert repository.attrib.get("type") == "git", (identity, repository.attrib)
            assert re.fullmatch(r"[0-9a-f]{40}", repository.attrib.get("commit", "")), (identity, repository.attrib)
            readme = archive.read("README.md").decode("utf-8-sig")
            assert readme.startswith(f"# {identity}\n"), (identity, readme.splitlines()[:1])
            inspect_readme_links(readme, entries)

            folder, assembly, representative_member, expected_dependencies = PACKAGE_LAYOUTS[identity]
            dll_path = f"{folder}/{assembly}.dll"
            xml_path = f"{folder}/{assembly}.xml"
            assert dll_path in entries, (identity, f"missing product assembly {dll_path}")
            assert xml_path in entries, (identity, f"missing XML documentation {xml_path}")
            documentation = ET.fromstring(archive.read(xml_path))
            assert documentation.findtext("./assembly/name") == assembly, (identity, "wrong XML assembly name")
            member_names = {node.attrib.get("name") for node in documentation.findall("./members/member")}
            if representative_member:
                assert representative_member in member_names, (identity, representative_member)

            dependencies = {node.attrib["id"] for node in manifest.iter() if node.tag.endswith("}dependency")}
            internal_dependencies = {dependency for dependency in dependencies if dependency.startswith("Cordis.")}
            assert internal_dependencies == expected_dependencies, (identity, internal_dependencies, expected_dependencies)
            if identity == "Cordis.NET.Tool":
                assert "tools/net10.0/any/Cordis.Composition.dll" in entries, identity
                assert "tools/net10.0/any/Cordis.Core.dll" in entries, identity
            if identity == "Cordis.NET.Clr":
                assert "buildTransitive/Cordis.NET.Clr.targets" in entries, identity
            if identity == "Cordis.NET.Composition":
                assert "analyzers/dotnet/cs/Cordis.Typert.Generator.dll" in entries, identity
                assert "buildTransitive/Cordis.NET.Composition.targets" in entries, identity
                for asset in ("Cordis.Typert.Compiler.dll", "Cordis.Typert.Compiler.deps.json", "Cordis.Typert.Compiler.runtimeconfig.json"):
                    assert "tools/net10.0/typert/" + asset in entries, (identity, asset)
                model_path = "cordis/typert/Cordis.Composition.cordis.model.json"
                model_entries = [name for name in archive.namelist()
                                 if name.startswith("cordis/typert/") and not name.endswith("/")]
                assert model_entries == [model_path], (identity, model_entries)
                model = json.loads(archive.read(model_path))
                declarations = {declaration["id"]: declaration for declaration in model["declarations"]}
                service_keys = [json.loads(attribute["arguments"][0]["json"])
                                for root_id in model["roots"]
                                for declaration in [declarations[root_id]]
                                if declaration["accessibility"] == "Public"
                                for attribute in declaration["attributes"]
                                if attribute["name"] == "Cordis.Composition.RemoteServiceAttribute"]
                assert len(service_keys) == 2 and set(service_keys) == {
                    "settingsController", "pluginManager"
                }, (identity, service_keys)
                assert not any(PurePosixPath(name).name.startswith("Microsoft.CodeAnalysis") for name in entries), identity

            assert any(name.startswith("LICENSES/") for name in entries), package
            assert not any(set(PurePosixPath(name).parts) & {"node_modules", "obj", "reference-materials", ".git"} for name in entries), package
            if identity == "Cordis.NET.Core":
                assert not any(dep in {"Jint", "Cordis.NET.Hosting", "Cordis.NET.Clr"} for dep in dependencies), dependencies
            symbol_package = directory / f"{identity}.{version}.snupkg"
            assert symbol_package.is_file(), (identity, "missing matching snupkg")
            with zipfile.ZipFile(symbol_package) as symbols:
                assert f"{folder}/{assembly}.pdb" in symbols.namelist(), (identity, "missing product portable PDB")
                if identity == "Cordis.NET.Composition":
                    for path in ("analyzers/dotnet/cs/Cordis.Typert.Generator.pdb", "tools/net10.0/typert/Cordis.Typert.Compiler.pdb"):
                        assert path in symbols.namelist(), (identity, "missing native generator/compiler portable PDB", path)

    expected = set(PACKAGE_LAYOUTS)
    assert found == expected, (found, expected)
    return sorted(found)


def inspect_symbols(directory: Path, source_root: Path, dotnet: str = "dotnet", frame: Path | None = None) -> dict:
    """Bind actual portable PDBs to their DLLs and committed/manifested source bytes."""
    project = Path(__file__).resolve().parent / "PackageSymbols" / "PackageSymbols.csproj"
    command = [dotnet, "run", "--project", str(project), "-c", "Release", "--",
               str(directory.resolve()), str(source_root.resolve())]
    if frame is not None:
        command.append(str(frame))
    result = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if result.returncode:
        raise RuntimeError(f"Package symbol inspection failed:\n{result.stdout}\n{result.stderr}")
    return json.loads(result.stdout.strip().splitlines()[-1])


def inspect_debug_consumer(directory: Path, version: str, source_root: Path, dotnet: str = "dotnet") -> dict:
    """Run an isolated NuGet consumer and resolve its actual library stack frame offline."""
    with tempfile.TemporaryDirectory() as temporary:
        consumer = Path(temporary)
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        properties = ET.SubElement(project, "PropertyGroup")
        for key, value in {"OutputType": "Exe", "TargetFramework": "net10.0", "ImplicitUsings": "enable", "Nullable": "enable",
                           "RestorePackagesPath": str(consumer / "cache")}.items():
            ET.SubElement(properties, key).text = value
        ET.SubElement(ET.SubElement(project, "ItemGroup"), "PackageReference", Include="Cordis.NET.Core", Version=version)
        ET.ElementTree(project).write(consumer / "Consumer.csproj", encoding="utf-8", xml_declaration=True)
        config = ET.Element("configuration")
        feeds = ET.SubElement(config, "packageSources")
        ET.SubElement(feeds, "clear")
        ET.SubElement(feeds, "add", key="validated-batch", value=str(directory.resolve()))
        ET.ElementTree(config).write(consumer / "NuGet.Config", encoding="utf-8", xml_declaration=True)
        (consumer / "Program.cs").write_bytes((Path(__file__).parent / "PackageSymbols" / "DebugConsumer.cs").read_bytes())
        build = subprocess.run([dotnet, "build", "-c", "Release"], cwd=consumer, capture_output=True, text=True, encoding="utf-8", errors="replace")
        if build.returncode:
            raise RuntimeError(f"Debug consumer build failed:\n{build.stdout}\n{build.stderr}")
        output = consumer / "bin" / "Release" / "net10.0"
        with zipfile.ZipFile(directory / f"Cordis.NET.Core.{version}.snupkg") as archive:
            (output / "Cordis.Core.pdb").write_bytes(archive.read("lib/net10.0/Cordis.Core.pdb"))
        execution = subprocess.run([dotnet, str(output / "Consumer.dll")], capture_output=True, text=True, encoding="utf-8", errors="replace")
        if execution.returncode:
            raise RuntimeError(f"Debug consumer execution failed:\n{execution.stdout}\n{execution.stderr}")
        frame = consumer / "frame.json"
        frame.write_text(execution.stdout, encoding="utf-8")
        try:
            report = inspect_symbols(directory, source_root, dotnet, frame)
        except RuntimeError as error:
            raise RuntimeError(f"Consumer executed and located frame {execution.stdout.strip()}, but source binding failed:\n{error}") from error
        return {"frame": json.loads(execution.stdout), "symbols": report}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--symbols", action="store_true", help="Verify matching snupkg, SourceLink and source checksums")
    parser.add_argument("--debug-consumer", action="store_true", help="Execute an isolated Core NuGet consumer and locate its embedded source frame")
    options = parser.parse_args()
    packages = inspect_packages(options.directory, options.version)
    symbols = inspect_symbols(options.directory, options.source_root, options.dotnet) if options.symbols else None
    consumer = inspect_debug_consumer(options.directory, options.version, options.source_root, options.dotnet) if options.debug_consumer else None
    print(json.dumps({"packages": packages, "symbols": symbols, "debugConsumer": consumer}, indent=2))
