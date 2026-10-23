import copy
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import verify_candidate_upstream as candidate
from verify_upstream import MATRIX, SKIPPABLE


class CandidateGateTests(unittest.TestCase):
    def fixtures(self):
        # Literal expected modulo partition of 4800 independent source cases.
        matrix = [["composition", MATRIX, f"matrix > case {index:04d}", 0] for index in range(4800)]
        twins = [["core", "core.spec.ts", "parameters > same title", index] for index in (0, 1)]
        inventory = sorted([*matrix, *twins, list(SKIPPABLE)])
        tools = candidate.tool_identity()
        trees = {"dsh": "candidate-tree", "origin": "fixed-origin-tree"}
        frozen = {"format": "cordis-candidate-discovery/v1", "dsh": candidate.DSH, "origin": candidate.ORIGIN,
                  "runId": "workflow:1", "tools": tools, "sourceTrees": trees, "inventory": inventory}
        reports = []
        for shard in range(1, 5):
            selected = [case for index, case in enumerate(matrix) if index % 4 + 1 == shard]
            if shard == 1:
                selected += twins + [list(SKIPPABLE)]
            outcomes = [{"case": case, "status": "skipped" if case == list(SKIPPABLE) else "passed"}
                        for case in selected]
            reports.append({"shard": shard, "shards": 4, "status": "passed", "sourceUnchanged": True,
                "identity": {"commit": "checkpoint", "platform": "Windows", "architecture": "AMD64",
                    "dsh": candidate.DSH, "origin": candidate.ORIGIN, "runId": "workflow:1", "tools": tools,
                    "sourceSha256": {"source": "digest"}, "sourceTrees": trees},
                "inventory": inventory, "outcomes": outcomes, "steps": []})
        return frozen, reports

    def merge(self, frozen, reports):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inventory = root / "inventory.json"
            inventory.write_text(json.dumps(frozen), encoding="utf8")
            for index, report in enumerate(reports):
                report["identity"].setdefault("inventorySha256", candidate.digest(inventory))
                path = root / str(index)
                path.mkdir()
                (path / "result.json").write_text(json.dumps(report), encoding="utf8")
            # External approved counts and frozen source bytes are test inputs.
            counts = {("composition", MATRIX): 4800, ("core", "core.spec.ts"): 2, SKIPPABLE[:2]: 1}
            with patch.object(candidate, "approved_counts", return_value=counts), \
                 patch.object(candidate, "source_hashes", return_value={"source": "digest"}):
                return candidate.aggregate(root, inventory, "checkpoint", "Windows", "workflow:1")

    def test_candidate_complete_union_keeps_duplicate_titles_and_explicit_skip(self):
        value = self.merge(*self.fixtures())
        self.assertEqual((value["cases"], value["passed"]), (4803, 4802))
        self.assertEqual(value["skipped"], [{"case": list(SKIPPABLE), "status": "skipped"}])
        twins = [row["case"][3] for row in value["outcomes"] if row["case"][1] == "core.spec.ts"]
        self.assertEqual(twins, [0, 1])

    def test_foreign_run_discovery_source_tool_or_checkpoint_cannot_merge(self):
        mutations = [
            lambda r: r[0]["identity"].update(runId="another-workflow:1"),
            lambda r: r[0]["identity"].update(inventorySha256="foreign-inventory"),
            lambda r: r[0]["identity"].update(sourceTrees={"dsh": "foreign-tree"}),
            lambda r: r[0]["identity"].update(sourceSha256={"source": "different"}),
            lambda r: r[0]["identity"].update(commit="different-checkpoint"),
            lambda r: r[0]["identity"]["tools"].update(lockSha256="different-tools"),
            lambda r: [report["identity"]["tools"].update(node="different-node") for report in r],
            lambda r: r[0]["identity"].update(dsh="639ed015397290b3745d163aafe02ffee4aa3f84"),
            lambda r: r[0].update(sourceUnchanged=False),
        ]
        for index, mutation in enumerate(mutations):
            with self.subTest(mutation=index):
                frozen, reports = self.fixtures()
                # Fixtures deliberately use independent external receipt objects.
                reports = copy.deepcopy(reports)
                mutation(reports)
                with self.assertRaises(ValueError):
                    self.merge(frozen, reports)

    def test_missing_duplicate_misplaced_failed_or_skipped_cases_cannot_pass(self):
        mutations = [
            lambda r: r[1]["outcomes"].pop(),
            lambda r: r[1]["outcomes"].append(copy.deepcopy(r[1]["outcomes"][0])),
            lambda r: r[1]["outcomes"].append(r[2]["outcomes"].pop()),
            lambda r: r[1]["outcomes"][0].update(status="failed"),
            lambda r: r[1]["outcomes"][0].update(status="skipped"),
            lambda r: r.pop(),
            lambda r: r.__setitem__(3, copy.deepcopy(r[0])),
        ]
        for index, mutation in enumerate(mutations):
            with self.subTest(mutation=index):
                frozen, reports = self.fixtures()
                mutation(reports)
                with self.assertRaises(ValueError):
                    self.merge(frozen, reports)

    def test_identically_incomplete_worker_discovery_cannot_pass(self):
        frozen, reports = self.fixtures()
        omitted = ["core", "core.spec.ts", "parameters > same title", 1]
        for report in reports:
            report["inventory"] = [case for case in report["inventory"] if case != omitted]
            report["outcomes"] = [row for row in report["outcomes"] if row["case"] != omitted]
        with self.assertRaisesRegex(ValueError, "frozen candidate"):
            self.merge(frozen, reports)

    def test_workflow_attempt_and_local_batch_identity_are_required(self):
        with patch.dict("os.environ", {"GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "2"}, clear=True):
            self.assertEqual(candidate.run_identity(), "123:2")
            with self.assertRaises(ValueError):
                candidate.run_identity("123:1")
        with patch.dict("os.environ", {}, clear=True):
            with self.assertRaises(ValueError):
                candidate.run_identity()
            self.assertEqual(candidate.run_identity("local-batch"), "local-batch")


if __name__ == "__main__":
    unittest.main()
