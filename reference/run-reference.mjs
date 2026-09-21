// Run the actual frozen DSH Core; no npm package substitution, lifecycle mocks or TS rewrite.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { registerHooks } from 'node:module';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
import { runScenarios } from './scenarios.mjs';
import { runExtendedScenarios } from './extended-scenarios.mjs';
import { runTimerProbes } from './timer-probes.mjs';
import { createRequire } from 'node:module';

const commit = 'ddefc45fbc7f8e46dd73185e68295696d1297887';
const arg = process.argv.find(a => a.startsWith('--dsh='));
if (!arg) throw new Error('Usage: node --experimental-transform-types reference/run-reference.mjs --dsh=/path/to/checkout');
const [major, minor] = process.versions.node.split('.').map(Number);
assert.ok((major === 22 && minor >= 19) || major >= 24,
  'Full DSH reference requires Node ^22.19.0 or >=24.0.0');
const root = resolve(arg.slice(6));
const git = (...args) => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8' }).trim();
assert.equal(git('rev-parse', 'HEAD'), commit, 'DSH checkout must be the locked commit');
assert.equal(git('status', '--porcelain', '--untracked-files=no', '--', 'vendor/cordis', 'vendor/cosmokit', 'vendor/include', 'vendor/loader', 'vendor/group', 'vendor/timer'), '',
  'Reference sources must be unmodified');
assert.equal(JSON.parse(readFileSync(resolve(root, 'vendor/cordis/package.json'), 'utf8')).version, '4.0.2');
// Confirm the checked-in excerpt probes still correspond to the baseline methods.
// Ignore indentation/blank lines only, not statements or identifiers.
const normalize = value => value.split(/\r?\n/).map(line => line.trim()).filter(Boolean).join('\n');
const originalFiber = normalize(readFileSync(resolve(root, 'vendor/cordis/src/fiber.ts'), 'utf8'));
for (const part of ['execute', 'effect']) {
  const fragment = normalize(readFileSync(new URL(`./source/${part}.tsfrag`, import.meta.url), 'utf8'));
  assert.ok(originalFiber.includes(fragment), `The ${part} excerpt drifted from the frozen Fiber source`);
}
const packages = new Map([
  ['@deepseek-ai/cordis', pathToFileURL(resolve(root, 'vendor/cordis/src/index.ts')).href],
  ['@deepseek-ai/cosmokit', pathToFileURL(resolve(root, 'vendor/cosmokit/src/index.ts')).href],
  ['@deepseek-ai/cordis-plugin-loader', pathToFileURL(resolve(root, 'vendor/loader/src/index.ts')).href],
  ['@deepseek-ai/cordis-plugin-include', pathToFileURL(resolve(root, 'vendor/include/src/index.ts')).href],
  ['@deepseek-ai/cordis-plugin-timer', pathToFileURL(resolve(root, 'vendor/timer/src/index.ts')).href],
]);
const require = createRequire(import.meta.url);
// This only redirects package specifiers to their ORIGINAL source files. It changes no source,
// and deliberately avoids building the unrelated DSH agent/native/UI workspace.
const hooks = registerHooks({
  resolve(specifier, context, nextResolve) {
    if (packages.has(specifier)) return { url: packages.get(specifier), shortCircuit: true };
    if (['js-yaml', 'node-addon-require-builtin'].includes(specifier)) return { url: pathToFileURL(require.resolve(specifier)).href, shortCircuit: true };
    return nextResolve(specifier, context);
  },
});
try {
  const { Context } = await import(packages.get('@deepseek-ai/cordis'));
  const cases = await runScenarios(Context);
  const { applyEntryPatches } = await import(packages.get('@deepseek-ai/cordis-plugin-include'));
  cases.push(...await runExtendedScenarios(Context, applyEntryPatches));
  const { TimerService } = await import(packages.get('@deepseek-ai/cordis-plugin-timer'));
  cases.push(...await runTimerProbes(Context, TimerService));
  console.log(JSON.stringify({ format: 'cordis-core-trace/v1', cases }, null, 2));
} finally { hooks.deregister(); }
