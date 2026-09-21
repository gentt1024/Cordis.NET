"""Checks the exact layout and metadata of Cordis.NET package artifacts."""

from __future__ import annotations

from pathlib import Path, PurePosixPath
import re
import xml.etree.ElementTree as ET
import zipfile


PACKAGE_LAYOUTS = {
    "Cordis.Core": ("lib/net10.0", "Cordis.Core", "T:Cordis.Context"),
    "Cordis.Composition": ("lib/net10.0", "Cordis.Composition", "T:Cordis.Composition.Loader"),
    "Cordis.Extensions": ("lib/net10.0", "Cordis.Extensions", "T:Cordis.Extensions.TimerService"),
    "Cordis.Clr": ("lib/net10.0", "Cordis.Clr", "T:Cordis.Clr.ClrModuleResolver"),
    "Cordis.Hosting": ("lib/net10.0", "Cordis.Hosting", "T:Cordis.Hosting.CordisHostExtensions"),
    "Cordis.JavaScript": ("lib/net10.0", "Cordis.JavaScript", "T:Cordis.JavaScript.JintExpressionEvaluator"),
    "Cordis.Tool": ("tools/net10.0/any", "Cordis.Cli", None),
    "Cordis.Example.Greeting": ("lib/net10.0", "Greeting.Plugin", "T:Cordis.Example.Greeting.GreetingModule"),
}


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

            folder, assembly, representative_member = PACKAGE_LAYOUTS[identity]
            dll_path = f"{folder}/{assembly}.dll"
            xml_path = f"{folder}/{assembly}.xml"
            assert dll_path in entries, (identity, f"missing product assembly {dll_path}")
            assert xml_path in entries, (identity, f"missing XML documentation {xml_path}")
            documentation = ET.fromstring(archive.read(xml_path))
            assert documentation.findtext("./assembly/name") == assembly, (identity, "wrong XML assembly name")
            member_names = {node.attrib.get("name") for node in documentation.findall("./members/member")}
            if representative_member:
                assert representative_member in member_names, (identity, representative_member)

            assert any(name.startswith("LICENSES/") for name in entries), package
            assert not any(set(PurePosixPath(name).parts) & {"node_modules", "obj", "reference-materials", ".git"} for name in entries), package
            if identity == "Cordis.Core":
                dependencies = [node.attrib["id"] for node in manifest.iter() if node.tag.endswith("}dependency")]
                assert not any("Jint" in dep or "Hosting" in dep or "Cordis.Clr" in dep for dep in dependencies), dependencies

    expected = set(PACKAGE_LAYOUTS)
    assert found == expected, (found, expected)
    return sorted(found)
