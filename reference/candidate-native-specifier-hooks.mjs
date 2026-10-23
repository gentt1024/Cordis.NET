import { registerHooks, createRequire } from 'node:module';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const reference = process.env.CORDIS_CANDIDATE_REFERENCE;
if (!reference) throw new Error('Missing candidate reference tool directory');
const require = createRequire(resolve(reference, 'candidate-tools/package.json'));
const { checkout, manifest, sourceResolver, toolRequire } = require(resolve(reference, 'candidate-support.mjs'));
const root = checkout(process.env.CORDIS_DSH_REFERENCE, manifest.dsh);
const source = sourceResolver(root);
const testRoot = process.env.CORDIS_CANDIDATE_SUITE === 'core' ? checkout(process.env.CORDIS_ORIGIN_REFERENCE, manifest.origin) : root;
registerHooks({ resolve(specifier, context, next) {
  if (specifier.startsWith('/packages/')) return { url: pathToFileURL(resolve(testRoot, specifier.slice(1))).href, shortCircuit: true };
  const filename = source(specifier);
  if (filename) return { url: pathToFileURL(filename).href, shortCircuit: true };
  try { return next(specifier, context); }
  catch (error) {
    if (specifier.startsWith('.') || specifier.startsWith('/') || specifier.includes(':')) throw error;
    return { url: pathToFileURL(toolRequire.resolve(specifier)).href, shortCircuit: true };
  }
} });
