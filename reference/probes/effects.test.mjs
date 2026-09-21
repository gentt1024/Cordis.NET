// These are source-excerpt probes with a fixture owner, NOT a full Cordis reference run.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { stripTypeScriptTypes } from 'node:module';
import test from 'node:test';

const source = name => readFileSync(new URL(`../source/${name}.tsfrag`, import.meta.url), 'utf8');
const fixture = `
export class ProbeOwner {
  state = 2
  uid = 0
  _disposables = new DisposableList()
  errors = []
  ctx = { logger: { error: error => this.errors.push(error) } }
  assertActive() { if (this.uid === null) throw new CordisError('INACTIVE_EFFECT') }
  ${source('execute')}
  ${source('effect')}
  async stop() {
    this.state = 5
    await Promise.all(this._disposables.clear().map(async disposer => {
      try { await Promise.resolve(); await runDisposable(disposer) }
      catch (error) { this.errors.push(error) }
    }))
    this.state = 2
  }
}`;
const js = stripTypeScriptTypes(source('helpers') + fixture, { mode: 'transform' });
const { ProbeOwner } = await import('data:text/javascript;base64,' + Buffer.from(js).toString('base64'));
const gate = () => Promise.withResolvers();

test('E01 immediate setup and single-shot public disposal', async () => {
  const p = new ProbeOwner(), log = [];
  const e = p.effect(() => { log.push('setup'); return () => log.push('cleanup'); });
  log.push('returned'); await e(); await e(); await p.stop();
  assert.deepEqual(log, ['setup', 'returned', 'cleanup']);
});
test('E02 owner stop begun inside setup sees returned cleanup', async () => {
  const p = new ProbeOwner(), log = []; let stopping;
  p.effect(() => { stopping = p.stop(); log.push('setup-end'); return () => log.push('cleanup'); });
  await stopping; assert.deepEqual(log, ['setup-end', 'cleanup']);
});
test('E03 async setup is drained before owner stop completes', async () => {
  const p = new ProbeOwner(), setup = gate(), entered = gate(), release = gate(); let complete = false;
  p.effect(async () => { await setup.promise; return async () => { entered.resolve(); await release.promise; }; });
  const stop = p.stop().then(() => { complete = true; });
  await Promise.resolve(); assert.equal(complete, false); setup.resolve();
  await entered.promise; assert.equal(complete, false); release.resolve(); await stop;
});
test('E04 owner joins cleanup even after a second public disposer returns', async () => {
  const p = new ProbeOwner(), entered = gate(), release = gate();
  const e = p.effect(() => async () => { entered.resolve(); await release.promise; });
  const first = e(); await entered.promise; assert.equal(e(), undefined);
  let complete = false; const stop = p.stop().then(() => { complete = true; });
  await Promise.resolve(); assert.equal(complete, false); release.resolve(); await first; await stop;
});
test('E05 nested handles are adopted and cleaned once in reverse order', async () => {
  const p = new ProbeOwner(), log = [];
  const e = p.effect(() => [p.effect(() => () => log.push('a')), p.effect(() => () => log.push('b'))]);
  assert.equal(p._disposables.length, 1); await e(); await p.stop(); assert.deepEqual(log, ['b', 'a']);
});
test('E06 partial iterable setup rolls back collected cleanups', async () => {
  const p = new ProbeOwner(), log = [];
  assert.throws(() => p.effect(function* () {
    yield () => log.push('a'); yield () => log.push('b'); throw new Error('setup');
  }), /setup/);
  await p.stop(); assert.deepEqual(log, ['b', 'a']);
});
test('E07 local failed group short-circuits but its sibling still cleans up', async () => {
  const p = new ProbeOwner(), log = [];
  p.effect(() => () => log.push('sibling'));
  p.effect(() => [() => log.push('older'), () => { log.push('failure'); throw new Error('cleanup'); }]);
  await p.stop(); assert.deepEqual(log, ['failure', 'sibling']); assert.equal(p.errors.length, 1);
});
test('E08 creating an effect during unload rejects before setup', async () => {
  const p = new ProbeOwner(); let called = false;
  p.effect(() => () => assert.throws(() => p.effect(() => { called = true; }), e => e.code === 'INACTIVE_EFFECT'));
  await p.stop(); assert.equal(called, false);
});

// Regression for C13: Apply's result is interpreted by this same _execute path.
// A logging expression must not accidentally return Array.push's numeric result.
test('E09 numeric synchronous setup result is invalid', () => {
  const p = new ProbeOwner(), log = [];
  assert.throws(() => p.effect(() => log.push('consumer-active')),
    { name: 'TypeError', message: 'Invalid effect' });
  assert.deepEqual(log, ['consumer-active']);
  assert.equal(p._disposables.length, 0);
});
test('E10 statement-bodied logging setup returns no effect', async () => {
  const p = new ProbeOwner(), log = [];
  const e = p.effect(() => { log.push('consumer-active'); });
  await e;
  await e();
  await p.stop();
  assert.deepEqual(log, ['consumer-active']);
  assert.equal(p._disposables.length, 0);
});
test('E11 cleanup may return a number without becoming a new effect', async () => {
  const p = new ProbeOwner(), log = [];
  const e = p.effect(() => () => log.push('cleanup'));
  await e();
  await p.stop();
  assert.deepEqual(log, ['cleanup']);
  assert.equal(p._disposables.length, 0);
});
test('E12 numeric asynchronous setup result is also invalid', async () => {
  const p = new ProbeOwner(), log = [];
  const e = p.effect(async () => log.push('setup'));
  await assert.rejects(Promise.resolve(e),
    { name: 'TypeError', message: 'Invalid effect' });
  await p.stop();
  assert.deepEqual(log, ['setup']);
  assert.equal(p._disposables.length, 0);
});
