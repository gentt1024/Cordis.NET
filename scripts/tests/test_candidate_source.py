import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import candidate_source as candidate


class CandidateSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root / "upstream"
        self.source.mkdir()
        candidate.git(self.source, "init", "--object-format=sha1")
        (self.source / "runtime.ts").write_bytes(b"export const path = 'plain';\n")
        (self.source / "original.spec.ts").write_bytes(b"expect(path).toBe('namespaced');\n")
        candidate.git(self.source, "add", ".")
        tree = candidate.git(self.source, "write-tree")
        author = "author Fixture <fixture@example.invalid> 1 +0000\ncommitter Fixture <fixture@example.invalid> 1 +0000\n"
        base = candidate.git(self.source, "hash-object", "-t", "commit", "-w", "--stdin",
                             data=f"tree {tree}\n{author}\nFixture\n".encode())
        candidate.git(self.source, "update-ref", "HEAD", base)
        (self.source / "runtime.ts").write_bytes(b"export const path = 'namespaced';\n")
        patch_bytes = subprocess.check_output(["git", "-C", str(self.source), "diff", "--binary"])
        candidate.git(self.source, "add", "runtime.ts")
        fixed_tree = candidate.git(self.source, "write-tree")
        commit_text = f"tree {fixed_tree}\nparent {base}\n{author}\nFix path\n"
        commit = candidate.git(self.source, "hash-object", "-t", "commit", "-w", "--stdin", data=commit_text.encode())
        self.record = self.root / "adaptation.json"
        self.patch_file = self.root / "fix.patch"
        self.patch_file.write_bytes(patch_bytes)
        self.value = {"format": "cordis-candidate-adaptation/v1", "upstreamCommit": base,
                      "upstreamTree": tree, "commit": commit, "tree": fixed_tree,
                      "commitObject": commit_text, "patch": "fix.patch",
                      "patchSha256": hashlib.sha256(patch_bytes).hexdigest(),
                      "files": {"runtime.ts": {
                          "beforeBlob": candidate.git(self.source, "rev-parse", base + ":runtime.ts"),
                          "afterBlob": candidate.git(self.source, "rev-parse", fixed_tree + ":runtime.ts")}}}
        self.save_record()
        for name, value in (("UPSTREAM", base), ("COMMIT", commit), ("RECORD", self.record)):
            replacement = patch.object(candidate, name, value)
            replacement.start()
            self.addCleanup(replacement.stop)

    def save_record(self):
        self.record.write_text(json.dumps(self.value), encoding="utf8")

    def test_reconstruction_preserves_original_tests_and_source_checkout_under_crlf_defaults(self):
        before = candidate.git(self.source, "status", "--porcelain")
        with patch.dict(os.environ, {"GIT_CONFIG_COUNT": "1", "GIT_CONFIG_KEY_0": "core.autocrlf",
                                    "GIT_CONFIG_VALUE_0": "true"}):
            for name in ("first", "second"):
                output = self.root / name
                candidate.prepare(output, self.source)
                self.assertEqual(candidate.git(output, "rev-parse", "HEAD"), self.value["commit"])
                self.assertEqual(candidate.git(output, "status", "--porcelain"), "")
                self.assertEqual((output / "runtime.ts").read_bytes(), b"export const path = 'namespaced';\n")
                self.assertEqual((output / "original.spec.ts").read_bytes(), b"expect(path).toBe('namespaced');\n")
        self.assertEqual(candidate.git(self.source, "status", "--porcelain"), before)
        self.assertEqual(candidate.git(self.source, "rev-parse", "HEAD"), self.value["upstreamCommit"])

    def test_existing_output_is_never_reset(self):
        output = self.root / "existing"
        output.mkdir()
        sentinel = output / "user.txt"
        sentinel.write_text("keep")
        with self.assertRaises(FileExistsError):
            candidate.prepare(output, self.source)
        self.assertEqual(sentinel.read_text(), "keep")

    def test_patch_and_commit_tampering_fail_before_creating_checkout(self):
        original = self.patch_file.read_bytes()
        self.patch_file.write_bytes(original + b"\n")
        output = self.root / "rejected"
        with self.assertRaisesRegex(ValueError, "patch digest"):
            candidate.prepare(output, self.source)
        self.assertFalse(output.exists())
        self.patch_file.write_bytes(original)
        self.value["commitObject"] += "changed\n"
        self.save_record()
        with self.assertRaisesRegex(ValueError, "commit object"):
            candidate.prepare(output, self.source)
        self.assertFalse(output.exists())

    def test_incorrect_blob_or_changed_file_claim_cannot_produce_accepted_checkout(self):
        self.value["files"]["runtime.ts"]["afterBlob"] = "0" * 40
        self.save_record()
        with self.assertRaisesRegex(ValueError, "Patched blob"):
            candidate.prepare(self.root / "wrong-blob", self.source)
        self.value["files"] = {}
        self.save_record()
        with self.assertRaisesRegex(ValueError, "unreviewed files"):
            candidate.prepare(self.root / "unclaimed-file", self.source)


if __name__ == "__main__":
    unittest.main()
