import { defineConfig } from './candidate-tools/node_modules/vitest/dist/config.js';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { readFileSync } from 'node:fs';
import { checkout, manifest, sourceResolver, decoratorPlugin, toolRequire } from './candidate-support.mjs';
const dsh = checkout(process.env.CORDIS_DSH_REFERENCE, manifest.dsh);
const origin = checkout(process.env.CORDIS_ORIGIN_REFERENCE, manifest.origin);
const suite = process.env.CORDIS_CANDIDATE_SUITE;
if (!Object.hasOwn(manifest.suites, suite)) throw new Error('Select a reviewed candidate suite');
const spec = manifest.suites[suite];
const root = spec.repository === 'origin' ? origin : dsh;
const selection = process.env.CORDIS_UPSTREAM_SELECTION ? JSON.parse(readFileSync(process.env.CORDIS_UPSTREAM_SELECTION, 'utf8')) : undefined;
const normalize = path => path.replaceAll('\\', '/');
const core = resolve(origin, 'packages/core/src');
const loader = resolve(origin, 'packages/loader/src');
const source = sourceResolver(dsh);
const routing = { name: 'candidate-origin-source-routing', enforce: 'pre', resolveId(id, importer) {
  if (id.startsWith('/packages/')) return resolve(root, id.slice(1));
  const filename = source(id);
  if (filename) return filename;
  if (suite !== 'core' || !importer || !id.startsWith('.')) return;
  const requested = resolve(dirname(importer), id);
  if (requested === loader) return '\0cordis-candidate-loader-export-shim';
  if (requested === core || normalize(requested).startsWith(normalize(core) + '/')) {
    const suffix = relative(core, requested);
    return resolve(dsh, 'vendor/cordis/src', suffix ? suffix.replace(/\.ts$/, '') + '.ts' : 'index.ts');
  }
}, load(id) {
  if (id === '\0cordis-candidate-loader-export-shim') return `export * from ${JSON.stringify(normalize(resolve(dsh, 'vendor/loader/src/index.ts')))}; export { default as Group } from ${JSON.stringify(normalize(resolve(dsh, 'vendor/group/src/index.ts')))};`;
} };
const externalTools = Object.keys(JSON.parse(readFileSync(new URL('./candidate-tools/package.json', import.meta.url), 'utf8')).dependencies).filter(name => !name.startsWith('@types/'));
export default defineConfig({ root,
  cacheDir: resolve(process.env.CORDIS_CANDIDATE_CACHE || 'artifacts/candidate-vite-cache', suite),
  plugins: [decoratorPlugin(), routing],
  resolve: { alias: [
    { find: /^\/packages\//, replacement: normalize(root) + '/packages/' },
    { find: 'cordis', replacement: resolve(dsh, 'vendor/cordis/src/index.ts') },
    { find: 'cosmokit', replacement: resolve(dsh, 'vendor/cosmokit/src/index.ts') },
    ...externalTools.map(find => ({ find: new RegExp('^' + find.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '$'), replacement: find === 'vitest' ? fileURLToPath(new URL('./candidate-tools/node_modules/vitest/dist/index.js', import.meta.url)) : toolRequire.resolve(find) })),
  ] },
  test: { environment: 'node',
    execArgv: ['--experimental-transform-types', ...(process.allowedNodeEnvironmentFlags.has('--webstorage') ? ['--no-webstorage'] : [])],
    setupFiles: suite === 'browser' ? [] : [fileURLToPath(new URL('./candidate-native-specifier-hooks.mjs', import.meta.url))],
    server: { deps: { external: suite === 'browser' ? [] : [/vendor[/\\]/] } },
    include: selection?.include ?? spec.include,
    exclude: manifest.excludedSourceTests.filter(item => (item.repository ?? 'dsh') === spec.repository).map(item => item.file),
    testNamePattern: selection?.testNamePattern ? new RegExp(selection.testNamePattern) : undefined,
    testTimeout: 30000, hookTimeout: 30000, fileParallelism: false, maxWorkers: 1,
    reporters: ['default', 'json'], outputFile: process.env.CORDIS_UPSTREAM_REPORT,
  },
});
