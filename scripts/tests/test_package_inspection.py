from pathlib import Path
import tempfile
import unittest
import zipfile

from scripts.package_inspection import inspect_packages


class PackageInspectionTests(unittest.TestCase):
    def test_wrapper_xml_does_not_count_as_api_documentation(self):
        artifact_root = Path(__file__).resolve().parents[2] / "artifacts"
        artifact_root.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=artifact_root) as temporary:
            package = Path(temporary) / "Cordis.Core.0.1.0-alpha.1.nupkg"
            with zipfile.ZipFile(package, "w") as archive:
                archive.writestr("[Content_Types].xml", "<Types />")
                archive.writestr("Cordis.Core.nuspec", """<?xml version="1.0"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata>
<id>Cordis.Core</id><version>0.1.0-alpha.1</version><authors>test</authors>
<description>test</description><tags>test</tags><license type="expression">MIT</license>
<readme>README.md</readme><projectUrl>https://github.com/gentt1024/Cordis.NET</projectUrl>
<repository type="git" url="https://github.com/gentt1024/Cordis.NET.git" commit="0123456789abcdef0123456789abcdef01234567" />
</metadata></package>""")
                archive.writestr("README.md", "# Cordis.Core\n")
                archive.writestr("NOTICE.md", "notice")
                archive.writestr("LICENSE", "license")
                archive.writestr("LICENSES/reference.txt", "reference")
                archive.writestr("lib/net10.0/Cordis.Core.dll", b"assembly")

            with self.assertRaisesRegex(AssertionError, "missing XML documentation"):
                inspect_packages(Path(temporary), "0.1.0-alpha.1")


if __name__ == "__main__":
    unittest.main()
