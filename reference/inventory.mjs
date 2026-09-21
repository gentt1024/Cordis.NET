// Source inventory, not a claim that a test has been ported or passed.
import ts from 'typescript';
import { readFileSync, readdirSync, writeFileSync, mkdirSync, statSync } from 'node:fs';
import { resolve, relative } from 'node:path';
import { createHash } from 'node:crypto';
const dsh = resolve(process.argv[2]);
const origin = resolve(process.argv[3]);
const target = 'ddefc45fbc7f8e46dd73185e68295696d1297887';
const origins = [
  { root: origin, repository: 'cordiverse/cordis', commit: '56b3d4f725681cf4556c1a8695a709cc3b6eed74', directories: ['packages/core/tests', 'packages/loader/tests'] },
  { root: dsh, repository: 'deepseek-ai/deepseek-harness', commit: target, directories: [
    'packages/boot/app-boot/tests', 'packages/boot/hmr/tests', 'packages/boot/plugin-manager/tests',
    'packages/util/package-manifest', 'packages/host/directory-picker-auto/tests', 'packages/boot/cmdline/tests',
    'apps/cli/tests/web-agent-presets.e2e.ts', 'apps/cli/tests/built-bin.e2e.ts', 'apps/cli/tests/windows-shell.spec.ts',
    'packages/session/session-telemetry-otel/tests/loader-composition.e2e.ts',
  ] },
];
function files(dir) {
  try {
    if (statSync(dir).isFile()) return [dir];
    return readdirSync(dir, { withFileTypes: true }).flatMap(e => e.isDirectory() ? files(resolve(dir, e.name)) : /\.(spec|test|e2e)\.ts$/.test(e.name) ? [resolve(dir, e.name)] : []);
  }
  catch (e) { if (e.code === 'ENOENT') return []; throw e; }
}
const records = [];
const unresolved = [];
for (const source of origins) for (const filename of source.directories.flatMap(d => files(resolve(source.root, d)))) {
  const text = readFileSync(filename, 'utf8');
  const sf = ts.createSourceFile(filename, text, ts.ScriptTarget.Latest, true);
  const bindings = new Map();
  function collect(n) { if (ts.isVariableDeclaration(n) && ts.isIdentifier(n.name) && n.initializer) bindings.set(n.name.text, n.initializer); ts.forEachChild(n, collect); }
  collect(sf);
  function values(n, seen = new Set()) {
    if (ts.isAsExpression(n) || ts.isParenthesizedExpression(n) || ts.isSatisfiesExpression(n)) return values(n.expression, seen);
    if (ts.isIdentifier(n) && bindings.has(n.text) && !seen.has(n.text)) return values(bindings.get(n.text), new Set([...seen, n.text]));
    if (ts.isArrayLiteralExpression(n)) return n.elements.map(e => e.getText(sf));
    return null;
  }
  function visit(n, suites = []) {
    if (ts.isCallExpression(n)) {
      const callee = n.expression.getText(sf);
      if (/^(describe|suite)(\.(skip|only))?$/.test(callee) && n.arguments[0] && ts.isStringLiteralLike(n.arguments[0])) {
        for (const arg of n.arguments.slice(1)) ts.forEachChild(arg, child => visit(child, [...suites, n.arguments[0].text]));
        return;
      }
      let parameters = [null];
      let test = /^(it|test)(\.(skip|only|concurrent|todo))?$/.test(callee);
      if (ts.isCallExpression(n.expression) && /^(it|test)(\.concurrent)?\.each$/.test(n.expression.expression.getText(sf))) {
        test = true;
        parameters = values(n.expression.arguments[0]);
        if (!parameters) { unresolved.push({ file: relative(source.root, filename).replaceAll('\\', '/'), expression: n.expression.arguments[0].getText(sf) }); parameters = ['UNEXPANDED']; }
      }
      if (test && n.arguments[0] && ts.isStringLiteralLike(n.arguments[0])) {
        const title = [...suites, n.arguments[0].text].join(' / ');
        const path = relative(source.root, filename).replaceAll('\\', '/');
        const assertions = [];
        function assertionsIn(a) { if (ts.isExpressionStatement(a) && /\b(expect|assert)\s*\(/.test(a.getText(sf))) assertions.push(a.getText(sf)); else ts.forEachChild(a, assertionsIn); }
        for (const arg of n.arguments.slice(1)) assertionsIn(arg);
        for (const parameter of parameters) {
          const id = createHash('sha256').update(`${source.repository}:${path}:${title}:${parameter}`).digest('hex').slice(0, 16);
          records.push({ id: `U-${id}`, targetCommit: target, sourceRepository: source.repository, sourceCommit: source.commit,
            file: path, title, parameter, line: sf.getLineAndCharacterOfPosition(n.getStart(sf)).line + 1,
            kind: 'original-test', keyAssertions: assertions, dotnet: [], platform: 'requires-semantic-review', status: 'unimplemented', evidence: [] });
        }
        return;
      }
    }
    ts.forEachChild(n, c => visit(c, suites));
  }
  visit(sf);
}
mkdirSync('docs', { recursive: true });
writeFileSync('docs/upstream-tests.json', JSON.stringify({ format: 'cordis-upstream-inventory/v1', targetCommit: target, unresolvedParameters: unresolved, tests: records }, null, 2) + '\n');
console.log(`${records.length} test instances inventoried; ${unresolved.length} unresolved parameter lists. No pass status inferred.`);
