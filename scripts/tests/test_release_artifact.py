import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_artifact import prepare, verify


class ReleaseArtifactTests(unittest.TestCase):
    # This is the release contract, independently enumerated from the sealing helper.
    packages = (
        "Cordis.NET.Core", "Cordis.NET.Composition", "Cordis.NET.Extensions",
        "Cordis.NET.Clr", "Cordis.NET.Hosting", "Cordis.NET.AspNetCore",
        "Cordis.NET.JavaScript", "Cordis.NET.Tool",
    )

    def test_complete_validated_batch_seals_only_public_packages(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.mkdir()
            for name in (*self.packages, "Cordis.Example.Greeting"):
                for suffix in ("nupkg", "snupkg"):
                    (source / f"{name}.0.2.0-alpha.1.{suffix}").write_bytes(name.encode())
            output = root / "publish"
            prepare(source, output, "0.2.0-alpha.1", "v0.2.0-alpha.1", "checkpoint")
            verify(output, "v0.2.0-alpha.1", "checkpoint")
            self.assertEqual({p.name for p in output.iterdir()}, {
                f"{name}.0.2.0-alpha.1.{suffix}"
                for name in self.packages for suffix in ("nupkg", "snupkg")
            } | {"release-manifest.json"})

    def test_removing_a_package_and_its_manifest_record_is_not_a_valid_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.mkdir()
            for name in (*self.packages, "Cordis.Example.Greeting"):
                for suffix in ("nupkg", "snupkg"):
                    (source / f"{name}.0.2.0-alpha.1.{suffix}").write_bytes(name.encode())
            output = root / "publish"
            prepare(source, output, "0.2.0-alpha.1", "v0.2.0-alpha.1", "checkpoint")
            manifest_path = output / "release-manifest.json"
            manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
            removed = manifest["packages"].pop()
            (output / removed["name"]).unlink()
            manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
            with self.assertRaises(AssertionError):
                verify(output, "v0.2.0-alpha.1", "checkpoint")


if __name__ == "__main__":
    unittest.main()
