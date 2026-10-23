// Temporary attribution probe only. This does not define or relax a source gate.
import { execFileSync } from 'node:child_process';
import { readFileSync, realpathSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const pin = '687f282e8625c6b8d11ff58211a90524be2e4317';
const file = 'packages/boot/hmr/tests/package-manifest.spec.ts';
const source = 'packages/boot/hmr/src/package-manifest.ts';
if (!process.env.CORDIS_MANIFEST_DIAGNOSTIC_SOURCE || !process.env.CORDIS_MANIFEST_DIAGNOSTIC_OUTPUT) {
  throw new Error('Set CORDIS_MANIFEST_DIAGNOSTIC_SOURCE and CORDIS_MANIFEST_DIAGNOSTIC_OUTPUT');
}
if (process.env.NODE_OPTIONS) throw new Error('Unset NODE_OPTIONS; this probe must not inherit preloads');
const root = realpathSync(resolve(process.env.CORDIS_MANIFEST_DIAGNOSTIC_SOURCE));
const reference = realpathSync(resolve(process.env.CORDIS_MANIFEST_REFERENCE || dirname(fileURLToPath(import.meta.url))));
const git = (...args) => execFileSync('git', ['-C', root, ...args]);
if (git('rev-parse', 'HEAD').toString().trim() !== pin) throw new Error('Unpinned diagnostic source');
if (!readFileSync(join(root, file)).equals(git('show', pin + ':' + file))) throw new Error('Original test changed');
const changed = git('diff', 'HEAD', '--name-only').toString().trim().split('\n').filter(Boolean);
if (changed.some(file => file !== source)) throw new Error('Diagnostic checkout has unrelated source changes');
const { defineConfig } = await import(pathToFileURL(join(reference, 'candidate-tools/node_modules/vitest/dist/config.js')).href);
const { sourceResolver, decoratorPlugin, toolRequire } = await import(pathToFileURL(join(reference, 'candidate-support.mjs')).href);
const lookup = sourceResolver(root);
export default defineConfig({
  root,
  cacheDir: resolve(process.env.CORDIS_MANIFEST_DIAGNOSTIC_OUTPUT, 'vite-cache'),
  plugins: [decoratorPlugin(), { name: 'manifest-diagnostic-upstream-source-aliases', enforce: 'pre',
    resolveId(id) { return lookup(id); },
  }],
  resolve: { alias: [
    { find: /^vitest$/, replacement: join(reference, 'candidate-tools/node_modules/vitest/dist/index.js') },
    { find: /^cordis$/, replacement: join(root, 'vendor/cordis/src/index.ts') },
    { find: /^cosmokit$/, replacement: join(root, 'vendor/cosmokit/src/index.ts') },
    { find: /^node-addon-require-builtin$/, replacement: toolRequire.resolve('node-addon-require-builtin') },
  ] },
  test: {
    include: [file], environment: 'node', pool: 'forks', fileParallelism: false, maxWorkers: 1,
    execArgv: ['--experimental-transform-types', ...(process.allowedNodeEnvironmentFlags.has('--webstorage') ? ['--no-webstorage'] : [])],
    // These are the exact setup entries declared by the pinned upstream config.
    // No candidate-native-specifier-hooks or binding/path adapter is loaded.
    setupFiles: [join(root, 'scripts/test-proxy-environment.ts'), join(root, 'scripts/test-dom-environment.ts')],
    testTimeout: 30000, hookTimeout: 30000, reporters: ['default', 'json'],
    outputFile: resolve(process.env.CORDIS_MANIFEST_DIAGNOSTIC_OUTPUT, 'package-manifest.json'),
  },
});
