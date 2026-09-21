import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';
import yaml from 'js-yaml';

const root = process.env.CORDIS_DSH_REFERENCE;
if (!root) throw new Error('CORDIS_DSH_REFERENCE is required');
const source = path => readFileSync(resolve(root, path), 'utf8');

test('locked loader repeatedly observes current lifecycle tasks', () => {
  const tree = source('vendor/loader/src/config/tree.ts');
  assert.match(tree, /while \(true\) \{\s*const tasks = this\.getTasks\(\)\s*if \(!tasks\.length\) return\s*await Promise\.allSettled\(tasks\)/s);
  assert.match(tree, /entry\._initTask \|\| entry\.fiber\?\.inertia/);
});

test('locked loader uses JavaScript truthiness and preserves missing config', () => {
  const entry = source('vendor/loader/src/config/entry.ts');
  assert.match(entry, /Boolean\(this\.evaluate\(options\.disabled\.__jsExpr\)\)/);
  assert.equal(Boolean(undefined), false);
  assert.match(entry, /registry\.plugin\(plugin, this\.options\.config,/);
  assert.equal(({ }).config, undefined);
  assert.equal(({ config: null }).config, null);
});

test('locked reflect write uses internal set waterfall while direct set stays direct', () => {
  const reflect = source('vendor/cordis/src/reflect.ts');
  assert.match(reflect, /events\.waterfall\('internal\/set', ctx, prop, value, error, \(\) => \{\s*return ctx\.reflect\.set\(prop, value, error\)/s);
  assert.match(reflect, /set\(name: string, value: any, error\?: Error\) \{/);
});

test('locked dependency normalization accepts duplicate array names', () => {
  const registry = source('vendor/cordis/src/registry.ts');
  assert.match(registry, /for \(const name of inject\) \{\s*result\[name\] = null/s);
  const result = Object.create(null);
  for (const name of ['messages', 'messages']) result[name] = null;
  assert.deepEqual(Object.keys(result), ['messages']);
});

test('locked js-yaml JSON schema resolves tags, bases, and quoted strings', () => {
  const value = yaml.load('bool: !!bool "false"\nint: !!int "12"\nhex: 0x10\nquoted: "0x10"\n', { schema: yaml.JSON_SCHEMA });
  assert.deepEqual(value, { bool: false, int: 12, hex: 16, quoted: '0x10' });
  assert.throws(() => yaml.load('value: !!bool invalid\n', { schema: yaml.JSON_SCHEMA }), /cannot resolve/);
  const text = yaml.dump({ decimal: '12', hex: '0x10', bool: 'false' }, { schema: yaml.JSON_SCHEMA });
  assert.deepEqual(yaml.load(text, { schema: yaml.JSON_SCHEMA }), { decimal: '12', hex: '0x10', bool: 'false' });
});
