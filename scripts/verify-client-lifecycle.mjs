import assert from 'node:assert/strict'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

// Consume the built public SDK. Controlled carriers test recovery deterministically;
// real script/HTTP arrival is exercised separately by the browser carrier.
globalThis.location = new URL('http://localhost/')
globalThis.document = { querySelectorAll() { return [] } }
const { bootClientModules, createManagementClient, ManagementOutcomeUnknown } = await import(
  pathToFileURL(resolve(process.argv[2] ?? 'clients/modules/lib', 'client.mjs')).href)
const empty = { rev: 'empty', entries: [], batches: [] }
const graph = { rev: 'one', entries: [{ id: 'panel', url: '/panel.js?rev=one', rev: 'one', inject: [], external: [] }],
  batches: [{ phase: 'application', url: '/panel.js?rev=one', rev: 'one', entries: ['panel'] }] }
let apply = 0, dispose = 0
const loadBundle = async () => {
  globalThis.__ModuleLoader__.load({ id: 'panel', factory: () => ({ apply(ctx) {
    apply++
    ctx.provide('panel', { draft: 'unsaved draft' })
    ctx.effect(() => () => { dispose++ })
  } }) })
}
async function until(predicate, message) {
  const deadline = Date.now() + 4000
  while (!predicate()) {
    assert.ok(Date.now() < deadline, message)
    await delay(2)
  }
}
const modules = await bootClientModules({ graph, loadBundle })
const initial = modules.context.get('panel')
let generation = 'host-1', currentGraph = graph, failing = false, reads = 0, writes = 0
const streams = []
const recovery = { backoffBaseMs: 5, backoffMaxMs: 5, generationReadyWarnMs: 1000, generationReadyTimeoutMs: 2000 }
const transport = {
  async fetch(url, init) {
    if (init.method === 'POST') { writes++; throw new Error('response lost after accepting write') }
    if (url.endsWith('/state')) {
      reads++
      return failing ? new Response('temporary failure', { status: 503 }) : Response.json({ protocol: 1, generation,
        restartRequired: false, selectedBundles: [], loadedBundles: [] })
    }
    if (url.endsWith('/client/graph')) return Response.json(currentGraph)
    throw new Error('Unexpected request: ' + url)
  },
  openEvents(url, callbacks, signal) {
    assert.ok(url.endsWith('/events'))
    const stream = { callbacks, signal, closed: false, sequence: 0 }
    streams.push(stream)
    queueMicrotask(() => callbacks.message(JSON.stringify({ generation, sequence: ++stream.sequence, kind: 'connected', value: null })))
    return () => { stream.closed = true }
  },
}
const client = createManagementClient({ modules, transport, recovery })
try {
  await until(() => client.connection.getSnapshot().phase === 'connected', 'Initial management handshake')
  const firstStream = streams.at(-1)
  firstStream.callbacks.error(new Error('carrier lost'))
  await until(() => streams.length > 1 && client.connection.getSnapshot().phase === 'connected', 'Automatic recovery')
  assert.equal(modules.context.get('panel'), initial)
  assert.equal(initial.draft, 'unsaved draft')
  assert.deepEqual([apply, dispose], [1, 0], 'Transport loss must not replace the plugin tree')
  assert.equal(firstStream.closed, true)
  generation = 'host-2'
  client.reconnect()
  await until(() => client.connection.getSnapshot().state?.generation === 'host-2', 'Manual generation refresh')
  assert.equal(modules.context.get('panel'), initial, 'Same artifacts across Host generations retain instances')
  await assert.rejects(client.enable('plugin', 'panel', true), ManagementOutcomeUnknown)
  const priorReads = reads
  failing = true
  client.reconnect()
  await until(() => reads > priorReads, 'Failed handshake was attempted')
  const failedStream = streams.at(-1)
  failing = false
  await until(() => reads > priorReads + 1 && client.connection.getSnapshot().phase === 'connected', 'Healthy endpoint recovers without a later invalidation event')
  assert.equal(failedStream.closed, true, 'Failed handshake releases a still-open event carrier')
  assert.equal(writes, 1, 'Recovery must not replay an uncertain write')
  // An obsolete stream cannot withdraw the current graph or republish stale readiness.
  firstStream.callbacks.message(JSON.stringify({ generation: 'obsolete', sequence: 50, kind: 'connected', value: null }))
  await delay(10)
  assert.equal(client.connection.getSnapshot().state.generation, 'host-2')
  assert.equal(modules.context.get('panel'), initial)
  currentGraph = empty
  const stream = streams.at(-1)
  stream.callbacks.message(JSON.stringify({ generation, sequence: ++stream.sequence, kind: 'client-modules', value: null }))
  await until(() => dispose === 1, 'An actual graph withdrawal releases effects')
  await client.close()
  const count = streams.length
  client.reconnect()
  await delay(15)
  assert.equal(streams.length, count, 'Close prevents further recovery')
  assert.equal(client.connection.getSnapshot().phase, 'closed')
} finally { await client.close(); await modules.dispose() }
console.log('PASS carrier recovery preserves plugins, fences generations and never replays writes')

// Startup audit must reject import/apply/pending failures and clean up for the next boot.
for (const mode of ['import', 'apply', 'pending']) {
  await assert.rejects(bootClientModules({ graph, async loadBundle() {
    if (mode === 'import') throw new Error('artifact unavailable')
    globalThis.__ModuleLoader__.load({ id: 'panel', factory: () => ({
      inject: mode === 'pending' ? ['missing-service'] : [],
      apply() { if (mode === 'apply') throw new Error('activation rejected') },
    }) })
  } }), mode === 'pending' ? /pending.*missing-service/ : mode === 'import' ? /import failed.*artifact unavailable/ : /panel: failed/)
  assert.equal(globalThis.__ModuleLoader__, undefined, 'Rejected startup must release its facade')
}
const retry = await bootClientModules({ graph: empty })
await retry.dispose()
console.log('PASS fixed upstream startup audit rejects import, apply and pending entries; retry remains possible')

// Faulty shell cleanup must not strand readiness or the controller's recovery loop.
let attempts = 0, activeCallbacks
const cleanupFailure = createManagementClient({ recovery, transport: {
  fetch: transport.fetch,
  openEvents(_url, callbacks) {
    attempts++
    activeCallbacks = callbacks
    queueMicrotask(() => callbacks.message(JSON.stringify({ generation, sequence: 1, kind: 'connected', value: null })))
    return () => { throw new Error('injected cleanup failure') }
  },
} })
await until(() => cleanupFailure.connection.getSnapshot().phase === 'connected', 'Cleanup fixture ready')
activeCallbacks.error(new Error('transport lost'))
await until(() => attempts >= 2 && cleanupFailure.connection.getSnapshot().phase === 'connected', 'Cleanup failure must not strand recovery')
await cleanupFailure.close()
console.log('PASS carrier cleanup failure still settles the connection generation')

// A slow in-flight handshake belongs to one generation and must abort before replacement.
let pendingSignal, pendingStarted = false, aborted = false, blocked = true
const cancellationClient = createManagementClient({ recovery, transport: {
  ...transport,
  async fetch(url, init) {
    if (blocked && url.endsWith('/state')) {
      pendingSignal = init.signal
      pendingStarted = true
      return new Promise((_resolve, reject) => init.signal.addEventListener('abort', () => {
        aborted = true
        reject(new Error('obsolete handshake aborted'))
      }, { once: true }))
    }
    return transport.fetch(url, init)
  },
} })
await until(() => pendingStarted, 'Handshake read pending')
blocked = false
cancellationClient.reconnect()
await until(() => cancellationClient.connection.getSnapshot().phase === 'connected', 'Reconnect drains cancelled read')
assert.equal(pendingSignal.aborted, true)
assert.equal(aborted, true)
await cancellationClient.close()
console.log('PASS reconnect cancels an in-flight handshake before replacement readiness')

// Browser ownership forwards offline/online to the fixed controller and removes its listeners.
const network = new EventTarget()
network.navigator = { onLine: false }
globalThis.window = network
const streamCount = streams.length
const networkClient = createManagementClient({ recovery, transport })
await delay(20)
assert.equal(streams.length, streamCount, 'Offline startup must not open the carrier')
network.dispatchEvent(new Event('online'))
await until(() => networkClient.connection.getSnapshot().phase === 'connected', 'Online resumes the controller')
network.dispatchEvent(new Event('offline'))
await until(() => networkClient.connection.getSnapshot().phase === 'disconnected', 'Offline invalidates readiness')
const paused = streams.length
await delay(20)
assert.equal(streams.length, paused)
await networkClient.close()
network.dispatchEvent(new Event('online'))
await delay(20)
assert.equal(streams.length, paused)
delete globalThis.window
console.log('PASS browser offline suspension, online recovery and owner cleanup')

// close immediately refuses new work, but every caller joins the same effect cleanup.
let releaseCleanup
const closingModules = await bootClientModules({ graph, async loadBundle() {
  globalThis.__ModuleLoader__.load({ id: 'panel', factory: () => ({ apply(ctx) {
    ctx.effect(() => () => new Promise(resolve => { releaseCleanup = resolve }))
  } }) })
} })
const closingClient = createManagementClient({ modules: closingModules, recovery, transport })
currentGraph = graph
await until(() => closingClient.connection.getSnapshot().phase === 'connected', 'Close fixture ready')
const writesBeforeClose = writes
const closing = closingClient.close()
assert.equal(closingClient.connection.getSnapshot().phase, 'closed')
assert.equal(closingClient.close(), closing, 'Repeated close must join the same withdrawal')
await until(() => releaseCleanup !== undefined, 'Plugin cleanup started')
await assert.rejects(closingClient.enable('plugin', 'panel', true), /not connected/)
await assert.rejects(closingClient.readState(), /closed/)
assert.equal(writes, writesBeforeClose)
let closed = false
void closing.then(() => { closed = true })
await delay(5)
assert.equal(closed, false, 'close must await async plugin withdrawal')
releaseCleanup()
await closing
await closingModules.dispose()
console.log('PASS close fences new operations and joins asynchronous withdrawal')
