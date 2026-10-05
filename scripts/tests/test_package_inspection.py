from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest
import zipfile

from scripts.package_inspection import PACKAGE_LAYOUTS, PUBLISHABLE_PACKAGE_IDS, inspect_packages, inspect_symbols


class PackageInspectionTests(unittest.TestCase):
    def test_wrapper_xml_does_not_count_as_api_documentation(self):
        artifact_root = Path(__file__).resolve().parents[2] / "artifacts"
        artifact_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=artifact_root) as temporary:
            package = Path(temporary) / "Cordis.NET.Core.0.1.0-alpha.2.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("[Content_Types].xml", "<Types />")
                archive.writestr("Cordis.NET.Core.nuspec", """<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>Cordis.NET.Core</id><version>0.1.0-alpha.2</version><authors>test</authors>
<description>test</description><tags>test</tags><license type="expression">MIT</license>
<readme>README.md</readme><projectUrl>https://github.com/gentt1024/Cordis.NET</projectUrl>
<repository type="git" url="https://github.com/gentt1024/Cordis.NET.git" commit="0123456789abcdef0123456789abcdef01234567" />
</metadata></package>""")
                archive.writestr("README.md", "# Cordis.NET.Core\n")
                archive.writestr("NOTICE.md", "notice")
                archive.writestr("LICENSE", "license")
                archive.writestr("LICENSES/reference.txt", "reference")
                archive.writestr("lib/net10.0/Cordis.Core.dll", b"assembly")

            with self.assertRaisesRegex(AssertionError, "missing XML documentation"):
                inspect_packages(Path(temporary), "0.1.0-alpha.2")

    def test_publishable_ids_and_internal_dependencies_are_explicit(self):
        self.assertEqual(PUBLISHABLE_PACKAGE_IDS, (
            "Cordis.NET.Core",
            "Cordis.NET.Composition",
            "Cordis.NET.Extensions",
            "Cordis.NET.Clr",
            "Cordis.NET.Hosting",
            "Cordis.NET.AspNetCore",
            "Cordis.NET.JavaScript",
            "Cordis.NET.Tool",
        ))
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Composition"][3], {"Cordis.NET.Core"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Extensions"][3], {"Cordis.NET.Core", "Cordis.NET.Composition"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Clr"][3], {"Cordis.NET.Composition"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Hosting"][3], {"Cordis.NET.Core"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.AspNetCore"][3], {"Cordis.NET.Extensions"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.JavaScript"][3], {"Cordis.NET.Composition"})
        # PackAsTool carries its managed dependencies instead of declaring package dependencies.
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Tool"][3], set())

    @unittest.skipUnless(shutil.which("dotnet") or os.environ.get("CORDIS_TEST_DOTNET"), "Symbol fixture requires the .NET SDK")
    def test_actual_symbols_bind_dll_and_committed_source_and_reject_broken_artifacts(self):
        dotnet = os.environ.get("CORDIS_TEST_DOTNET", "dotnet")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            def run(*command):
                subprocess.run(command, cwd=root, check=True, capture_output=True)
            run("git", "init")
            run("git", "remote", "add", "origin", "https://github.com/gentt1024/Cordis.NET.git")
            project = root / "Fixture.csproj"
            project.write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net10.0</TargetFramework><PackageId>Symbols.Fixture</PackageId><Version>1.0.0</Version>
<RepositoryUrl>https://github.com/gentt1024/Cordis.NET.git</RepositoryUrl><PublishRepositoryUrl>true</PublishRepositoryUrl>
<EmbedUntrackedSources>true</EmbedUntrackedSources><EmbedAllSources>true</EmbedAllSources><IncludeSymbols>true</IncludeSymbols><SymbolPackageFormat>snupkg</SymbolPackageFormat>
</PropertyGroup></Project>''', encoding="utf-8")
            source = root / "Fixture.cs"
            source.write_bytes(b"public class Fixture { public int Value => 1; }\n")
            run("git", "add", "Fixture.cs", "Fixture.csproj")
            run("git", "-c", "user.name=Symbol fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture")
            good = root / "good"
            run(dotnet, "pack", str(project), "-o", str(good), "-p:ContinuousIntegrationBuild=true")
            report = inspect_symbols(good, root, dotnet)["packages"]
            self.assertEqual(len(report), 1)
            self.assertGreater(report[0]["sourceDocuments"], 0)

            # The real compiler emits a different source checksum without changing Git HEAD.
            source.write_bytes(b"public class Fixture { public int Value => 2; }\n")
            dirty = root / "dirty"
            run(dotnet, "pack", str(project), "-o", str(dirty), "-p:ContinuousIntegrationBuild=true")
            with self.assertRaisesRegex(RuntimeError, "Source checksum mismatch"):
                inspect_symbols(dirty, root, dotnet)

            def broken(name, archive_suffix, transform):
                destination = root / name
                shutil.copytree(good, destination)
                target = next(destination.glob(f"*.{archive_suffix}"))
                with zipfile.ZipFile(target) as archive:
                    entries = {entry: archive.read(entry) for entry in archive.namelist()}
                transform(entries)
                with zipfile.ZipFile(target, "w") as archive:
                    for entry, data in entries.items():
                        archive.writestr(entry, data)
                return destination

            with zipfile.ZipFile(next(dirty.glob("*.nupkg"))) as archive:
                different_dll = archive.read("lib/net10.0/Fixture.dll")
            mismatch = broken("mismatch", "nupkg", lambda entries: entries.update({"lib/net10.0/Fixture.dll": different_dll}))
            with self.assertRaisesRegex(RuntimeError, "DLL/PDB identity mismatch"):
                inspect_symbols(mismatch, root, dotnet)

            missing = broken("missing", "snupkg", lambda entries: [entries.pop(key) for key in list(entries) if key.endswith(".pdb")])
            with self.assertRaisesRegex(RuntimeError, "no source-bound portable PDB"):
                inspect_symbols(missing, root, dotnet)

            archive_source = root / "source-archive"
            archive_source.mkdir()
            import hashlib
            import json
            original = b"public class Fixture { public int Value => 1; }\n"
            (archive_source / "Fixture.cs").write_bytes(original)
            (archive_source / "SOURCE_SHA256.json").write_text(json.dumps({"commit": report[0]["commit"], "files": {
                "Fixture.cs": hashlib.sha256(original).hexdigest()}}), encoding="utf-8")
            self.assertEqual(inspect_symbols(good, archive_source, dotnet)["packages"][0]["sourceDocuments"], report[0]["sourceDocuments"])
            (archive_source / "Fixture.cs").write_bytes(b"changed")
            with self.assertRaisesRegex(RuntimeError, "Source archive hash mismatch"):
                inspect_symbols(good, archive_source, dotnet)


if __name__ == "__main__":
    unittest.main()
