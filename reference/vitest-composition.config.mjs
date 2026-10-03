import { defineConfig } from 'vitest/config';
import { resolve, dirname } from 'node:path';
import { createRequire } from 'node:module';
import { execFileSync } from 'node:child_process';
import ts from 'typescript';
import { readFileSync } from 'node:fs';
const require = createRequire(import.meta.url);
const root = resolve(process.env.CORDIS_DSH_REFERENCE);
const pin = JSON.parse(readFileSync(new URL('../upstream.lock.json', import.meta.url), 'utf8')).harness.commit;
const upgrade = pin === '639ed015397290b3745d163aafe02ffee4aa3f84';
if (execFileSync('git', ['-C', root, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim() !== pin) throw new Error('Unpinned DSH');
if (execFileSync('git', ['-C', root, 'status', '--porcelain', '--untracked-files=no'], { encoding: 'utf8' }).trim()) throw new Error('DSH reference has modified tracked files');
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
  '@deepseek-ai/dsh-hmr': 'packages/boot/hmr/src/index.ts',
  '@deepseek-ai/dsh-system-prompt': 'packages/core/system-prompt/src/index.ts',
  '@deepseek-ai/dsh-scope': 'packages/core/scope/src/index.ts',
};
export default defineConfig({
  root,
  cacheDir: resolve('artifacts/composition-vite-cache'),
  plugins: [{ name: 'standard-typescript-decorators', enforce: 'pre', transform(code, id) {
    if (!/\.ts$/.test(id) || !/^\s*@[A-Za-z_$]/m.test(code)) return;
    const result = ts.transpileModule(code, { fileName: id, compilerOptions: {
      target: ts.ScriptTarget.ES2024, module: ts.ModuleKind.ESNext, sourceMap: true,
    } });
    return { code: result.outputText, map: result.sourceMapText };
  } }],
  resolve: { alias: {
    ...Object.fromEntries(Object.entries(local).map(([name, path]) => [name, resolve(root, path)])),
    vitest: resolve(dirname(require.resolve('vitest/package.json')), 'dist/index.js'),
    'js-yaml': require.resolve('js-yaml'),
    'node-addon-require-builtin': require.resolve('node-addon-require-builtin'),
    'resolve.exports': require.resolve('resolve.exports'),
    'chokidar': require.resolve('chokidar'),
    'picomatch': require.resolve('picomatch'),
    '@babel/code-frame': require.resolve('@babel/code-frame'),
    ...(upgrade ? { semver: require.resolve('semver'), '@eslint-community/regexpp': require.resolve('@eslint-community/regexpp') } : {}),
  } },
  test: {
    execArgv: ['--experimental-transform-types'],
    setupFiles: [resolve('reference/native-specifier-hooks.mjs')],
    server: { deps: { external: [/vendor[/\\]/] } },
    include: upgrade ? [
      'scripts/loader-config-diff.spec.ts', 'scripts/loader-volatile-update.spec.ts', 'scripts/volatile-config.spec.ts',
      'packages/boot/app-boot/tests/{app-boot,compatibility-preflight,config-reload,linked-resolution-matrix,loader-shape.compat,package-meta,plugin-compatibility,profile-compatibility,profile-plugins,profile-resolution-service,profile-resolution-worker-bootstrap,profile-resolution,profile-sanitize,profile,user-patches}.spec.ts',
      'packages/boot/hmr/tests/*.spec.ts',
    ] : ['packages/boot/app-boot/tests/*.spec.ts', 'packages/boot/hmr/tests/*.spec.ts'],
    testTimeout: 15000, fileParallelism: false, reporters: ['default', 'json'],
    outputFile: resolve('artifacts/verification/original-composition-reference.json'),
  },
});
