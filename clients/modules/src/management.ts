import { ConnectionController, type ConnectionRecoveryConfig, type ConnectionGenerationSource } from '@cordis-net/connection'
import type { RemoteClient as NotificationClient } from '../.types/standard/notifications.js'
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
export type ConfigurationOperation =
  | { readonly op: 'set'; readonly path: readonly string[]; readonly value: unknown }
  | { readonly op: 'unset'; readonly path: readonly string[] }
export interface ConfigurationChange {
  readonly saved: boolean; readonly applied: boolean; readonly revision: string | null
  readonly error: string | null; readonly diagnostic: string | null; readonly recoveryErrors: readonly string[] | null
}
export interface ConfigurationView { readonly entryId: string; readonly revision: string; readonly raw: unknown }
type NotificationResult = ReturnType<NotificationClient['remoteNotifications/watch']> extends AsyncIterable<infer Item> ? Item : never
export type ManagementEvent = NotificationResult

/** A transport refusal has a known response; it is distinct from an unobserved mutation outcome. */
export class ManagementHttpError extends Error {
  constructor(readonly status: number, readonly detail: unknown) { super(`Management request refused: HTTP ${status}`) }
}
/** A raw configuration mutation with an unobserved response must not be replayed automatically. */
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
function configurationChange(wire: unknown): ConfigurationChange {
  const row = record(wire)
  return { saved: boolean(row.saved), applied: boolean(row.applied), revision: nullableText(row.revision), error: nullableText(row.error),
    diagnostic: nullableText(row.diagnostic), recoveryErrors: nullableTexts(row.recoveryErrors) }
}

/** Host state, catalog synchronization and raw configuration use their independent HTTP contract. */
export interface ManagementClient {
  readonly connection: { getSnapshot(): ManagementConnection; subscribe(listener: () => void): () => void }
  /** Original selected notifications. Ready is followed by a full state and catalog query. */
  subscribe(listener: (event: ManagementEvent) => void): () => void
  /** Cancel observation and open a fresh subscription. Never replays mutations. */
  reconnect(): void
  readState(): Promise<ManagementState>
  readConfiguration(entryId: string): Promise<ConfigurationView>
  readConfigurationSchema(entryId: string): Promise<unknown>
  mutateConfiguration(entryId: string, operations: readonly ConfigurationOperation[], revision: string): Promise<ConfigurationChange>
  /** Close recovery and withdraw ClientModules. The caller owns the modules root and notification client. */
  close(): Promise<void>
}

/** A shell may supply the host HTTP carrier. Reads cooperate with cancellation; writes are never retried. */
export interface ManagementTransport {
  fetch?: (url: string, init: RequestInit) => Promise<Response>
}
export interface ManagementClientOptions {
  /** The actual generated RemoteNotifications client, owned by the shell. */
  readonly notifications: NotificationClient
  readonly baseUrl?: string
  readonly modules?: ClientModules
  readonly transport?: ManagementTransport
  readonly recovery?: ConnectionRecoveryConfig
}
export type { ConnectionRecoveryConfig } from '@cordis-net/connection'

/**
 * Recover one generated notification subscription over the fixed connection controller.
 * A ready item precedes querying current Host state and graph. Transient loss retains page plugins;
 * reconnect queries again because notifications have no replay guarantee. Raw configuration writes
 * retain the Host generation fence. Plugin management is consumed directly through its generated client.
 */
export function createManagementClient(options: ManagementClientOptions): ManagementClient {
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
  const connect: ConnectionGenerationSource = async (signal, ready) => {
    if (signal.aborted || closed) return
    readController = new AbortController()
    eventGeneration = undefined
    let subscribed = false
    let ended = false
    const abort = (): void => { epoch++; readController.abort() }
    signal.addEventListener('abort', abort, { once: true })
    const synchronize = async (reconnect: boolean): Promise<void> => {
      const token = ++epoch
      if (reconnect) publish({ phase: 'connecting' })
      refresh = refresh.catch(() => {}).then(async () => {
        if (ended || closed || signal.aborted || token !== epoch) return
        const state = await readState()
        if (ended || closed || signal.aborted || token !== epoch) return
        eventGeneration = state.generation
        if (modules !== undefined) {
          const graph = await read('/client/graph')
          if (ended || closed || signal.aborted || token !== epoch) return
          if ((await readState()).generation !== state.generation) throw new Error('Host generation changed while reading the graph')
          if (ended || closed || signal.aborted || token !== epoch) return
          await modules.sync(graph)
        }
        if (ended || closed || signal.aborted || token !== epoch) return
        ready({ home: '' })
        publish({ phase: 'connected', state })
      })
      await refresh
    }
    try {
      for await (const event of options.notifications['remoteNotifications/watch']({}, signal)) {
        if (signal.aborted || closed) return
        if (event.kind === 'ready') {
          if (subscribed) throw new Error('Notification subscription sent duplicate readiness')
          subscribed = true
          await synchronize(true)
        } else if (event.kind === 'event') {
          if (!subscribed) throw new Error('Notification arrived before subscription readiness')
          if (!event.event?.startsWith('plugin-manager/install-')) await synchronize(false)
          for (const listener of [...events]) {
            try { listener(event) } catch (error) { console.error('management: event subscriber failed', error) }
          }
        } else throw new Error('Unknown notification item')
      }
      if (!signal.aborted && !closed) throw new Error('Notification subscription ended')
    } catch (error) {
      if (!signal.aborted && !closed) publish({ phase: 'disconnected', diagnostic: error instanceof Error ? error.message : String(error) })
    } finally {
      ended = true
      abort()
      signal.removeEventListener('abort', abort)
    }
  }
  const controller = new ConnectionController(connect, {
    onStateChange(phase) { if (!closed && phase !== 'connected') publish({ phase }) },
  }, options.recovery)
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
    async readConfiguration(entryId) {
      const row = record(await read('/configuration?' + new URLSearchParams({ entryId })))
      return { entryId: text(row.entryId), revision: text(row.revision), raw: row.raw }
    },
    readConfigurationSchema: entryId => read('/configuration/schema?' + new URLSearchParams({ entryId })),
    async mutateConfiguration(entryId, operations, revision) {
      const wire = await mutation('/configuration', { entryId, operations, revision })
      try { return configurationChange(wire) } catch (error) { throw new ManagementOutcomeUnknown('/configuration', error) }
    },
    close() {
      if (closing !== undefined) return closing
      closed = true
      epoch++
      controller.stop()
      browser?.removeEventListener('online', online)
      browser?.removeEventListener('offline', offline)
      readController.abort()
      closing = Promise.resolve().then(async () => {
        try { await Promise.all([refresh.catch(() => {}), modules?.disconnect()]) }
        finally { listeners.clear(); events.clear() }
      })
      publish({ phase: 'closed' })
      return closing
    },
  }
}
