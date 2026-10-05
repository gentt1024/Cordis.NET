"""The exported commit must survive Git's working-tree text filters unchanged."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile


SCRIPT = (Path(__file__).resolve().parents[1] / "source-archive.py").read_bytes()


class SourceArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="cordis-archive-contract-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "fixture"
        (self.root / "scripts").mkdir(parents=True)
        (self.root / "scripts/source-archive.py").write_bytes(SCRIPT)
        (self.root / ".gitattributes").write_bytes(b"* text=auto eol=lf\n*.bin binary\n")
        (self.root / "Cordis.slnx").write_bytes(b"<Solution />\n")
        (self.root / "Fixture.csproj").write_bytes(b"<Project />\n")
        (self.root / "build.ps1").write_bytes(b"# Archive contract fixture\n")
        (self.root / "README.md").write_bytes(b"# Fixture\nline two\nline three\n")
        (self.root / "data.bin").write_bytes(b"\x00binary\r\nunchanged\xff")
        self.git("init", "-q")
        self.git("config", "user.name", "Archive contract fixture")
        self.git("config", "user.email", "fixture@example.invalid")
        self.git("add", ".")
        self.git("commit", "-qm", "Archive contract fixture")

    def git(self, *arguments):
        return subprocess.check_output(["git", *arguments], cwd=self.root, stderr=subprocess.PIPE)

    def export(self):
        return subprocess.run([sys.executable, "scripts/source-archive.py"], cwd=self.root,
                              capture_output=True, text=True)

    def test_clean_filtered_text_exports_committed_bytes_and_preserves_binary(self):
        (self.root / "README.md").write_bytes(b"# Fixture\r\nline two\nline three\r\n")
        self.git("add", "README.md")
        self.assertEqual(b"", self.git("status", "--porcelain"))
        result = self.export()
        self.assertEqual(0, result.returncode, result.stderr)
        report = json.loads(result.stdout)
        with zipfile.ZipFile(report["archive"]) as archive:
            manifest = json.loads(archive.read("Cordis.NET/SOURCE_SHA256.json"))
            for name, digest in manifest["files"].items():
                with self.subTest(file=name):
                    committed = self.git("show", f"HEAD:{name}")
                    self.assertEqual(committed, archive.read("Cordis.NET/" + name))
                    self.assertEqual(hashlib.sha256(committed).hexdigest(), digest)

    def test_uncommitted_content_is_refused(self):
        (self.root / "README.md").write_bytes(b"# Changed content\n")
        result = self.export()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Commit all implementation files", result.stderr)
        self.assertEqual([], list(self.root.parent.glob("*-source.zip")))

    def test_local_archive_attributes_cannot_substitute_source_contents(self):
        marker = b"# Fixture\n$Format:%H$\n"
        (self.root / "README.md").write_bytes(marker)
        self.git("add", "README.md")
        self.git("commit", "-qm", "Literal source marker")
        (self.root / ".git/info/attributes").write_bytes(b"README.md export-subst\n")
        result = self.export()
        self.assertEqual(0, result.returncode, result.stderr)
        with zipfile.ZipFile(json.loads(result.stdout)["archive"]) as archive:
            self.assertEqual(marker, archive.read("Cordis.NET/README.md"))


if __name__ == "__main__":
    unittest.main()
