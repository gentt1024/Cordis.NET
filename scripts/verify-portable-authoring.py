"""Record a Linux authoring batch and consume those exact bytes on another runner."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import sys


def hashes(directory):
    return {path.relative_to(directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob('*')) if path.is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--record', action='store_true')
    parser.add_argument('--packages', type=Path, required=True)
    parser.add_argument('--inputs', type=Path, required=True)
    parser.add_argument('--output', type=Path)
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
    batch = {'packages': hashes(packages), 'runtime': hashes(runtime),
             'tool': {'file': tool.name, 'sha256': hashlib.sha256(tool.read_bytes()).hexdigest()}}
    if not batch['packages'] or not batch['runtime']:
        raise RuntimeError('Package batch and browser runtime must be present')
    if options.record:
        manifest.write_text(json.dumps({'checkoutCommit': commit,
            'branchCommit': os.environ.get('AUTHORING_BRANCH_COMMIT', commit),
            'producerPlatform': platform.system(), **batch}, indent=2) + '\n', encoding='utf-8')
        return
    if options.output is None:
        parser.error('--output is required when consuming a batch')
    output = options.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    producer = json.loads(manifest.read_text(encoding='utf-8'))
    if producer['checkoutCommit'] != commit or producer['producerPlatform'] != 'Linux':
        raise RuntimeError('Consumer must use the same checkout and a Linux-produced batch')
    for key, value in batch.items():
        if producer[key] != value:
            raise RuntimeError(f'Downloaded {key} bytes differ from producer receipt')
    result = {'status': 'failed', 'producer': producer, 'consumerPlatform': platform.system(),
              'checkoutCommit': commit, 'steps': [], 'aotRequested': False}
    commands = [
        ('client-safety', ['node', 'scripts/verify-client-safety.mjs', str(runtime),
                           str(output / 'client-safety')]),
        ('management', [sys.executable, 'scripts/verify-typert-dotnet.py', '--packages', str(packages),
                        '--output', str(output / 'management'), '--management-only']),
        ('browser', ['node', 'scripts/verify-client-author-package.mjs', str(runtime),
                     str(output / 'browser'), '--headless']),
    ]
    env = dict(os.environ, CORDIS_CLIENT_TOOLS_TGZ=str(tool))
    try:
        for name, command in commands:
            print('RUN portable ' + name, flush=True)
            with (output / (name + '.log')).open('wb') as log:
                process = subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT)
            result['steps'].append({'name': name, 'exitCode': process.returncode})
            if process.returncode:
                raise RuntimeError(f'Portable {name} failed; see {output / (name + ".log")}')
        result['status'] = 'requested-checks-passed'
    finally:
        (output / 'results.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')


if __name__ == '__main__':
    main()
