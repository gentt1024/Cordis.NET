import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'

const [base, directory, runtime] = process.argv.slice(2)
const { createRemote, mountRemote } = await import(pathToFileURL(resolve(directory, 'remote.mjs')))
const client = createRemote(base)
assert.deepEqual(await client['sample/Tuple']({ value: ['tuple', 4] }), { ok: true, value: ['tuple', 4] })
for (const value of [[4, 'tuple'], ['tuple'], ['tuple', 4, true]]) {
  assert.equal((await client['sample/Tuple']({ value })).error.code, 'gateway/input-invalid')
}
const echo = await client['sample/Echo']({ request: { Text: 'typescript', Count: 4 } })
assert.deepEqual(echo, { ok: true, value: { Text: 'typescript', Count: 4 } })
assert.deepEqual(await client['sample/NullableEcho']({ text: null }), { ok: true, value: null })
assert.deepEqual(await client['sample/NullableEcho']({ text: 'present' }), { ok: true, value: 'present' })
assert.deepEqual(await client['sample/ReturnsNull']({}), { ok: true, value: null })
assert.deepEqual(await client['sample/NullableTree']({ tree: null }), { ok: true, value: null })
const tree = { Value: 'root', Children: [{ Value: 'leaf', Children: [] }] }
assert.deepEqual(await client['sample/NullableTree']({ tree }), { ok: true, value: tree })
assert.equal((await client['sample/NullableTree']({ tree: { Value: 'root', Children: [null] } })).error.code, 'gateway/input-invalid')
assert.equal((await client['sample/Echo']({ request: { Text: 'invalid' } })).error.code, 'gateway/input-invalid')
const failure = await client['sample/Failure']({ text: 'owner error' })
assert.equal(failure.error.code, 'sample/refused')
assert.deepEqual(failure.error.details, { reason: 'example' })
assert.ok(failure.error instanceof Error)
assert.equal(failure.error.isDSHRemoteError, true)
assert.equal((await client['sample/Scoped']({ text: 'ts', contextId: 'scope-1' })).value, 'selected:ts')
assert.equal((await client['sample/Lookup']({ documentId: 'doc-1' })).value, 'loaded')
const stream = []
for await (const item of client['sample/Count']({ count: 3 })) { assert.equal(item.ok, true); stream.push(item.value) }
assert.deepEqual(stream, [0, 1, 2])
const binary = await client['sample/Binary']({ text: 'binary' })
assert.equal(Buffer.from(binary.value, 'base64').toString(), 'binary')
const cancellation = new AbortController()
const cancelled = client['sample/Hold']({ text: 'cancel' }, cancellation.signal)
cancellation.abort()
assert.equal((await cancelled).error.code, 'gateway/cancelled')

// Real pinned Cordis owns client registrations; no simulated Fiber or service map.
const { Context } = await import(pathToFileURL(resolve(runtime, 'client.mjs')))
const context = new Context()
let mounted
const fiber = context.plugin({ async apply(ctx) { mounted = await mountRemote(ctx, base) } })
await fiber
const callback = context.get('remote.sample').Echo
assert.equal((await callback({ request: { Text: 'mounted', Count: 1 } })).ok, true)
const outstanding = mounted.remote['sample/Hold']({ text: 'old' })
await mounted.dispose()
assert.equal(context.get('remote.sample'), undefined)
assert.equal((await outstanding).error.code, 'gateway/internal')
assert.equal((await callback({ request: { Text: 'stale', Count: 1 } })).error.code, 'gateway/internal')
await fiber.dispose()
const replacement = context.plugin({ async apply(ctx) { await mountRemote(ctx, base) } })
await replacement
const newCallback = context.get('remote.sample').Echo
assert.equal((await newCallback({ request: { Text: 'replacement', Count: 1 } })).ok, true)
assert.equal((await callback({ request: { Text: 'old generation', Count: 1 } })).error.code, 'gateway/internal')
await replacement.dispose()
assert.equal(context.get('remote.sample'), undefined)
assert.equal((await newCallback({ request: { Text: 'disposed', Count: 1 } })).error.code, 'gateway/internal')

const echoPart = await import(pathToFileURL(resolve(directory, 'Echo.mjs')))
const failurePart = await import(pathToFileURL(resolve(directory, 'Failure.mjs')))
const echoFiber = context.plugin({ async apply(ctx) { await echoPart.mountRemote(ctx, base) } })
await echoFiber
const failureFiber = context.plugin({ async apply(ctx) { await failurePart.mountRemote(ctx, base) } })
await failureFiber
const sharedNamespace = context.get('remote.sample')
assert.equal((await sharedNamespace.Echo({ request: { Text: 'shared', Count: 2 } })).ok, true)
assert.equal((await sharedNamespace.Failure({ text: 'shared error' })).error.code, 'sample/refused')
await assert.rejects(mountRemote(context, base), /already mounted/)
assert.equal(context.get('remote.sample'), sharedNamespace)
await echoFiber.dispose()
assert.equal(sharedNamespace.Echo, undefined)
assert.equal((await sharedNamespace.Failure({ text: 'retained' })).error.code, 'sample/refused')
await failureFiber.dispose()
assert.equal(context.get('remote.sample'), undefined)
const dependentPart = await import(pathToFileURL(resolve(directory, 'dependent.mjs')))
const parent = context.plugin({ async apply(ctx) { await echoPart.mountRemote(ctx, base) } })
await parent
const dependent = context.plugin({ inject: ['remote.sample'], async apply(ctx) { await dependentPart.mountRemote(ctx, base) } })
await dependent
assert.equal(typeof context.get('remote.dependent').Echo, 'function')
let deadline
try {
  await Promise.race([parent.dispose(), new Promise((_, reject) => { deadline = setTimeout(() => reject(new Error('Remote dependency withdrawal deadlocked')), 5000) })])
} finally { clearTimeout(deadline) }
assert.equal(context.get('remote.sample'), undefined)
assert.equal(context.get('remote.dependent'), undefined)
await dependent.dispose()
let secondary
let secondaryMounted
const publishedMethods = []
context.on('internal/service', (name, value) => {
  if (name === 'remote.sample' && value && !secondary) {
    publishedMethods.push(Object.keys(value))
    secondary = failurePart.mountRemote(context, base).then(mounted => { secondaryMounted = mounted })
  }
})
const primary = await echoPart.mountRemote(context, base)
await secondary
assert.deepEqual(publishedMethods, [['Echo']])
assert.deepEqual(Object.keys(context.get('remote.sample')).sort(), ['Echo', 'Failure'])
await primary.dispose()
assert.deepEqual(Object.keys(context.get('remote.sample')), ['Failure'])
await secondaryMounted.dispose()
assert.equal(context.get('remote.sample'), undefined)
let oldMount
const oldOwner = context.plugin({ async apply(ctx) { oldMount = await echoPart.mountRemote(ctx, base) } })
await oldOwner
let releaseRetirement
let retirementStarted
const retirementBarrier = new Promise(resolve => { releaseRetirement = resolve })
const retirementEntered = new Promise(resolve => { retirementStarted = resolve })
let cleanups = 0
const slowDependent = context.plugin({ inject: ['remote.sample'], apply(ctx) {
  ctx.effect(() => async () => { if (++cleanups === 1) { retirementStarted(); await retirementBarrier } })
} })
await slowDependent
const retiring = oldMount.dispose()
await retirementEntered
const replacementOwner = context.plugin({ async apply(ctx) { await echoPart.mountRemote(ctx, base) } })
const disjoint = await dependentPart.mountRemote(context, base)
assert.equal(typeof context.get('remote.dependent').Echo, 'function')
await disjoint.dispose()
releaseRetirement()
await retiring
await replacementOwner
const replacementService = context.get('remote.sample')
let inheritedService
let inheritedError
const reader = context.plugin({ apply(ctx) { try { inheritedService = ctx['remote.sample'] } catch (error) { inheritedError = error } } })
await reader
assert.equal(inheritedError, undefined)
assert.equal(inheritedService, replacementService)
await reader.dispose()
await replacementOwner.dispose()
await slowDependent.dispose()
await oldOwner.dispose()
await context.fiber.dispose()
client.dispose()

// A carrier may ignore AbortSignal: generation and cancellation checks must still hold after awaits.
let releaseResponse
const ignoredAbort = createRemote(base, () => new Promise(resolve => { releaseResponse = resolve }))
const ignoredPending = ignoredAbort['sample/Echo']({ request: { Text: 'late', Count: 1 } })
ignoredAbort.dispose()
releaseResponse(new Response('', { status: 500 }))
assert.equal((await ignoredPending).error.code, 'gateway/internal')
const ignoredCancellation = new AbortController()
const ignoringCarrier = createRemote(base, () => new Promise(resolve => { releaseResponse = resolve }))
const cancellingPending = ignoringCarrier['sample/Echo']({ request: { Text: 'cancel', Count: 1 } }, ignoredCancellation.signal)
ignoredCancellation.abort()
releaseResponse(Response.json({ ok: true, value: { Text: 'cancel', Count: 1 } }))
assert.equal((await cancellingPending).error.code, 'gateway/cancelled')
ignoringCarrier.dispose()
let closeStream
const ignoringStream = createRemote(base, async () => new Response(new ReadableStream({ start(controller) { closeStream = () => controller.close() } })))
const iterator = ignoringStream['sample/Count']({ count: 0 })[Symbol.asyncIterator]()
const waitingRead = iterator.next()
await new Promise(resolve => setTimeout(resolve, 0))
ignoringStream.dispose()
closeStream()
assert.equal((await waitingRead).value.error.code, 'gateway/internal')
await iterator.return()

await writeFile(resolve(directory, 'consumer.mts'), `import { createRemote } from './remote.mjs'
const remote = createRemote('http://localhost/remote')
async function call() {
  const tuple = await remote['sample/Tuple']({value:['tuple',4]})
  if (tuple.ok) { const value: readonly [string, number] = tuple.value; void value }
  // @ts-expect-error Tuple positions retain their declared types.
  remote['sample/Tuple']({value:[4,'tuple']})
  // @ts-expect-error Both tuple positions are required.
  remote['sample/Tuple']({value:['tuple']})
  // @ts-expect-error A closed tuple cannot include a tail.
  remote['sample/Tuple']({value:['tuple',4,true]})
  const result = await remote['sample/Echo']({request:{Text:'typed', Count:1}})
  if (result.ok) { const count: number = result.value.Count; void count }
  const nullable = await remote['sample/NullableEcho']({text:null})
  if (nullable.ok) { const text: string | null = nullable.value; void text }
  const nullResult = await remote['sample/ReturnsNull']({})
  if (nullResult.ok) {
    // @ts-expect-error A nullable result must be narrowed before use as a string.
    const text: string = nullResult.value; void text
  }
  const nullTree = await remote['sample/NullableTree']({tree:null})
  if (nullTree.ok) { const node: {Value:string, Children:readonly unknown[]} | null = nullTree.value; void node }
  remote['sample/NullableTree']({tree:{Value:'root',Children:[{Value:'leaf',Children:[]}]}})
  // @ts-expect-error Nullable root must not make recursive children nullable.
  remote['sample/NullableTree']({tree:{Value:'root',Children:[null]}})
  // @ts-expect-error Count is a required number in the generated source contract.
  remote['sample/Echo']({request:{Text:'bad', Count:'one'}})
  // @ts-expect-error Missing business argument must fail client compilation.
  remote['sample/Lookup']({})
}
void call
`)
await writeFile(resolve(directory, 'tuple-consumer.mts'), `import { createRemote as nested } from './tuple-nested.mjs'
import { createRemote as optional } from './tuple-optional.mjs'
import { createRemote as rest } from './tuple-rest.mjs'
import { createRemote as open } from './tuple-open.mjs'
async function call() {
  const result = await nested('')['sample/Tuple']({value:['tuple',[4,true]]})
  if (result.ok) { const value: readonly [string, readonly [number, boolean]] = result.value; void value }
  // @ts-expect-error Nested reference tuples retain positional types.
  nested('')['sample/Tuple']({value:['tuple',[true,4]]})
  optional('')['sample/Tuple']({value:['tuple']})
  optional('')['sample/Tuple']({value:['tuple',4]})
  // @ts-expect-error The required prefix remains required.
  optional('')['sample/Tuple']({value:[]})
  // @ts-expect-error Optional means omitted, not any type or null.
  optional('')['sample/Tuple']({value:['tuple',null]})
  rest('')['sample/Tuple']({value:['tuple',true,false]})
  // @ts-expect-error Rest elements retain their declared type.
  rest('')['sample/Tuple']({value:['tuple',4]})
  open('')['sample/Tuple']({value:[]})
  open('')['sample/Tuple']({value:['tuple',null,{}]})
  // @ts-expect-error An omitted minItems does not erase prefix types when present.
  open('')['sample/Tuple']({value:[4]})
}
void call
`)
execFileSync(process.execPath, [resolve('clients/modules/node_modules/typescript/bin/tsc'), '--strict', '--noEmit', '--target', 'ES2022', '--module', 'NodeNext', '--moduleResolution', 'NodeNext', resolve(directory, 'consumer.mts'), resolve(directory, 'tuple-consumer.mts')], { stdio: 'inherit' })
console.log('PASS generated TypeScript declarations, real HTTP/stream/error/lookup/scope/cancellation/binary projection and pinned Cordis mount withdrawal with stale callbacks')
