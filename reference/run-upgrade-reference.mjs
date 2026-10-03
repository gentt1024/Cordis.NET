// Execute original source at the fixed upgrade commit. The relevant source inventory
// binds each loaded file even when the reference is an exported tree without .git.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { createRequire, registerHooks } from 'node:module';
import { resolve, relative } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { runVolatileUpgradeScenarios } from './upgrade-volatile.mjs';
import { runProfileUpgradeScenarios } from './upgrade-profile.mjs';
import { runPublicReviewScenarios } from './public-review-scenarios.mjs';
import { runScenarios } from './scenarios.mjs';
import { runExtendedScenarios } from './extended-scenarios.mjs';
import { runTimerProbes } from './timer-probes.mjs';

const arg = process.argv.find(a => a.startsWith('--dsh='));
if (!arg) throw new Error('Usage: node --experimental-transform-types reference/run-upgrade-reference.mjs --dsh=/fixed/source');
assert.ok(Number(process.versions.node.split('.')[0]) >= 24, 'Use Node >=24 for the fixed source');
const root = resolve(arg.slice(6));
const inventory = JSON.parse(readFileSync(new URL('./upgrade-source.json', import.meta.url), 'utf8'));
assert.equal(inventory.commit, '639ed015397290b3745d163aafe02ffee4aa3f84');
const local = {
  '@deepseek-ai/cordis': 'vendor/cordis/src/index.ts',
  '@deepseek-ai/cosmokit': 'vendor/cosmokit/src/index.ts',
  '@deepseek-ai/schemastery': 'vendor/schemastery/src/index.ts',
  '@deepseek-ai/cordis-plugin-loader': 'vendor/loader/src/index.ts',
  '@deepseek-ai/cordis-plugin-include': 'vendor/include/src/index.ts',
  '@deepseek-ai/cordis-plugin-group': 'vendor/group/src/index.ts',
  '@deepseek-ai/cordis-plugin-timer': 'vendor/timer/src/index.ts',
  '@deepseek-ai/dsh-home-paths': 'packages/util/home-paths/src/index.ts',
  '@deepseek-ai/dsh-launch-environment': 'packages/util/launch-environment/src/index.ts',
  '@deepseek-ai/dsh-atomic-write': 'packages/util/atomic-write/src/index.ts',
  '@deepseek-ai/dsh-app-boot': 'packages/boot/app-boot/src/index.ts',
  '@deepseek-ai/dsh-system-prompt': 'packages/core/system-prompt/src/index.ts',
  '@deepseek-ai/dsh-scope': 'packages/core/scope/src/index.ts',
};
const loaded = new Set();
const require = createRequire(pathToFileURL(resolve(root, 'package.json')));
const hostRequire = createRequire(import.meta.url);
const hooks = registerHooks({
  resolve(specifier, context, nextResolve) {
    if (local[specifier]) return { url: pathToFileURL(resolve(root, local[specifier])).href, shortCircuit: true };
    try { return nextResolve(specifier, context); }
    catch (error) {
      if (specifier.startsWith('.') || specifier.startsWith('/') || specifier.includes(':')) throw error;
      let filename;
      try { filename = require.resolve(specifier); } catch { filename = hostRequire.resolve(specifier); }
      return { url: pathToFileURL(filename).href, shortCircuit: true };
    }
  },
  load(url, context, nextLoad) {
    if (url.startsWith('file:')) {
      const name = relative(root, fileURLToPath(url)).replaceAll('\\', '/');
      if (!name.startsWith('../') && !name.startsWith('node_modules/')) {
        const expected = inventory.files[name];
        assert.ok(expected, `Unbound DSH source dependency: ${name}`);
        const bytes = readFileSync(fileURLToPath(url));
        const oid = createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
        assert.equal(oid, expected, `Modified fixed DSH source: ${name}`); loaded.add(name);
      }
    }
    return nextLoad(url, context);
  },
});
try {
  const { Context } = await import(pathToFileURL(resolve(root, local['@deepseek-ai/cordis'])));
  const { Loader } = await import(pathToFileURL(resolve(root, local['@deepseek-ai/cordis-plugin-loader'])));
  const { default: Schema } = await import(pathToFileURL(resolve(root, local['@deepseek-ai/schemastery'])));
  const established = process.argv.includes('--established');
  const cases = established ? await runScenarios(Context) : await runVolatileUpgradeScenarios(Context, Loader, Schema);
  if (established) {
    const { applyEntryPatches } = await import(pathToFileURL(resolve(root, local['@deepseek-ai/cordis-plugin-include'])));
    const { TimerService } = await import(pathToFileURL(resolve(root, local['@deepseek-ai/cordis-plugin-timer'])));
    cases.push(...await runExtendedScenarios(Context, applyEntryPatches), ...await runTimerProbes(Context, TimerService));
  } else cases.push(...await runPublicReviewScenarios(Context, Loader, Schema), ...await runProfileUpgradeScenarios(root));
  console.error(JSON.stringify({ fixedCommit: inventory.commit, originalSourceFiles: [...loaded].sort() }));
  console.log(JSON.stringify({ format: established ? 'cordis-core-trace/v1' : 'cordis-upgrade-trace/v1', cases }, null, 2));
} finally { hooks.deregister(); }
