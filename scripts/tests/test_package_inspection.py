from pathlib import Path
import tempfile
import unittest
import zipfile

from scripts.package_inspection import PACKAGE_LAYOUTS, PUBLISHABLE_PACKAGE_IDS, inspect_packages


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
            "Cordis.NET.JavaScript",
            "Cordis.NET.Tool",
        ))
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Composition"][3], {"Cordis.NET.Core"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Extensions"][3], {"Cordis.NET.Core", "Cordis.NET.Composition"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Clr"][3], {"Cordis.NET.Composition"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Hosting"][3], {"Cordis.NET.Core"})
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.JavaScript"][3], {"Cordis.NET.Composition"})
        # PackAsTool bundles Composition and Core assemblies instead of declaring NuGet dependencies.
        self.assertEqual(PACKAGE_LAYOUTS["Cordis.NET.Tool"][3], set())


if __name__ == "__main__":
    unittest.main()
