import assert from 'node:assert/strict';

const gate = () => Promise.withResolvers();
const fiber = value => value.ctx.fiber; // unwrap the PromiseLike proxy; retain the original Fiber
const P = (apply, inject = []) => ({ apply, inject });
const guard = (promise, id) => {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`Scenario timed out: ${id}`)), 15000);
  })]).finally(() => clearTimeout(timer));
};
class Messages {
  handlers = new Set();
  subscribe(handler) { this.handlers.add(handler); return () => this.handlers.delete(handler); }
  emit(name) { for (const h of [...this.handlers]) h(name); }
  get count() { return this.handlers.size; }
}

export async function runScenarios(Context) {
  const cases = [];
  async function run(id, body) {
    const ctx = new Context();
    const trace = [];
    try {
      await guard(body(ctx, trace), id);
      cases.push({ id, trace: [...trace] });
      console.error(`PASS ${id}`);
    } finally { await guard(ctx.fiber.dispose(), `${id}/cleanup`); }
  }
  await run('C01-greeting-lifecycle', async (ctx, log) => {
    const a = new Messages(), b = new Messages();
    const plugin = P((c, prefix) => {
      const source = c.get('messages');
      c.effect(() => source.subscribe(name => log.push(`${prefix}, ${name}!`)));
    }, ['messages']);
    const f = fiber(ctx.plugin(plugin, 'Hello'));
    await f.await(); assert.equal(f.state, 0); log.push('pending');
    const first = ctx.provide('messages', a);
    await f.await(); assert.equal(a.count, 1); a.emit('Otto');
    f.update('Hi'); await f.await(); assert.equal(a.count, 1); a.emit('Otto');
    await first(); assert.equal(a.count, 0); assert.equal(f.state, 0); log.push('provider-removed');
    const second = ctx.provide('messages', b);
    await f.await(); b.emit('Otto'); await f.dispose();
    assert.equal(b.count, 0); b.emit('ignored'); await second(); log.push('disposed');
  });
  await run('C02-effect-immediate-single-shot', async (ctx, log) => {
    const e = ctx.effect(() => { log.push('setup'); return () => log.push('cleanup'); });
    log.push('returned'); await e; await e(); await e(); log.push('disposed-twice');
  });
  await run('C03-reentrant-owner-disposal', async (ctx, log) => {
    let stopping;
    ctx.effect(() => {
      log.push('setup-start'); stopping = ctx.fiber.dispose(); log.push('setup-end');
      return () => log.push('cleanup');
    });
    log.push('returned'); await stopping; assert.equal(ctx.fiber.state, 2); log.push('root-active');
  });
  await run('C04-pending-owned-effect', async (ctx, log) => {
    const f = fiber(ctx.plugin(P(() => { throw new Error('must not apply'); }, ['absent'])));
    f.ctx.effect(() => { log.push('setup'); return () => log.push('cleanup'); });
    await f.await(); assert.equal(f.state, 0); await f.dispose(); assert.equal(f.state, 4); log.push('disposed');
  });
  await run('C05-dispose-before-load-checkpoint', async (ctx, log) => {
    const f = fiber(ctx.plugin(P(() => { log.push('must-not-apply'); })));
    await f.dispose(); assert.equal(f.state, 4); assert.equal(log.length, 0); log.push('disposed-before-apply');
  });
  await run('C06-async-setup-and-cleanup', async (ctx, log) => {
    const setup = gate(), cleanup = gate(), entered = gate();
    const e = ctx.effect(async () => {
      log.push('setup-start'); await setup.promise; log.push('setup-end');
      return async () => { log.push('cleanup-start'); entered.resolve(); await cleanup.promise; log.push('cleanup-end'); };
    });
    try {
      let stopped = false;
      const stopping = ctx.fiber.dispose().then(() => { stopped = true; });
      assert.equal(stopped, false); log.push('waits-for-setup'); setup.resolve();
      await entered.promise; assert.equal(stopped, false); log.push('waits-for-cleanup');
      cleanup.resolve(); await stopping; await e; log.push('stopped');
    } finally { setup.resolve(); cleanup.resolve(); }
  });
  await run('C07-owner-joins-started-cleanup', async (ctx, log) => {
    const entered = gate(), release = gate();
    const e = ctx.effect(() => async () => {
      log.push('cleanup-start'); entered.resolve(); await release.promise; log.push('cleanup-end');
    });
    try {
      const first = e(); await entered.promise; await e(); log.push('second-public-call-returned');
      let stopped = false;
      const stop = ctx.fiber.dispose().then(() => { stopped = true; });
      assert.equal(stopped, false); log.push('owner-waits'); release.resolve();
      await first; await stop; log.push('stopped');
    } finally { release.resolve(); }
  });
  await run('C08-nested-effect-reverse-order', async (ctx, log) => {
    const outer = ctx.effect(() => [
      ctx.effect(() => { log.push('setup-a'); return () => log.push('cleanup-a'); }),
      ctx.effect(() => { log.push('setup-b'); return () => log.push('cleanup-b'); }),
    ]);
    await outer(); await ctx.fiber.dispose();
    assert.deepEqual(log, ['setup-a', 'setup-b', 'cleanup-b', 'cleanup-a']);
  });
  await run('C09-generator-setup-rollback', async (ctx, log) => {
    assert.throws(() => ctx.effect(function* () {
      yield ctx.effect(() => { log.push('setup-a'); return () => log.push('cleanup-a'); });
      yield ctx.effect(() => { log.push('setup-b'); return () => log.push('cleanup-b'); });
      throw new Error('setup-failed');
    }), /setup-failed/);
    await ctx.fiber.dispose(); log.push('failure-observed');
  });
  await run('C10-failed-group-does-not-starve-sibling', async (ctx, log) => {
    ctx.effect(() => () => log.push('sibling'));
    ctx.effect(() => [() => log.push('must-not-run'), () => { log.push('failed-cleanup'); throw new Error('cleanup'); }]);
    await ctx.fiber.dispose();
    assert.deepEqual(log, ['failed-cleanup', 'sibling']);
    assert.equal(ctx.logger.buffer.filter(m => m.type === 'error').length, 1);
    log.push('one-diagnostic');
  });
  await run('C11-reject-effect-during-unload', async (ctx, log) => {
    ctx.effect(() => () => {
      assert.throws(() => ctx.effect(() => { log.push('must-not-setup'); return () => {}; }),
        error => error.code === 'INACTIVE_EFFECT');
      log.push('rejected');
    });
    await ctx.fiber.dispose(); assert.equal(log.length, 1);
  });
  await run('C12-apply-failure-cleanup', async (ctx, log) => {
    const f = fiber(ctx.plugin(P(c => {
      c.effect(() => { log.push('setup'); return () => log.push('cleanup'); }); throw new Error('apply-failed');
    })));
    await assert.rejects(f.await(), /apply-failed/); assert.equal(f.state, 3); log.push('failed');
    await f.dispose(); assert.equal(f.state, 4); log.push('disposed');
  });
  await run('C13-loading-provider-invisible', async (ctx, log) => {
    const published = gate(), release = gate(), value = {};
    const consumer = fiber(ctx.plugin(P(() => { log.push('consumer-active'); }, ['service'])));
    const provider = fiber(ctx.plugin(P(async c => { c.provide('service', value); published.resolve(); await release.promise; })));
    try {
      await published.promise; assert.equal(provider.state, 1); assert.equal(ctx.get('service'), undefined);
      assert.equal(ctx.get('service', false), value); await consumer.await(); assert.equal(consumer.state, 0);
      log.push('loading-not-visible'); release.resolve(); await provider.await(); await consumer.await();
      assert.equal(consumer.state, 2); log.push('ready');
    } finally { release.resolve(); }
  });
  await run('C14-inject-child-not-parent-restart', async (ctx, log) => {
    let child, parents = 0;
    const parent = fiber(ctx.plugin(P(c => {
      parents++;
      child = fiber(c.inject(['service'], cc => {
        cc.effect(() => { log.push('child-setup'); return () => log.push('child-cleanup'); });
      }));
    })));
    await parent.await(); assert.equal(child.state, 0);
    const service = ctx.provide('service', {}); await child.await(); await service();
    assert.equal(parents, 1); assert.equal(parent.state, 2); assert.equal(child.state, 0);
    log.push('parent-unchanged'); await parent.dispose(); assert.equal(child.state, 4);
    log.push('child-disposed-with-parent');
  });
  await run('C15-pending-and-active-config-update', async (ctx, log) => {
    let validations = 0;
    const plugin = {
      inject: ['service'],
      Config: { '~standard': { version: 1, vendor: 'cordis-slice-probe', validate(value) {
        validations++; return value === 'good' ? { value } : { issues: [{ message: 'expected good' }] };
      }}},
      apply: (_c, config) => { log.push('apply:' + config); },
    };
    const f = fiber(ctx.plugin(plugin, 'bad')); await f.await(); assert.equal(validations, 0);
    f.update('good'); assert.equal(validations, 0); log.push('deferred-validation');
    ctx.provide('service', {}); await f.await();
    assert.throws(() => f.update('bad')); assert.equal(f.config, 'good'); assert.equal(f._config, 'bad');
    assert.equal(f.state, 2); log.push('invalid-update-preserves-active-config');
    await assert.rejects(f.restart()); assert.equal(f.state, 3); log.push('restart-uses-raw-config');
    f.update('good'); await f.await(); assert.equal(f.state, 2); log.push('recovered');
  });
  await run('C16-multiple-dependencies', async (ctx, log) => {
    let starts = 0;
    const f = fiber(ctx.plugin(P(() => { starts++; }, ['a', 'b'])));
    const a = ctx.provide('a', {}); await f.await(); assert.equal(f.state, 0);
    ctx.provide('b', {}); await f.await(); assert.equal(starts, 1);
    await a(); assert.equal(f.state, 0); ctx.provide('a', {}); await f.await(); assert.equal(starts, 2);
    log.push('both-required;two-activations');
  });
  await run('C17-owner-joins-disposing-child', async (ctx, log) => {
    const entered = gate(), release = gate();
    const child = fiber(ctx.plugin(P(c => { c.effect(() => async () => {
      log.push('child-cleanup-start'); entered.resolve(); await release.promise; log.push('child-cleanup-end');
    }); })));
    try {
      await child.await(); const first = child.dispose(); await entered.promise;
      let stopped = false; const rootStop = ctx.fiber.dispose().then(() => { stopped = true; });
      assert.equal(stopped, false); log.push('root-waits'); release.resolve();
      await first; await rootStop; log.push('stopped');
    } finally { release.resolve(); }
  });
  await run('C18-stable-context-on-restart', async (ctx, log) => {
    const seen = [];
    const f = fiber(ctx.plugin(P(c => { seen.push(c); })));
    await f.await(); const uid = f.uid; await f.restart();
    assert.equal(seen.length, 2); assert.equal(seen[0], seen[1]); assert.equal(f.uid, uid);
    log.push('same-context;same-fiber;two-applies');
  });
  // Five named tests adapted from the upstream dispose.spec.ts; assertions retained.
  await run('U01-effects-dispose-by-plugin', async (root, log) => {
    let calls = 0;
    const f = fiber(root.plugin(P(ctx => { ctx.effect(() => () => { calls++; }, 'test'); })));
    await f.await();
    assert.deepEqual(f.getEffects(), [{ label: 'test', children: [] }]);
    assert.equal(calls, 0); await f.dispose(); assert.equal(calls, 1);
    await f.dispose(); assert.equal(calls, 1); log.push('metadata:test;calls:0,1,1');
  });
  await run('U02-effects-dispose-manually', async (root, log) => {
    let calls = 0;
    const e = root.effect(() => () => { calls++; });
    assert.deepEqual(root.fiber.getEffects(), [{ label: 'anonymous', children: [] }]);
    assert.equal(calls, 0); const first = e(); assert.equal(calls, 1);
    const second = e(); assert.equal(calls, 1); await first; await second;
    log.push('metadata:anonymous;calls:0,1,1');
  });
  await run('U03-effects-return-with-error', async (root, log) => {
    const sequence = [];
    assert.throws(() => root.effect(() => { throw new Error('test'); }), /test/);
    assert.deepEqual(sequence, []); log.push('throws;sequence:empty');
  });
  await run('U04-effects-yield-with-error', async (root, log) => {
    const sequence = [];
    assert.throws(() => root.effect(function* () { yield () => sequence.push(1); throw new Error('test'); }), /test/);
    assert.deepEqual(sequence, [1]); log.push('throws;sequence:1');
  });
  await run('U05-effects-async-return-with-error', async (root, log) => {
    const sequence = [];
    const e = root.effect(async () => { throw new Error('test'); });
    assert.deepEqual(sequence, []); await assert.rejects(Promise.resolve(e));
    assert.deepEqual(sequence, []); log.push('rejects;sequence:empty');
  });
  return cases;
}
