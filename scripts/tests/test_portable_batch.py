import copy
import hashlib
from pathlib import Path
import runpy
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
SCRIPT = runpy.run_path(str(Path(__file__).resolve().parents[1] / 'verify-portable-authoring.py'))


class PortableBatchTests(unittest.TestCase):
    def batch(self, root):
        packages = root / 'packages'
        packages.mkdir()
        (packages / 'Cordis.NET.Core.test.nupkg').write_bytes(b'the tested package')
        return SCRIPT['hashes'](packages)

    def test_receipt_accepts_exact_verified_package_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            packages = self.batch(Path(directory))
            self.assertEqual(packages, {'Cordis.NET.Core.test.nupkg': hashlib.sha256(b'the tested package').hexdigest()})
            report = {'status': 'requested-checks-passed', 'sourceUnchanged': True,
                      'commit': 'checkpoint', 'sourceSha256': {'input': 'source'},
                      'steps': [{'name': 'package-batch-unchanged', 'exitCode': 0, 'sha256': packages}]}
            SCRIPT['verified_packages'](report, 'checkpoint', {'input': 'source'}, packages)
            (Path(directory) / 'packages/Cordis.NET.Core.test.nupkg').write_bytes(b'a later package')
            with self.assertRaisesRegex(RuntimeError, 'exact batch'):
                SCRIPT['verified_packages'](report, 'checkpoint', {'input': 'source'},
                                             SCRIPT['hashes'](Path(directory) / 'packages'))

    def test_failed_drifting_unsealed_or_different_source_verification_is_refused(self):
        report = {'status': 'requested-checks-passed', 'sourceUnchanged': True,
                  'commit': 'checkpoint', 'sourceSha256': {'input': 'source'},
                  'steps': [{'name': 'package-batch-unchanged', 'exitCode': 0, 'sha256': {'p': 'bytes'}}]}
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
                    SCRIPT['verified_packages'](changed, 'checkpoint', {'input': 'source'}, {'p': 'bytes'})

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
