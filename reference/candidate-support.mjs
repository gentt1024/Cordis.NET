// The candidate has its own immutable identity; the formal reference remains unchanged.
import { readFileSync, existsSync, statSync, realpathSync, lstatSync, symlinkSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import ts from './candidate-tools/node_modules/typescript/lib/typescript.js';
export const manifest = JSON.parse(readFileSync(new URL('./candidate-manifest.json', import.meta.url), 'utf8'));
if (manifest.dsh !== '687f282e8625c6b8d11ff58211a90524be2e4317' ||
    manifest.origin !== '56b3d4f725681cf4556c1a8695a709cc3b6eed74') throw new Error('Unreviewed candidate identity');
export const toolRequire = createRequire(new URL('./candidate-tools/package.json', import.meta.url));
export function checkout(path, pin) {
  const root = realpathSync(resolve(path));
  const git = (...args) => execFileSync('git', ['-C', root, ...args], { encoding: 'utf8' }).trim();
  if (git('rev-parse', 'HEAD') !== pin) throw new Error('Unpinned candidate source');
  if (git('status', '--porcelain', '--untracked-files=no')) throw new Error('Modified candidate source');
  return root;
}
function bindDirectory(link, target) {
  let existing;
  try { existing = lstatSync(link); } catch (error) { if (error.code !== 'ENOENT') throw error; }
  if (existing) {
    if (!existing.isSymbolicLink() || realpathSync(link) !== target) throw new Error('Candidate dependency path already belongs to another tool installation');
  } else {
    mkdirSync(dirname(link), { recursive: true });
    symlinkSync(target, link, process.platform === 'win32' ? 'junction' : 'dir');
  }
}
export function bindTools(path) {
  const root = checkout(path, manifest.dsh);
  const target = realpathSync(fileURLToPath(new URL('./candidate-tools/node_modules', import.meta.url)));
  bindDirectory(resolve(root, 'node_modules'), target);
  // Original manager tests deliberately invoke the Desktop-bundled pnpm path.
  const pnpm = realpathSync(fileURLToPath(new URL('./candidate-tools/node_modules/pnpm', import.meta.url)));
  const expected = JSON.parse(readFileSync(resolve(root, 'apps/desktop/package.json'), 'utf8')).devDependencies.pnpm;
  if (JSON.parse(readFileSync(resolve(pnpm, 'package.json'), 'utf8')).version !== expected) throw new Error('Candidate bundled pnpm version differs');
  bindDirectory(resolve(root, 'apps/desktop/node_modules/pnpm'), pnpm);
}
export function sourceResolver(root) {
  const config = ts.parseConfigFileTextToJson('tsconfig.base.json', readFileSync(resolve(root, 'tsconfig.base.json'), 'utf8'));
  if (config.error) throw new Error('Invalid candidate tsconfig');
  const paths = Object.entries(config.config.compilerOptions.paths).sort(([a], [b]) => b.length - a.length);
  const sourceFile = path => {
    for (const candidate of [path, path + '.ts', path + '.tsx', resolve(path, 'index.ts')]) {
      if (existsSync(candidate) && statSync(candidate).isFile()) return candidate;
    }
    return path;
  };
  return specifier => {
    for (const [key, values] of paths) {
      if (!key.includes('*') && specifier === key) return sourceFile(resolve(root, values[0]));
      if (key.includes('*')) {
        const [prefix, suffix] = key.split('*');
        if (specifier.startsWith(prefix) && specifier.endsWith(suffix)) {
          const capture = specifier.slice(prefix.length, suffix ? -suffix.length : undefined);
          return sourceFile(resolve(root, values[0].replace('*', capture)));
        }
      }
      // Published source subpaths point back into the same pinned package tree.
      if (!key.includes('*') && specifier.startsWith(key + '/src/')) {
        const source = resolve(root, values[0]);
        const marker = source.replaceAll('\\', '/').lastIndexOf('/src');
        if (marker >= 0) return sourceFile(resolve(source.slice(0, marker), specifier.slice(key.length + 1)));
      }
    }
  };
}
export function decoratorPlugin() {
  return { name: 'candidate-standard-typescript-decorators', enforce: 'pre', transform(code, id) {
    const file = id.split('?', 1)[0];
    if (!/\.[cm]?tsx?$/.test(file) || !/^\s*@[A-Za-z_$]/m.test(code)) return;
    const result = ts.transpileModule(code, { fileName: file, compilerOptions: {
      target: ts.ScriptTarget.ES2024, module: ts.ModuleKind.ESNext,
      jsx: file.endsWith('x') ? ts.JsxEmit.ReactJSX : undefined, sourceMap: true,
    } });
    return { code: result.outputText, map: result.sourceMapText };
  } };
}
