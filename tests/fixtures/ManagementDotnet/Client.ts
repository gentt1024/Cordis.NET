import { createRemote as createManagementRemote, TypertConnection, RemoteError, type RemoteResult } from './management.mjs'
import { createEvents, type EventListeners } from './events.mjs'
import { createRemote as createSettingsRemote } from './settings.mjs'
import type { RemoteEventContext } from './cordis-remote.mjs'
import { RemoteStreamCarrierError, typertOwnedValue } from './cordis-remote.mjs'
type Notification = { [K in keyof EventListeners]: { event: K; args: Parameters<EventListeners[K]> } }[keyof EventListeners]
type NotificationResult = Notification
type EventOwner = RemoteEventContext & { fiber: {dispose(): Promise<void>} }
function assert(value: unknown, reason: string): asserts value { if (!value) throw new Error(reason) }
function accepted<T>(result: RemoteResult<T>): T { if (!result.ok) throw result.error; return result.value }
async function failure(call: Promise<RemoteResult<unknown>>, code: string) {
  const result = await call
  assert(!result.ok && result.error.code === code, 'Expected Remote failure: ' + code)
}
async function next(reader: AsyncIterator<NotificationResult>): Promise<Notification> {
  const result = await reader.next()
  assert(!result.done, 'Notification subscription ended without its expected item')
  return result.value
}
async function event(reader: AsyncIterator<NotificationResult>, match: (frame: Notification) => boolean) {
  while (true) { const frame = await next(reader); if (match(frame)) return frame }
}

/** Strict independent TypeScript author consuming generated declarations and the actual native Host. */
export async function run(origin: string, createContext: () => EventOwner) {
  const fetcher: typeof fetch = (input, init) => {
    const headers = new Headers(init?.headers)
    headers.set('Authorization', 'consumer-policy')
    return fetch(input, { ...init, headers })
  }
  const connection = new TypertConnection(origin + '/remote', fetcher, url => { url.searchParams.set('consumer', 'consumer-policy'); return new WebSocket(url) })
  await scopedEvents(origin, connection, createContext)
  const manager = createManagementRemote(connection)
  const settings = createSettingsRemote(connection)
  const notifications = await observe(connection, createContext)
  const reader = notifications.reader
  const remotes = [manager, settings, notifications]
  const nullableSample: Parameters<EventListeners['alpha/changed']> = [[null], {empty: null}, ['required']]
  // @ts-expect-error The third argument still has non-null elements.
  const invalidRequired: Parameters<EventListeners['alpha/changed']>[2] = [null]
  void nullableSample; void invalidRequired
  const emitted = await fetch(origin + '/control/collection-events', {method: 'POST'})
  assert(emitted.ok, 'Collection event fixture did not emit')
  const firstCollection = await next(reader)
  assert(firstCollection.event === 'alpha/changed' && firstCollection.args[0]?.[0] === 'text' &&
    firstCollection.args[0][1] === null && firstCollection.args[1].empty === null &&
    firstCollection.args[2][0] === 'required', 'Generated TS listener lost nullable elements')
  const secondCollection = await next(reader)
  assert(secondCollection.event === 'alpha/changed' && secondCollection.args[0] === null &&
    Object.keys(secondCollection.args[1]).length === 0 && secondCollection.args[2].length === 0,
    'Generated TS listener lost nullable root')
  try {
    assert(accepted(await settings['settings/describe']({})).hasDocument, 'Standard Settings lost its document capability')
    assert(accepted(await settings['settings/openSettingsDocument']({})).opened, 'Standard Settings did not accept document opening')
    assert(await (await fetch(origin + '/control/opened-documents')).json() === 1,
      'Generated TS document call did not reach the current host opener exactly once')
    const plugins = accepted(await manager['pluginManager/listPlugins']({}))
    const worker = plugins.find(row => row.entryId === 'root:worker')
    assert(worker?.enabled && worker.patchId === 'worker' && worker.readOnlyReason == null, 'Generated inventory lost the writable profile entry')
    assert(plugins.find(row => row.entryId === 'root')?.readOnlyReason === 'management-required', 'Generated inventory lost the protected root')
    const disabled = accepted(await manager['pluginManager/setPluginEnabled']({ id: 'root:worker', enabled: false }))
    assert(disabled.changed && disabled.application === 'applied' && disabled.error == null, 'Typed disable lost the actual change result')
    assert(accepted(await manager['pluginManager/listPlugins']({})).some(row => row.entryId === 'root:worker' && !row.enabled), 'Real plugin did not become disabled')
    accepted(await manager['pluginManager/setPluginEnabled']({ id: 'root:worker', enabled: true }))
    await event(reader, frame => frame.event === 'plugin-manager/changed')
    const [source] = accepted(await manager['pluginManager/sources']({}))
    assert(source !== undefined, 'Selected source was absent')
    assert(accepted(await manager['pluginManager/versions']({ name: 'existing', source })).includes('2.0.0'), 'Versions did not use the selected source')
    assert(accepted(await manager['pluginManager/waitForInstall']({ requestId: 'unknown' })) === null, 'Unknown wait implied success')
    assert(accepted(await manager['pluginManager/cancelInstall']({ requestId: 'unknown' })).status === 'not-running', 'Unknown cancel lost its status')
    const request = { name: 'existing', version: '2.0.0', source }
    const inspection = accepted(await manager['pluginManager/inspect']({ request }))
    assert(inspection.request.name === 'existing' && inspection.requiresBuildApproval, 'Inspection lost independent build authority')
    await deny('install', true)
    await failure(manager['pluginManager/installBundle']({ request, options: { requestId: 'resource-denied', inspectedHash: inspection.contentHash, enabled: true } }), 'plugin-manager/forbidden')
    await deny('install', false)
    assert(accepted(await manager['pluginManager/waitForInstall']({ requestId: 'resource-denied' })) === null, 'Resource refusal admitted an installation')
    const stale = accepted(await manager['pluginManager/installBundle']({ request, options: { requestId: 'stale', inspectedHash: 'stale', enabled: true } }))
    assert(stale.stage === 'inspect' && stale.application === 'failed' && stale.error === 'inspection-changed' && stale.installed, 'Admitted stale approval lost its failure result')
    await deny('build', true)
    const buildDenied = accepted(await manager['pluginManager/installBundle']({ request, options: { requestId: 'denied', inspectedHash: inspection.contentHash, enabled: true } }))
    assert(buildDenied.stage === 'inspect' && buildDenied.application === 'failed' && buildDenied.error === 'build-not-approved', 'Admitted build refusal lost its failure result')
    assert((await probe()).preparations === 0, 'Inspect authority became build permission')
    await deny('build', false)
    const incompatible = { name: 'incompatible', version: '1.0.0', source }
    const incompatibleInspection = accepted(await manager['pluginManager/inspect']({ request: incompatible }))
    const refused = accepted(await manager['pluginManager/installBundle']({ request: incompatible,
      options: { requestId: 'incompatible', inspectedHash: incompatibleInspection.contentHash, enabled: true } }))
    assert(refused.application === 'failed' && refused.stage === 'inspect' && !refused.installed, 'Build authority granted a version exemption')
    accepted(await manager['pluginManager/setVersionExemption']({ packageVersion: 'incompatible@1.0.0', runtimeVersion: '0.2.0-rc.2', enabled: true, acceptRisk: true }))
    const grants = accepted(await manager['pluginManager/listVersionExemptions']({}))
    assert(grants.exemptions['incompatible@1.0.0'].includes('0.2.0-rc.2'), 'Exact-version dictionary contract was lost')
    await deny('build', true)
    const grantWithoutBuild = accepted(await manager['pluginManager/installBundle']({ request: incompatible,
      options: { requestId: 'grant-without-build', inspectedHash: incompatibleInspection.contentHash, enabled: true } }))
    assert(grantWithoutBuild.error === 'build-not-approved' && !grantWithoutBuild.installed, 'A version grant authorized build execution')
    await deny('build', false)
    await control('hold', 'existing', '2.0.0')
    const response = new AbortController()
    const installing = manager['pluginManager/installBundle']({ request,
      options: { requestId: 'disconnected-update', inspectedHash: inspection.contentHash, enabled: true } }, response.signal)
    await control('wait-preparing', 'existing', '2.0.0')
    const log = await event(reader, frame => frame.event === 'plugin-manager/install-log')
    assert(log.args?.length === 1 && object(log.args[0]).requestId === 'disconnected-update', 'Notification lost the original event argument')
    await notifications.dispose()
    await reader.return?.()
    response.abort()
    await failure(installing, 'gateway/cancelled')
    assert((await probe()).cancellations === 0, 'Request disconnect cancelled platform preparation')
    const resumedRemote = await observe(connection, createContext)
    remotes.push(resumedRemote)
    const resumed = resumedRemote.reader
    assert(accepted(await manager['pluginManager/listBundles']({})).some(row => row.name === 'existing' && row.version === '1.0.0'), 'Reconnect inferred success without querying state')
    const waiting = manager['pluginManager/waitForInstall']({ requestId: 'disconnected-update' })
    await control('wait-observed', 'disconnected-update')
    await control('release', 'existing', '2.0.0')
    const updated = accepted(await waiting)
    assert(updated !== null && updated.requestId === 'disconnected-update' && updated.installed && updated.selected &&
      updated.application === 'restart-required' && updated.error == null, 'Original request wait lost restart-required outcome: ' + JSON.stringify(updated))
    assert(accepted(await manager['pluginManager/listBundles']({})).find(row => row.name === 'existing')?.version === '1.0.0',
      'Install rewrote the live mapping before restart')
    await event(resumed, frame => frame.event === 'plugin-manager/changed')
    assert(object(object(JSON.parse((await probe()).manifest)).dependencies).existing === '2.0.0', 'Update did not persist the new profile version')
    assert(accepted(await manager['pluginManager/waitForInstall']({ requestId: 'disconnected-update' })) === null, 'Completed installation was retained as invented task history')
    await control('remove-source')
    let terminal: unknown
    try { for await (const _ of { [Symbol.asyncIterator]: () => resumed }) { /* Drain queued business notifications. */ } }
    catch (error) { terminal = error }
    assert(terminal instanceof RemoteStreamCarrierError, 'Source removal did not terminate the typed observer')
    await control('restore-source')
    const restoredRemote = await observe(connection, createContext)
    remotes.push(restoredRemote)
    const restored = restoredRemote.reader
    assert(accepted(await manager['pluginManager/listPlugins']({})).find(row => row.entryId === 'root:worker')?.enabled, 'Notification failure changed plugin outcome')
    await restored.return?.()
  } finally {
    await notifications.dispose()
    await Promise.all(remotes.map(remote => remote.dispose()))
    await connection.dispose()
    await reader.return?.()
  }
  async function control(name: string, target?: string, version?: string, mode?: string) {
    const query = target === undefined ? '' : '?' + new URLSearchParams({ name: target, version: version ?? '', mode: mode ?? '' })
    const response = await fetch(origin + '/control/' + name + query, { method: 'POST' })
    assert(response.ok, 'Fixture control refused: ' + name)
  }
  async function deny(operation: string, enabled: boolean) {
    const response = await fetch(origin + '/control/deny?' + new URLSearchParams({ operation, enabled: String(enabled) }), { method: 'POST' })
    assert(response.ok, 'Fixture policy control refused')
  }
  async function probe() {
    const row = object(await (await fetch(origin + '/control/probe')).json())
    assert(typeof row.preparations === 'number' && typeof row.cancellations === 'number' && typeof row.manifest === 'string', 'Fixture probe is invalid')
    return { preparations: row.preparations, cancellations: row.cancellations, manifest: row.manifest }
  }
}
function object(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error('Object expected')
  return Object.fromEntries(Object.entries(value))
}

async function observe(connection: TypertConnection, createContext: () => EventOwner) {
  const owner = createContext()
  const client = createEvents(owner, connection)
  const values: Notification[] = []
  let wake: (() => void) | undefined
  let ended = false
  let failure: { error: unknown } | undefined
  const publish = (frame: Notification): void => { values.push(frame); wake?.() }
  client.on(owner, 'plugin-manager/changed', value => publish({event:'plugin-manager/changed',args:[value]}))
  client.on(owner, 'alpha/changed', (values, labels, required) => publish({event:'alpha/changed',args:[values, labels, required]}))
  client.on(owner, 'plugin-manager/install-log', value => publish({event:'plugin-manager/install-log',args:[value]}))
  client.on(owner, 'plugin-manager/install-state', value => publish({event:'plugin-manager/install-state',args:[value]}))
  const controller = new AbortController()
  let accept!: () => void
  let reject!: (error: unknown) => void
  const ready = new Promise<void>((resolve, refused) => { accept=resolve; reject=refused })
  const pump = client.run(controller.signal, () => accept()).catch((error: unknown) => { failure={error}; reject(error) })
    .finally(() => {ended=true; wake?.()})
  await ready
  const reader: AsyncIterator<Notification> = {
    async next() {
      while (!values.length && !ended) await new Promise<void>(resolve => {wake=resolve})
      if (values.length) return {done:false,value:values.shift()!}
      if (failure) throw failure.error
      return {done:true,value:undefined}
    },
    async return() { controller.abort(); await pump; return {done:true,value:undefined} },
  }
  return {reader, async dispose() {controller.abort(); await client.dispose(); await pump; await owner.fiber.dispose()} }
}

export async function scopedEvents(origin: string, connection: TypertConnection, createContext: () => EventOwner) {
  const owner = createContext()
  let resolves = 0, releases = 0, adapterReads = 0
  const releaseCount = (): number => releases
  let firstReleased!: () => void, secondReleased!: () => void
  const firstRelease = new Promise<void>(resolve => {firstReleased=resolve})
  const secondRelease = new Promise<void>(resolve => {secondReleased=resolve})
  let owned = false
  const events = createEvents(owner, connection, {getClient(key) {
    assert(key === 'agent', 'Fixed agent adapter key changed')
    adapterReads++
    return { resolve(identity) {
      assert(identity === 'agent-1', 'Host subject identity was not projected')
      resolves++
      return owned ? typertOwnedValue(owner, () => {
        releases++
        if (releases === 1) firstReleased()
        if (releases === 2) secondReleased()
      }) : owner
    }}
  }})
  let settle!: () => void
  let entered!: () => void
  let cancelled!: () => void
  const holding = new Promise<void>(resolve => {settle=resolve})
  const entry = new Promise<void>(resolve => {entered=resolve})
  const cancellation = new Promise<void>(resolve => {cancelled=resolve})
  events.on(owner, 'fixture/decision', async (request, next) => {
    assert(request.agent === owner, 'Reverse Context did not replace the Host subject')
    if (request.mode === 'next') return next()
    if (request.mode === 'undefined') return undefined
    if (request.mode === 'hold') {
      request.signal.addEventListener('abort', () => cancelled(), {once:true})
      entered()
      await holding
      assert(releaseCount() === 2, 'Cancelled handler lost its owned Context before settlement')
    }
    return null
  })
  const signal = new AbortController()
  let ready!: () => void
  const opening = new Promise<void>(resolve => {ready=resolve})
  const pump = events.run(signal.signal, () => ready())
  try {
    await opening
    const ask = (mode: string, signal?: AbortSignal) => fetch(origin + '/control/decision?mode=' + mode, {method:'POST',signal})
    const next = object(await (await ask('next')).json())
    assert(next.hasValue === true && next.value === 'fallback' && releaseCount() === 0, 'Borrowed continuation changed or was released')
    owned = true
    const absent = object(await (await ask('undefined')).json())
    assert(absent.hasValue === false, 'Undefined was converted to null or continuation')
    await firstRelease
    await fetch(origin + '/control/hold-event-reply', {method:'POST'})
    const answered = ask('null')
    await fetch(origin + '/control/wait-event-reply', {method:'POST'})
    assert(releaseCount() === 1, 'Owned Context was released before its reply settled')
    await fetch(origin + '/control/release-event-reply', {method:'POST'})
    const nil = object(await (await answered).json())
    assert(nil.hasValue === true && nil.value === null, 'Explicit null was lost')
    await secondRelease
    const abort = new AbortController()
    const pending = ask('hold', abort.signal).catch((error: unknown) => error)
    await entry
    abort.abort()
    await cancellation
    const closing = events.dispose()
    let closed = false
    void closing.then(() => {closed=true})
    await Promise.resolve()
    assert(!closed && releaseCount() === 2, 'Cancellation released active owned work')
    settle()
    await closing
    await pending
    assert(releaseCount() === 3 && resolves === 4 && adapterReads === 4, 'Resolution/release was cached, skipped or duplicated')
    owner.on('borrowed-owner-alive', () => {})
  } finally {
    settle(); signal.abort()
    await events.dispose(); await pump; await owner.fiber.dispose()
  }
}
