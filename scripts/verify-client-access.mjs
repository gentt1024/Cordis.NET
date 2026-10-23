import { createRequire } from 'node:module'
import { execFileSync } from 'node:child_process'
import { mkdtemp, mkdir, cp, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-client-access-'))
const runtime = process.argv[2] === undefined ? resolve(directory, 'runtime') : resolve(process.argv[2])
if (process.argv[2] === undefined) execFileSync(process.execPath, [resolve(root, 'scripts/build-client-modules.mjs'), runtime], { stdio: 'inherit' })
// An author can use this default SDK request without installing or declaring an external supplier.
const author = resolve(directory, 'author')
const plugin = resolve(directory, 'plugin')
await mkdir(author)
await writeFile(resolve(author, 'package.json'), JSON.stringify({ name: 'access-consumer', dsh: { client: { platform: 'web' } } }))
await writeFile(resolve(author, 'client.ts'), `import { ClientAccessState, mountAccess } from '@cordis-net/client-modules/access'
export function apply(ctx) { ctx.provide('access-class', ClientAccessState); ctx.provide('access-mount', mountAccess) }
`)
execFileSync(process.execPath, [resolve(root, 'clients/tools/build.mjs'), author, plugin], { stdio: 'inherit' })
await mkdir(resolve(directory, 'node_modules/@cordis-net'), { recursive: true })
await cp(runtime, resolve(directory, 'node_modules/@cordis-net/client-modules'), { recursive: true })
await writeFile(resolve(directory, 'package.json'), '{"private":true,"type":"module"}\n')
await writeFile(resolve(directory, 'consumer.ts'), `import { Context } from '@cordis-net/client-modules'
import { ClientAccessState, mountAccess, parseInjectedBaseline, type Baseline, type ClientRemoteAccess } from '@cordis-net/client-modules/access'
import { TypertConnection } from '@cordis-net/client-modules/management'
const context = new Context()
const baseline: Baseline = { type: 'baseline', owner: false, grants: [{ resource: { kind: 'project', id: 'one' }, actions: ['project/read'] }] }
const access: ClientRemoteAccess = new ClientAccessState(parseInjectedBaseline(baseline))
const snapshot: boolean = access.source('project', 'one', 'project/read').getSnapshot()
export async function attach(connection: TypertConnection) {
  const mounted = await mountAccess({ context, connection, initial: baseline })
  const answer: boolean = context.remoteAccess.global('settings/read').getSnapshot()
  mounted.reconnect()
  await mounted.dispose()
  return snapshot && answer
}
// @ts-expect-error owner=false requires its complete grant snapshot
const invalid: Baseline = { type: 'baseline', owner: false }
`)
const requireTools = createRequire(resolve(root, 'clients/modules/package.json'))
const ts = requireTools('typescript')
const program = ts.createProgram([resolve(directory, 'consumer.ts')], {
  noEmit: true, strict: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts', 'lib.esnext.disposable.d.ts'], types: [],
})
const diagnostics = ts.getPreEmitDiagnostics(program)
if (diagnostics.length) throw new Error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
  getCurrentDirectory: () => directory, getCanonicalFileName: name => name, getNewLine: () => '\n',
}))
await writeFile(resolve(directory, 'consumer.mjs'), String.raw`
import assert from 'node:assert/strict'
import { setTimeout as delay } from 'node:timers/promises'
import { readFile } from 'node:fs/promises'
import { runInThisContext } from 'node:vm'
import { Context, bootClientModules } from '@cordis-net/client-modules'
import { ClientAccessState, mountAccess, parseInjectedBaseline } from '@cordis-net/client-modules/access'
import { TypertConnection, mountRemote as mountManagement } from '@cordis-net/client-modules/management'
const read = 'project/read', write = 'project/write'
const resource = { kind: 'project', id: 'one' }
const baseline = (actions = [read]) => ({ type: 'baseline', owner: false, grants: [{ resource, actions }] })
const state = new ClientAccessState()
const source = state.source('project', 'one', read)
assert.equal(source.getSnapshot(), false)
assert.equal(state.global(read).getSnapshot(), false)
assert.equal(source, state.source('project', 'one', read))
assert.notEqual(state.source('a\u0000b', 'c', read), state.source('a', 'b\u0000c', read))
assert.notEqual(state.global(read), state.source('\u0000global', '', read))
state.update({ type: 'upsert', grant: { resource, actions: [read] } })
assert.equal(source.getSnapshot(), false, 'Closed state ignores deltas')
let notifications = 0
const stop = source.subscribe(() => {
  notifications++
  assert.equal(source.getSnapshot(), state.global(read).getSnapshot(), 'A baseline publishes every grant atomically')
})
state.replace({ type: 'baseline', owner: false, grants: [{ resource, actions: [read] }, { resource: {kind:'global'}, actions:[read] }] })
assert.equal(notifications, 1)
stop()
state.update({ type: 'upsert', grant: { resource, actions: [write] } })
assert.equal(source.getSnapshot(), false)
assert.equal(state.source('project', 'one', write).getSnapshot(), true)
state.update({ type: 'remove', resource })
assert.equal(state.source('project', 'one', write).getSnapshot(), false)
state.update({ type: 'remove', resource: {kind:'global'} })
assert.equal(state.global(read).getSnapshot(), false)
state.replace(baseline())
assert.throws(() => state.replace({ type: 'baseline', owner:false, grants:[{resource,actions:[]}, {resource,actions:[1]}] }))
assert.equal(source.getSnapshot(), true, 'Malformed replacement never publishes its valid prefix')
assert.equal(parseInjectedBaseline(undefined), undefined)
assert.throws(() => parseInjectedBaseline({type:'remove',resource}))
assert.throws(() => parseInjectedBaseline({type:'baseline',owner:true,grants:[]}))
state.unavailable()
assert.equal(source.getSnapshot(), false)
state.replace({type:'baseline',owner:true})
state.update({type:'remove',resource})
state.unavailable()
assert.equal(source.getSnapshot(), true)
assert.equal(state.global('unknown/action').getSnapshot(), true)
console.log('PASS installed access state: closed/owner/grants, stable sources, atomic validation, upsert/remove and terminal failure')

globalThis.location = new URL('http://localhost/')
globalThis.document = {querySelectorAll() {return []}}
const artifact = await readFile(new URL('./plugin/client.js', import.meta.url), 'utf8')
const manifest = JSON.parse(await readFile(new URL('./plugin/package.json', import.meta.url), 'utf8'))
assert.equal(manifest.dsh.client.external,undefined,'Author does not explicitly list an external request')
assert.match(artifact,/require\(["']@cordis-net\/client-modules\/access["']\)/,'Builder leaves the default access dependency external')
const rev = 'access-static'
const graph = {rev, entries:[{id:manifest.name,url:'/access.js?rev='+rev,rev}],
  batches:[{phase:'application',url:'/access.js?rev='+rev,rev,entries:[manifest.name]}]}
const modules = await bootClientModules({graph, async loadBundle() {
  runInThisContext(artifact,{filename:'independent-access-author.js'})
}})
assert.equal(modules.context.get('access-class'), ClientAccessState, 'Actual author factory preserves the installed state class identity')
assert.equal(modules.context.get('access-mount'), mountAccess, 'Actual author factory preserves the installed lifecycle function identity')
await modules.dispose()
console.log('PASS actual author default access import remains external and uses the installed static module identity')

async function until(predicate, message) {
  const deadline = Date.now() + 4000
  while (!predicate()) { assert.ok(Date.now() < deadline, message); await delay(2) }
}
const recovery = {backoffBaseMs:5,backoffMaxMs:5,generationReadyWarnMs:1000,generationReadyTimeoutMs:2000}
function transport(context) {
  const streams = []
  const cancellationOrder = []
  class Socket extends EventTarget {
    calls = new Map()
    closed = false
    constructor(url) {
      super()
      assert.equal(url.pathname, '/remote/remote.mux')
      queueMicrotask(() => this.dispatchEvent(new Event('open')))
    }
    send(text) {
      const frame = JSON.parse(text)
      if (frame.type === 'cancel') {
        const call = this.calls.get(frame.streamId)
        if (call) {
          cancellationOrder.push(context.get('remote.remoteAccess', false) !== undefined)
          call.cancelled = true
        }
        this.calls.delete(frame.streamId)
        return
      }
      assert.equal(frame.type, 'open')
      assert.equal(frame.endpoint, 'remoteAccess/follow')
      assert.deepEqual(frame.payload.args, {})
      const socket = this
      const stream = {
        cancelled:false,
        send(value) {socket.dispatchEvent(new MessageEvent('message', {data:JSON.stringify({type:'item',streamId:frame.streamId,value})}))},
        end() {socket.dispatchEvent(new MessageEvent('message', {data:JSON.stringify({type:'end',streamId:frame.streamId})}))},
        error() {socket.dispatchEvent(new MessageEvent('message', {data:JSON.stringify({type:'error',streamId:frame.streamId,error:{code:'remote-access/denied',message:'revoked',details:{reason:'no-allow'}}})}))},
        lose() {socket.close()},
      }
      this.calls.set(frame.streamId,stream)
      streams.push(stream)
    }
    close() {
      if (this.closed) return
      this.closed = true
      this.dispatchEvent(new Event('close'))
    }
  }
  const connection = new TypertConnection('http://localhost/remote', async (_url, init) => {
    const request = JSON.parse(init.body)
    return Response.json({type:'server-response',rpcId:request.rpcId,result:{ok:false,error:{code:'fixture/reached',message:'reached',details:{}}}})
  }, url => new Socket(url))
  return {connection,streams,cancellationOrder}
}
const context = new Context()
const carrier = transport(context)
const errors = []
const management = await mountManagement(context, carrier.connection)
const managementNamespace = context.get('remote.pluginManager')
assert.ok(managementNamespace)
let mount = await mountAccess({context,connection:carrier.connection,recovery,failed:error=>errors.push(error)})
const answer = mount.source('project','one',read)
assert.equal(context.remoteAccess.source('project','one',read),answer)
await assert.rejects(mountAccess({context,connection:carrier.connection,recovery}), /already mounted/)
assert.equal(context.remoteAccess.source('project','one',read),answer,'Rejected duplicate mount leaves the existing service intact')
await until(()=>carrier.streams.length===1,'Follow opened')
carrier.streams[0].send(baseline())
await until(()=>answer.getSnapshot(),'Initial baseline accepted')
carrier.streams[0].lose()
await until(()=>carrier.streams.length===2,'Carrier loss automatically retries')
assert.equal(answer.getSnapshot(),true,'Reconnect retains the last accepted snapshot until its baseline arrives')
carrier.streams[1].send({type:'baseline',owner:false,grants:[]})
await until(()=>!answer.getSnapshot(),'Reconnect atomically replaces the last generation')
carrier.streams[0].send(baseline())
await delay(10)
assert.equal(answer.getSnapshot(),false,'Old physical generation cannot publish after replacement')
carrier.streams[1].send({type:'upsert',grant:{resource,actions:[read]}})
await until(()=>answer.getSnapshot(),'Current delta accepted')
mount.reconnect()
await until(()=>carrier.streams.length===3,'Manual reconnect opens a fresh generation')
assert.equal(carrier.streams[1].cancelled,true)
assert.equal(answer.getSnapshot(),true)
carrier.streams[2].send(baseline())
await delay(5)
carrier.streams[2].error()
await until(()=>errors.length===1,'Terminal business failure reported')
assert.equal(answer.getSnapshot(),false,'Guest terminal failure closes access')
const terminalCount = carrier.streams.length
mount.reconnect()
await delay(20)
assert.equal(carrier.streams.length,terminalCount,'Terminal failure cannot be retried by the disposed controller')
await mount.dispose()
assert.equal(context.get('remote.remoteAccess'),undefined)
assert.equal(context.get('remoteAccess'),undefined)
assert.deepEqual(Object.keys(context.get('remote.pluginManager')),Object.keys(managementNamespace))
assert.equal((await context.get('remote.pluginManager').listPlugins({})).error.code,'fixture/reached','Other generated namespace still invokes through the borrowed connection')
assert.ok(carrier.cancellationOrder.every(Boolean),'Follow cancels before its namespace is withdrawn')

mount = await mountAccess({context,connection:carrier.connection,recovery,failed:error=>errors.push(error)})
assert.equal(mount.source('project','one',read).getSnapshot(),false,'Remount has no implicit retained baseline')
await until(()=>carrier.streams.length===terminalCount+1,'Remount opens a new follow')
carrier.streams.at(-1).send({type:'baseline',owner:true})
await until(()=>mount.global(read).getSnapshot(),'Owner baseline accepted')
carrier.streams.at(-1).error()
await until(()=>errors.length===2,'Owner terminal error reported')
assert.equal(mount.global(read).getSnapshot(),true,'Owner answers survive terminal failure')
await mount.dispose()
console.log('PASS real generated namespace + TypertConnection + ConnectionController recovery, generation fencing and fresh remount')

for (const [name, frames] of [
  ['delta-before-baseline',[{type:'upsert',grant:{resource,actions:[read]}}]],
  ['duplicate-baseline',[baseline(),baseline()]],
  ['malformed-delta',[baseline(),{type:'upsert',grant:{resource,actions:[1]}}]],
  ['malformed-baseline',[{type:'baseline',owner:false,grants:[{resource,actions:[read]},{resource:{kind:'project'},actions:[read]}]}]],
]) {
  const before = errors.length
  const count = carrier.streams.length
  mount = await mountAccess({context,connection:carrier.connection,recovery,failed:error=>errors.push(error)})
  await until(()=>carrier.streams.length===count+1,name+' opened')
  for (const frame of frames) carrier.streams.at(-1).send(frame)
  await until(()=>errors.length===before+1,name+' terminal rejection')
  assert.equal(mount.source('project','one',read).getSnapshot(),false,name+' closes guest answers')
  await mount.dispose()
}
const finalCount = carrier.streams.length
mount = await mountAccess({context,connection:carrier.connection,recovery,initial:baseline(),failed:error=>errors.push(error)})
assert.equal(mount.source('project','one',read).getSnapshot(),true,'Explicit baseline is available before carrier readiness')
await until(()=>carrier.streams.length===finalCount+1,'Final mounted follow opened')
await context.fiber.dispose()
assert.equal(context.get('remote.remoteAccess'),undefined)
assert.ok(carrier.cancellationOrder.every(Boolean),'Fiber cleanup also cancels before namespace withdrawal')
await mount.dispose()
await management.dispose()
await carrier.connection.dispose()
console.log('PASS stream protocol errors fail closed; mount and Fiber cleanup own follow cancellation before namespace withdrawal')
`)
execFileSync(process.execPath, [resolve(directory, 'consumer.mjs')], { cwd: directory, stdio: 'inherit' })
console.log('PASS independent strict TypeScript consumer and installed-package access acceptance')
