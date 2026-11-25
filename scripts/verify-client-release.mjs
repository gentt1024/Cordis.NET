import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'
import { runInThisContext } from 'node:vm'

// Execute real factory scripts fetched through HTTP, using the public package wrapper.
const loads = [], factories = [], starts = [], stops = []
const server = createServer((request, response) => {
  const revision = new URL(request.url, 'http://localhost').searchParams.get('rev')
  response.setHeader('content-type', 'text/javascript')
  response.end(`globalThis.__ModuleLoader__.load({id:'panel',factory(require) {
    const probe = require('@fixture/release');
    const revision = ${JSON.stringify(revision)};
    probe.factories.push(revision);
    return {apply(ctx) {
      probe.starts.push(revision);
      ctx.provide('panel', {revision});
      ctx.effect(() => () => {probe.stops.push(revision)});
    }};
  }});`)
})
await new Promise(done => server.listen(0, '127.0.0.1', done))
const origin = `http://127.0.0.1:${server.address().port}`
globalThis.location = new URL(origin)
globalThis.document = { querySelectorAll() { return [] } }
const runtime = resolve(process.argv[2] ?? 'clients/modules/lib')
const { bootClientModules, createManagementClient, Context } = await import(pathToFileURL(resolve(runtime, 'client.mjs')).href)
const { createEvents } = await import(pathToFileURL(resolve(runtime, 'events.mjs')).href)
const { TypertConnection } = await import(pathToFileURL(resolve(runtime, 'cordis-remote.mjs')).href)
const page = { version: '1.2.3', commit: 'fixed' }
const foreign = { version: '1.2.4', commit: 'fixed' }
const graph = (revision, release = page) => {
  const entry = { id: 'panel', url: '/panel.js?rev=' + revision, rev: revision, external: ['@fixture/release'] }
  return { rev: revision, ...(release === undefined ? {} : { release }), entries: [entry],
    batches: [{ phase: 'application', url: entry.url, rev: revision, entries: ['panel'] }] }
}
function deferred() {
  let resolve
  const promise = new Promise(done => { resolve = done })
  return { promise, resolve }
}
async function until(predicate, message) {
  const deadline = Date.now() + 4000
  while (!predicate()) { assert.ok(Date.now() < deadline, message); await delay(2) }
}
let pause, fail
const loadBundle = async request => {
  const revision = new URL(request, origin).searchParams.get('rev')
  loads.push(revision)
  if (pause?.revision === revision) { pause.entered.resolve(); await pause.release.promise }
  if (fail === revision) throw new Error('fixture download failure')
  const response = await fetch(new URL(request, origin))
  assert.equal(response.status, 200)
  runInThisContext(await response.text(), { filename: request })
}
const boot = wire => bootClientModules({ graph: wire, loadBundle,
  staticModules: { '@fixture/release': { factories, starts, stops } } })
let modules
try {
  modules = await boot(graph('one'))
  const stable = modules.context.get('panel')
  const observable = modules.releaseChange
  let announcements = 0, stateChanges = 0
  const unsubscribe = observable.subscribe(() => { announcements++ })
  const stopState = modules.state.subscribe(() => { stateChanges++ })
  const initialState = modules.state.getSnapshot()
  const unknown = { release: foreign, get entries() { throw new Error('foreign graph body inspected') } }
  await modules.sync(unknown)
  assert.equal(modules.releaseChange, observable)
  assert.deepEqual(observable.getSnapshot(), { page, previous: page, host: foreign })
  assert.equal(modules.context.get('panel'), stable)
  assert.equal(modules.state.getSnapshot(), initialState)
  assert.equal(stateChanges, 0)
  assert.deepEqual([loads, factories, starts, stops], [['one'], ['one'], ['one'], []])
  await modules.sync({ release: { ...foreign, ignored: true }, future: true })
  assert.equal(announcements, 1, 'Equal identities retain the notification snapshot')
  await modules.sync({ release: { version: page.version } })
  assert.deepEqual(observable.getSnapshot(), { page, previous: foreign, host: { version: page.version } },
    'Omitting a commit differs from the boot identity')
  await modules.sync({ release: foreign })
  const changedCommit = { version: foreign.version, commit: 'different' }
  await modules.sync({ release: changedCommit })
  assert.deepEqual(observable.getSnapshot(), { page, previous: foreign, host: changedCommit })
  modules.dismissRelease()
  assert.equal(observable.getSnapshot(), undefined)
  assert.equal(announcements, 5)
  await modules.sync(graph('blocked', changedCommit))
  assert.equal(announcements, 5, 'Dismissal suppresses announcements without accepting code')
  assert.equal(modules.context.get('panel'), stable)
  await modules.retry()
  assert.equal(observable.getSnapshot(), undefined, 'Retry does not observe a fake Host return')
  assert.deepEqual(loads, ['one'], 'Retry uses the last accepted boot graph')
  await modules.sync({ release: foreign })
  assert.deepEqual(observable.getSnapshot(), { page, previous: changedCommit, host: foreign })
  await modules.sync(graph('two', { ...page, ignored: 'dropped' }))
  assert.equal(observable.getSnapshot(), undefined)
  assert.equal(modules.context.get('panel').revision, 'two', 'Boot release return resumes synchronization')

  // An accepted download remains live while a later foreign graph is ignored.
  pause = { revision: 'pending', entered: deferred(), release: deferred() }
  const pending = modules.sync(graph('pending'))
  await pause.entered.promise
  await modules.sync({ release: foreign, entries: 'future format' })
  const mismatch = observable.getSnapshot()
  pause.release.resolve()
  await pending
  assert.equal(modules.context.get('panel').revision, 'pending')
  assert.equal(observable.getSnapshot(), mismatch)
  pause = undefined

  // Failed accepted code can retry while the foreign Host notification stays visible.
  await modules.sync(graph('three'))
  fail = 'retry'
  await modules.sync(graph('retry'))
  assert.ok(modules.state.getSnapshot().failures.length)
  const failedState = modules.state.getSnapshot()
  const failedNotices = stateChanges
  await modules.sync({ release: changedCommit })
  assert.equal(modules.state.getSnapshot(), failedState)
  assert.equal(stateChanges, failedNotices)
  const retryMismatch = observable.getSnapshot()
  fail = undefined
  await modules.retry()
  assert.equal(modules.context.get('panel').revision, 'retry')
  assert.equal(modules.state.getSnapshot().failures.length, 0)
  assert.equal(observable.getSnapshot(), retryMismatch, 'Accepted retry retains the actual foreign Host history')
  await modules.sync({ release: foreign })
  assert.deepEqual(observable.getSnapshot(), { page, previous: changedCommit, host: foreign })

  // Missing and malformed identities compare as absent before the complete parser runs.
  for (const wire of [{}, null, { release: { version: 42 } }, { release: { version: page.version, commit: null } }]) {
    await modules.sync(wire)
    assert.equal(modules.context.get('panel').revision, 'retry')
    assert.equal(observable.getSnapshot().host, undefined)
  }
  assert.throws(() => modules.sync({ release: page }), /rev/,
    'Matching release must run the complete graph parser')
  await modules.sync(graph('retry'))
  await modules.sync({ release: foreign })
  const disconnectMismatch = observable.getSnapshot()
  await modules.disconnect()
  assert.equal(modules.context.get('panel'), undefined)
  assert.equal(observable.getSnapshot(), disconnectMismatch, 'Synthetic withdrawal does not observe another Host')
  await modules.sync(graph('still-blocked', foreign))
  assert.equal(modules.context.get('panel'), undefined)
  await modules.sync(graph('restored'))
  assert.equal(modules.context.get('panel').revision, 'restored')
  await modules.sync({ release: foreign })
  const disposalMismatch = observable.getSnapshot()
  await modules.dispose()
  assert.equal(observable.getSnapshot(), disposalMismatch)
  await assert.rejects(modules.sync({ release: page }), /disposed/)
  unsubscribe(); stopState()
  modules = undefined

  const absent = { rev: 'absent', entries: [], batches: [] }
  modules = await boot(absent)
  await modules.sync({ release: foreign, unknown: true })
  assert.deepEqual(modules.releaseChange.getSnapshot(), { page: undefined, previous: undefined, host: foreign })
  for (const release of [null, { version: 4 }, { version: 'x', commit: null }]) {
    assert.throws(() => modules.sync({ ...absent, release }), /release/,
      'Malformed identity compares as absent, then the full parser rejects it')
  }
  await modules.sync(absent)
  assert.equal(modules.releaseChange.getSnapshot(), undefined)
  await modules.dispose(); modules = undefined
  for (const release of [null, { version: 4 }, { version: 'x', commit: null }]) {
    await assert.rejects(boot({ ...absent, release }), /release/)
  }
  // A notification subscriber may synchronously receive another Host graph. Admission
  // still belongs to each observed frame, even when publishing changes the latest Host.
  modules = await boot({ rev: 'reentrant', release: page, entries: [], batches: [] })
  const nested = []
  const stopReentrant = modules.releaseChange.subscribe(() => {
    if (modules.releaseChange.getSnapshot()?.host?.version === foreign.version) {
      nested.push(modules.sync(graph('reentrant-home')))
    }
  })
  const beforeReentrant = loads.length
  await modules.sync(graph('reentrant-foreign', foreign))
  await Promise.all(nested)
  assert.deepEqual(loads.slice(beforeReentrant), ['reentrant-home'],
    'A reentrant boot-release graph must not admit the foreign graph whose notification triggered it')
  assert.equal(modules.context.get('panel').revision, 'reentrant-home')
  assert.equal(modules.releaseChange.getSnapshot(), undefined)
  stopReentrant()
  await modules.sync({ release: foreign })
  const lastHost = { version: '1.2.5', commit: 'fixed' }
  const stopReturning = modules.releaseChange.subscribe(() => {
    if (modules.releaseChange.getSnapshot() === undefined) {
      nested.push(modules.sync({ release: lastHost, future: true }))
    }
  })
  await modules.sync(graph('reentrant-return'))
  await Promise.all(nested)
  assert.equal(modules.context.get('panel').revision, 'reentrant-return',
    'A later reentrant foreign observation must not refuse an already accepted boot-release frame')
  assert.deepEqual(modules.releaseChange.getSnapshot(), { page, previous: page, host: lastHost })
  stopReturning()
  await modules.dispose(); modules = undefined
  console.log('PASS public release gate: exact identity, early foreign refusal, dismissal, history, retry, pending work, reentrant subscribers and teardown')

  // Run the generated event client and shared mux transport through management reconnects.
  const streams = []
  class Socket extends EventTarget {
    calls = new Map()
    closed = false
    constructor() { super(); queueMicrotask(() => this.dispatchEvent(new Event('open'))) }
    send(text) {
      const frame = JSON.parse(text)
      if (frame.type === 'cancel') { this.calls.delete(frame.streamId); return }
      assert.equal(frame.type, 'open')
      assert.equal(frame.endpoint, '$events')
      const socket = this
      const stream = { send(value) {
        socket.dispatchEvent(new MessageEvent('message', { data: JSON.stringify({ type: 'item', streamId: frame.streamId, value }) }))
      } }
      this.calls.set(frame.streamId, stream)
      streams.push(stream)
      queueMicrotask(() => stream.send({ type: 'ready', clientId: frame.streamId, host: { home: '' } }))
    }
    close() { if (this.closed) return; this.closed = true; this.calls.clear(); this.dispatchEvent(new Event('close')) }
  }
  let generation = 'first', hostGraph = graph('management')
  modules = await boot(hostGraph)
  const original = modules.context.get('panel')
  const connection = new TypertConnection(origin + '/remote', fetch, () => new Socket())
  const context = new Context()
  const events = createEvents(context, connection)
  const management = createManagementClient({ modules, events,
    recovery: { backoffBaseMs: 5, backoffMaxMs: 5, generationReadyWarnMs: 1000, generationReadyTimeoutMs: 2000 },
    transport: { async fetch(url) {
      if (url.endsWith('/state')) return Response.json({ protocol: 1, generation, restartRequired: false,
        selectedBundles: [], loadedBundles: [] })
      if (url.endsWith('/client/graph')) return Response.json(hostGraph)
      throw new Error('Unexpected management request: ' + url)
    } },
  })
  try {
    await until(() => management.connection.getSnapshot().phase === 'connected', 'Management boot')
    const before = loads.length
    generation = 'foreign'; hostGraph = { release: foreign, unknown: 'future graph' }
    management.reconnect()
    await until(() => management.connection.getSnapshot().state?.generation === generation, 'Foreign reconnect settles')
    assert.equal(modules.context.get('panel'), original)
    assert.equal(loads.length, before)
    assert.deepEqual(modules.releaseChange.getSnapshot(), { page, previous: page, host: foreign })
    modules.dismissRelease()
    generation = 'foreign-again'; management.reconnect()
    await until(() => management.connection.getSnapshot().state?.generation === generation, 'Dismissed reconnect settles')
    assert.equal(modules.releaseChange.getSnapshot(), undefined)
    assert.equal(modules.context.get('panel'), original)
    generation = 'home'; hostGraph = graph('management-return')
    management.reconnect()
    await until(() => management.connection.getSnapshot().state?.generation === generation, 'Boot release reconnect settles')
    assert.equal(modules.context.get('panel').revision, 'management-return')
    assert.equal(modules.releaseChange.getSnapshot(), undefined)
    await management.close()
    assert.equal(modules.context.get('panel'), undefined)
  } finally {
    await management.close(); await events.dispose(); await connection.dispose(); await context.fiber.dispose()
  }
  console.log('PASS generated management reconnect blocks unknown foreign graphs, retains plugins and resumes the boot release')
} finally {
  pause?.release.resolve()
  await modules?.dispose()
  await new Promise(done => server.close(done))
}
assert.equal(globalThis.__ModuleLoader__, undefined)
for (const revision of new Set(starts)) assert.equal(starts.filter(value => value === revision).length,
  stops.filter(value => value === revision).length, 'Effects settle for ' + revision)
