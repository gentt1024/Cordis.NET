import hashlib
import json
from pathlib import Path
import runpy
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
authoring = runpy.run_path(str(Path(__file__).resolve().parents[1] / "verify-authoring.py"))


class AuthoringReuseTests(unittest.TestCase):
    def test_same_checkout_results_are_reused_but_stale_or_altered_evidence_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            results = root / "artifacts" / "verification" / "tests-one"
            results.mkdir(parents=True)
            trx = results / "suite.trx"
            trx.write_text("<TestRun />", encoding="utf-8")
            hashes = {"src/Entry.cs": "source-digest"}
            report = {
                "status": "requested-checks-passed", "sourceUnchanged": True,
                "checkoutRoot": str(root), "sourceSha256": hashes, "initialSourceSha256": hashes,
                "rid": "win-x64", "sdkVersion": "10.0.111",
                "testResults": {"directory": str(results), "sha256": {
                    "suite.trx": hashlib.sha256(trx.read_bytes()).hexdigest()}},
                "steps": [{"name": "test", "exitCode": 0}, {"name": "test-map", "exitCode": 0}],
            }
            path = root / "verification.json"
            reuse = authoring["reuse_test_results"]
            with patch.dict(reuse.__globals__, ROOT=root):
                path.write_text(json.dumps(report), encoding="utf-8")
                self.assertEqual(reuse(path, hashes, "win-x64", "10.0.111"), results)
                for name, value in (
                    ("status", "failed"), ("sourceUnchanged", False),
                    ("checkoutRoot", str(root / "other-checkout")),
                    ("sourceSha256", {"src/Entry.cs": "edited"}),
                    ("initialSourceSha256", {}), ("rid", "linux-x64"),
                    ("sdkVersion", "10.0.100"), ("steps", []),
                    ("testResults", {"directory": str(results), "sha256": {}}),
                ):
                    with self.subTest(name=name):
                        path.write_text(json.dumps(report | {name: value}), encoding="utf-8")
                        with self.assertRaises(ValueError):
                            reuse(path, hashes, "win-x64", "10.0.111")
                path.write_text(json.dumps(report), encoding="utf-8")
                trx.write_text("<TestRun altered='true' />", encoding="utf-8")
                with self.assertRaises(ValueError):
                    reuse(path, hashes, "win-x64", "10.0.111")
                trx.unlink()
                with self.assertRaises(ValueError):
                    reuse(path, hashes, "win-x64", "10.0.111")


if __name__ == "__main__":
    unittest.main()
