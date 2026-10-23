// The Vitest list formatter drops skipped tests; discovery must retain them.
import { writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { createVitest } from './candidate-tools/node_modules/vitest/dist/node.js';

const [config, output, ...extra] = process.argv.slice(2);
if (!config || !output || extra.length) throw new Error('Use collect-candidate-tests.mjs <config> <output>');
const vitest = await createVitest('test', { config: resolve(config), watch: false });
try {
  const { testModules, unhandledErrors } = await vitest.collect();
  const errors = [...unhandledErrors];
  const cases = [];
  for (const module of testModules) {
    for (const suite of [module, ...module.children.allSuites()]) errors.push(...suite.errors());
    for (const test of module.children.allTests()) {
      const result = test.result();
      if (result.state !== 'pending' && result.state !== 'skipped') {
        throw new Error('Discovery unexpectedly executed a test: ' + test.fullName);
      }
      cases.push({ name: test.fullName, file: module.moduleId, collectionState: result.state });
    }
  }
  if (errors.length) throw new Error('Candidate collection failed: ' + JSON.stringify(errors));
  if (!testModules.length || !cases.length) throw new Error('Candidate collection found no test cases');
  writeFileSync(output, JSON.stringify(cases, null, 2) + '\n', { flag: 'wx' });
} finally {
  await vitest.close();
}
