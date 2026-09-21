import assert from 'node:assert/strict';
import yaml from 'js-yaml';

export async function runExtendedScenarios(Context, applyPatches) {
  const cases = [];
  async function run(id, body) {
    const root = new Context(); const trace = [];
    try { await body(root, trace); cases.push({ id, trace }); console.error(`PASS ${id}`); }
    finally { await root.fiber.dispose(); }
  }
  await run('C19-event-bail-values-and-once', async (ctx, trace) => {
    ctx.on('check', () => { trace.push('false'); return false; });
    ctx.on('check', () => { trace.push('null'); return null; });
    ctx.on('check', () => { trace.push('undefined'); });
    ctx.on('check', () => { trace.push('zero'); return 0; });
    ctx.on('check', () => { trace.push('not-called'); return 1; });
    assert.equal(ctx.bail('check'), 0);
    ctx.once('once', () => { trace.push('once'); ctx.emit('once'); });
    ctx.emit('once'); ctx.emit('once');
  });
  await run('C20-event-waterfall-veto', async (ctx, trace) => {
    ctx.on('water', (value, next) => { trace.push('before'); const result = next(); trace.push('after'); return result + 1; });
    ctx.on('water', (value, next) => { trace.push('inner'); return next(); });
    assert.equal(ctx.waterfall('water', 4, () => { trace.push('final'); return 4; }), 5);
    ctx.on('veto', () => { trace.push('veto'); return null; });
    assert.equal(ctx.waterfall('veto', () => { trace.push('must-not-run'); }), null);
  });
  await run('C21-service-shared-realm', async (ctx, trace) => {
    const left = ctx.isolate('s', 'shared'); const right = ctx.isolate('s', 'shared'); const privateCtx = ctx.isolate('s');
    const value = {};
    left.provide('s', value);
    assert.equal(right.get('s'), value); assert.equal(ctx.get('s'), undefined); assert.equal(privateCtx.get('s'), undefined);
    trace.push('shared-visible;root-hidden;private-hidden');
  });
  await run('C22-logger-exporter-removal', async (ctx, trace) => {
    const first = ctx.logger.exporter({ export() { trace.push('first'); } });
    const second = ctx.logger.exporter({ export() { trace.push('second'); } });
    await first(); ctx.logger.info('message'); await second(); ctx.logger.info('ignored');
    assert.deepEqual(trace, ['second']);
  });
  await run('C23-patch-aliases-and-layer-order', async (_ctx, trace) => {
    const base = [{ id: 'a', name: 'alpha', config: { old: 1, keep: 2 } }];
    const cloneArray = applyPatches(base, [], () => {});
    assert.notEqual(cloneArray, base); assert.equal(cloneArray[0], base[0]);
    const inserted = { id: 'b', name: 'beta', config: { v: 1 } };
    const patch = [{ insert: [inserted] }, { id: 'b', config: { v: 2 } }, { id: 'a', name: 'wrong', disabled: true }, { id: 'a', config: { new: 3 } }, { id: 'missing', disabled: true }];
    const warnings = [];
    const result = applyPatches(base, patch, m => warnings.push(m));
    assert.notEqual(result[0], base[0]); assert.equal(result[1], inserted); assert.equal(inserted.config.v, 2);
    assert.deepEqual(result[0].config, { new: 3 }); assert.deepEqual(base[0].config, { old: 1, keep: 2 });
    assert.equal(result[0].disabled, undefined); assert.equal(warnings.length, 2);
    trace.push('array-copy;shared-unpatched;cloned-base;insert-alias;later-hit;replace-config;two-warnings');
  });
  await run('C24-patch-group-include-boundaries', async (_ctx, trace) => {
    const base = [{ id: 'g', name: 'cordis:group', group: true, config: [{ id: 'child', name: 'a' }] }, { id: 'i', name: 'cordis:include', config: { initial: [{ id: 'hidden', name: 'b' }] } }];
    const warnings = [];
    const rows = applyPatches(base, [{ id: 'child', disabled: true }, { id: 'hidden', disabled: true }, { id: 'i', insert: [{ id: 'x', name: 'x' }] }], m => warnings.push(m));
    assert.equal(rows[0].config[0].disabled, true); assert.equal(rows[1].config.initial[0].disabled, undefined); assert.equal(warnings.length, 2);
    trace.push('group-traversed;include-boundary;two-warnings');
  });
  await run('C25-fiber-local-update-hooks-survive-reload', async (ctx, trace) => {
    let generation = 0;
    const fiber = ctx.plugin((child) => {
      const current = ++generation;
      trace.push(`apply:${current}`);
      child.on('internal/update', (_config, _noSave, next) => {
        trace.push(`hook:${current}`);
        return next();
      });
    }, 0).ctx.fiber;
    await fiber.await();
    fiber.update(1); await fiber.await();
    fiber.update(2); await fiber.await();
    assert.deepEqual(trace, ['apply:1', 'hook:1', 'apply:2', 'hook:1', 'hook:2', 'apply:3']);
  });
  await run('C28-internal-set-has-no-dispatch-receiver', async (ctx, trace) => {
    ctx.provide('value', 1);
    ctx[Context.filter] = () => false;
    let calls = 0;
    ctx.on('internal/set', (writer, name, value, error, next) => {
      calls++;
      assert.equal(writer, ctx); assert.equal(name, 'value'); assert.equal(value, 2); assert.ok(error instanceof Error);
      trace.push('hook');
      return next();
    });
    ctx.value = 2;
    assert.equal(ctx.get('value'), 2); assert.equal(calls, 1);
    ctx.set('value', 3);
    assert.equal(ctx.get('value'), 3); assert.equal(calls, 1);
    trace.push('write:2;direct:3;calls:1');
  });
  await run('C29-yaml-nonfinite-round-trip', async (_ctx, trace) => {
    const first = yaml.load('positive: .inf\nnegative: -.inf\nnan: .nan\n');
    const second = yaml.load(yaml.dump(first));
    assert.equal(second.positive, Infinity); assert.equal(second.negative, -Infinity); assert.ok(Number.isNaN(second.nan));
    trace.push('positive:infinity;negative:-infinity;nan:number');
  });
  await run('C30-json-nonfinite-matches-stringify', async (_ctx, trace) => {
    const value = {
      positive: Infinity,
      negative: -Infinity,
      nan: NaN,
      array: [Infinity, 'finite', null, { nested: NaN }],
      finite: 1.25,
      text: 'unchanged',
      nil: null,
    };
    const json = JSON.stringify(value, null, 2);
    assert.deepEqual(JSON.parse(json), {
      positive: null,
      negative: null,
      nan: null,
      array: [null, 'finite', null, { nested: null }],
      finite: 1.25,
      text: 'unchanged',
      nil: null,
    });
    trace.push(json);
  });
  return cases;
}
