"""Record a verified Linux batch and consume those exact bytes on another runner."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

from verify import source_hashes


def hashes(directory):
    return {path.relative_to(directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob('*')) if path.is_file()}


def verify_producer(producer, batch, commit, sources, run_id, run_attempt):
    if producer['checkoutCommit'] != commit or producer['producerPlatform'] != 'Linux':
        raise RuntimeError('Consumer must use the same checkout and a Linux-produced batch')
    if producer['sourceSha256'] != sources:
        raise RuntimeError('Consumer source bytes differ from the verified producer')
    if producer['runId'] != run_id or producer['runAttempt'] != run_attempt:
        raise RuntimeError('Consumer and producer must belong to the same workflow run and attempt')
    for key, value in batch.items():
        if producer[key] != value:
            raise RuntimeError(f'Downloaded {key} bytes differ from producer receipt')


def verified_packages(verification, commit, sources, packages, run_id=None, run_attempt=None):
    if verification.get('status') != 'requested-checks-passed' or verification.get('sourceUnchanged') is not True:
        raise RuntimeError('Producer verification did not pass without source changes')
    if verification.get('commit') != commit or verification.get('sourceSha256') != sources:
        raise RuntimeError('Producer verification belongs to another source checkpoint')
    if verification.get('runId') != run_id or verification.get('runAttempt') != run_attempt:
        raise RuntimeError('Producer verification belongs to another workflow run or attempt')
    seals = [step for step in verification['steps'] if step['name'] == 'package-batch-unchanged']
    if len(seals) != 1 or seals[0].get('exitCode') != 0 or seals[0].get('sha256') != packages:
        raise RuntimeError('Packages differ from the exact batch consumed by verification')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--record', action='store_true')
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--inputs', type=Path, required=True)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--aot', action='store_true', help='Run independent static package callers with Native AOT too')
    options = parser.parse_args()
    packages, inputs = options.packages.resolve(), options.inputs.resolve()
    runtime = inputs / 'client-modules'
    tools = inputs / 'verification/client-author-package'
    archives = list(tools.glob('*.tgz'))
    if len(archives) != 1:
        raise RuntimeError('Expected exactly one installed author-tool archive')
    tool = archives[0]
    manifest = inputs / 'portable-authoring-inputs.json'
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    sources = source_hashes()
    run_id = os.environ.get('GITHUB_RUN_ID')
    run_attempt = os.environ.get('GITHUB_RUN_ATTEMPT')
    verification = inputs / 'verification/verification.json'
    batch = {'packages': hashes(packages), 'runtime': hashes(runtime),
             'tool': {'file': tool.name, 'sha256': hashlib.sha256(tool.read_bytes()).hexdigest()},
             'verificationSha256': hashlib.sha256(verification.read_bytes()).hexdigest()}
    if not batch['packages'] or not batch['runtime']:
        raise RuntimeError('Package batch and browser runtime must be present')
    verified_packages(json.loads(verification.read_text(encoding='utf-8')), commit, sources, batch['packages'], run_id, run_attempt)
    if options.record:
        if platform.system() != 'Linux':
            raise RuntimeError('The portable package producer must run on Linux')
        manifest.write_text(json.dumps({'checkoutCommit': commit,
            'branchCommit': os.environ.get('AUTHORING_BRANCH_COMMIT', commit),
            'producerPlatform': platform.system(), 'runId': run_id, 'runAttempt': run_attempt,
            'sourceSha256': sources, **batch}, indent=2) + '\n', encoding='utf-8')
        return
    if options.output is None:
        parser.error('--output is required when consuming a batch')
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    producer = json.loads(manifest.read_text(encoding='utf-8'))
    verify_producer(producer, batch, commit, sources, run_id, run_attempt)
    result = {'status': 'failed', 'producer': producer, 'consumerPlatform': platform.system(),
              'checkoutCommit': commit, 'steps': [], 'aotRequested': options.aot}
    aot = ['--aot'] if options.aot else []
    commands = [
        ('client-safety', ['node', 'scripts/verify-client-safety.mjs', str(runtime),
                           str(output / 'client-safety')]),
        ('typert', [sys.executable, 'scripts/verify-typert.py', '--packages', str(packages),
                    '--runtime', str(runtime), '--output', str(output / 'typert'), *aot]),
        ('typert-dotnet', [sys.executable, 'scripts/verify-typert-dotnet.py', '--packages', str(packages),
                           '--output', str(output / 'typert-dotnet'), *aot]),
        ('native-release-browser', [sys.executable, 'scripts/verify-client-release-browser.py',
                                     '--packages', str(packages), '--runtime', str(runtime),
                                     '--output', str(output / 'native-release-browser')]),
        ('browser', ['node', 'scripts/verify-client-author-package.mjs', str(runtime),
                     str(output / 'browser'), '--headless']),
    ]
    for name in ('remote-metadata', 'remote-identity', 'remote-access', 'access-client'):
        commands.append((name, [sys.executable, 'scripts/verify-' + name + '.py', '--packages', str(packages),
                                '--output', str(output / name)]))
    if platform.system() in ('Windows', 'Linux'):
        architecture = 'arm64' if platform.machine().lower() in ('aarch64', 'arm64') else 'x64'
        rid = ('win-' if os.name == 'nt' else 'linux-') + architecture
        commands.extend([
            ('clr-deployment', [sys.executable, 'scripts/verify-clr-deployment.py', '--dotnet', shutil.which('dotnet') or 'dotnet',
                                '--rid', rid, '--packages', str(packages), '--output', str(output / 'clr-deployment')]),
            ('clr-multi-entry', [sys.executable, 'scripts/verify-clr-multi-entry.py', '--packages', str(packages),
                                 '--output', str(output / 'clr-multi-entry')]),
        ])
    env = dict(os.environ, CORDIS_CLIENT_TOOLS_TGZ=str(tool))
    try:
        for name, command in commands:
            print('RUN portable ' + name, flush=True)
            with (output / (name + '.log')).open('wb') as log:
                process = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT)
            result['steps'].append({'name': name, 'exitCode': process.returncode})
            if process.returncode:
                raise RuntimeError(f'Portable {name} failed; see {output / (name + ".log")}')
        verify_producer(producer, {'packages': hashes(packages), 'runtime': hashes(runtime),
            'tool': {'file': tool.name, 'sha256': hashlib.sha256(tool.read_bytes()).hexdigest()},
            'verificationSha256': hashlib.sha256(verification.read_bytes()).hexdigest()},
            commit, source_hashes(), run_id, run_attempt)
        result['status'] = 'requested-checks-passed'
    finally:
        (output / 'results.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
