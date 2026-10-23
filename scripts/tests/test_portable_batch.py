import copy
import hashlib
import json
from pathlib import Path
import runpy
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from verify import artifact_hashes, check_client_artifacts, verified_author_tool
SCRIPT = runpy.run_path(str(Path(__file__).resolve().parents[1] / 'verify-portable-authoring.py'))


class PortableBatchTests(unittest.TestCase):
    def batch(self, root):
        packages = root / 'packages'
        packages.mkdir()
        (packages / 'Cordis.NET.Core.test.nupkg').write_bytes(b'the tested package')
        return SCRIPT['hashes'](packages)

    def verified_report(self, batch):
        return {'status': 'requested-checks-passed', 'sourceUnchanged': True,
                'commit': 'checkpoint', 'sourceSha256': {'input': 'source'},
                'steps': [{'name': 'package-batch-unchanged', 'exitCode': 0, 'sha256': batch['packages']},
                          {'name': 'client-runtime-unchanged', 'exitCode': 0, 'sha256': batch['runtime']},
                          {'name': 'client-author-tool-unchanged', 'exitCode': 0, 'tool': batch['tool']}]}

    def test_receipt_accepts_exact_verified_package_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            packages = self.batch(Path(directory))
            self.assertEqual(packages, {'Cordis.NET.Core.test.nupkg': hashlib.sha256(b'the tested package').hexdigest()})
            batch = {'packages': packages, 'runtime': {'client.mjs': 'tested runtime'},
                     'tool': {'file': 'author.tgz', 'sha256': 'tested archive'}}
            report = self.verified_report(batch)
            SCRIPT['verified_inputs'](report, 'checkpoint', {'input': 'source'}, batch)
            (Path(directory) / 'packages/Cordis.NET.Core.test.nupkg').write_bytes(b'a later package')
            changed = {**batch, 'packages': SCRIPT['hashes'](Path(directory) / 'packages')}
            with self.assertRaisesRegex(RuntimeError, 'exact batch'):
                SCRIPT['verified_inputs'](report, 'checkpoint', {'input': 'source'}, changed)

    def test_failed_drifting_unsealed_or_different_source_verification_is_refused(self):
        batch = {'packages': {'p': 'bytes'}, 'runtime': {'client.mjs': 'tested runtime'},
                 'tool': {'file': 'author.tgz', 'sha256': 'tested archive'}}
        report = self.verified_report(batch)
        mutations = [lambda r: r.update(status='failed'), lambda r: r.update(sourceUnchanged=False),
                     lambda r: r.update(commit='another-checkpoint'),
                     lambda r: r.update(sourceSha256={'input': 'changed'}),
                     lambda r: r.update(runId='another-run'),
                     lambda r: r.update(runAttempt='2'),
                     lambda r: r['steps'].clear(),
                     lambda r: r['steps'].append(copy.deepcopy(r['steps'][0]))]
        for mutate in mutations:
            with self.subTest(mutation=mutations.index(mutate)):
                changed = copy.deepcopy(report)
                mutate(changed)
                with self.assertRaises(RuntimeError):
                    SCRIPT['verified_inputs'](changed, 'checkpoint', {'input': 'source'}, batch)

    def test_runtime_and_tool_replacements_and_missing_duplicate_or_failed_seals_are_refused(self):
        batch = {'packages': {'p': 'bytes'}, 'runtime': {'client.mjs': 'tested runtime'},
                 'tool': {'file': 'author.tgz', 'sha256': 'tested archive'}}
        for category in ('packages', 'runtime', 'tool'):
            with self.subTest(replaced=category):
                changed = copy.deepcopy(batch)
                changed[category] = {'replacement': 'new bytes'}
                with self.assertRaisesRegex(RuntimeError, 'exact batch'):
                    SCRIPT['verified_inputs'](self.verified_report(batch), 'checkpoint', {'input': 'source'}, changed)
        for index in range(3):
            for mutation in ('missing', 'duplicate', 'failed'):
                with self.subTest(seal=index, mutation=mutation):
                    report = self.verified_report(batch)
                    if mutation == 'missing':
                        report['steps'].pop(index)
                    elif mutation == 'duplicate':
                        report['steps'].append(copy.deepcopy(report['steps'][index]))
                    else:
                        report['steps'][index]['exitCode'] = 1
                    with self.assertRaisesRegex(RuntimeError, 'exact batch'):
                        SCRIPT['verified_inputs'](report, 'checkpoint', {'input': 'source'}, batch)

    def test_producer_uses_tested_runtime_snapshot_and_author_consumer_archive_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runtime = root / 'runtime'
            runtime.mkdir()
            client = runtime / 'client.mjs'
            client.write_bytes(b'tested runtime')
            archive = root / 'author.tgz'
            archive.write_bytes(b'tested installed archive')
            digest = hashlib.sha256(archive.read_bytes()).hexdigest()
            (root / 'result.json').write_text(json.dumps({'status': 'passed',
                'toolArchive': str(archive), 'toolSha256': digest}), encoding='utf8')
            before = artifact_hashes(runtime)
            tool, identity = verified_author_tool(root)
            self.assertEqual(identity, {'file': 'author.tgz', 'sha256': digest})
            check_client_artifacts(runtime, before, tool, identity)
            client.write_bytes(b'replaced after tests')
            with self.assertRaisesRegex(RuntimeError, 'runtime changed'):
                check_client_artifacts(runtime, before, tool, identity)
            client.write_bytes(b'tested runtime')
            archive.write_bytes(b'replaced after tests')
            with self.assertRaisesRegex(RuntimeError, 'archive changed'):
                check_client_artifacts(runtime, before, tool, identity)
            with self.assertRaisesRegex(RuntimeError, 'bytes recorded by its consumer'):
                verified_author_tool(root)

    def test_mixed_run_source_platform_or_downloads_cannot_pass(self):
        batch = {'packages': {'p': 'bytes'}, 'runtime': {'client': 'bytes'},
                 'tool': {'file': 'author.tgz', 'sha256': 'bytes'}, 'verificationSha256': 'verified'}
        producer = {'checkoutCommit': 'checkpoint', 'producerPlatform': 'Linux',
                    'sourceSha256': {'input': 'source'}, 'runId': 'one-run', 'runAttempt': '1', **batch}
        SCRIPT['verify_producer'](producer, batch, 'checkpoint', {'input': 'source'}, 'one-run', '1')
        mutations = [lambda r: r.update(checkoutCommit='another-checkpoint'),
                     lambda r: r.update(producerPlatform='Darwin'),
                     lambda r: r.update(sourceSha256={'input': 'changed'}),
                     lambda r: r.update(runId='another-run'),
                     lambda r: r.update(runAttempt='2'),
                     lambda r: r['packages'].update(p='different bytes'),
                     lambda r: r['runtime'].update(client='different bytes'),
                     lambda r: r['tool'].update(sha256='different bytes'),
                     lambda r: r.update(verificationSha256='another receipt')]
        for mutate in mutations:
            with self.subTest(mutation=mutations.index(mutate)):
                changed = copy.deepcopy(producer)
                mutate(changed)
                with self.assertRaises(RuntimeError):
                    SCRIPT['verify_producer'](changed, batch, 'checkpoint', {'input': 'source'}, 'one-run', '1')


if __name__ == '__main__':
    unittest.main()
