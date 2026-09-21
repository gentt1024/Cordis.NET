import { registerHooks, createRequire } from 'node:module';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';
const require = createRequire(import.meta.url);
const root = resolve(process.env.CORDIS_DSH_REFERENCE);
const packages = new Map([
  ['@deepseek-ai/cordis', 'vendor/cordis/src/index.ts'],
  ['@deepseek-ai/cosmokit', 'vendor/cosmokit/src/index.ts'],
  ['@deepseek-ai/schemastery', 'vendor/schemastery/src/index.ts'],
  ['@deepseek-ai/cordis-plugin-loader', 'vendor/loader/src/index.ts'],
  ['@deepseek-ai/cordis-plugin-include', 'vendor/include/src/index.ts'],
  ['@deepseek-ai/cordis-plugin-group', 'vendor/group/src/index.ts'],
  ['@deepseek-ai/cordis-plugin-timer', 'vendor/timer/src/index.ts'],
]);
registerHooks({ resolve(specifier, context, next) {
  if (packages.has(specifier)) return { url: pathToFileURL(resolve(root, packages.get(specifier))).href, shortCircuit: true };
  if (['js-yaml', 'node-addon-require-builtin', '@standard-schema/spec'].includes(specifier))
    return { url: pathToFileURL(require.resolve(specifier)).href, shortCircuit: true };
  return next(specifier, context);
} });
