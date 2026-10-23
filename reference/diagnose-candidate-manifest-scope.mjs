// Diagnostic reproduction of the fixed upstream deleted-manifest scope case.
// This is not a validation gate and never normalizes paths or changes assertions.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, realpathSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, resolve, toNamespacedPath } from 'node:path';
import { pathToFileURL } from 'node:url';

const pin = '687f282e8625c6b8d11ff58211a90524be2e4317';
const sourceFile = 'packages/boot/hmr/src/package-manifest.ts';
const testFile = 'packages/boot/hmr/tests/package-manifest.spec.ts';
const testTitle = 'package manifest reader parity > continues to the enclosing scope after the manifest is deleted';
const options = {};
for (const argument of process.argv.slice(2)) {
  if (argument === '--with-candidate-hook') options.hook = true;
  else if (/^--(?:dsh|reference|output)=/.test(argument)) {
    const separator = argument.indexOf('=');
    options[argument.slice(2, separator)] = argument.slice(separator + 1);
  } else throw new Error('Use --dsh= --reference= --output= [--with-candidate-hook]');
}
for (const name of ['dsh', 'reference', 'output']) if (!options[name]) throw new Error('Missing --' + name);
const output = resolve(options.output);
if (existsSync(output)) throw new Error('Preserve previous diagnostic evidence; select a new output file');
mkdirSync(dirname(output), { recursive: true });
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const observe = callback => {
  try { return { ok: true, value: JSON.parse(JSON.stringify(callback()) ?? 'null') }; }
  catch (error) { return { ok: false, error: String(error), code: error.code, stack: error.stack }; }
};
const result = {
  format: 'cordis-manifest-scope-diagnostic/v1', status: 'error',
  meaning: 'Independent diagnostic only. A successful probe is not original-suite or native-equivalence acceptance.',
  platform: process.platform, architecture: process.arch, node: process.version,
  execArgv: process.execArgv, candidateHook: Boolean(options.hook),
  upstreamManifestHooks: true, originalTest: { file: testFile, title: testTitle }, checks: [],
};
let fixture, manifests, sourceRoot, git;
try {
  assert.ok(!process.env.NODE_OPTIONS, 'Unset NODE_OPTIONS so this process cannot inherit a loader/preload');
  assert.ok(!process.execArgv.some(argument => /^(?:--import|--require|--loader|--experimental-loader)(?:=|$)|^-r$/.test(argument)),
    'Do not preload a loader or require hook into this independent process');
  sourceRoot = realpathSync(resolve(options.dsh));
  const reference = realpathSync(resolve(options.reference));
  git = (...args) => execFileSync('git', ['-C', sourceRoot, ...args], { encoding: 'utf8' }).trim();
  assert.equal(git('rev-parse', 'HEAD'), pin, 'Candidate must be the fixed original source');
  assert.equal(git('status', '--porcelain', '--untracked-files=no'), '', 'Original tracked source must be unchanged');
  result.source = { commit: pin, tree: git('rev-parse', 'HEAD^{tree}'),
    implementationBlob: git('rev-parse', pin + ':' + sourceFile),
    testBlob: git('rev-parse', pin + ':' + testFile),
    implementationSha256: sha(readFileSync(join(sourceRoot, sourceFile))),
    testSha256: sha(readFileSync(join(sourceRoot, testFile))),
    packageJsonSha256: sha(readFileSync(join(sourceRoot, 'package.json'))),
    originalConfigSha256: sha(readFileSync(join(sourceRoot, 'vitest.config.ts'))),
    originalSetupSha256: Object.fromEntries(['scripts/test-proxy-environment.ts', 'scripts/test-dom-environment.ts']
      .map(file => [file, sha(readFileSync(join(sourceRoot, file)))])),
  };
  const tools = createRequire(join(reference, 'candidate-tools/package.json'));
  result.tools = { lockSha256: sha(readFileSync(join(reference, 'candidate-tools/package-lock.json'))) };
  result.declaredNodeRange = JSON.parse(readFileSync(join(sourceRoot, 'package.json'), 'utf8')).engines.node;
  result.nodeWithinDeclaredRange = tools('semver').satisfies(process.version, result.declaredNodeRange);
  assert.ok(result.nodeWithinDeclaredRange, 'Diagnostic Node is outside the fixed upstream engine declaration');
  if (options.hook) {
    process.env.CORDIS_CANDIDATE_REFERENCE = reference;
    process.env.CORDIS_DSH_REFERENCE = sourceRoot;
    process.env.CORDIS_CANDIDATE_SUITE = 'composition';
    const hook = join(reference, 'candidate-native-specifier-hooks.mjs');
    result.tools.candidateHookSha256 = sha(readFileSync(hook));
    await import(pathToFileURL(hook).href);
  }
  const sourceRequire = createRequire(pathToFileURL(join(sourceRoot, sourceFile)));
  const addonPath = realpathSync(sourceRequire.resolve('node-addon-require-builtin'));
  assert.equal(addonPath, realpathSync(tools.resolve('node-addon-require-builtin')), 'Addon must come from the same locked tools');
  result.tools.addonEntrySha256 = sha(readFileSync(addonPath));
  const addon = sourceRequire('node-addon-require-builtin');
  const reader = addon.requireBuiltin('internal/modules/package_json_reader');
  const formats = addon.requireBuiltin('internal/modules/esm/get_format');
  const binding = addon.requireBuiltin('internal/bootstrap/realm').internalBinding('modules');
  const rawScope = binding.getPackageScopeConfig;
  const rawNearest = reader.getNearestParentPackageJSON;
  result.rawFunctionSha256 = { scopeBinding: sha(rawScope.toString()), nearestReader: sha(rawNearest.toString()) };
  const { PackageManifests } = await import(pathToFileURL(join(sourceRoot, sourceFile)).href);
  const file = (path, text) => { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, text); };
  fixture = realpathSync.native(mkdtempSync(join(tmpdir(), 'cordis-manifest-scope-diagnostic-')));
  const directory = join(fixture, 'packages', 'pkg');
  const manifest = join(directory, 'package.json');
  const enclosing = join(fixture, 'packages', 'package.json');
  const importer = join(fixture, 'importer', 'entry.mjs');
  file(join(fixture, 'importer', 'package.json'), '{"type":"module","dependencies":{"pkg":"*"}}');
  file(importer, 'export {}');
  mkdirSync(join(fixture, 'importer', 'node_modules'), { recursive: true });
  symlinkSync(directory, join(fixture, 'importer', 'node_modules', 'pkg'), process.platform === 'win32' ? 'junction' : 'dir');
  for (const name of ['a', 'b']) {
    file(join(directory, name + '.mjs'), `export const marker = '${name}'`);
    file(join(directory, name + '.cjs'), `module.exports = { marker: '${name}' }`);
  }
  manifests = new PackageManifests();
  file(enclosing, '{"name":"packages","type":"commonjs"}');
  file(manifest, '{"name":"pkg","type":"module"}');
  const source = join(directory, 'lib', 'entry.js');
  file(source, '');
  const url = pathToFileURL(source).href;
  result.fixture = { source, sourceURL: url, enclosing, expectedNamespacedEnclosing: toNamespacedPath(enclosing) };
  // Preserve the original lookup order: do not prewarm the enclosing scope.
  result.before = { scope: observe(() => reader.getPackageScopeConfig(url)) };
  rmSync(manifest);
  manifests.invalidate(manifest);
  result.after = {
    scope: observe(() => reader.getPackageScopeConfig(url)),
    sourceFormat: observe(() => formats.defaultGetFormatWithoutErrors(pathToFileURL(join(directory, 'bin', 'tool')))),
    enclosingFormat: observe(() => formats.defaultGetFormatWithoutErrors(pathToFileURL(join(fixture, 'packages', 'tool')))),
    nearest: observe(() => reader.getNearestParentPackageJSON(source)),
    // Extra diagnostic calls follow every lookup made by the original case.
    rawBindingEnclosing: observe(() => rawScope(pathToFileURL(enclosing).href)),
    rawNearest: observe(() => rawNearest(source)),
  };
  const check = (name, callback) => {
    try { callback(); result.checks.push({ name, passed: true }); }
    catch (error) { result.checks.push({ name, passed: false, error: String(error), actual: error.actual, expected: error.expected }); }
  };
  check('original-before-type-module', () => assert.equal(result.before.scope.value?.type, 'module'));
  check('original-scope-subset-after-delete', () => {
    assert.ok(result.after.scope.ok, 'Reader threw after invalidation');
    const actual = result.after.scope.value;
    assert.deepEqual({ exists: actual.exists, type: actual.type, pjsonPath: actual.pjsonPath },
      { exists: true, type: 'commonjs', pjsonPath: toNamespacedPath(enclosing) });
  });
  check('original-extensionless-format-parity', () => {
    assert.ok(result.after.sourceFormat.ok && result.after.enclosingFormat.ok, 'Format lookup threw');
    assert.equal(result.after.sourceFormat.value, result.after.enclosingFormat.value);
  });
  check('original-nearest-path-after-delete', () => {
    assert.ok(result.after.nearest.ok, 'Nearest reader threw');
    assert.equal(result.after.nearest.value?.path, toNamespacedPath(enclosing));
  });
  result.status = result.checks.every(check => check.passed) ? 'passed' : 'failed';
} catch (error) {
  result.error = String(error);
  result.stack = error.stack;
} finally {
  try {
    manifests?.dispose();
    if (fixture) rmSync(fixture, { recursive: true, force: true });
    if (git) {
      result.originalSourceUnchanged = git('rev-parse', 'HEAD') === pin && git('status', '--porcelain', '--untracked-files=no') === '';
      assert.ok(result.originalSourceUnchanged, 'Original source changed during the diagnostic');
    }
  } catch (error) { result.cleanupError = String(error); result.status = 'error'; }
  writeFileSync(output, JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify({ status: result.status, platform: result.platform, node: result.node,
    candidateHook: result.candidateHook, output, checks: result.checks }, null, 2));
  process.exitCode = result.status === 'passed' ? 0 : result.status === 'failed' ? 1 : 2;
}
