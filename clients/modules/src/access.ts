import type { Context } from '@deepseek-ai/cordis'
import { ConnectionController, type ConnectionRecoveryConfig } from '@cordis-net/connection'
import { mountRemote, resultCodecs, RemoteError, RemoteStreamCarrierError } from '../.types/standard/access-remote.js'
import type { TypertConnection, RemoteOwner } from '../.types/standard/access-remote.js'

/** The generated native follow codec owns the wire contract. */
export type RemoteAccessFrame = ReturnType<typeof resultCodecs['remoteAccess/follow']['parse']>
export type Baseline = Extract<RemoteAccessFrame, { type: 'baseline' }>
export type RemoteAccessGrant = Extract<RemoteAccessFrame, { type: 'upsert' }>['grant']
export type RemoteAccessResource = RemoteAccessGrant['resource']
export interface AccessSource {
  getSnapshot(): boolean
  subscribe(listener: () => void): () => void
}
export interface ClientRemoteAccess {
  source(kind: string, id: string, action: string): AccessSource
  global(action: string): AccessSource
}

declare module '@deepseek-ai/cordis' {
  interface Context {
    /** Local answers over the calling Peer's latest access snapshot. */
    remoteAccess: ClientRemoteAccess
  }
}

type Snapshot =
  | { readonly kind: 'closed' }
  | { readonly kind: 'owner' }
  | { readonly kind: 'granted'; readonly global: ReadonlySet<string>; readonly resources: ReadonlyMap<string, ReadonlySet<string>> }
const CLOSED: Snapshot = { kind: 'closed' }
const OWNER: Snapshot = { kind: 'owner' }
const parseFrame = resultCodecs['remoteAccess/follow'].parse

/** Validate a page's optional opening baseline before publishing any answers. */
export function parseInjectedBaseline(value: unknown): Baseline | undefined {
  if (value === undefined) return undefined
  const frame = parseFrame(value)
  if (frame.type !== 'baseline') throw new TypeError('remote access: expected an opening baseline')
  return frame
}

/** One mount's snapshot, with stable observable identities across replacement and recovery. */
export class ClientAccessState implements ClientRemoteAccess {
  private snapshot: Snapshot = CLOSED
  private readonly listeners = new Set<() => void>()
  private readonly sources = new Map<string, AccessSource>()

  constructor(initial?: Baseline) {
    if (initial !== undefined) this.replace(initial)
  }

  global(action: string): AccessSource {
    return this.cached(JSON.stringify(['global', action]), snapshot => snapshot.global.has(action))
  }

  source(kind: string, id: string, action: string): AccessSource {
    const resource = JSON.stringify([kind, id])
    return this.cached(JSON.stringify(['resource', kind, id, action]), snapshot => snapshot.resources.get(resource)?.has(action) ?? false)
  }

  /** A complete replacement is validated and built before listeners can observe it. */
  replace(value: Baseline): void {
    const frame = parseInjectedBaseline(value)
    if (frame === undefined) throw new TypeError('remote access: expected an opening baseline')
    if (frame.owner) { this.publish(OWNER); return }
    let global: ReadonlySet<string> = new Set()
    const resources = new Map<string, ReadonlySet<string>>()
    for (const grant of frame.grants) {
      if (grant.resource.kind === 'global') global = new Set(grant.actions)
      else resources.set(resourceKey(grant.resource), new Set(grant.actions))
    }
    this.publish({ kind: 'granted', global, resources })
  }

  update(value: Exclude<RemoteAccessFrame, { type: 'baseline' }>): void {
    const frame = parseFrame(value)
    if (frame.type === 'baseline') throw new TypeError('remote access: a delta cannot replace the baseline')
    const current = this.snapshot
    if (current.kind !== 'granted') return
    const resource = frame.type === 'upsert' ? frame.grant.resource : frame.resource
    const actions = frame.type === 'upsert' ? frame.grant.actions : []
    if (resource.kind === 'global') {
      this.publish({ ...current, global: new Set(actions) })
      return
    }
    const resources = new Map(current.resources)
    if (frame.type === 'remove') resources.delete(resourceKey(resource))
    else resources.set(resourceKey(resource), new Set(actions))
    this.publish({ ...current, resources })
  }

  get owner(): boolean { return this.snapshot.kind === 'owner' }

  /** Terminal loss closes guest answers; owner answers survive independently of the stream. */
  unavailable(): void { if (!this.owner) this.publish(CLOSED) }

  private cached(key: string, granted: (snapshot: Extract<Snapshot, { kind: 'granted' }>) => boolean): AccessSource {
    let source = this.sources.get(key)
    if (source === undefined) {
      source = {
        getSnapshot: () => this.snapshot.kind === 'owner' || (this.snapshot.kind === 'granted' && granted(this.snapshot)),
        subscribe: listener => { this.listeners.add(listener); return () => { this.listeners.delete(listener) } },
      }
      this.sources.set(key, source)
    }
    return source
  }

  private publish(snapshot: Snapshot): void {
    this.snapshot = snapshot
    for (const listener of [...this.listeners]) listener()
  }
}

function resourceKey(resource: RemoteAccessResource): string {
  return JSON.stringify([resource.kind, 'id' in resource ? resource.id : ''])
}

export interface AccessMountOptions {
  readonly context: Context
  /** Borrowed native connection. Disposing this mount leaves the connection available. */
  readonly connection: TypertConnection
  readonly initial?: Baseline
  readonly recovery?: ConnectionRecoveryConfig
  readonly failed?: (error: unknown) => void
}
export interface ClientAccessMount extends ClientRemoteAccess {
  readonly state: ClientAccessState
  /** Replace the physical generation while keeping the last published snapshot. */
  reconnect(): void
  /** Stop follow and await its cleanup before withdrawing the generated namespace. */
  dispose(): Promise<void>
}

/** Mount the generated native namespace and one reconnecting baseline/delta consumer. */
export async function mountAccess(options: AccessMountOptions): Promise<ClientAccessMount> {
  const state = new ClientAccessState(options.initial)
  const { context } = options
  let stopped = false
  let terminal = false
  let generation = 0
  let pending = Promise.resolve()
  let closing: Promise<void> | undefined
  const fail = (error: unknown): void => {
    terminal = true
    controller.stop()
    state.unavailable()
    if (options.failed !== undefined) options.failed(error)
    else context.logger.error('access snapshot stream failed', error)
  }
  const consume = async (signal: AbortSignal, ready: (host: { readonly home: string }) => void): Promise<void> => {
    if (stopped || terminal || signal.aborted) return
    const token = ++generation
    let seen = false
    let stream: ReturnType<typeof mounted.remote['remoteAccess/follow']> | undefined
    try {
      stream = mounted.remote['remoteAccess/follow']({}, signal)
      for await (const value of stream) {
        if (stopped || signal.aborted || token !== generation) return
        const frame = parseFrame(value)
        if (frame.type === 'baseline') {
          if (seen) throw new RemoteError('gateway/internal', 'Remote access stream emitted more than one opening snapshot')
          state.replace(frame)
          seen = true
          ready({ home: '' })
        } else {
          if (!seen) throw new RemoteError('gateway/internal', 'Remote access stream emitted an update before its opening snapshot')
          state.update(frame)
        }
      }
      if (stopped || signal.aborted || token !== generation) return
      if (!seen) throw new RemoteError('gateway/internal', 'Remote access stream ended before its opening snapshot')
      throw new RemoteStreamCarrierError('Remote access stream ended without a terminal result')
    } catch (error) {
      if (!stopped && !signal.aborted && token === generation && !(error instanceof RemoteStreamCarrierError)) fail(error)
    } finally { await stream?.dispose() }
  }
  const controller = new ConnectionController((signal, ready) => {
    pending = consume(signal, ready)
    return pending
  }, {}, options.recovery)
  let stopping: Promise<void> | undefined
  const stopFollow = (): Promise<void> => {
    if (stopping !== undefined) return stopping
    stopped = true
    generation++
    controller.stop()
    return stopping = pending
  }
  // Native namespaces publish on the root, whose effects unload concurrently. Gate both
  // registrations through follow cleanup, retaining the actual root's shared namespace table.
  const provideNamespace = (name: string, value: object): (() => Promise<void>) => context.root.effect(function* () {
    yield context.root.provide(name, value)
    yield stopFollow
  }, 'remote-access.client.namespace')
  const owner: RemoteOwner = {
    root: new Proxy(context.root, {
      get(target, property) {
        return property === 'provide' ? provideNamespace : Reflect.get(target, property, target)
      },
    }),
    effect: setup => context.effect(async () => {
      const cleanup = await setup()
      return async () => { await stopFollow(); await cleanup() }
    }, 'remote-access.client.remote'),
  }
  const mounted = await mountRemote(owner, options.connection)
  let withdraw: (() => void | Promise<void>) | undefined
  let dispose: () => Promise<void>
  try {
    const service: ClientRemoteAccess = { source: (kind, id, action) => state.source(kind, id, action), global: action => state.global(action) }
    // These effects are collected under one owner so Fiber disposal observes the same ordering.
    const lifetime = context.effect(function* () {
      yield mounted.dispose
      withdraw = context.provide('remoteAccess', service)
      yield withdraw
      yield stopFollow
    }, 'remote-access.client.mount')
    dispose = () => closing ??= Promise.resolve(lifetime())
    controller.start()
    return { state, ...service, reconnect() { if (!stopped && !terminal) controller.reconnect() }, dispose }
  } catch (error) {
    await stopFollow()
    await withdraw?.()
    await mounted.dispose()
    throw error
  }
}
