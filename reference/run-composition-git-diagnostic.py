"""One diagnostic-only replay of the frozen 3b composition selection and environment."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--dsh', type=Path, required=True)
parser.add_argument('--origin', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(repo / 'scripts'))
from verify_candidate_upstream import (CONFIG, DSH, ORIGIN, VITEST, SuiteExecutionError,
                                       environment, execute, fixed_sources, git)

sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
read = lambda path: json.loads(path.read_text(encoding='utf-8'))
write = lambda path, value: path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')
dsh, origin, output = (path.resolve() for path in (args.dsh, args.origin, args.output))
output.mkdir(parents=True, exist_ok=False)
frozen = read(repo / 'reference/diagnostic-3b-object-sha256.json')
for file, identity in frozen['files'].items():
    if sha(repo / file) != identity['sha256']:
        raise SystemExit('Diagnostic production file differs from frozen Git object: ' + file)
selection = repo / 'reference/diagnostic-composition-selection.json'
if sha(selection) != frozen['selectionSha256']:
    raise SystemExit('Diagnostic selection differs from the failed production batch')
fixed_sources(dsh, origin)
source_before = {name: {'commit': git(path, 'rev-parse', 'HEAD'), 'tree': git(path, 'rev-parse', 'HEAD^{tree}'),
                        'trackedStatus': git(path, 'status', '--porcelain', '--untracked-files=no')}
                 for name, path in [('dsh', dsh), ('origin', origin)]}
tests = {file: sha(dsh / file) for file in read(selection)['include']}
identity = {'diagnosticCommit': git(repo, 'rev-parse', 'HEAD'),
            'workflow': {key: os.environ.get(key) for key in ('GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT')},
            'frozenProductionFiles': frozen, 'sourceBefore': source_before,
            'testSha256': tests, 'nodeVersion': subprocess.check_output(['node', '--version'], text=True).strip(),
            'reason': 'The previous three-process diagnostic omitted the production PATH prefix. This single replay uses the frozen production environment helper, actual dsh cwd, and full 1450-case composition selection. No retry acceptance or source assertion change.'}
write(output / 'identity.json', identity)
directory = output / '1'
directory.mkdir()
report_path = directory / 'report.json'
env = dict(environment(dsh, origin, directory, 'composition'),
           CORDIS_UPSTREAM_SELECTION=str(selection), CORDIS_UPSTREAM_REPORT=str(report_path),
           GIT_TRACE2_EVENT=str(directory / 'git-trace.jsonl'))
steps = []
failure = None
try:
    execute(directory, steps, 'original-composition', ['node', VITEST, 'run', '--config', CONFIG], env, cwd=dsh)
except (SuiteExecutionError, subprocess.TimeoutExpired) as error:
    failure = str(error)
finally:
    unchanged = (all(not git(path, 'status', '--porcelain', '--untracked-files=no') for path in (dsh, origin))
                 and all(sha(dsh / file) == expected for file, expected in tests.items()))
    result = {'steps': steps, 'failure': failure, 'sourceUnchanged': unchanged,
              'diagnosticOnly': True, 'productionBatchStillFailed': '39922002150:1'}
    if report_path.exists():
        report = read(report_path)
        result['counts'] = {key: report[key] for key in ('numTotalTests', 'numPassedTests', 'numFailedTests', 'numPendingTests')}
        assertions = [case for file in report['testResults'] for case in file['assertionResults']]
        result['statusCounts'] = dict(Counter(case['status'] for case in assertions))
        result['failedCases'] = [case for case in assertions if case['status'] == 'failed']
        result['gitFallbackCases'] = [case for case in assertions if 'through real Git and pnpm with private repository SSH fallback' in case['fullName']]
        result['fileOrder'] = [file['name'] for file in sorted(report['testResults'], key=lambda file: file['startTime'])]
    write(output / 'results.json', result)
if failure or not unchanged or result.get('counts', {}).get('numTotalTests') != 1450:
    raise SystemExit(1)
