import { createRemote, mountRemote, getRemote, resultCodecs, info, TypertConnection, RemoteError, type RemoteOwner, type RemoteCaller } from './shared.mjs'

export async function resultContracts(origin: string) {
  let sends = 0
  const connection = new TypertConnection(origin + '/remote', (input, init) => {
    sends++
    const headers = new Headers(init?.headers); headers.set('Authorization', 'consumer-policy')
    return fetch(input, { ...init, headers })
  })
  const client = createRemote(connection)
  const bytesCodec = resultCodecs['sharedData/bytes']
  const attachmentsCodec = resultCodecs['sharedData/attachments']
  const rejects = (parse: (value: unknown) => unknown, value: unknown) => {
    let error: unknown
    try { parse(value) } catch (caught) { error = caught }
    check(error instanceof TypeError, 'Invalid decoded result was not rejected')
  }
  try {
    const plain = resultCodecs['result/plainBytes']
    const nested = resultCodecs['result/composedObject']
    for (const value of [new Uint8Array([1, 2, 3]), new Uint8Array()]) {
      check(plain.parse(value) === value && bytesCodec.parse(value) === value,
        'Redundant non-null constraint rejected decoded bytes or changed their identity')
      const wrapped = { Payload: value }
      check(nested.parse(wrapped) === wrapped, 'Composed object contract rejected nested bytes or copied the result')
    }
    for (const invalid of [null, 'AQID', undefined]) {
      rejects(plain.parse, invalid); rejects(bytesCodec.parse, invalid)
    }
    for (const invalid of [null, {}, { Payload: null }, { Payload: 'AQID' }, { Payload: undefined },
      { Payload: new Uint8Array(), extra: new Uint8Array() }]) rejects(nested.parse, invalid)
    const bytes = await client['sharedData/bytes']({})
    check(bytes.ok, 'Real root bytes failed')
    equal([...bytes.value], [0, 13, 10, 255], 'Root byte contents changed')
    check(bytesCodec.parse(bytes.value) === bytes.value, 'Codec copied the original byte view')
    const reply = await client['sharedData/attachments']({})
    check(reply.ok, 'Real nested binary result failed')
    const value = reply.value
    equal([...value.items[0]], [0, 13, 10, 255], 'Nested bytes changed')
    equal([...value.files['part/name']], [1, 2], 'Dictionary byte path changed')
    check(value.text === 'AA0K/w==' && !Object.hasOwn(value, 'optional'), 'Output text or omission changed')
    check(value.child?.payload instanceof Uint8Array && value.child.next?.payload.length === 0 &&
      value.child.next.next === null, 'Recursive binary output changed')
    const before = sends
    check(attachmentsCodec.parse(value) === value, 'Reusable result codec changed the result identity')
    attachmentsCodec.parse({ ...value, optional: null })
    attachmentsCodec.parse({ ...value, optional: new Uint8Array() })
    for (const invalid of [
      { ...value, items: ['AA0K/w=='] }, { ...value, files: { bad: 'AQI=' } },
      { ...value, child: { payload: new Uint8Array(), next: { payload: 'AA==', next: null } } },
      { ...value, items: undefined }, { ...value, optional: undefined }, { ...value, text: new Uint8Array() },
      { files: value.files, text: value.text, child: null },
    ]) rejects(attachmentsCodec.parse, invalid)
    for (const invalid of ['AA0K/w==', null, undefined]) rejects(bytesCodec.parse, invalid)
    check(resultCodecs['sharedData/nullable'].parse(null) === null, 'Explicit null was not retained')
    rejects(resultCodecs['sharedData/nullable'].parse, undefined)
    check(resultCodecs['sharedData/acknowledge'].parse(undefined) === undefined, 'Void lost absence')
    rejects(resultCodecs['sharedData/acknowledge'].parse, null)
    const optional = resultCodecs['result/optional']
    check(optional.parse(undefined) === undefined && optional.parse(null) === null, 'Optional result lost absence or null')
    check(optional.parse('text') === 'text', 'Optional result lost its declared value')
    rejects(optional.parse, new Uint8Array())
    const encoded = resultCodecs['result/encodedText']
    check(encoded.parse('AQID') === 'AQID', 'Base64 annotation changed a string into bytes')
    rejects(encoded.parse, new Uint8Array([1, 2, 3]))
    const literal = resultCodecs['result/literal']
    literal.parse(new Uint8Array([1, 2, 3])); literal.parse(null)
    for (const invalid of [new Uint8Array([1, 2, 4]), new Uint8Array(), 'AQID', undefined]) rejects(literal.parse, invalid)
    check(sends === before, 'Independent codec use sent a request')
    check(Object.isFrozen(resultCodecs) && Object.isFrozen(bytesCodec), 'Result codecs are mutable')
  } finally { await client.dispose(); await connection.dispose() }

  // The same generated caller must check a reconstructed result, not just its carrier.
  for (const mode of ['wrong-string', 'missing-part', 'good-bytes', 'good-object', 'bad-object', 'cancelled', 'withdrawn', 'remote-error', 'ordinary-json']) {
    const abort = new AbortController()
    let disposal: Promise<void> | undefined
    const injected = new TypertConnection(origin + '/remote', async (_input, init) => {
      const { rpcId } = JSON.parse(String(init?.body))
      const envelope = { type: 'server-response', rpcId, result: { ok: true, value: 'AA0K/w==' } }
      if (mode === 'cancelled') abort.abort(new Error('cancel before result validation'))
      if (mode === 'withdrawn') disposal = caller.dispose()
      if (mode === 'remote-error') return Response.json({ ...envelope,
        result: { ok: false, error: { code: 'example/rejected', message: 'business refusal', details: {} } } })
      if (mode === 'good-bytes' || mode === 'good-object' || mode === 'missing-part') {
        const form = new FormData()
        form.set('metadata', JSON.stringify({ ...envelope, result: { ok: true, value: mode === 'good-object' ? { Payload: null } : null },
          attachments: [{ codec: 'bytes', part: 'bytes', path: mode === 'good-object' ? ['Payload'] : [] }] }))
        if (mode !== 'missing-part') form.set('bytes', new Blob([new Uint8Array([0, 255])]))
        return new Response(form)
      }
      if (mode === 'bad-object') return Response.json({ ...envelope, result: { ok: true, value: { Payload: 'AP8=' } } })
      if (mode === 'ordinary-json') return Response.json({ ...envelope, result: { ok: true, value: 42 } })
      return Response.json(envelope)
    })
    const caller = createRemote(injected)
    try {
      const reply = mode === 'ordinary-json'
        ? await caller['sharedData/nullable']({})
        : mode === 'good-object' || mode === 'bad-object' ? await caller['result/composedObject']({})
        : await caller['sharedData/bytes']({}, abort.signal)
      if (mode === 'good-bytes') {
        check(reply.ok && reply.value instanceof Uint8Array, 'Valid multipart failed decoding')
      } else if (mode === 'good-object') {
        check(reply.ok && object(reply.value) && reply.value.Payload instanceof Uint8Array,
          'Composed nested result rejected a valid multipart response')
      } else if (mode === 'ordinary-json') {
        check(reply.ok, 'Ordinary JSON acquired a new automatic validation policy')
        rejects(resultCodecs['sharedData/nullable'].parse, reply.value)
      } else {
        check(!reply.ok && reply.error instanceof RemoteError, 'Invalid or failed result escaped the standard outcome')
        const code = mode === 'remote-error' ? 'example/rejected'
          : mode === 'cancelled' ? 'gateway/cancelled' : 'gateway/internal'
        check(reply.error.code === code, 'Result validation replaced the established error boundary')
        if (mode === 'wrong-string' || mode === 'bad-object') check(reply.error.cause instanceof TypeError, 'Decoder error cause was lost')
        if (mode === 'withdrawn') check(reply.error.message.includes('withdrawn'), 'Withdrawal did not precede decoding')
      }
    } finally { await caller.dispose(); await disposal; await injected.dispose() }
  }
}

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
    await resultContracts(origin)
    await scopedCalls(origin, socketOrigin, createContext)
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

export async function scopedCalls(origin: string, socketOrigin: string, createContext: () => ScopedOwner) {
  const sent = { http: 0, open: 0 }
  const connection = new TypertConnection(origin + '/remote', (input, init) => {
    sent.http++
    const headers = new Headers(init?.headers); headers.set('Authorization', 'consumer-policy')
    return fetch(input, { ...init, headers })
  }, () => {
    const socket = new WebSocket(socketOrigin)
    const send = socket.send.bind(socket)
    socket.send = data => {
      if (typeof data === 'string' && JSON.parse(data).type === 'open') sent.open++
      send(data)
    }
    return socket
  })
  const owner = createContext()
  const caller = owner.extend({ documentIdentity: 'doc-1', receiverIdentity: 7 })
  const adapters = new Map([
    ['shared:document', { identity: (ctx: RemoteCaller): unknown => Reflect.get(ctx, 'documentIdentity') }],
    ['shared:receiver', { identity: (ctx: RemoteCaller): unknown => Reflect.get(ctx, 'receiverIdentity') }],
  ])
  const mounted = await mountRemote(owner, connection, { getClient: key => adapters.get(key) })
  const preparationFailures: string[] = []
  async function refused(invoke: () => Promise<unknown> | AsyncIterable<unknown>, stream: boolean, reason: string) {
    const before = { ...sent }
    let value: Promise<unknown> | AsyncIterable<unknown> | undefined, synchronous: unknown, rejection: unknown
    try { value = invoke() } catch (error) { synchronous = error }
    if (stream) {
      if (!synchronous && value && Symbol.asyncIterator in value) {
        // Drain an incorrectly admitted stream so the real Host response and carrier settle.
        try { for await (const item of value) { void item } } catch { }
      }
    } else {
      try { await value } catch (error) { rejection = error }
    }
    const error = stream ? synchronous : rejection
    if (!(error instanceof Error) || error instanceof RemoteError || !error.message.includes(reason) ||
        (!stream && (synchronous !== undefined || !(value instanceof Promise))) ||
        sent.http !== before.http || sent.open !== before.open)
      preparationFailures.push((stream ? 'stream' : 'unary') + ': ' + reason + ' ' +
        JSON.stringify({ synchronous: String(synchronous), rejection: String(rejection), before, after: sent }))
  }
  try {
    const service = getRemote(caller, 'sharedData')
    const metadata = info.methods['sharedData/echo']
    check(info.descriptors.includes(metadata), 'Named metadata copied its descriptor')
    check(metadata.input() === metadata.input({ strict: false }) && metadata.input() !== metadata.input({ strict: true }),
      'Input policies did not retain separate stable views')
    check(metadata.input().jsonSchema === undefined && typeof metadata.input().jsonSchemaError === 'string',
      'Recursive metadata claimed a lossless tool schema')
    const named = metadata.input().create().parse({ value: { display_name: 'metadata', count: 11 } })
    const namedReply = await metadata.invoke(caller, named)
    check(namedReply.ok && namedReply.value.display_name === 'metadata' && namedReply.value.count === 14,
      'Metadata invocation lost the mounted caller or omitted defaults over HTTP')
    check(info.methods['sharedData/bytes'].result.jsonSchema === undefined,
      'Binary result was incorrectly projected as a JSON string')
    equal(await service.scopeLayers({}), { ok: true, value: 'child>peer>global' },
      'Scope layer package consumption lost Peer inheritance or owner cleanup')
    equal(await service.peerRouting({}),
      { ok: true, value: 'global:peer,peer:peer,global:child,peer:child,child:child,global:none' },
      'Host operator Peer did not preserve scope-targeted listener routing')
    const read = service.read
    equal(await service.scopedRead({}), { ok: true, value: 'selected:default' }, 'Receiver Context was not selected')
    equal(await read({}), { ok: true, value: 'root:doc-1!' }, 'Lookup projection changed the Host receiver')
    Reflect.set(caller, 'documentIdentity', 'doc-2')
    equal(await read({ suffix: '?' }), { ok: true, value: 'root:doc-2?' }, 'Retained callback cached caller identity')
    equal(await collect(service.watch({})), ['root:doc-2', 'complete'], 'Scoped stream lost its lookup identity')
    for (const absentAdapter of [true, false]) {
      if (absentAdapter) adapters.delete('shared:document')
      else adapters.set('shared:document', { identity: () => undefined })
      for (const signal of [undefined, AbortSignal.abort(new Error('pre-cancelled'))]) {
        await refused(() => read({}, signal), false, 'requires argument "documentId"')
        await refused(() => service.watch({}, signal), true, 'requires argument "documentId"')
      }
    }
    const undefinedResult = await Reflect.apply(read, service, [{ documentId: undefined }])
    check(!undefinedResult.ok && undefinedResult.error.code === 'gateway/arguments-invalid',
      'Explicit undefined was confused with a missing argument slot or accepted by the Host')
    const directNull = await Reflect.apply(read, service, [{ documentId: null }])
    check(!directNull.ok && directNull.error.code === 'gateway/input-invalid', 'Explicit null was rejected as a missing slot')
    const identityError = new Error('adapter identity failed')
    adapters.set('shared:document', { identity: () => { throw identityError } })
    let identityFailure: unknown
    try { void read({}) } catch (error) { identityFailure = error }
    check(identityFailure === identityError, 'Variant selection moved the adapter exception into a Promise')
    adapters.set('shared:document', { identity: () => 'doc-1' })
    equal(await read({}), { ok: true, value: 'root:doc-1!' }, 'Retained callback cached the adapter')
    equal(await read({ suffix: undefined }), { ok: true, value: 'root:doc-1!' }, 'Optional undefined lost its default')
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
      await connection.dispose()
      const liveService = getRemote(caller, 'sharedData')
      await refused(() => liveService.read({ documentId: 'doc-2' }), false, 'no active Connection')
      await refused(() => liveService.watch({ documentId: 'doc-2' }), true, 'no active Connection')
    } finally { await replacement.dispose() }
    check(preparationFailures.length === 0, preparationFailures.join('\n'))
    // These are generated authoring boundaries; the runtime checks above own behavior.
    if (false) {
      // @ts-expect-error scoped receiver identity belongs to the adapter
      void service.scopedRead({ receiverId: 7 })
      // @ts-expect-error string lookup identities cannot become numbers
      void read({ documentId: 7 })
      // @ts-expect-error an empty scoped overload must not admit a mistyped direct identity
      void service.watch({ documentId: 7 })
    }
  } finally { await mounted.dispose(); await owner.fiber.dispose(); await connection.dispose() }
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
