import copy
import hashlib
import json
import os
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
    def test_real_timeout_preserves_both_logs_and_reraises_the_original_exception(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary)
            steps, errors = [], []
            original_run = subprocess.run

            def short_timeout(*args, **kwargs):
                kwargs['timeout'] = 2
                try:
                    return original_run(*args, **kwargs)
                except subprocess.TimeoutExpired as error:
                    errors.append(error)
                    raise

            with patch.object(candidate.subprocess, 'run', side_effect=short_timeout):
                with self.assertRaises(subprocess.TimeoutExpired) as caught:
                    candidate.execute(output, steps, 'timeout-probe', [sys.executable, '-c',
                        "import sys,time;print('stdout before timeout',flush=True);"
                        "print('stderr before timeout',file=sys.stderr,flush=True);time.sleep(5)"], dict(os.environ))
            self.assertIs(caught.exception, errors[0])
            self.assertIn(b'stdout before timeout', (output / 'timeout-probe.stdout.log').read_bytes())
            self.assertIn(b'stderr before timeout', (output / 'timeout-probe.stderr.log').read_bytes())
            self.assertEqual(len(steps), 1)
            self.assertEqual((steps[0]['name'], steps[0]['status'], steps[0]['exitCode'],
                              steps[0]['timeoutSeconds']), ('timeout-probe', 'timed-out', None, 2))

    def run_observation_fixture(self, root, failed_label=None, drifting=False, timeout=None):
        frozen, _ = self.fixtures()
        for suite in ('remote', 'metadata', 'browser'):
            frozen['inventory'].append([suite, suite + '.spec.ts', 'ordinary', 0])
        frozen['inventory'].sort()
        frozen['sourceFiles'] = {}
        path = root / 'inventory.json'
        path.write_text(json.dumps(frozen))
        output = root / 'worker'
        calls = []

        def execute(directory, steps, label, command, env, cwd):
            calls.append(label)
            steps.append({'name': label, 'exitCode': 1 if label == failed_label else 0})
            Path(env['CORDIS_UPSTREAM_REPORT']).write_text('{"originalReportPreserved":true}')
            if label == failed_label:
                if timeout is not None:
                    steps[-1].update(exitCode=None, status='timed-out')
                    raise timeout
                raise candidate.SuiteExecutionError(label + ' failed')

        sources = {'source': 'digest'}
        # Input/report validation has separate counterexample tests. Here external
        # fixtures isolate the policy for continuing independent process failures.
        with patch.object(candidate, 'validate_frozen'), \
             patch.object(candidate, 'fixed_sources', return_value=frozen['sourceTrees']), \
             patch.object(candidate, 'expected_files', return_value={}), \
             patch.object(candidate, 'source_hashes', side_effect=lambda: {'source': 'changed'}
                          if drifting and calls else sources), \
             patch.object(candidate, 'git', return_value='checkpoint'), \
             patch.object(candidate, 'collect', return_value=(frozen['inventory'], {})), \
             patch.object(candidate, 'execute', side_effect=execute), \
             patch.object(candidate, 'candidate_report', return_value=[]):
            with self.assertRaises(subprocess.TimeoutExpired if timeout is not None else ValueError) as caught:
                candidate.run_shard(root, root, path, output, 1, 'workflow:1')
            if timeout is not None:
                self.assertIs(caught.exception, timeout)
        return calls, json.loads((output / 'result.json').read_text()), output

    def test_independent_suites_continue_after_runtime_failure_but_receipt_stays_failed(self):
        for failed in ('linked-resolution-matrix', 'original-composition'):
            with self.subTest(failed=failed), tempfile.TemporaryDirectory() as temporary:
                calls, result, output = self.run_observation_fixture(Path(temporary), failed)
                self.assertEqual(calls, ['linked-resolution-matrix', 'original-composition', 'original-remote',
                                         'original-metadata', 'original-browser', 'original-core'])
                self.assertEqual(result['status'], 'failed')
                self.assertEqual([row['name'] for row in result['suiteFailures']], [failed])
                self.assertEqual(result['outcomes'], [])
                report = output / ('matrix.json' if failed == 'linked-resolution-matrix' else 'composition.json')
                self.assertEqual(json.loads(report.read_text()), {'originalReportPreserved': True})

    def test_source_drift_still_stops_before_the_next_independent_suite(self):
        with tempfile.TemporaryDirectory() as temporary:
            calls, result, _ = self.run_observation_fixture(Path(temporary), drifting=True)
            self.assertEqual(calls, ['linked-resolution-matrix'])
            self.assertEqual(result['status'], 'failed')
            self.assertFalse(result['sourceUnchanged'])

    def test_timeout_stops_before_later_suites_and_retains_failed_receipt(self):
        for failed in ('linked-resolution-matrix', 'original-composition'):
            with self.subTest(failed=failed), tempfile.TemporaryDirectory() as temporary:
                timeout = subprocess.TimeoutExpired('fixture-vitest', 2400)
                calls, result, _ = self.run_observation_fixture(Path(temporary), failed, timeout=timeout)
                expected = ['linked-resolution-matrix']
                if failed == 'original-composition':
                    expected.append('original-composition')
                self.assertEqual(calls, expected)
                self.assertEqual(result['status'], 'failed')
                self.assertTrue(result['sourceUnchanged'])
                self.assertEqual(result['suiteFailures'], [{'name': failed, 'error': str(timeout)}])
                self.assertEqual(result['steps'][-1], {'name': failed, 'exitCode': None, 'status': 'timed-out'})
                self.assertEqual(result['outcomes'], [])

    def test_worker_path_prefers_fixed_tools_without_losing_the_node_path(self):
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {'PATH': 'ambient-node-path'}):
            root = Path(temporary)
            env = candidate.environment(root, root, root, 'composition')
            tools = str(candidate.ROOT / 'reference/candidate-tools/node_modules/.bin')
            self.assertEqual(env['PATH'].split(os.pathsep), [tools, 'ambient-node-path'])

    def collector_fixture(self, directory, source):
        entry = candidate.ROOT / 'reference/candidate-tools/node_modules/vitest/dist/index.js'
        fixture = directory / 'fixture.spec.mjs'
        fixture.write_text('import { describe, it, expect } from ' + json.dumps(entry.as_uri()) + ';\n' + source,
                           encoding='utf8')
        config = directory / 'config.mjs'
        config.write_text('export default ' + json.dumps({'root': directory.as_posix(), 'test': {
            'include': ['fixture.spec.mjs'], 'watch': False, 'maxWorkers': 1, 'fileParallelism': False}}), encoding='utf8')
        return config

    def test_real_collector_retains_skipped_cases_without_executing_or_changing_runtime_skips(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            config = self.collector_fixture(root, '''
import { writeFileSync } from 'node:fs';
it('active', () => { writeFileSync(new URL('./executed', import.meta.url), 'ran'); expect(1).toBe(1) });
describe.skip('excluded', () => {
  it.each([1, 2])('same title', () => { throw new Error('Skipped body must not execute') });
});
describe.skipIf(true)('platform excluded', () => {
  it('third skipped case', () => { throw new Error('Skipped body must not execute') });
});
''')
            discovery = root / 'discovery.json'
            subprocess.run(['node', candidate.COLLECTOR, str(config), str(discovery)],
                           cwd=root, check=True, capture_output=True, text=True, timeout=60)
            cases = json.loads(discovery.read_text(encoding='utf8'))
            self.assertFalse((root / 'executed').exists(), 'Collection executed a test body')
            self.assertEqual([(case['name'], case['collectionState']) for case in cases], [
                ('active', 'pending'), ('excluded > same title', 'skipped'),
                ('excluded > same title', 'skipped'), ('platform excluded > third skipped case', 'skipped')])
            self.assertEqual({Path(case['file']).resolve() for case in cases}, {root / 'fixture.spec.mjs'})
            report = root / 'runtime.json'
            subprocess.run(['node', candidate.VITEST, 'run', '--config', str(config),
                            '--reporter=json', '--outputFile', str(report)],
                           cwd=root, check=True, capture_output=True, text=True, timeout=60)
            runtime = json.loads(report.read_text(encoding='utf8'))
            self.assertEqual((runtime['numTotalTests'], runtime['numPassedTests'],
                              runtime['numPendingTests'], runtime['numFailedTests']), (4, 1, 3, 0))
            self.assertEqual((root / 'executed').read_text(), 'ran')
            outcomes = runtime['testResults'][0]['assertionResults']
            self.assertEqual([case['status'] for case in outcomes], ['passed', 'skipped', 'skipped', 'skipped'])

    def test_real_collector_rejects_collection_errors_and_empty_discovery(self):
        for source in ("it('declared before failure', () => {}); throw new Error('collection explosion');",
                       "describe('empty', () => {});"):
            with self.subTest(source=source), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary).resolve()
                config = self.collector_fixture(root, source)
                discovery = root / 'discovery.json'
                result = subprocess.run(['node', candidate.COLLECTOR, str(config), str(discovery)],
                                        cwd=root, capture_output=True, text=True, timeout=60)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(discovery.exists(), 'Failed collection published an accepted inventory')

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
                  "runId": "workflow:1", "tools": tools, "sourceTrees": trees, "inventory": inventory,
                  "sourceAdaptation": candidate.source_adaptation()}
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
                    "sourceSha256": {"source": "digest"}, "sourceTrees": trees, "sourceAdaptation": candidate.source_adaptation()},
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
            lambda r: r[0]["identity"]["sourceAdaptation"].update(commit=candidate.source_adaptation()["upstreamCommit"]),
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

    def test_discovery_cannot_omit_or_relabel_the_source_adaptation(self):
        for value in (None, {"commit": candidate.source_adaptation()["upstreamCommit"]}):
            frozen, reports = self.fixtures()
            frozen["sourceAdaptation"] = value
            with self.assertRaisesRegex(ValueError, "adaptation differs"):
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
