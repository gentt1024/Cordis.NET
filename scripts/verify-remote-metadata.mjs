import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { cp, mkdtemp, mkdir, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const generated = process.argv[2]
assert.ok(generated, 'Pass the artifact directory emitted by the independent RemoteMetadataClient fixture.')
const requireTools = createRequire(resolve(process.argv[3] ?? resolve(root, 'clients/modules/package.json')))
const ts = requireTools('typescript')
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-remote-metadata-'))
const installed = resolve(directory, 'node_modules/@fixture/remote')
await mkdir(installed, { recursive: true })
await cp(resolve(generated), installed, { recursive: true })
await writeFile(resolve(directory, 'package.json'), '{"private":true,"type":"module"}\n')
await writeFile(resolve(installed, 'package.json'), JSON.stringify({
  name: '@fixture/remote', type: 'module', exports: {
    '.': { types: './remote.d.mts', import: './remote.mjs' },
    './runtime': { types: './cordis-remote.d.mts', import: './cordis-remote.mjs' },
  },
}) + '\n')
await writeFile(resolve(directory, 'consumer.ts'), `
import { info, type RemoteClient, type RemoteServices, type RemoteDuplexHandle } from '@fixture/remote'
import type { RemoteCaller } from '@fixture/remote/runtime'
declare const services: RemoteServices
declare const caller: RemoteCaller
declare const named: Parameters<RemoteClient['metadata/echo']>[0]
const parsed: typeof named = info.methods['metadata/echo'].input({strict:true}).create().parse(named)
const result: ReturnType<RemoteClient['metadata/echo']> = info.methods['metadata/echo'].invoke(services, parsed)
info.methods['metadata/echo'].invoke(caller, named)
const duplex: RemoteDuplexHandle<string, string> = info.methods['metadata/duplex'].invoke(services, {value:'hello'})
duplex.send('next')
const mode: 'stream' = info.methods['metadata/duplex'].mode
const unary: undefined = info.methods['metadata/echo'].mode
const lookup: {documentId:string} = info.methods['metadata/lookup'].input().create().parse({documentId:'doc'})
// @ts-expect-error Context receiver metadata is excluded from direct named methods.
info.methods['metadata/context']
// @ts-expect-error Exact generated endpoint keys are retained.
info.methods['missing/endpoint']
// @ts-expect-error Named wire arguments retain their generated types.
info.methods['metadata/lookup'].invoke(services, {documentId:42})
// @ts-expect-error Duplex input is generated independently of its output.
duplex.send(42)
export { result, lookup }
`)
const program = ts.createProgram([resolve(directory, 'consumer.ts')], {
  noEmit: true, strict: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts', 'lib.esnext.disposable.d.ts'], types: [],
})
const diagnostics = ts.getPreEmitDiagnostics(program)
if (diagnostics.length) throw new Error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
  getCurrentDirectory: () => directory, getCanonicalFileName: name => name, getNewLine: () => '\n',
}))
const { info, resultCodecs, createRemote, mountRemote } = await import(pathToFileURL(resolve(installed, 'remote.mjs')).href)
const { createRemoteInfo } = await import(pathToFileURL(resolve(installed, 'cordis-remote.mjs')).href)
assert.equal(info.package, 'metadata-package')
assert.equal(info.descriptors.length, 19)
assert.equal(Object.keys(info.methods).length, 18)
assert.equal(Object.getPrototypeOf(info.methods), null)
assert.equal(info.methods['metadata/context'], undefined)
const echo = info.methods['metadata/echo']
assert.equal(info.descriptors.find(value => value.method === 'echo'), echo)
assert.equal(echo.service, 'metadata-service')
assert.equal(echo.id, 'metadata-package#metadata/echo')
assert.equal(echo.description, 'Echo the declared payload.')
assert.equal(echo.parameters[0].name, 'payload')
assert.equal(echo.parameters[0].wire, 'value')
assert.equal(echo.parameters[0].description, 'The supplied payload.')
assert.deepEqual(echo.cancellation, {parameter:'signal'})
assert.equal(echo.result.description, 'The returned payload.')
const normal = echo.input()
const strict = echo.input({strict:true})
assert.equal(normal, echo.input({strict:false}))
assert.equal(strict, echo.input({strict:true}))
assert.notEqual(normal, strict)
assert.equal(normal.create(), normal.create())
assert.notEqual(normal.create(), strict.create())
const named = {value:{name:'sample', nested:{label:'child'}, extra:{retained:true}}}
assert.equal(normal.create().parse(named), named)
assert.equal(named.value.extra.retained, true)
assert.throws(() => strict.create().parse(named), TypeError)
assert.throws(() => strict.create().parse({value:{name:'sample', nested:{label:'child', unknown:true}}}), TypeError)
assert.throws(() => normal.create().parse({value:{name:42, nested:{label:'child'}}}), TypeError)
assert.throws(() => normal.create().parse({value:named.value, extra:true}), TypeError)
assert.throws(() => normal.create().parse({}), TypeError)
const closed = {value:{name:'sample', nested:{label:'child'}}}
assert.equal(strict.create().parse(closed), closed)
assert.equal(normal.jsonSchemaError, undefined)
assert.equal(normal.jsonSchema, normal.jsonSchema)
assert.equal(normal.jsonSchema.properties.value.description, 'The supplied payload.')
assert.equal(strict.jsonSchema.properties.value.additionalProperties, false)
assert.equal(echo.result.jsonSchema.description, 'The returned payload.')
assert.equal(echo.result.create().parse(closed.value), closed.value)
assert.equal(info.methods['metadata/optional'].input().create().parse({}).label, undefined)
assert.equal(info.methods['metadata/optional'].input().jsonSchema.properties.label.type, 'string')
assert.deepEqual(info.methods['metadata/optional'].input().jsonSchema.required, [])
const optionalUplink = info.methods['metadata/optional-uplink'].uplink.codec
assert.equal(optionalUplink.jsonSchema, undefined)
assert.match(optionalUplink.jsonSchemaError, /absent/)
assert.equal(optionalUplink.create().parse(undefined), undefined)
assert.equal(optionalUplink.create().parse('item'), 'item')
const requiredOptional = info.methods['metadata/required-optional'].input()
assert.equal(requiredOptional.jsonSchema, undefined)
assert.match(requiredOptional.jsonSchemaError, /absent/)
assert.equal(requiredOptional.create().parse({value:'present'}).value, 'present')
assert.throws(() => requiredOptional.create().parse({}), TypeError)
assert.throws(() => requiredOptional.create().parse({value:undefined}), TypeError)
const arbitrary = info.methods['metadata/json'].input().create()
const arbitraryValue = {deep:[null, true, 4.5, {extra:'retained'}]}
assert.equal(arbitrary.parse({value:arbitraryValue}).value, arbitraryValue)
for (const value of [Infinity, NaN, {deep:[{extra:Infinity}]}, JSON.parse('{"deep":[1e400]}')])
  assert.throws(() => arbitrary.parse({value}), TypeError)
assert.throws(() => normal.create().parse({value:{...closed.value, extra:{number:Infinity}}}), TypeError)
assert.throws(() => info.methods['metadata/lookup'].input().create().parse({}), TypeError)
assert.equal(info.methods['metadata/lookup'].parameters[0].source, 'lookup')
assert.deepEqual(info.methods['metadata/lookup'].scope, {context:'document', wire:'documentId'})
for (const [endpoint, value] of [
  ['recursive', {name:'root', next:{name:'child', next:null}}],
  ['dictionary', {one:'first', two:'second'}],
  ['overlap', 'other'],
  ['tuple', ['item']],
]) {
  const method = info.methods['metadata/' + endpoint]
  const input = method.input()
  assert.equal(input.jsonSchema, undefined, endpoint + ' projection must refuse without weakening constraints')
  assert.equal(typeof input.jsonSchemaError, 'string')
  assert.equal(input.create().parse({value}).value, value)
  assert.equal(method.result.create().parse(value), value)
}
assert.throws(() => info.methods['metadata/overlap'].input().create().parse({value:'selected'}), TypeError)
assert.equal(info.methods['metadata/dictionary'].input({strict:true}).create().parse({value:{arbitrary:'allowed'}}).value.arbitrary, 'allowed')
assert.throws(() => info.methods['metadata/recursive'].input({strict:true}).create().parse({value:{name:'root', next:{name:'child', next:null, extra:true}}}), TypeError)
for (const endpoint of ['composed', 'ref-siblings']) {
  const method = info.methods['metadata/' + endpoint]
  const input = method.input({strict:true}).create()
  const value = {a:'first', b:'second'}
  assert.equal(input.parse({value}).value, value)
  assert.throws(() => input.parse({value:{...value, c:'extra'}}), TypeError)
}
const composedUnion = info.methods['metadata/composed-union'].input({strict:true}).create()
assert.deepEqual(composedUnion.parse({value:{a:'a', b:'b'}}), {value:{a:'a', b:'b'}})
assert.deepEqual(composedUnion.parse({value:{a:'a', c:'c'}}), {value:{a:'a', c:'c'}})
assert.throws(() => composedUnion.parse({value:{a:'a', b:'b', c:'c'}}), TypeError)
assert.equal(info.methods['metadata/recursive'].result.description, 'Recursive result documentation.')
assert.equal(info.methods['metadata/recursive'].result.jsonSchema, undefined)
for (const endpoint of ['optional-result', 'void']) {
  const result = info.methods['metadata/' + endpoint].result
  assert.equal(result.jsonSchema, undefined)
  assert.match(result.jsonSchemaError, /absent/)
  assert.equal(result.create().parse(undefined), undefined)
}
const prototypeField = info.methods['metadata/prototype-field'].input({strict:true})
const prototypeValue = JSON.parse('{"__proto__":"declared"}')
assert.equal(prototypeField.create().parse({value:prototypeValue}).value, prototypeValue)
assert.equal(Object.hasOwn(prototypeField.jsonSchema.properties.value.properties, '__proto__'), true)
assert.equal(prototypeField.jsonSchema.properties.value.properties.__proto__.type, 'string')
assert.throws(() => prototypeField.create().parse({value:{...prototypeValue, unknown:true}}), TypeError)
const bytes = new Uint8Array([1, 2, 3])
assert.equal(resultCodecs['metadata/binary'].parse(bytes), bytes)
assert.equal(info.methods['metadata/binary'].result.create().parse(bytes), bytes)
assert.equal(info.methods['metadata/binary'].result.jsonSchema, undefined)
assert.match(info.methods['metadata/binary'].result.jsonSchemaError, /binary/)
assert.equal(info.methods['metadata/binary'].input().create().parse({value:'AQID'}).value, 'AQID')
assert.throws(() => info.methods['metadata/binary'].input().create().parse({value:bytes}), TypeError)

// Invocation reads the current namespace, passes the original named object and retains all return handles.
let namespace
const remote = {get(key) { assert.equal(key, 'remote.metadata'); return namespace }}
const signal = new AbortController().signal
const task = Promise.resolve({ok:true, value:'borrowed'})
namespace = {echo(value, cancellation) { assert.equal(this, namespace); assert.equal(value, named); assert.equal(cancellation, signal); return task }}
assert.equal(echo.invoke(remote, named, signal), task)
const handle = {send() {}, end() {}, dispose() {}, async *[Symbol.asyncIterator]() { yield 'item' }}
namespace = {duplex(value, cancellation) { assert.equal(value, named); assert.equal(cancellation, signal); return handle }}
assert.equal(info.methods['metadata/duplex'].invoke(remote, named, signal), handle)
const replacement = Promise.resolve({ok:true, value:'replacement'})
namespace = {echo() { return replacement }}
assert.equal(echo.invoke(remote, named), replacement)
assert.equal(echo.invoke({metadata:namespace}, named), replacement)
namespace = undefined
assert.throws(() => echo.invoke(remote, named), /not mounted/)

// Drive the actual generated Connection adapter and mount into a borrowed Context.
const calls = []
const connection = {active:true, call(endpoint, args) { calls.push([endpoint, args]); return Promise.resolve({ok:true, value:closed.value}) }}
const generatedClient = createRemote(connection)
const generatedTask = generatedClient['metadata/echo'](closed)
assert.deepEqual(await generatedTask, {ok:true, value:closed.value})
assert.equal(calls[0][1], closed)
await generatedClient.dispose()
const values = new Map()
let cleanup
const ctx = {root:{get:key => values.get(key), provide(key, value) {values.set(key, value); return () => values.delete(key)}},
  get:key => values.get(key), effect(setup) {const ready = setup().then(value => {cleanup = value}); const dispose = async () => {await ready; await cleanup()}; dispose.then = ready.then.bind(ready); return dispose}}
const mounted = await mountRemote(ctx, connection)
assert.deepEqual(await echo.invoke(ctx, closed), {ok:true, value:closed.value})
await mounted.dispose()
assert.throws(() => echo.invoke(ctx, closed), /not mounted/)

// Lazy channels are observable independently, and failed factories retry instead of poisoning their cache.
let creates = 0
let projects = 0
let fail = true
let failProjection = true
const codec = {typeSymbol:'string', create() {creates++; if (fail) {fail = false; throw new Error('factory retry')} return {parse:value => value}},
  project() {projects++; if (failProjection) {failProjection = false; throw new Error('projection retry')} return {jsonSchema:{type:'string'}}}}
const lazy = createRemoteInfo({package:'lazy', methods:{'lazy/read':{
  service:'lazy', invocation:{kind:'direct'}, parameters:[{name:'value', source:'json', codec, strictCodec:codec}],
  result:{...codec, description:'Lazy result.'},
}}})
assert.equal(lazy.package, 'lazy')
const lazyMethod = lazy.methods['lazy/read']
assert.equal(lazy.descriptors[0], lazyMethod)
const lazyInput = lazyMethod.input()
assert.equal(creates, 0)
assert.equal(projects, 0)
assert.throws(() => lazyInput.create(), /factory retry/)
assert.equal(creates, 1)
assert.equal(projects, 0)
assert.equal(lazyInput.create(), lazyInput.create())
assert.equal(creates, 2)
assert.throws(() => lazyInput.jsonSchema, /projection retry/)
assert.equal(projects, 1)
assert.equal(lazyInput.jsonSchema.properties.value.type, 'string')
assert.equal(projects, 2)
assert.equal(lazyInput.jsonSchema, lazyInput.jsonSchema)
assert.equal(creates, 2)
assert.equal(lazyMethod.result.description, 'Lazy result.')
assert.equal(creates, 2)
assert.equal(projects, 2)
assert.equal(lazyMethod.result.jsonSchema.description, 'Lazy result.')
assert.equal(projects, 3)
assert.equal(creates, 2)
assert.equal(lazyMethod.result.create(), lazyMethod.result.create())
assert.equal(creates, 3)
console.log('PASS generated Remote metadata: installed strict TypeScript, independent lazy channels, strict input, named invocation, Context exclusion and handle identity.')
