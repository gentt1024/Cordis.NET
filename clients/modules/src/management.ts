import { ConnectionController, type ConnectionRecoveryConfig, type ConnectionGenerationSource } from '@cordis-net/connection'
import type { ClientModules } from './index.ts'

export interface ManagementState {
  readonly generation: string
  readonly restartRequired: boolean
  readonly selectedBundles: readonly string[]
  readonly loadedBundles: readonly string[]
}
export type ManagementConnection =
  | { readonly phase: 'connecting' | 'disconnected'; readonly diagnostic?: string }
  | { readonly phase: 'connected'; readonly state: ManagementState }
  | { readonly phase: 'closed' }
export type SettingsOperation =
  | { readonly op: 'set'; readonly path: readonly string[]; readonly value: unknown }
  | { readonly op: 'unset'; readonly path: readonly string[] }
export interface PackageRequest { readonly name: string; readonly version: string; readonly source: string }
export interface PackageInspection { readonly hash: string; readonly description: string; readonly requiresBuildApproval: boolean }
export interface PackageChange {
  readonly requestId: string; readonly target: string; readonly stage: string
  readonly installed: boolean; readonly selected: boolean; readonly application: string
  readonly error: string | null; readonly diagnostic: string | null
  readonly residuals: readonly string[] | null; readonly toolExitCode: number | null
}
export interface SettingsView {
  readonly entryId: string; readonly revision: string; readonly diagnostics: readonly string[]
  readonly fields: readonly { readonly name: string; readonly kind: string; readonly value: unknown; readonly overridden: boolean }[]
  readonly secrets: readonly { readonly path: readonly string[]; readonly set: boolean }[]
}
export interface SettingsChange {
  readonly saved: boolean; readonly applied: boolean; readonly revision: string | null
  readonly error: string | null; readonly diagnostic: string | null; readonly recoveryErrors: readonly string[] | null
}
export interface ConfigurationView { readonly entryId: string; readonly revision: string; readonly raw: unknown }
export interface CompatibilityView {
  readonly rewritable: boolean; readonly warnings: readonly string[]
  readonly exemptions: Readonly<Record<string, readonly string[]>>
}
export interface ManagementChange { readonly changed: boolean; readonly application: string; readonly error: string | null; readonly diagnostic: string | null }
export interface PluginView {
  readonly entryId: string; readonly module: string; readonly enabled: boolean
  readonly readOnlyReason: string | null; readonly state: string | null
}
export interface ManagementEvent {
  readonly generation: string; readonly sequence: number
  readonly kind: 'connected' | 'configuration' | 'refresh' | 'package' | 'client-modules'
  readonly value: unknown
}

/** A transport refusal has a known response; it is distinct from an unobserved mutation outcome. */
export class ManagementHttpError extends Error {
  constructor(readonly status: number, readonly detail: unknown) { super(`Management request refused: HTTP ${status}`) }
}
/** Never retry this mutation automatically. Installation callers may query waitForInstall(requestId). */
export class ManagementOutcomeUnknown extends Error {
  constructor(readonly operation: string, readonly cause: unknown) { super(`Management response unknown: ${operation}`) }
}

function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error('Management response must be an object')
  return Object.fromEntries(Object.entries(value))
}
function text(value: unknown): string { if (typeof value !== 'string') throw new Error('Management string expected'); return value }
function boolean(value: unknown): boolean { if (typeof value !== 'boolean') throw new Error('Management boolean expected'); return value }
function texts(value: unknown): string[] { if (!Array.isArray(value)) throw new Error('Management string array expected'); return value.map(text) }
function nullableText(value: unknown): string | null { return value === null ? null : text(value) }
function nullableTexts(value: unknown): string[] | null { return value === null ? null : texts(value) }
function packageChange(wire: unknown): PackageChange {
  const row = record(wire)
  if (row.toolExitCode !== null && (typeof row.toolExitCode !== 'number' || !Number.isInteger(row.toolExitCode))) throw new Error('Package exit status expected')
  return { requestId: text(row.requestId), target: text(row.target), stage: text(row.stage), installed: boolean(row.installed),
    selected: boolean(row.selected), application: text(row.application), error: nullableText(row.error), diagnostic: nullableText(row.diagnostic),
    residuals: nullableTexts(row.residuals), toolExitCode: row.toolExitCode }
}
function managementChange(wire: unknown): ManagementChange {
  const row = record(wire)
  return { changed: boolean(row.changed), application: text(row.application), error: nullableText(row.error), diagnostic: nullableText(row.diagnostic) }
}
function settingsChange(wire: unknown): SettingsChange {
  const row = record(wire)
  return { saved: boolean(row.saved), applied: boolean(row.applied), revision: nullableText(row.revision), error: nullableText(row.error),
    diagnostic: nullableText(row.diagnostic), recoveryErrors: nullableTexts(row.recoveryErrors) }
}
function eventOf(wire: unknown): ManagementEvent {
  const row = record(wire)
  if (typeof row.sequence !== 'number' || !Number.isSafeInteger(row.sequence) || row.sequence < 1) throw new Error('Management event sequence expected')
  if (row.kind !== 'connected' && row.kind !== 'configuration' && row.kind !== 'refresh' && row.kind !== 'package' && row.kind !== 'client-modules') throw new Error('Unknown management event')
  return { generation: text(row.generation), sequence: row.sequence, kind: row.kind, value: row.value }
}

/** Explicit HTTP calls share the native management owner. The client stores connection facts, not a second plugin/service table. */
export interface ManagementClient {
  readonly connection: { getSnapshot(): ManagementConnection; subscribe(listener: () => void): () => void }
  /** Subscribe to progress/invalidation. A sequence gap triggers a full refresh instead of treating missed events as applied. */
  subscribe(listener: (event: ManagementEvent) => void): () => void
  /** Cancel the current read/stream generation and immediately open a fresh one. Never replays mutations. */
  reconnect(): void
  readState(): Promise<ManagementState>
  readPlugins(): Promise<readonly PluginView[]>
  readBundles(): Promise<unknown>
  readSources(): Promise<readonly string[]>
  readVersions(name: string, source: string): Promise<readonly string[]>
  readSettings(entryId: string): Promise<SettingsView>
  readSettingsSchema(entryId: string): Promise<unknown>
  readConfiguration(entryId: string): Promise<ConfigurationView>
  readConfigurationSchema(entryId: string): Promise<unknown>
  mutateConfiguration(entryId: string, operations: readonly SettingsOperation[], revision: string): Promise<SettingsChange>
  readCompatibility(): Promise<CompatibilityView>
  mutateSettings(entryId: string, operations: readonly SettingsOperation[], revision: string): Promise<SettingsChange>
  enable(kind: 'plugin' | 'bundle', target: string, enabled: boolean): Promise<ManagementChange>
  inspect(request: PackageRequest): Promise<PackageInspection>
  /** One install attempt. A lost response queries the active result; null remains unknown and is never success. */
  install(request: PackageRequest, options: { inspectedHash: string; requestId: string; enabled?: boolean }): Promise<PackageChange>
  waitForInstall(requestId: string): Promise<PackageChange | null>
  cancelInstall(requestId: string): Promise<string>
  remove(name: string): Promise<PackageChange>
  compatibility(packageVersion: string, runtimeVersion: string, enabled: boolean, acceptRisk: boolean): Promise<ManagementChange>
  /** Close recovery and withdraw ClientModules. The caller still owns the ClientModules root. */
  close(): Promise<void>
}

/** Physical HTTP/SSE carriers supplied by a shell. They do not own plugin lifetimes or recovery. */
export interface ManagementTransport {
  /** Native management HTTP requests. Honor read cancellation; writes have no automatic retry. */
  fetch?: (url: string, init: RequestInit) => Promise<Response>
  /** Attach SSE data/error delivery. Release listeners/resources on abort and when the returned disposer runs.
   * A replacement starts only after this attempt ends. Late callbacks are ignored by the client.
   */
  openEvents?: (url: string, callbacks: { message(data: string): void; error(error: unknown): void }, signal: AbortSignal) => () => void
}
export interface ManagementClientOptions {
  readonly baseUrl?: string
  readonly modules?: ClientModules
  readonly transport?: ManagementTransport
  readonly recovery?: ConnectionRecoveryConfig
}
export type { ConnectionRecoveryConfig } from '@cordis-net/connection'

function openBrowserEvents(url: string, callbacks: { message(data: string): void; error(error: unknown): void }, signal: AbortSignal): () => void {
  const source = new EventSource(url, { withCredentials: true })
  source.onmessage = message => { callbacks.message(message.data) }
  source.onerror = () => { callbacks.error(new Error('Event connection lost')) }
  const close = (): void => { source.close() }
  signal.addEventListener('abort', close, { once: true })
  return () => {
    signal.removeEventListener('abort', close)
    source.close()
  }
}

/**
 * Attach to native CordisManagement HTTP/SSE. Every mutation carries the current Host generation.
 * Connection loss invalidates remote readiness while preserving page plugins. Reconnection reads full state and graph before becoming ready;
 * it never replays a mutation. Authentication is the host's same-origin policy, not a product account flow.
 */
export function createManagementClient(options: ManagementClientOptions = {}): ManagementClient {
  const base = new URL(options.baseUrl ?? '/cordis', location.href)
  if (base.origin !== location.origin || base.search || base.hash) throw new Error('Management requires a same-origin base URL')
  base.pathname = base.pathname.replace(/\/$/, '')
  let snapshot: ManagementConnection = { phase: 'connecting' }
  const listeners = new Set<() => void>()
  const events = new Set<(event: ManagementEvent) => void>()
  let closed = false
  let closing: Promise<void> | undefined
  let epoch = 0
  let eventGeneration: string | undefined
  let refresh = Promise.resolve()
  let readController = new AbortController()
  const modules = options.modules
  const request = options.transport?.fetch ?? fetch
  const openEvents = options.transport?.openEvents ?? openBrowserEvents
  const publish = (next: ManagementConnection): void => {
    snapshot = next
    for (const listener of [...listeners]) { try { listener() } catch (error) { console.error('management: state subscriber failed', error) } }
  }
  const urlOf = (path: string): string => base.href + path
  const read = async (path: string): Promise<unknown> => {
    if (closed) throw new Error('Management is closed')
    const headers: Record<string, string> = eventGeneration === undefined ? {} : { 'If-Cordis-Generation': eventGeneration }
    const response = await request(urlOf(path), { signal: readController.signal, credentials: 'same-origin', cache: 'no-store', headers })
    if (!response.ok) throw new ManagementHttpError(response.status, await response.text())
    return response.json()
  }
  const readState = async (): Promise<ManagementState> => {
    const row = record(await read('/state'))
    if (row.protocol !== 1) throw new Error('Unsupported management protocol')
    return { generation: text(row.generation), restartRequired: boolean(row.restartRequired),
      selectedBundles: texts(row.selectedBundles), loadedBundles: texts(row.loadedBundles) }
  }
  const mutation = async (path: string, body: object): Promise<unknown> => {
    if (closed || snapshot.phase !== 'connected') throw new Error('Management is not connected; refresh before mutating')
    let response: Response
    try {
      response = await request(urlOf(path), { method: 'POST', credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json', 'If-Cordis-Generation': snapshot.state.generation }, body: JSON.stringify(body) })
    } catch (error) { throw new ManagementOutcomeUnknown(path, error) }
    if (!response.ok) throw new ManagementHttpError(response.status, await response.text())
    try { return await response.json() } catch (error) { throw new ManagementOutcomeUnknown(path, error) }
  }
  const mutate = async <T>(path: string, body: object, decode: (wire: unknown) => T): Promise<T> => {
    const wire = await mutation(path, body)
    try { return decode(wire) } catch (error) { throw new ManagementOutcomeUnknown(path, error) }
  }
  // The fixed controller owns retries/deadlines; this source owns one HTTP/SSE generation.
  // Losing that generation invalidates remote writes without withdrawing page-owned plugins.
  const connect: ConnectionGenerationSource = async (signal, ready) => {
    if (signal.aborted || closed) return
    readController = new AbortController()
    eventGeneration = undefined
    let sequence = 0
    let release: (() => void) | undefined
    let ended = false
    let settle!: () => void
    const lifetime = new Promise<void>(resolve => { settle = resolve })
    const finish = (error?: unknown): void => {
      if (ended) return
      ended = true
      epoch++
      readController.abort()
      try { release?.() } catch (cleanupError) {
        console.error('management: carrier cleanup failed', cleanupError)
        error ??= cleanupError
      }
      signal.removeEventListener('abort', abort)
      if (!closed && !signal.aborted) {
        publish({ phase: 'disconnected', diagnostic: error instanceof Error ? error.message : String(error ?? 'Event connection ended') })
      }
      settle()
    }
    const abort = (): void => { finish() }
    signal.addEventListener('abort', abort, { once: true })
    const synchronize = (generation: string, reconnect: boolean): void => {
      const token = ++epoch
      if (reconnect) publish({ phase: 'connecting' })
      refresh = refresh.catch(() => {}).then(async () => {
        if (ended || closed || token !== epoch) return
        const state = await readState()
        if (ended || closed || token !== epoch) return
        if (state.generation !== generation) throw new Error('Host generation changed while reading state')
        if (modules !== undefined) {
          const graph = await read('/client/graph')
          if (ended || closed || token !== epoch) return
          await modules.sync(graph)
        }
        if (ended || closed || token !== epoch) return
        ready({ home: '' }) // Native management has no account-home fact; it is not used by recovery.
        publish({ phase: 'connected', state })
      }).catch(error => {
        if (!ended && !closed && token === epoch) finish(error)
      })
    }
    try {
      release = openEvents(urlOf('/events'), {
        error(error) { if (!ended) finish(error) },
        message(data) {
          if (ended || closed) return
          try {
            const event = eventOf(JSON.parse(data))
            const reconnect = event.kind === 'connected' || eventGeneration !== event.generation || event.sequence !== sequence + 1
            eventGeneration = event.generation
            sequence = event.sequence
            if (reconnect) synchronize(event.generation, true)
            else if (event.kind === 'configuration' || event.kind === 'refresh' || event.kind === 'client-modules') synchronize(event.generation, false)
            for (const listener of [...events]) {
              try { listener(event) } catch (error) { console.error('management: event subscriber failed', error) }
            }
          } catch (error) { finish(error) }
        },
      }, signal)
      // Injected carriers may synchronously report failure before returning their disposer.
      if (ended) {
        try { release() } catch (error) { console.error('management: carrier cleanup failed', error) }
      }
      await lifetime
    } catch (error) { finish(error) }
  }
  const controller = new ConnectionController(connect, {
    onStateChange(phase) {
      if (!closed && phase !== 'connected') publish({ phase })
    },
  }, options.recovery)
  // Match the upstream browser owner: offline suspends automatic attempts and
  // returning online resets backoff. Custom non-browser carriers need no watcher.
  const browser = typeof window === 'undefined' ? undefined : window
  const online = (): void => { controller.setNetworkAvailable(true) }
  const offline = (): void => { controller.setNetworkAvailable(false) }
  if (browser?.navigator.onLine !== undefined) controller.setNetworkAvailable(browser.navigator.onLine)
  browser?.addEventListener('online', online)
  browser?.addEventListener('offline', offline)
  controller.start()

  return {
    connection: { getSnapshot: () => snapshot, subscribe(listener) { listeners.add(listener); return () => { listeners.delete(listener) } } },
    subscribe(listener) { events.add(listener); return () => { events.delete(listener) } },
    reconnect() { if (!closed) controller.reconnect() },
    readState,
    async readPlugins() {
      const rows = await read('/plugins')
      if (!Array.isArray(rows)) throw new Error('Plugin inventory array expected')
      return rows.map(wire => {
        const row = record(wire)
        return { entryId: text(row.entryId), module: text(row.module), enabled: boolean(row.enabled),
          readOnlyReason: nullableText(row.readOnlyReason), state: nullableText(row.state) }
      })
    },
    readBundles: () => read('/bundles'), readSources: async () => texts(await read('/sources')),
    readVersions: async (name, sourceName) => texts(await read('/versions?' + new URLSearchParams({ name, source: sourceName }))),
    async readSettings(entryId) {
      const row = record(await read('/settings?' + new URLSearchParams({ entryId })))
      if (!Array.isArray(row.fields)) throw new Error('Settings field array expected')
      if (!Array.isArray(row.secrets)) throw new Error('Settings secret sidecar expected')
      return { entryId: text(row.entryId), revision: text(row.revision), diagnostics: texts(row.diagnostics), fields: row.fields.map(value => {
        const field = record(value)
        return { name: text(field.name), kind: text(field.kind), value: field.value, overridden: boolean(field.overridden) }
      }), secrets: row.secrets.map(value => {
        const secret = record(value)
        return { path: texts(secret.path), set: boolean(secret.set) }
      }) }
    },
    readSettingsSchema: entryId => read('/settings/schema?' + new URLSearchParams({ entryId })),
    async readConfiguration(entryId) {
      const row = record(await read('/configuration?' + new URLSearchParams({ entryId })))
      return { entryId: text(row.entryId), revision: text(row.revision), raw: row.raw }
    },
    readConfigurationSchema: entryId => read('/configuration/schema?' + new URLSearchParams({ entryId })),
    mutateConfiguration: (entryId, operations, revision) => mutate('/configuration', { entryId, operations, revision }, settingsChange),
    async readCompatibility() {
      const row = record(await read('/compatibility'))
      return { rewritable: boolean(row.rewritable), warnings: texts(row.warnings),
        exemptions: Object.fromEntries(Object.entries(record(row.exemptions)).map(([name, values]) => [name, texts(values)])) }
    },
    async mutateSettings(entryId, operations, revision) {
      return mutate('/settings', { entryId, operations, revision }, settingsChange)
    },
    enable: (kind, target, enabled) => mutate('/enable', { kind, target, enabled }, managementChange),
    async inspect(request) {
      return mutate('/inspect', request, wire => {
        const row = record(wire)
        return { hash: text(row.hash), description: text(row.description), requiresBuildApproval: boolean(row.requiresBuildApproval) }
      })
    },
    async install(request, installOptions) {
      const generation = snapshot.phase === 'connected' ? snapshot.state.generation : undefined
      try { return await mutate('/install', { ...request, ...installOptions }, packageChange) }
      catch (error) {
        if (!(error instanceof ManagementOutcomeUnknown)) throw error
        try {
          if ((await readState()).generation !== generation) throw error
          const result = await read('/install/wait?' + new URLSearchParams({ requestId: installOptions.requestId }))
          if ((await readState()).generation === generation && result !== null) return packageChange(result)
        } catch { /* The mutation still has an unknown outcome; preserve its original error. */ }
        throw error
      }
    },
    waitForInstall: async requestId => {
      const result = await read('/install/wait?' + new URLSearchParams({ requestId }))
      return result === null ? null : packageChange(result)
    },
    cancelInstall: requestId => mutate('/install/cancel', { requestId }, wire => text(record(wire).status)),
    remove: name => mutate('/remove', { name }, packageChange),
    compatibility: (packageVersion, runtimeVersion, enabled, acceptRisk) => mutate('/compatibility',
      { packageVersion, runtimeVersion, enabled, acceptRisk }, managementChange),
    close() {
      if (closing !== undefined) return closing
      closed = true
      epoch++
      controller.stop()
      browser?.removeEventListener('online', online)
      browser?.removeEventListener('offline', offline)
      readController.abort()
      closing = Promise.resolve().then(async () => {
        try {
          await Promise.all([refresh.catch(() => {}), modules?.disconnect()])
        } finally {
          listeners.clear()
          events.clear()
        }
      })
      publish({ phase: 'closed' })
      return closing
    },
  }
}
