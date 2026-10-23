import copy
import hashlib
import json
from pathlib import Path
import sys
import subprocess
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import verify_candidate_upstream as candidate
from verify_upstream import MATRIX, SKIPPABLE, validate_report


class CandidateGateTests(unittest.TestCase):
    def fixtures(self, target_platform="Windows", process_cases=False):
        # Literal expected modulo partition of 4800 independent source cases.
        matrix = [["composition", MATRIX, f"matrix > case {index:04d}", 0] for index in range(4800)]
        twins = [["core", "core.spec.ts", "parameters > same title", index] for index in (0, 1)]
        process = []
        if process_cases:
            process = [rule["case"] for rule in candidate.manifest()["platformSkips"]]
        inventory = sorted([*matrix, *twins, list(SKIPPABLE), *process])
        tools = candidate.tool_identity()
        trees = {"dsh": "candidate-tree", "origin": "fixed-origin-tree"}
        frozen = {"format": "cordis-candidate-discovery/v1", "dsh": candidate.DSH, "origin": candidate.ORIGIN,
                  "runId": "workflow:1", "tools": tools, "sourceTrees": trees, "inventory": inventory}
        reports = []
        for shard in range(1, 5):
            selected = [case for index, case in enumerate(matrix) if index % 4 + 1 == shard]
            if shard == 1:
                selected += twins + [list(SKIPPABLE)] + process
            rules = candidate.skip_rules(target_platform)
            outcomes = [{"case": case, "status": "skipped", "skipReason": rules[tuple(case)]}
                        if tuple(case) in rules else {"case": case, "status": "passed"} for case in selected]
            reports.append({"shard": shard, "shards": 4, "status": "passed", "sourceUnchanged": True,
                "identity": {"commit": "checkpoint", "platform": target_platform, "architecture": "AMD64",
                    "dsh": candidate.DSH, "origin": candidate.ORIGIN, "runId": "workflow:1", "tools": tools,
                    "sourceSha256": {"source": "digest"}, "sourceTrees": trees},
                "inventory": inventory, "outcomes": outcomes, "steps": []})
        return frozen, reports

    def merge(self, frozen, reports, target_platform="Windows"):
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
            if any(case[1].endswith("operations-process.spec.ts") for case in frozen["inventory"]):
                counts[("composition", "packages/boot/plugin-manager/tests/operations-process.spec.ts")] = 3
            with patch.object(candidate, "approved_counts", return_value=counts), \
                 patch.object(candidate, "source_hashes", return_value={"source": "digest"}):
                return candidate.aggregate(root, inventory, "checkpoint", target_platform, "workflow:1")

    def test_candidate_complete_union_keeps_duplicate_titles_and_explicit_skip(self):
        value = self.merge(*self.fixtures())
        self.assertEqual((value["cases"], value["passed"]), (4803, 4802))
        self.assertEqual(value["skipped"], [{"case": list(SKIPPABLE), "status": "skipped",
                         "skipReason": candidate.skip_rules("Windows")[SKIPPABLE]}])
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

    def test_only_exact_upstream_windows_process_skips_can_merge(self):
        value = self.merge(*self.fixtures(process_cases=True))
        self.assertEqual((value["cases"], value["passed"], len(value["skipped"])), (4806, 4802, 4))
        value = self.merge(*self.fixtures("Linux", process_cases=True), target_platform="Linux")
        self.assertEqual((value["cases"], value["passed"], len(value["skipped"])), (4806, 4805, 1))
        mutations = [
            lambda rows: rows[-1].update(skipReason="unreviewed reason"),
            lambda rows: rows[-1]["case"].__setitem__(2, "another title"),
            lambda rows: rows[-1]["case"].__setitem__(3, 1),
        ]
        for mutate in mutations:
            frozen, reports = self.fixtures(process_cases=True)
            mutate(reports[0]["outcomes"])
            with self.assertRaises(ValueError):
                self.merge(frozen, reports)
        frozen, reports = self.fixtures("Linux", process_cases=True)
        reports[0]["outcomes"][-1].update(status="skipped", skipReason="Windows-only fixture")
        with self.assertRaises(ValueError):
            self.merge(frozen, reports, target_platform="Linux")

    def test_formal_report_validator_still_refuses_candidate_process_skip(self):
        rule = candidate.manifest()["platformSkips"][0]
        case = tuple(rule["case"])
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = {"success": True, "numFailedTests": 0, "numTotalTests": 1,
                      "numPassedTests": 0, "numPendingTests": 1,
                      "testResults": [{"name": str(root / case[1]), "status": "passed",
                        "assertionResults": [{"ancestorTitles": ["a run with descendants"],
                            "title": case[2].split(" > ", 1)[1], "status": "skipped", "failureMessages": []}]}]}
            with self.assertRaises(ValueError):
                validate_report(report, "composition", root, {case}, {case})
            with patch.object(candidate.platform, "system", return_value="Windows"):
                result = candidate.candidate_report(report, "composition", root, {case}, {case})
            self.assertEqual(result, [{"case": list(case), "status": "skipped", "skipReason": rule["reason"]}])
            with patch.object(candidate.platform, "system", return_value="Linux"):
                with self.assertRaises(ValueError):
                    candidate.candidate_report(report, "composition", root, {case}, {case})

    def test_historical_absence_needs_an_explicit_exclusion_before_claiming_outside_scope(self):
        script = """
import { historicalDisposition } from './reference/candidate-inventory-scope.mjs';
const exclusions = [
  {file:'official.spec.ts', status:'excluded', scopeDecision:'outside-native-runtime-contract', reason:'official product'},
  {file:'catalog.spec.ts', status:'source-skipped', scopeDecision:'outside-native-runtime-contract', reason:'upstream describe.skip'},
];
console.log(JSON.stringify([
  historicalDisposition('dsh','ordinary.spec.ts',true,exclusions),
  historicalDisposition('dsh','ordinary.spec.ts',false,exclusions),
  historicalDisposition('dsh','official.spec.ts',false,exclusions),
  historicalDisposition('dsh','catalog.spec.ts',false,exclusions),
  historicalDisposition('origin','official.spec.ts',false,exclusions),
]));
"""
        value = subprocess.run(["node", "--input-type=module", "-e", script], cwd=candidate.ROOT,
                               check=True, capture_output=True, text=True)
        rows = json.loads(value.stdout)
        self.assertEqual([row["candidateExecution"] for row in rows], [
            "file-in-runtime-matrix-case-correspondence-unreviewed", "not-executed-scope-unreviewed",
            "not-executed-outside-native-runtime-scope", "not-executed-source-skipped", "not-executed-scope-unreviewed"])
        self.assertEqual([row["candidateScopeDecision"] for row in rows], [
            "runtime-source-included", "unreviewed", "outside-native-runtime-contract", "outside-native-runtime-contract", "unreviewed"])
        self.assertEqual([row["candidateScopeReason"] for row in rows], [None, None, "official product", "upstream describe.skip", None])

    def test_workflow_attempt_and_local_batch_identity_are_required(self):
        with patch.dict("os.environ", {"GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "2"}, clear=True):
            self.assertEqual(candidate.run_identity(), "123:2")
            with self.assertRaises(ValueError):
                candidate.run_identity("123:1")
        with patch.dict("os.environ", {}, clear=True):
            with self.assertRaises(ValueError):
                candidate.run_identity()
            self.assertEqual(candidate.run_identity("local-batch"), "local-batch")

    def test_delta_review_requires_actual_source_cases_and_unchanged_native_assertions(self):
        script = """
import { validateCandidateReview } from './reference/candidate-review.mjs';
const identity = ['remote', 'test.ts', 'same title', 1];
const inventory = {dsh:'candidate', inventory:[identity]};
const original = {
  format:'cordis-candidate-delta-review/v1', candidate:'candidate',
  sourceFiles:{'test.ts':{candidateBlob:'source-blob'}}, nativeFiles:{'Test.cs':'native-sha'},
  clusters:Array.from({length:9}, (_, i) => ({id:'contract-'+i, slice:'S'+(i+1),
    review:'reviewed-adaptation', assertions:['Refusal leaves persisted state unchanged.'],
    sourceCases:[identity], sourceAlgorithms:['test.ts'], native:[{file:'Test.cs',method:'Refuses'}]}))
};
const inputs = {blobFor:()=>'source-blob', readNative:()=>({sha256:'native-sha',text:'void Refuses()'})};
const accepted = validateCandidateReview(original, inventory, inputs);
const mutations = [
  r=>r.candidate='foreign',
  r=>r.sourceFiles['test.ts'].candidateBlob='other',
  r=>r.nativeFiles['Test.cs']='changed',
  r=>r.clusters[0].sourceCases=[['remote','test.ts','same title',0]],
  r=>r.clusters[0].native[0].method='Missing',
  r=>r.clusters[0].review='gap',
  r=>r.clusters.pop(),
];
const rejected = mutations.map(change => {
  const review = structuredClone(original); change(review);
  try { validateCandidateReview(review, inventory, inputs); return false; } catch { return true; }
});
console.log(JSON.stringify({accepted, rejected}));
"""
        result = subprocess.run(["node", "--input-type=module", "-e", script], cwd=candidate.ROOT,
                                check=True, capture_output=True, text=True)
        value = json.loads(result.stdout)
        self.assertEqual(value["accepted"]["assertionClusters"], 9)
        self.assertEqual(value["accepted"]["referencedSourceCases"], 1)
        self.assertEqual(value["rejected"], [True] * 7)


if __name__ == "__main__":
    unittest.main()
