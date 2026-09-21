import { defineConfig } from 'vitest/config';
import { resolve, dirname, relative } from 'node:path';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import ts from 'typescript';

const require = createRequire(import.meta.url);
const dsh = resolve(process.env.CORDIS_DSH_REFERENCE || '../deepseek-harness');
const origin = resolve(process.env.CORDIS_ORIGIN_REFERENCE || '../upstream-cordis');
if (execFileSync('git', ['-C', dsh, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim() !== 'ddefc45fbc7f8e46dd73185e68295696d1297887') throw new Error('Unpinned DSH');
if (execFileSync('git', ['-C', origin, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim() !== '56b3d4f725681cf4556c1a8695a709cc3b6eed74') throw new Error('Unpinned origin tests');
for (const path of [dsh, origin]) if (execFileSync('git', ['-C', path, 'status', '--porcelain', '--untracked-files=no'], { encoding: 'utf8' }).trim()) throw new Error('Reference has modified tracked files: ' + path);
const core = resolve(origin, 'packages/core/src');
const loader = resolve(origin, 'packages/loader/src');
const normalize = path => path.replaceAll('\\', '/');
export default defineConfig({
  root: origin,
  cacheDir: resolve('artifacts/reference-vite-cache'),
  plugins: [{ name: 'standard-typescript-decorators', enforce: 'pre', transform(code, id) {
    if (!/\.ts$/.test(id) || !/^\s*@[A-Za-z_$]/m.test(code)) return;
    const result = ts.transpileModule(code, { fileName: id, compilerOptions: {
      target: ts.ScriptTarget.ES2024, module: ts.ModuleKind.ESNext, sourceMap: true,
    } });
    return { code: result.outputText, map: result.sourceMapText };
  } }, { name: 'pinned-source-specifier-routing', enforce: 'pre', resolveId(id, importer) {
    if (!importer || !id.startsWith('.')) return;
    const requested = resolve(dirname(importer), id);
    if (requested === loader) return '\0cordis-loader-origin-export-shim';
    if (requested === core || normalize(requested).startsWith(normalize(core) + '/')) {
      const suffix = relative(core, requested);
      return resolve(dsh, 'vendor/cordis/src', suffix ? suffix.replace(/\.ts$/, '') + '.ts' : 'index.ts');
    }
  }, load(id) {
    if (id === '\0cordis-loader-origin-export-shim') return `export * from ${JSON.stringify(normalize(resolve(dsh, 'vendor/loader/src/index.ts')))}; export { default as Group } from ${JSON.stringify(normalize(resolve(dsh, 'vendor/group/src/index.ts')))};`;
  } }],
  resolve: { alias: {
    'vitest': resolve(dirname(require.resolve('vitest/package.json')), 'dist/index.js'),
    'cordis': resolve(dsh, 'vendor/cordis/src/index.ts'),
    'cosmokit': resolve(dsh, 'vendor/cosmokit/src/index.ts'),
    '@deepseek-ai/cordis': resolve(dsh, 'vendor/cordis/src/index.ts'),
    '@deepseek-ai/cosmokit': resolve(dsh, 'vendor/cosmokit/src/index.ts'),
    '@standard-schema/spec': require.resolve('@standard-schema/spec'),
    '@deepseek-ai/cordis-plugin-loader': resolve(dsh, 'vendor/loader/src/index.ts'),
    'node-addon-require-builtin': require.resolve('node-addon-require-builtin'),
  } },
  test: { include: ['packages/{core,loader}/tests/*.spec.ts'], globals: false, testTimeout: 10000, fileParallelism: false,
    reporters: ['default', 'json'], outputFile: resolve('artifacts/verification/original-core-reference.json') },
});
