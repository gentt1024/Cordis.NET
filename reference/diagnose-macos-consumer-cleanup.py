"""One cross-run diagnostic replay; never an admission receipt."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

FROZEN = 'f32047a001919a14adefa6ce8a8d2c2936f07972'
PRODUCER_RUN = '39928987495'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--inputs', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--mount', type=Path, required=True)
    args = parser.parse_args()
    source, packages, inputs, output, mount = (
        value.resolve() for value in (args.source, args.packages, args.inputs, args.output, args.mount))
    output.mkdir(parents=True, exist_ok=False)
    consumers = output / 'consumers'
    consumers.mkdir()
    runner_temp = Path(os.environ['RUNNER_TEMP']).resolve()
    dotnet = shutil.which('dotnet')
    if not dotnet:
        raise RuntimeError('Selected SDK executable is absent')
    dotnet = str(Path(dotnet).resolve())
    steps, errors = [], []
    result = {'diagnosticOnly': True, 'admissionReceipt': False, 'status': 'failed',
              'productionCommit': FROZEN, 'producerRun': PRODUCER_RUN + ':1',
              'identity': {name: os.environ.get(name) for name in (
                  'GITHUB_SHA', 'GITHUB_RUN_ID', 'GITHUB_RUN_ATTEMPT', 'GITHUB_JOB',
                  'GITHUB_REPOSITORY', 'RUNNER_OS', 'RUNNER_ARCH', 'ImageOS', 'ImageVersion')},
              'selectedDotnet': dotnet, 'consumerTmpdir': os.environ.get('TMPDIR'),
              'mount': str(mount), 'steps': steps, 'errors': errors}

    def write_result():
        (output / 'diagnostic.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')

    def run(name, command, *, timeout=120, env=None, cwd=source, accepted=(0,)):
        start = time.monotonic()
        record = {'name': name, 'command': [str(value) for value in command],
                  'startedUtc': datetime.now(timezone.utc).isoformat(), 'timeoutSeconds': timeout}
        stdout_path, stderr_path = output / (name + '.stdout.log'), output / (name + '.stderr.log')
        with stdout_path.open('wb') as stdout, stderr_path.open('wb') as stderr:
            try:
                completed = subprocess.run(command, cwd=cwd, env=env, stdout=stdout, stderr=stderr, timeout=timeout)
                record['exitCode'] = completed.returncode
            except subprocess.TimeoutExpired:
                record.update(exitCode=None, timedOut=True)
        record['seconds'] = round(time.monotonic() - start, 3)
        steps.append(record)
        if record['exitCode'] not in accepted:
            errors.append(name + ' failed: ' + str(record['exitCode']))
        write_result()
        print(json.dumps(record), flush=True)
        return record, stdout_path.read_text(encoding='utf-8', errors='replace')

    def snapshot(label):
        record, ps = run(label + '-processes', ['/bin/ps', '-axo', 'pid,ppid,pgid,lstart,state,comm'], timeout=30)
        # Preserve only relevant process metadata, never arguments or environment.
        lines = [line for line in ps.splitlines() if re.search(r'dotnet|VBCSCompiler|MSBuild|diskimages|hdiutil|Chrome', line, re.I)]
        (output / (label + '-processes.stdout.log')).write_text('\n'.join(lines) + '\n', encoding='utf-8')
        pids = [line.split()[0] for line in lines if line.split() and line.split()[0].isdigit()]
        run(label + '-volume-lsof', ['sudo', '-n', '/usr/sbin/lsof', '-nP', '+c', '0', '+f', '--', str(mount)],
            timeout=60, accepted=(0, 1))
        if pids:
            run(label + '-unix-pipes-lsof', ['sudo', '-n', '/usr/sbin/lsof', '-nP', '+c', '0', '-a', '-p', ','.join(pids), '-U'],
                timeout=60, accepted=(0, 1))
            run(label + '-process-files-lsof', ['sudo', '-n', '/usr/sbin/lsof', '-nP', '+c', '0', '-p', ','.join(pids)],
                timeout=60, accepted=(0, 1))
        result[label + 'ProcessIds'] = pids

    try:
        if os.environ.get('TMPDIR', '').rstrip('/') != str(mount / 'tmp') or not os.path.ismount(mount):
            raise RuntimeError('Consumers must use the mounted case-sensitive volume')
        if subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=source, text=True).strip() != FROZEN:
            raise RuntimeError('Wrong production checkout')
        if subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=no'], cwd=source, text=True).strip():
            raise RuntimeError('Modified production checkout')
        sys.path.insert(0, str(source / 'scripts'))
        from verify import artifact_hashes, source_hashes
        sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
        read = lambda path: json.loads(path.read_text(encoding='utf-8-sig'))
        sources = source_hashes()
        runtime = inputs / 'client-modules'
        tools = list((inputs / 'verification/client-author-package').glob('*.tgz'))
        if len(tools) != 1:
            raise RuntimeError('Expected the one original author-tool archive')
        tool = tools[0]
        verification_path = inputs / 'verification/verification.json'
        batch = {'packages': artifact_hashes(packages), 'runtime': artifact_hashes(runtime),
                 'tool': {'file': tool.name, 'sha256': sha(tool)}, 'verificationSha256': sha(verification_path)}
        producer = read(inputs / 'portable-authoring-inputs.json')
        verification = read(verification_path)
        for value in (producer, verification):
            if value.get('runId') != PRODUCER_RUN or value.get('runAttempt') != '1' or value.get('sourceSha256') != sources:
                raise RuntimeError('Original producer identity or frozen source hashes differ')
        if producer['checkoutCommit'] != FROZEN or producer['producerPlatform'] != 'Linux' or verification['commit'] != FROZEN:
            raise RuntimeError('Inputs are not the frozen Linux batch')
        if verification.get('status') != 'requested-checks-passed' or verification.get('sourceUnchanged') is not True:
            raise RuntimeError('Original producer verification did not pass unchanged')
        if any(producer[key] != value for key, value in batch.items()):
            raise RuntimeError('Downloaded input bytes differ from the original producer receipt')
        if not batch['packages'] or not batch['runtime']:
            raise RuntimeError('Empty original input batch')
        result.update(inputHashes=batch, productionSourceSha256=sources,
                      formalLockSha256=sha(source / 'upstream.lock.json'),
                      note='Cross-run diagnostic only; original producer receipt and diagnostic run identity remain separate.')
        selected, version = run('selected-sdk-version', [dotnet, '--version'])
        expected_sdk = read(source / 'global.json')['sdk']['version']
        if selected['exitCode'] != 0 or version.strip() != expected_sdk:
            raise RuntimeError('SDK selection differs from frozen global.json')
        run('selected-sdk-info', [dotnet, '--info'])
        # These are the nine unchanged macOS commands, in production portable order.
        commands = [
            ('client-safety', ['node', 'scripts/verify-client-safety.mjs', str(runtime), str(consumers / 'client-safety')]),
            ('typert', [sys.executable, 'scripts/verify-typert.py', '--packages', str(packages), '--runtime', str(runtime), '--output', str(consumers / 'typert')]),
            ('typert-dotnet', [sys.executable, 'scripts/verify-typert-dotnet.py', '--packages', str(packages), '--output', str(consumers / 'typert-dotnet')]),
            ('native-release-browser', [sys.executable, 'scripts/verify-client-release-browser.py', '--packages', str(packages), '--runtime', str(runtime), '--output', str(consumers / 'native-release-browser')]),
            ('browser', ['node', 'scripts/verify-client-author-package.mjs', str(runtime), str(consumers / 'browser'), '--headless']),
        ]
        for name in ('remote-metadata', 'remote-identity', 'remote-access', 'access-client'):
            commands.append((name, [sys.executable, 'scripts/verify-' + name + '.py', '--packages', str(packages), '--output', str(consumers / name)]))
        env = dict(os.environ, CORDIS_CLIENT_TOOLS_TGZ=str(tool))
        result['consumerCommands'] = commands
        for name, command in commands:
            record, _ = run('consumer-' + name, command, timeout=1800, env=env)
            if record['exitCode'] != 0:
                raise RuntimeError('Original consumer failed: ' + name)
        unchanged = batch == {'packages': artifact_hashes(packages), 'runtime': artifact_hashes(runtime),
                              'tool': {'file': tool.name, 'sha256': sha(tool)}, 'verificationSha256': sha(verification_path)}
        result['sourceUnchanged'] = source_hashes() == sources
        result['inputBytesUnchanged'] = unchanged
        if not unchanged or not result['sourceUnchanged']:
            raise RuntimeError('Consumer modified frozen source or original input bytes')
        result['nineOriginalConsumersPassed'] = True
    except Exception as error:
        errors.append(str(error))
    finally:
        # Keep the consumers' exact TMPDIR and selected SDK until server shutdown completes.
        result['shutdownTmpdir'] = os.environ.get('TMPDIR')
        snapshot('before-shutdown')
        shutdown, _ = run('build-server-shutdown', [dotnet, 'build-server', 'shutdown'], timeout=120)
        result['shutdownExitCode'] = shutdown['exitCode']
        snapshot('after-shutdown')
        os.environ['TMPDIR'] = str(runner_temp)
        with Path(os.environ['GITHUB_ENV']).open('a', encoding='utf-8') as github_env:
            github_env.write('TMPDIR=' + str(runner_temp) + '\n')
        result['detachTmpdir'] = os.environ['TMPDIR']
        # Exactly one ordinary detach. Failure is retained and makes the diagnostic fail.
        detached, _ = run('detach-once', ['hdiutil', 'detach', str(mount)], timeout=180)
        result['detachExitCode'] = detached['exitCode']
        result['mountStillPresent'] = os.path.ismount(mount)
        if result['mountStillPresent']:
            errors.append('Task volume remains mounted')
        result['status'] = 'diagnostic-completed' if not errors else 'failed'
        write_result()
    if errors:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
