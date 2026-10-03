from pathlib import Path
import json
import sys
import tempfile
import unittest
from unittest.mock import patch

with patch.object(sys, "path", [str(Path(__file__).resolve().parents[1]), *sys.path]):
    from scripts import verify


class SourceResultsTests(unittest.TestCase):
    def record(self, status="skipped", title=None, passed=0, pending=1, success=True):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repository = root / "reference"
            source = "packages/boot/app-boot/tests/package-meta.spec.ts"
            (root / "docs").mkdir()
            (root / "docs/upstream-tests.json").write_text('{"tests": []}', encoding="utf-8")
            (root / "upstream.lock.json").write_text(json.dumps({"harness": {
                "commit": "639ed015397290b3745d163aafe02ffee4aa3f84"}}), encoding="utf-8")
            report = root / "upstream.json"
            report.write_text(json.dumps({
                "success": success, "numTotalTests": 1, "numPassedTests": passed,
                "numPendingTests": pending, "numFailedTests": int(status == "failed"),
                "testResults": [{"name": str(repository / source), "status": "passed",
                    "assertionResults": [{"status": status, "fullName": title or
                        "plugin locale display metadata rejects case-equivalent language files on case-sensitive filesystems"}]}],
            }), encoding="utf-8")
            with patch.object(verify, "ROOT", root), patch.object(verify, "OUT", root):
                verify.record_source_results([(report, repository)])
            return json.loads((root / "source-test-evidence.json").read_text(encoding="utf-8"))

    def test_known_windows_capability_skip_remains_visible_as_unpassed(self):
        result = self.record()
        entry = result["files"][0]
        self.assertFalse(entry["allOriginalInstancesPassed"])
        self.assertEqual(entry["passedInstanceCount"], 0)
        self.assertEqual(len(entry["skippedInstances"]), 1)
        self.assertIn("case-insensitive", entry["skippedInstances"][0]["reason"])

    def test_actual_pass_has_no_platform_skip(self):
        result = self.record(status="passed", passed=1, pending=0)
        self.assertTrue(result["files"][0]["allOriginalInstancesPassed"])
        self.assertEqual(result["files"][0]["skippedInstances"], [])

    def test_unknown_skip_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.record(title="some other test")

    def test_failure_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.record(status="failed", pending=0, success=False)

    def test_unreported_skip_counter_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.record(pending=2)


if __name__ == "__main__":
    unittest.main()
