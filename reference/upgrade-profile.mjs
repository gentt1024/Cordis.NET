import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname, relative } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

export async function runProfileUpgradeScenarios(sourceRoot) {
  const { loadProfileDirectory } = await import(pathToFileURL(join(sourceRoot, 'packages/boot/app-boot/src/profile.ts')).href);
  const { applyEntryPatches } = await import(pathToFileURL(join(sourceRoot, 'vendor/include/src/index.ts')).href);
  const root = mkdtempSync(join(tmpdir(), 'dsh-upgrade-profile-'));
  try {
    const at = name => join(root, name);
    const write = (path, text) => { mkdirSync(dirname(at(path)), { recursive: true }); writeFileSync(at(path), text); };
    const bundle = (name, patch) => write(`install/node_modules/${name}/package.json`, JSON.stringify({ name, version: '1.0.0', dsh: { bundle: { patch } } }));
    write('install/package.json', '{}');
    bundle('upgrade-multi', ['one/first.yml', 'two/second.yml']);
    bundle('upgrade-broken', ['prefix.yml', 'missing.yml']);
    bundle('upgrade-later', 'after.yml');
    bundle('upgrade-empty', []);
    write('install/node_modules/upgrade-multi/one/first.yml', '- insert:\n  - id: item\n    name: ./plugin.dll\n    config: first\n');
    write('install/node_modules/upgrade-multi/two/second.yml', '- id: item\n  config: second\n');
    write('install/node_modules/upgrade-broken/prefix.yml', '- insert:\n  - id: leaked\n    name: ./leaked.dll\n');
    write('install/node_modules/upgrade-later/after.yml', '- insert:\n  - id: tail\n    name: ./tail.dll\n    config: tail\n');
    const selection = ['upgrade-multi', 'upgrade-broken', 'upgrade-later', 'upgrade-empty', 'upgrade-missing'];
    write('profile/package.json', JSON.stringify({ dsh: { profile: { bundles: selection } } }));
    write('profile/cordis.patch.yml', '- id: item\n  config: profile-value\n- insert:\n  - id: user\n    name: ./user.dll\n    config: user\n');
    const profile = loadProfileDirectory('dsh', at('profile'), at('install/package.json'));
    const rows = applyEntryPatches([], [...profile.layers.flatMap(layer => layer.patches), ...profile.patches]);
    assert.equal(profile.layers.length, 3); assert.equal(profile.skippedBundles.length, 2);
    assert.deepEqual(rows.map(row => row.id), ['item', 'tail', 'user']); assert.equal(rows[0].config, 'profile-value');
    const trace = [
      'selected:' + JSON.parse(readFileSync(at('profile/package.json'), 'utf8')).dsh.profile.bundles.join(','),
      'loaded:' + profile.layers.map(layer => layer.packageName).join(','),
      'skipped:' + profile.skippedBundles.map(layer => layer.packageName).join(','),
      'sources:' + profile.layers.map(layer => layer.packageName + ':' + layer.patchPaths.map(path => relative(layer.packageDir, path).replaceAll('\\', '/')).join(',')).join(';'),
      'rows:' + rows.map(row => row.id + ':' + row.config + '@' + relative(root, fileURLToPath(row.name)).replaceAll('\\', '/')).join('|'),
      'prefix-leaked:' + rows.some(row => row.id === 'leaked'),
    ];
    write('profile/cordis.patch.yml', '[broken');
    assert.throws(() => loadProfileDirectory('dsh', at('profile'), at('install/package.json')));
    const bundlesOnly = loadProfileDirectory('dsh', at('profile'), at('install/package.json'), { userLayer: false });
    assert.equal(bundlesOnly.patches.length, 0);
    trace.push('user-layer:failed;false:' + bundlesOnly.patches.length);
    return [{ id: 'P01-ordered-bundles-atomic-failure-and-provenance', trace }];
  } finally { rmSync(root, { recursive: true, force: true }); }
}
