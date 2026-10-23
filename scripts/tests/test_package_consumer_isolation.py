import copy
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from scripts.package_inspection import inspect_package_consumer, prepare_package_consumer


ROOT = Path(__file__).resolve().parents[2]


class PackageConsumerIsolationTests(unittest.TestCase):
    def test_actual_restore_blocks_ancestor_builds_and_foreign_fallback_packages(self):
        dotnet = os.environ.get('CORDIS_TEST_DOTNET') or shutil.which('dotnet')
        self.assertIsNotNone(dotnet, 'Package isolation regression requires the configured .NET SDK')
        with tempfile.TemporaryDirectory() as temporary:
            parent = Path(temporary).resolve()
            for name in ('props', 'targets'):
                (parent / ('Directory.Build.' + name)).write_text(
                    '<Project><Target Name="Ambient' + name + '" BeforeTargets="PrepareForBuild">'
                    '<Error Text="Ancestor build escaped the consumer boundary"/></Target></Project>')
            batch, empty = parent / 'batch', parent / 'empty'
            batch.mkdir()
            empty.mkdir()
            archive = batch / 'Cordis.NET.QualityProbe.1.0.0.nupkg'
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr('Cordis.NET.QualityProbe.nuspec', '<package><metadata>'
                    '<id>Cordis.NET.QualityProbe</id><version>1.0.0</version><authors>test</authors>'
                    '<description>Private restore boundary fixture</description></metadata></package>')
                package.writestr('lib/net10.0/_._', '')

            def project(directory):
                directory.mkdir()
                path = directory / 'Consumer.csproj'
                path.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                    '<TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit>'
                    '</PropertyGroup><ItemGroup><PackageReference Include="Cordis.NET.QualityProbe" '
                    'Version="1.0.0"/></ItemGroup></Project>')
                return path

            selected = parent / 'selected & consumer'
            selected_project = project(selected)
            with patch.dict(os.environ, {'NUGET_FALLBACK_PACKAGES': str(parent / 'foreign-cache')}):
                env = prepare_package_consumer(selected, ROOT, batch, dotnet)
            self.assertEqual(env['NUGET_FALLBACK_PACKAGES'], '')
            for command in ([dotnet, 'restore', selected_project],
                            [dotnet, 'build', selected_project, '--no-restore']):
                process = subprocess.run(command, cwd=selected, env=env, capture_output=True, timeout=60)
                self.assertEqual(process.returncode, 0, (process.stdout + process.stderr).decode())
            receipt = inspect_package_consumer(selected_project, selected / 'cache', batch, '1.0.0')
            self.assertEqual(receipt['restoredArchiveSha256'], {
                archive.name.lower(): hashlib.sha256(archive.read_bytes()).hexdigest()})
            self.assertEqual(receipt['resolvedPackages'], ['Cordis.NET.QualityProbe/1.0.0'])

            isolated = parent / 'isolated'
            isolated_project = project(isolated)
            with patch.dict(os.environ, {'NUGET_FALLBACK_PACKAGES': str(selected / 'cache')}):
                env = prepare_package_consumer(isolated, ROOT, empty, dotnet)
            process = subprocess.run([dotnet, 'restore', isolated_project], cwd=isolated, env=env,
                                     capture_output=True, timeout=60)
            self.assertNotEqual(process.returncode, 0, 'Restore reused the foreign cached package')
            self.assertIn(b'NU1101', process.stdout + process.stderr)

    def test_actual_assets_and_archives_must_support_the_isolation_claim(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            cache, batch, obj = root / 'cache', root / 'batch', root / 'obj'
            for directory in (cache, batch, obj):
                directory.mkdir()
            name = 'cordis.example.greeting.1.0.0.nupkg'
            (batch / name).write_bytes(b'selected example package')
            (cache / name).write_bytes(b'selected example package')
            assets = {'libraries': {'Cordis.Example.Greeting/1.0.0': {'type': 'package'}},
                      'packageFolders': {str(cache): {}},
                      'project': {'restore': {'sources': {str(batch): {}}}}}
            path = obj / 'project.assets.json'
            path.write_text(json.dumps(assets))
            receipt = inspect_package_consumer(root / 'Consumer.csproj', cache, batch, '1.0.0')
            self.assertTrue(receipt['packageReferencesOnly'])
            self.assertEqual(list(receipt['restoredArchiveSha256']), [name])
            mutations = [lambda a: a['libraries'].update({'Injected/1.0.0': {'type': 'project'}}),
                         lambda a: a['libraries']['Cordis.Example.Greeting/1.0.0'].update(type='reference'),
                         lambda a: a['packageFolders'].update({str(root / 'foreign-cache'): {}}),
                         lambda a: a['project']['restore']['sources'].clear(),
                         lambda a: a.update(libraries={'Cordis.Example.Greeting/2.0.0': {'type': 'package'}})]
            for mutation in mutations:
                changed = copy.deepcopy(assets)
                mutation(changed)
                path.write_text(json.dumps(changed))
                with self.assertRaises(ValueError):
                    inspect_package_consumer(root / 'Consumer.csproj', cache, batch, '1.0.0')
            path.write_text(json.dumps(assets))
            (cache / name).write_bytes(b'foreign example archive of the same version')
            with self.assertRaisesRegex(ValueError, 'differs from'):
                inspect_package_consumer(root / 'Consumer.csproj', cache, batch, '1.0.0')


if __name__ == '__main__':
    unittest.main()
