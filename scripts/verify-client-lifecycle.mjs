import assert from 'node:assert/strict'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

globalThis.location = new URL('http://localhost/')
globalThis.document = { querySelectorAll() { return [] } }
const runtime = resolve(process.argv[2] ?? 'clients/modules/lib')
const { bootClientModules, createManagementClient, ManagementOutcomeUnknown } = await import(pathToFileURL(resolve(runtime, 'client.mjs')).href)
const { createRemote, TypertConnection } = await import(pathToFileURL(resolve(runtime, 'notifications.mjs')).href)
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
// The controller's recovery and plugin ownership are tested through the shared
// mux client. The fixture controls only socket frames, not notification semantics.
function notifications() {
  class Socket extends EventTarget {
    calls = new Map()
    closed = false
    constructor(url) {
      super()
      assert.equal(url.pathname, '/remote/remote.mux')
      queueMicrotask(() => this.dispatchEvent(new Event('open')))
    }
    send(text) {
      const frame = JSON.parse(text)
      if (frame.type === 'cancel') {
        this.calls.get(frame.streamId).closed = true
        this.calls.delete(frame.streamId)
        return
      }
      assert.equal(frame.type, 'open')
      assert.equal(frame.endpoint, 'remoteNotifications/watch')
      const socket = this
      const stream = { closed: false,
        send(value) {
          if (!this.closed) socket.dispatchEvent(new MessageEvent('message', {
            data: JSON.stringify({ type: 'item', streamId: frame.streamId, value }),
          }))
        },
        fail() { socket.close() },
      }
      this.calls.set(frame.streamId, stream)
      streams.push(stream)
      queueMicrotask(() => stream.send({ kind: 'ready' }))
    }
    close() {
      if (this.closed) return
      this.closed = true
      for (const stream of this.calls.values()) stream.closed = true
      this.calls.clear()
      this.dispatchEvent(new Event('close'))
    }
  }
  const connection = new TypertConnection('http://localhost/remote', fetch, url => new Socket(url))
  const remote = createRemote(connection)
  return { ...remote, async dispose() { await remote.dispose(); await connection.dispose() } }
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
} finally { await client.close(); await notificationClient.dispose(); await modules.dispose() }
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
await cancellationNotifications.dispose()
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
await networkNotifications.dispose()
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
await closingNotifications.dispose()
await closingModules.dispose()
console.log('PASS close fences new operations and joins asynchronous withdrawal')

// Carrier admission: cancellation before open must have no Host-visible effect.
const unobserved = []
const observeRejection = error => unobserved.push(error)
process.on('unhandledRejection', observeRejection)
try {
  for (const mode of ['pre-aborted', 'pre-aborted-null', 'immediate-dispose', 'connecting']) {
    const frames = []
    class ControlledSocket extends EventTarget {
      send(text) { frames.push(JSON.parse(text)) }
      close() { this.dispatchEvent(new Event('close')) }
      receive(frame) { this.dispatchEvent(new MessageEvent('message', {data: JSON.stringify(frame)})) }
    }
    const socket = new ControlledSocket()
    const connection = new TypertConnection('http://localhost/remote', fetch, () => socket)
    const remote = createRemote(connection)
    const sibling = remote['remoteNotifications/watch']({})
    try {
      if (mode !== 'connecting') {
        socket.dispatchEvent(new Event('open'))
        await until(() => frames.length === 1, 'Sibling open reached the ready carrier')
      }
      const controller = new AbortController()
      const reason = mode === 'pre-aborted-null' ? null : new Error('cancel before wire admission')
      if (mode.startsWith('pre-aborted')) controller.abort(reason)
      const cancelled = remote['remoteNotifications/watch']({}, controller.signal)
      if (mode === 'immediate-dispose') await cancelled.dispose()
      else {
        if (mode === 'connecting') controller.abort(reason)
        await assert.rejects(cancelled[Symbol.asyncIterator]().next(), error => error === reason)
      }
      if (mode === 'connecting') {
        socket.dispatchEvent(new Event('open'))
        await until(() => frames.length !== 0, 'Sibling survives connection preparation')
      }
      assert.deepEqual(frames.map(frame => frame.type), ['open'], mode + ': cancelled stream sent frames')
      const item = sibling[Symbol.asyncIterator]().next()
      socket.receive({type:'item', streamId:frames[0].streamId, value:{kind:'ready'}})
      assert.deepEqual(await item, {done:false, value:{kind:'ready'}})
    } finally { await remote.dispose(); await connection.dispose() }
  }
  let connections = 0
  const freshConnection = new TypertConnection('http://localhost/remote', fetch, () => { connections++; throw new Error('unexpected carrier') })
  const fresh = createRemote(freshConnection)
  const reason = new Error('already cancelled')
  const signal = AbortSignal.abort(reason)
  try {
    const call = fresh['remoteNotifications/watch']({}, signal)
    await assert.rejects(call[Symbol.asyncIterator]().next(), error => error === reason)
    assert.equal(connections, 0, 'Pre-cancelled call must not acquire a carrier')
    const invalid = {}; invalid.self = invalid
    const invalidCall = fresh['remoteNotifications/watch'](invalid, signal)
    await assert.rejects(invalidCall[Symbol.asyncIterator]().next(), error => error instanceof TypeError)
    assert.equal(connections, 0, 'Local preparation failure must precede cancellation and carrier creation')
  } finally { await fresh.dispose(); await freshConnection.dispose() }
  await delay(0)
  assert.deepEqual(unobserved, [], 'Cancelled carrier admission leaked a rejection')
} finally { process.off('unhandledRejection', observeRejection) }
console.log('PASS pre-abort, immediate dispose and connecting cancellation send zero open frames and preserve siblings')
