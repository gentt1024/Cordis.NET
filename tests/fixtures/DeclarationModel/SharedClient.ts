import { createRemote, mountRemote, getRemote, TypertConnection, RemoteError, type RemoteOwner, type RemoteCaller } from './shared.mjs'

function check(value: unknown, reason: string): asserts value { if (!value) throw new Error(reason) }
function equal(actual: unknown, expected: unknown, reason: string) { check(JSON.stringify(actual) === JSON.stringify(expected), reason) }
async function collect(values: AsyncIterable<string>) { const result = []; for await (const value of values) result.push(value); return result }

export async function run(origin: string, socketOrigin: string, createContext: () => ScopedOwner) {
  const fetcher: typeof fetch = (input, init) => {
    const headers = new Headers(init?.headers); headers.set('Authorization', 'consumer-policy')
    return fetch(input, { ...init, headers })
  }
  const connection = new TypertConnection(origin + '/remote', fetcher, () => new WebSocket(socketOrigin))
  const client = createRemote(connection)
  const signal = AbortSignal.timeout(15000)
  try {
    await scopedCalls(connection, createContext)
    const text = client['sharedData/inputText']({ prefix: 'ts:' }, signal)
    text.send('first'); text.send('second'); text.end(); text.end()
    equal(await collect(text), ['ts:first', 'ts:second', 'ended'], 'Pre-read sends and half-close lost their order')
    text.end()
    let closed = false
    try { text.send('late') } catch { closed = true }
    check(closed, 'A retired handle still accepted input')

    const optional = client['sharedData/inputOptional']({}, signal)
    const cyclic: { self?: unknown } = {}; cyclic.self = cyclic
    for (const value of [NaN, Infinity, -0, { nested: undefined }, [undefined], new Date(), cyclic]) {
      let refused = false
      try { Reflect.apply(optional.send, optional, [value]) } catch { refused = true }
      check(refused, 'Lossy JSON input was accepted')
    }
    optional.send(undefined); optional.send(null); optional.send('text'); optional.end()
    equal(await collect(optional), ['undefined', 'null', 'text'], 'Absent and null input were merged')

    const nested = client['sharedData/inputList']({}, signal)
    nested.send(null); nested.send(['text', null]); nested.end()
    equal(await collect(nested), ['null-list', 'text,null-item'], 'Nested nullability disagrees with the generated types')

    const invalid = client['sharedData/inputText']({ prefix: '' }, signal)
    // Deliberately bypass the generated signature to exercise Host codec admission.
    Reflect.apply(invalid.send, invalid, [null])
    try { await collect(invalid); throw new Error('Host accepted null for a non-null input') }
    catch (error) { check(error instanceof RemoteError && error.code === 'gateway/input-invalid', 'Host codec failure lost its Remote boundary') }
    const caught = client['sharedData/inputCaught']({}, signal)
    Reflect.apply(caught.send, caught, [null])
    let escaped = false
    try { for await (const ignored of caught) { void ignored; escaped = true } throw new Error('Caught codec failure became success') }
    catch (error) { check(!escaped && error instanceof RemoteError && error.code === 'gateway/input-invalid', 'Handler catch hid the uplink codec rejection') }
    const sibling = client['sharedData/inputText']({ prefix: 'alive:' }, signal)
    sibling.send('yes'); sibling.end()
    equal(await collect(sibling), ['alive:yes', 'ended'], 'Input failure killed the shared carrier')
    const downlink = client['sharedData/hold']({}, signal)
    check(!('send' in downlink) && !('end' in downlink), 'Downlink acquired undeclared input capabilities')
    await downlink.dispose()
    await rawViolations(socketOrigin)
    for (const frame of [
      '{"type":"open","streamId":"missing","endpoint":"sharedData/hold"}',
      '{"type":"item","streamId":"invalid","value":{"nested":-0}}',
    ]) {
      const malformed = new WebSocket(socketOrigin)
      const close = new Promise<CloseEvent>(resolve => { malformed.onclose = resolve })
      await new Promise<void>(resolve => { malformed.onopen = () => resolve() })
      malformed.send(frame)
      check((await close).code === 1008, 'Malformed frame did not close its physical carrier')
    }
  } finally { await client.dispose(); await connection.dispose() }
}

interface ScopedOwner extends RemoteOwner, RemoteCaller {
  extend(metadata: object): ScopedOwner
  fiber: { dispose(): Promise<void> }
}

export async function scopedCalls(connection: TypertConnection, createContext: () => ScopedOwner) {
  const owner = createContext()
  const caller = owner.extend({ documentIdentity: 'doc-1', receiverIdentity: 7 })
  const adapters = new Map([
    ['shared:document', { identity: (ctx: RemoteCaller): unknown => Reflect.get(ctx, 'documentIdentity') }],
    ['shared:receiver', { identity: (ctx: RemoteCaller): unknown => Reflect.get(ctx, 'receiverIdentity') }],
  ])
  const mounted = await mountRemote(owner, connection, { getClient: key => adapters.get(key) })
  try {
    const service = getRemote(caller, 'sharedData')
    const read = service.read
    equal(await service.scopedRead({}), { ok: true, value: 'selected:default' }, 'Receiver Context was not selected')
    equal(await read({}), { ok: true, value: 'root:doc-1!' }, 'Lookup projection changed the Host receiver')
    Reflect.set(caller, 'documentIdentity', 'doc-2')
    equal(await read({ suffix: '?' }), { ok: true, value: 'root:doc-2?' }, 'Retained callback cached caller identity')
    equal(await collect(service.watch({})), ['root:doc-2', 'complete'], 'Scoped stream lost its lookup identity')
    adapters.set('shared:document', { identity: () => 'doc-1' })
    equal(await read({}), { ok: true, value: 'root:doc-1!' }, 'Retained callback cached the adapter')
    adapters.delete('shared:document')
    equal(await read({ documentId: 'doc-2' }), { ok: true, value: 'root:doc-2!' }, 'Direct lookup fallback was lost')
    adapters.set('shared:document', { identity: () => null })
    const nullResult = await read({ documentId: 'doc-2' })
    check(!nullResult.ok && nullResult.error.code === 'gateway/input-invalid', 'Null identity became absent or fell back to direct lookup')
    adapters.set('shared:document', { identity: () => undefined })
    equal(await read({ documentId: 'doc-2' }), { ok: true, value: 'root:doc-2!' }, 'Undefined identity did not select direct lookup')
    adapters.delete('shared:receiver')
    let missing = false
    try { await service.scopedRead({}) } catch (error) { missing = String(error).includes('Context adapter') }
    check(missing, 'A receiver-scoped method without an adapter was dispatched')
    adapters.set('shared:receiver', { identity: () => undefined })
    let absent = false
    try { await service.scopedRead({}) } catch (error) { absent = String(error).includes('requires') }
    check(absent, 'An undefined receiver identity was dispatched')
    await mounted.dispose()
    const stale = await read({})
    check(!stale.ok && stale.error.code === 'gateway/internal', 'Retained callback survived contribution withdrawal')
    check(connection.active, 'Contribution disposed the borrowed Connection')
    const replacement = await mountRemote(owner, connection, { getClient: key => adapters.get(key) })
    try {
      equal(await getRemote(caller, 'sharedData').read({ documentId: 'doc-2' }),
        { ok: true, value: 'root:doc-2!' }, 'Borrowed Context could not consume a replacement contribution')
      check(!(await read({ documentId: 'doc-2' })).ok, 'Replacement revived a retained old callback')
    } finally { await replacement.dispose() }
    // These are generated authoring boundaries; the runtime checks above own behavior.
    if (false) {
      // @ts-expect-error scoped receiver identity belongs to the adapter
      void service.scopedRead({ receiverId: 7 })
      // @ts-expect-error string lookup identities cannot become numbers
      void read({ documentId: 7 })
    }
  } finally { await mounted.dispose(); await owner.fiber.dispose() }
}

function object(value: unknown): value is Record<string, unknown> { return typeof value === 'object' && value !== null && !Array.isArray(value) }
async function rawViolations(address: string) {
  const socket = new WebSocket(address)
  const messages: unknown[] = []
  let wake: (() => void) | undefined
  socket.onmessage = event => { messages.push(JSON.parse(String(event.data))); wake?.() }
  await new Promise<void>((resolve, reject) => { socket.onopen = () => resolve(); socket.onerror = () => reject(new Error('Raw socket failed')) })
  const signal = AbortSignal.timeout(10000)
  const next = async () => {
    while (!messages.length) {
      signal.throwIfAborted()
      await new Promise<void>((resolve, reject) => {
        const abort = () => reject(signal.reason)
        signal.addEventListener('abort', abort, { once: true })
        wake = () => { signal.removeEventListener('abort', abort); resolve() }
      })
    }
    const value = messages.shift(); check(object(value), 'Expected an object mux frame'); return value
  }
  const send = (value: unknown) => socket.send(JSON.stringify(value))
  try {
    for (const [id, code, frames] of [
      ['after-end', 'gateway/protocol', [{ type: 'end' }, { type: 'item', value: 'late' }]],
      ['overflow', 'gateway/uplink-overflow', [{ type: 'item', value: 'x'.repeat(262144) }]],
    ] satisfies [string, string, object[]][]) {
      send({ type: 'open', streamId: id, endpoint: 'sharedData/hold', payload: { args: {} } })
      check((await next()).type === 'item', 'Host did not enter the held stream')
      for (const frame of frames) send({ ...frame, streamId: id })
      const failed = await next()
      check(failed.type === 'error' && failed.streamId === id && object(failed.error) && failed.error.code === code,
        'Uplink violation did not fail its logical stream')
      check(object(failed.error.details) && failed.error.details.endpoint === 'sharedData/hold', 'Uplink error lost its endpoint details')
    }
    send({ type: 'open', streamId: 'sibling', endpoint: 'sharedData/inputText', payload: { args: { prefix: '' } } })
    send({ type: 'item', streamId: 'sibling', value: 'survived' }); send({ type: 'end', streamId: 'sibling' })
    equal(await next(), { type: 'item', streamId: 'sibling', value: 'survived' }, 'Violation destroyed the physical carrier')
    check((await next()).value === 'ended' && (await next()).type === 'end', 'Half-close did not settle')
  } finally { socket.close() }
}
