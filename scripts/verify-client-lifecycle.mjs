import assert from 'node:assert/strict'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

globalThis.location = new URL('http://localhost/')
globalThis.document = { querySelectorAll() { return [] } }
const runtime = resolve(process.argv[2] ?? 'clients/modules/lib')
const { bootClientModules, createManagementClient, ManagementOutcomeUnknown } = await import(pathToFileURL(resolve(runtime, 'client.mjs')).href)
const { createRemote } = await import(pathToFileURL(resolve(runtime, 'notifications.mjs')).href)
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
  while (!predicate()) { assert.ok(Date.now() < deadline, message); await delay(2) }
}
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
}
// Controlled HTTP bytes pass through the actual generated notification client. No SDK protocol wrapper is supplied.
function notifications() {
  return createRemote('http://localhost/remote', async (url, init) => {
    assert.ok(url.endsWith('/remoteNotifications/watch/stream'))
    let output
    const stream = { closed: false,
      send(value) { if (!this.closed) output.enqueue(new TextEncoder().encode(JSON.stringify({ ok: true, value }) + '\n')) },
      fail(error) { if (!this.closed) { this.closed = true; output.error(error) } },
    }
    const abort = () => stream.fail(new Error('Notification request aborted'))
    const body = new ReadableStream({
      start(controller) { output = controller; streams.push(stream); init.signal.addEventListener('abort', abort, { once: true }) },
      cancel() { stream.closed = true; init.signal.removeEventListener('abort', abort) },
    })
    queueMicrotask(() => stream.send({ kind: 'ready' }))
    return new Response(body, { headers: { 'content-type': 'application/x-ndjson' } })
  })
}
const modules = await bootClientModules({ graph, loadBundle })
const initial = modules.context.get('panel')
const notificationClient = notifications()
const client = createManagementClient({ modules, notifications: notificationClient, transport, recovery })
try {
  await until(() => client.connection.getSnapshot().phase === 'connected', 'Initial notification handshake')
  const firstStream = streams.at(-1)
  const progress = []
  const unsubscribe = client.subscribe(event => progress.push(event))
  const progressReads = reads
  const progressFrames = [
    { kind: 'event', event: 'plugin-manager/install-state', args: [{ requestId: 'original-install', phase: 'prepare' }] },
    { kind: 'event', event: 'plugin-manager/install-log', args: [{ requestId: 'original-install', phase: 'prepare', output: 'building' }] },
  ]
  for (const frame of progressFrames) firstStream.send(frame)
  await until(() => progress.length === 2, 'Original install progress forwarded')
  assert.deepEqual(progress, progressFrames, 'Progress must preserve original event names and arguments')
  assert.equal(reads, progressReads, 'Progress must not query state or synchronize the graph')
  assert.deepEqual([apply, dispose], [1, 0], 'Progress must preserve page plugin instances')
  unsubscribe()
  firstStream.fail(new Error('carrier lost'))
  await until(() => streams.length > 1 && client.connection.getSnapshot().phase === 'connected', 'Automatic resubscription and query')
  assert.equal(modules.context.get('panel'), initial)
  assert.equal(initial.draft, 'unsaved draft')
  assert.deepEqual([apply, dispose], [1, 0], 'Transport loss must not replace the plugin tree')
  assert.equal(firstStream.closed, true)
  generation = 'host-2'
  client.reconnect()
  await until(() => client.connection.getSnapshot().state?.generation === 'host-2', 'Manual generation refresh')
  assert.equal(modules.context.get('panel'), initial, 'Same artifacts across Host generations retain instances')
  await assert.rejects(client.mutateConfiguration('root:panel', [{ op: 'set', path: ['draft'], value: 'saved' }], 'config-revision'), ManagementOutcomeUnknown)
  const priorReads = reads
  failing = true
  client.reconnect()
  await until(() => reads > priorReads, 'Failed handshake was attempted')
  const failedStream = streams.at(-1)
  failing = false
  await until(() => reads > priorReads + 1 && client.connection.getSnapshot().phase === 'connected', 'Healthy endpoint recovers without a later notification')
  assert.equal(failedStream.closed, true, 'Failed query releases its still-open notification stream')
  assert.equal(writes, 1, 'Recovery must not replay an uncertain write')
  currentGraph = empty
  streams.at(-1).send({ kind: 'event', event: 'client-modules/changed', args: [] })
  await until(() => dispose === 1, 'Actual graph withdrawal releases effects')
  await client.close()
  const count = streams.length
  client.reconnect()
  await delay(15)
  assert.equal(streams.length, count, 'Close prevents further recovery')
  assert.equal(client.connection.getSnapshot().phase, 'closed')
} finally { await client.close(); notificationClient.dispose(); await modules.dispose() }
console.log('PASS generated notification recovery queries state, preserves plugins and never replays writes')

for (const mode of ['import', 'apply', 'pending']) {
  await assert.rejects(bootClientModules({ graph, async loadBundle() {
    if (mode === 'import') throw new Error('artifact unavailable')
    globalThis.__ModuleLoader__.load({ id: 'panel', factory: () => ({
      inject: mode === 'pending' ? ['missing-service'] : [],
      apply() { if (mode === 'apply') throw new Error('activation rejected') },
    }) })
  } }), mode === 'pending' ? /pending.*missing-service/ : mode === 'import' ? /import failed.*artifact unavailable/ : /panel: failed/)
  assert.equal(globalThis.__ModuleLoader__, undefined, 'Rejected startup releases its facade')
}
const retry = await bootClientModules({ graph: empty })
await retry.dispose()
console.log('PASS fixed upstream startup audit rejects import, apply and pending entries; retry remains possible')

let pendingSignal, pendingStarted = false, aborted = false, blocked = true
const cancellationNotifications = notifications()
const cancellationClient = createManagementClient({ notifications: cancellationNotifications, recovery, transport: {
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
cancellationNotifications.dispose()
console.log('PASS reconnect cancels an in-flight query before replacement readiness')

const network = new EventTarget()
network.navigator = { onLine: false }
globalThis.window = network
const streamCount = streams.length
const networkNotifications = notifications()
const networkClient = createManagementClient({ notifications: networkNotifications, recovery, transport })
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
networkNotifications.dispose()
network.dispatchEvent(new Event('online'))
await delay(20)
assert.equal(streams.length, paused)
delete globalThis.window
console.log('PASS browser offline suspension, online recovery and owner cleanup')

let releaseCleanup
const closingModules = await bootClientModules({ graph, async loadBundle() {
  globalThis.__ModuleLoader__.load({ id: 'panel', factory: () => ({ apply(ctx) {
    ctx.effect(() => () => new Promise(resolve => { releaseCleanup = resolve }))
  } }) })
} })
const closingNotifications = notifications()
const closingClient = createManagementClient({ modules: closingModules, notifications: closingNotifications, recovery, transport })
currentGraph = graph
await until(() => closingClient.connection.getSnapshot().phase === 'connected', 'Close fixture ready')
const writesBeforeClose = writes
const closing = closingClient.close()
assert.equal(closingClient.connection.getSnapshot().phase, 'closed')
assert.equal(closingClient.close(), closing, 'Repeated close joins the same withdrawal')
await until(() => releaseCleanup !== undefined, 'Plugin cleanup started')
await assert.rejects(closingClient.mutateConfiguration('root:panel', [], 'revision'), /not connected/)
await assert.rejects(closingClient.readState(), /closed/)
assert.equal(writes, writesBeforeClose)
let closed = false
void closing.then(() => { closed = true })
await delay(5)
assert.equal(closed, false, 'close must await async plugin withdrawal')
releaseCleanup()
await closing
closingNotifications.dispose()
await closingModules.dispose()
console.log('PASS close fences new operations and joins asynchronous withdrawal')
