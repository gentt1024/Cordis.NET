from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET


class ApiSnapshotTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("dotnet") or os.environ.get("CORDIS_TEST_DOTNET"), "API metadata fixture requires the .NET SDK")
    def test_extension_boundaries_change_the_real_snapshot(self):
        root = Path(__file__).resolve().parents[2]
        with tempfile.TemporaryDirectory() as temporary:
            project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
            properties = ET.SubElement(project, "PropertyGroup")
            for key, value in {"TargetFramework": "net10.0", "OutputType": "Exe", "ImplicitUsings": "enable", "Nullable": "enable"}.items():
                ET.SubElement(properties, key).text = value
            items = ET.SubElement(project, "ItemGroup")
            for source in [root / "tools/Cordis.ApiCheck/ApiSnapshot.cs", root / "scripts/tests/api_snapshot_probe.cs"]:
                ET.SubElement(items, "Compile", Include=str(source))
            path = Path(temporary) / "Probe.csproj"
            ET.ElementTree(project).write(path, encoding="utf-8")
            result = subprocess.run([os.environ.get("CORDIS_TEST_DOTNET", "dotnet"), "run", "--project", str(path)], capture_output=True, text=True, encoding="utf-8", errors="replace")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertIn("API snapshot extension-boundary probes passed.", result.stdout)
