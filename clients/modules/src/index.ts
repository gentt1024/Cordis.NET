import * as cordis from '@deepseek-ai/cordis'
import * as cosmokit from '@deepseek-ai/cosmokit'
import Loader from '@deepseek-ai/cordis-plugin-loader'
import { ClientModuleSystem } from '@cordis-net/modules/system'
import { parseBootManifest, stripClientSuffix } from '@cordis-net/modules/manifest'
import type { ClientModuleCreateOptions, ClientModuleLoaderTarget, WebBootGraph } from '@cordis-net/modules/manifest'
import type { ClientEntryState } from '@cordis-net/modules/entries'
import * as management from './management.ts'
import * as slots from '@cordis-net/modules/slots'

export { Context, Service } from '@deepseek-ai/cordis'
export { SlotCore } from '@cordis-net/modules/slots'
export type { Plugin } from '@deepseek-ai/cordis'
export type { ClientEntryState } from '@cordis-net/modules/entries'
export type { WebBootGraph } from '@cordis-net/modules/manifest'
export * from './management.ts'

declare global {
  var __ModuleLoader__: ClientModuleLoaderTarget | undefined
}

/** One page's Cordis-owned plugins. Connection owners withdraw contributions on disconnect. */
export interface ClientModules {
  /** Existing Cordis Context, also used for application-owned services and effects. */
  readonly context: cordis.Context
  /** Stable synchronization diagnostics. Failed activation never changes Host enablement. */
  readonly state: {
    getSnapshot(): ClientEntryState
    subscribe(listener: () => void): () => void
  }
  /** Apply a full roster. Retain active fibers on failed arrival; refresh consumers after successful replacement. */
  sync(graph: unknown): Promise<void>
  /** Retry the latest failed roster, including unchanged artifacts. */
  retry(): Promise<void>
  /** Withdraw the roster and all its effects while leaving the page ready to reconnect. */
  disconnect(): Promise<void>
  /** Release the Context and registration facade. No further operations are accepted. */
  dispose(): Promise<void>
}

/** Inputs for a page consumer; the Host graph selects immutable factory scripts. */
export interface ClientModulesOptions {
  readonly graph: unknown
  /** Exact module requests supplied by the page shell. Values retain their identity and shell ownership.
   * SDK modules are available by default; supplied keys take precedence. A root key does not supply its subpaths.
   */
  readonly staticModules?: ClientModuleCreateOptions['staticModules']
  /** Fetch and register a classic factory script before resolving. Uses the upstream script transport when omitted. */
  readonly loadBundle?: ClientModuleCreateOptions['loadBundle']
  /** Install application-owned services through the same Context before entries activate. */
  readonly configure?: (context: cordis.Context) => void | Promise<void>
}

/** Validate the fixed graph boundary and restrict executable script URLs to the page's origin. */
function graphOf(wire: unknown): WebBootGraph {
  const manifest = parseBootManifest(wire)
  const entries = manifest.modules.map(row => ({
    id: row.id, url: row.url, rev: row.rev, inject: row.inject, external: row.external,
    immediately: manifest.plugins.find(plugin => plugin.id === row.id)?.immediately ?? false,
  }))
  for (const row of entries) {
    const url = new URL(row.url, location.href)
    if (url.origin !== location.origin || !['http:', 'https:'].includes(url.protocol)
      || url.searchParams.get('rev') !== row.rev) {
      throw new Error(`client-modules: ${row.id} requires a same-origin revisioned script`)
    }
  }
  // One-resource initial batches keep the consumer independent of Host batching choices.
  return { rev: manifest.rev, entries, batches: entries.map(row => ({
    phase: 'application', url: row.url, rev: row.rev, entries: [row.id],
  })) }
}

function changedClosure(previous: WebBootGraph, next: WebBootGraph): Set<string> {
  const wanted = new Map(next.entries.map(row => [row.id, row]))
  const affected = new Set(previous.entries.filter(row => {
    const replacement = wanted.get(row.id)
    return replacement === undefined || JSON.stringify([row.rev, row.inject, row.external])
      !== JSON.stringify([replacement.rev, replacement.inject, replacement.external])
  }).map(row => row.id))
  let changed = true
  while (changed) {
    changed = false
    for (const row of previous.entries) {
      if (affected.has(row.id)) continue
      if ([...row.inject ?? [], ...row.external ?? []].some(request => affected.has(stripClientSuffix(request)))) {
        affected.add(row.id)
        changed = true
      }
    }
  }
  return affected
}

function without(graph: WebBootGraph, ids: ReadonlySet<string>): WebBootGraph {
  const entries = graph.entries.filter(row => !ids.has(row.id))
  return { rev: graph.rev, entries, batches: entries.map(row => ({
    phase: 'application', url: row.url, rev: row.rev, entries: [row.id],
  })) }
}

/**
 * Boot the fixed DSH module system over its existing Cordis Loader.
 * Factory bundles register through globalThis.__ModuleLoader__.load. Their apply functions own effects
 * through Context. This adapter remounts reverse dependency consumers after successful code arrival,
 * so a consumer cannot retain exports from a superseded provider. Static ES modules and local chunks
 * are separate formats and are not accepted by the supported client artifact build.
 */
export async function bootClientModules(options: ClientModulesOptions): Promise<ClientModules> {
  if (globalThis.__ModuleLoader__ !== undefined) throw new Error('client-modules: this page already owns a module facade')
  let graph = graphOf(options.graph)
  const facade: ClientModuleLoaderTarget = {
    mode: 'queue', pendingQueue: [],
    load(registration) { this.pendingQueue.push(registration) },
    create() { throw new Error('client-modules: the package entry owns bootstrap') },
  }
  const modules = new ClientModuleSystem({
    manifest: parseBootManifest(graph), registrationTarget: facade,
    bootstrapModule: { id: '@cordis-net/client-modules', exports: {
      ...management, SlotCore: slots.SlotCore, Context: cordis.Context, Service: cordis.Service, bootClientModules,
    } },
    staticModules: {
      '@deepseek-ai/cordis': cordis, '@deepseek-ai/cosmokit': cosmokit, '@cordis-net/client-modules/slots': slots,
      ...options.staticModules,
    },
    ...(options.loadBundle === undefined ? {} : { loadBundle: options.loadBundle }),
  })
  const context = new cordis.Context()
  // Publish only after shell inputs and local construction succeed. A rejected bootstrap
  // must leave the page available for a retry; subsequent setup failures use the cleanup below.
  globalThis.__ModuleLoader__ = facade
  try {
    await options.configure?.(context)
    await context.plugin(Loader)
    // Fixed Loader's internal seam is declared as Node-only; the upstream browser boot fills this same slot.
    Object.assign(context.loader, { internal: modules })
    await modules.entries.start(context.loader, parseBootManifest(graph))
    await modules.entries.sync(graph)
  } catch (error) {
    await context.fiber.dispose()
    if (globalThis.__ModuleLoader__ === facade) delete globalThis.__ModuleLoader__
    throw error
  }
  let stopped = false
  let update = 0
  let queue = Promise.resolve()
  const enqueue = (task: () => Promise<void>): Promise<void> => {
    if (stopped) return Promise.reject(new Error('client-modules: this consumer is disposed'))
    // ClientEntries must see a newer desired graph immediately, even while a script is arriving.
    const operation = task()
    queue = Promise.all([queue, operation.catch(() => {})]).then(() => {})
    return operation
  }
  const pendingRemount = new Set<string>()
  const synchronize = async (next: WebBootGraph): Promise<void> => {
    const token = ++update
    const changed = changedClosure(graph, next)
    for (const id of changed) {
      const before = graph.entries.find(row => row.id === id)
      const after = next.entries.find(row => row.id === id)
      if (before !== undefined && after !== undefined && before.rev === after.rev) pendingRemount.add(id)
    }
    graph = next
    await modules.entries.sync(graph)
    if (token !== update) return
    if (modules.entries.state.getSnapshot().failures.length > 0) return
    const remount = new Set([...pendingRemount].filter(id => graph.entries.some(row => row.id === id)))
    if (remount.size > 0) {
      await modules.entries.sync(without(graph, remount))
      if (token !== update) return
      for (const id of remount) modules.invalidate(id)
      await modules.entries.sync(graph)
    }
    if (modules.entries.state.getSnapshot().failures.length === 0) pendingRemount.clear()
  }
  return {
    context, state: modules.entries.state,
    sync(wire) {
      const next = graphOf(wire)
      return enqueue(() => synchronize(next))
    },
    retry() { return enqueue(() => synchronize(graph)) },
    disconnect() { return enqueue(() => synchronize({ rev: 'disconnected', entries: [], batches: [] })) },
    async dispose() {
      if (stopped) return queue
      stopped = true
      update++
      const withdraw = modules.entries.sync({ rev: 'disposed', entries: [], batches: [] })
      // Drain accepted updates before root disposal. The root is the lifecycle owner, not this adapter.
      queue = Promise.all([queue, withdraw]).then(async () => {
        await context.fiber.dispose()
        for (const row of graph.entries) {
          modules.invalidate(row.id)
          for (const style of document.querySelectorAll('style[data-plugin]')) {
            if (style.getAttribute('data-plugin') === row.id) style.remove()
          }
        }
        if (globalThis.__ModuleLoader__ === facade) delete globalThis.__ModuleLoader__
      })
      return queue
    },
  }
}
