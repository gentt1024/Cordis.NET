import { createRemote as createManagementRemote, type RemoteResult } from './management.mjs'
import { createRemote as createNotificationRemote } from './notifications.mjs'

type NotificationClient = ReturnType<typeof createNotificationRemote>
type NotificationResult = ReturnType<NotificationClient['remoteNotifications/watch']> extends AsyncIterable<infer Item> ? Item : never
type Notification = Extract<NotificationResult, { readonly ok: true }>['value']
function assert(value: unknown, reason: string): asserts value { if (!value) throw new Error(reason) }
function accepted<T>(result: RemoteResult<T>): T { if (!result.ok) throw result.error; return result.value }
async function failure(call: Promise<RemoteResult<unknown>>, code: string) {
  const result = await call
  assert(!result.ok && result.error.code === code, 'Expected Remote failure: ' + code)
}
async function next(reader: AsyncIterator<NotificationResult>): Promise<Notification> {
  const result = await reader.next()
  assert(!result.done, 'Notification subscription ended without its expected item')
  return accepted(result.value)
}
async function event(reader: AsyncIterator<NotificationResult>, match: (frame: Notification) => boolean) {
  while (true) { const frame = await next(reader); if (match(frame)) return frame }
}

/** Strict independent TypeScript author consuming generated declarations and the actual native Host. */
export async function run(origin: string) {
  const fetcher: typeof fetch = (input, init) => {
    const headers = new Headers(init?.headers)
    headers.set('Authorization', 'consumer-policy')
    return fetch(input, { ...init, headers })
  }
  const manager = createManagementRemote(origin + '/remote', fetcher)
  const notifications = createNotificationRemote(origin + '/remote', fetcher)
  const observation = new AbortController()
  const reader = notifications['remoteNotifications/watch']({}, observation.signal)[Symbol.asyncIterator]()
  const remotes = [manager, notifications]
  try {
    assert((await next(reader)).kind === 'ready', 'Ready must precede the inventory query')
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
    observation.abort()
    await reader.return?.()
    response.abort()
    await failure(installing, 'gateway/cancelled')
    assert((await probe()).cancellations === 0, 'Request disconnect cancelled platform preparation')
    const resumedRemote = createNotificationRemote(origin + '/remote', fetcher)
    remotes.push(resumedRemote)
    const resumed = resumedRemote['remoteNotifications/watch']({})[Symbol.asyncIterator]()
    assert((await next(resumed)).kind === 'ready', 'Reconnect did not subscribe again')
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
    let terminal: NotificationResult | undefined
    for await (const item of { [Symbol.asyncIterator]: () => resumed }) {
      if (!item.ok) { terminal = item; break }
    }
    assert(terminal !== undefined && !terminal.ok && terminal.error.code === 'gateway/service-unavailable', 'Source removal did not terminate the typed observer')
    await control('restore-source')
    const restoredRemote = createNotificationRemote(origin + '/remote', fetcher)
    remotes.push(restoredRemote)
    const restored = restoredRemote['remoteNotifications/watch']({})[Symbol.asyncIterator]()
    assert((await next(restored)).kind === 'ready', 'Restored source did not admit a new observer')
    assert(accepted(await manager['pluginManager/listPlugins']({})).find(row => row.entryId === 'root:worker')?.enabled, 'Notification failure changed plugin outcome')
    await restored.return?.()
  } finally {
    observation.abort()
    for (const remote of remotes) remote.dispose()
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
