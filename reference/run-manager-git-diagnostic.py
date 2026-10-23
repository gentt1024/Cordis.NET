"""Diagnostic only: repeat the unchanged upstream manager file with Git trace2 events."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time

parser = argparse.ArgumentParser()
parser.add_argument('--repository', type=Path, required=True)
parser.add_argument('--dsh', type=Path, required=True)
parser.add_argument('--origin', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--repeat', type=int, default=3)
args = parser.parse_args()
if not 1 <= args.repeat <= 3:
    parser.error('--repeat must be between 1 and 3')
repo, dsh, origin, output = (path.resolve() for path in (args.repository, args.dsh, args.origin, args.output))
sys.path.insert(0, str(repo / 'scripts'))
from verify_candidate_upstream import environment
output.mkdir(parents=True, exist_ok=True)
test = dsh / 'packages/boot/plugin-manager/tests/manager.spec.ts'
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
git = lambda directory, *tail: subprocess.check_output(['git', '-C', str(directory), *tail], text=True).strip()
before = {name: {'commit': git(path, 'rev-parse', 'HEAD'), 'trackedStatus': git(path, 'status', '--porcelain', '--untracked-files=no')}
          for name, path in [('repository', repo), ('dsh', dsh), ('origin', origin)]}
if before['dsh']['commit'] != '687f282e8625c6b8d11ff58211a90524be2e4317':
    raise SystemExit('Wrong candidate source')
if before['origin']['commit'] != '56b3d4f725681cf4556c1a8695a709cc3b6eed74':
    raise SystemExit('Wrong origin source')
if before['dsh']['trackedStatus'] or before['origin']['trackedStatus']:
    raise SystemExit('Modified original source')
test_sha = sha(test)
rows = []
for attempt in range(1, args.repeat + 1):
    directory = output / str(attempt)
    directory.mkdir(exist_ok=False)
    env = {**environment(dsh, origin, directory, 'composition'),
           'CORDIS_UPSTREAM_REPORT': str(directory / 'report.json'),
           'GIT_TRACE2_EVENT': str(directory / 'git-trace.jsonl')}
    command = ['node', str(repo / 'reference/candidate-tools/node_modules/vitest/vitest.mjs'), 'run',
               '--config', str(repo / 'reference/vitest-candidate.config.mjs'),
               'packages/boot/plugin-manager/tests/manager.spec.ts']
    start = time.monotonic()
    timed_out = False
    with (directory / 'stdout.log').open('wb') as stdout, (directory / 'stderr.log').open('wb') as stderr:
        try:
            completed = subprocess.run(command, cwd=dsh, env=env, stdout=stdout, stderr=stderr, timeout=180)
            exit_code = completed.returncode
        except subprocess.TimeoutExpired:
            exit_code = None
            timed_out = True
    row = {'attempt': attempt, 'exitCode': exit_code, 'timedOut': timed_out,
           'seconds': round(time.monotonic() - start, 3), 'selected': []}
    report_path = directory / 'report.json'
    if report_path.exists():
        report = json.loads(report_path.read_text(encoding='utf-8'))
        row['selected'] = [assertion for file in report['testResults'] for assertion in file['assertionResults']
                           if 'through real Git and pnpm with private repository SSH fallback' in assertion['fullName']]
        row['counts'] = {name: report[name] for name in ['numTotalTests', 'numPassedTests', 'numFailedTests', 'numPendingTests']}
    trace_path = directory / 'git-trace.jsonl'
    events = [json.loads(line) for line in trace_path.read_text(encoding='utf-8').splitlines()] if trace_path.exists() else []
    sessions = {event['sid']: event for event in events if event['event'] == 'start'}
    row['gitSessions'] = []
    for sid, event in sessions.items():
        terminal = [item for item in events if item['sid'] == sid and item['event'] in ('exit', 'atexit')]
        row['gitSessions'].append({'sid': sid, 'argv': event.get('argv'), 'start': event.get('time'), 'terminal': terminal})
    row['sourceUnchanged'] = (sha(test) == test_sha
        and all(not git(path, 'status', '--porcelain', '--untracked-files=no') for path in (dsh, origin)))
    rows.append(row)
    (output / 'results.json').write_text(json.dumps({'before': before, 'testSha256': test_sha, 'workflow': {name: os.environ.get(name) for name in ['GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT']}, 'configSha256': sha(repo / 'reference/vitest-candidate.config.mjs'), 'toolLockSha256': sha(repo / 'reference/candidate-tools/package-lock.json'), 'nodeVersion': subprocess.check_output(['node', '--version'], text=True).strip(), 'attempts': rows}, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'attempt': attempt, 'exitCode': exit_code, 'timedOut': timed_out, 'seconds': row['seconds'],
                      'selectedStatus': [case['status'] for case in row['selected']], 'sourceUnchanged': row['sourceUnchanged']}), flush=True)
    if timed_out:
        break
if any(row['exitCode'] != 0 or not row['sourceUnchanged'] or len(row['selected']) != 3
       or row.get('counts') != {'numTotalTests': 108, 'numPassedTests': 108, 'numFailedTests': 0, 'numPendingTests': 0}
       or any(case['status'] != 'passed' for case in row['selected']) for row in rows):
    raise SystemExit(1)
