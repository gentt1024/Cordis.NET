import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { resolve } from 'node:path'
import { pathToFileURL } from 'node:url'
import { runInNewContext, runInThisContext } from 'node:vm'

// Boot and inspect the same published factory with separate shell bindings.
// Only the live page is allowed to apply the plugin or modify page services.
const server = createServer((_request, response) => {
  response.setHeader('content-type', 'text/javascript')
  response.end(`globalThis.__ModuleLoader__.load({id:'panel',factory(require) {
    const probe = require('@fixture/inspection')
    probe.factories++
    const plugin = { apply(ctx) { probe.starts++; ctx.provide('panel', { live: true }) } }
    if (probe.config) Object.defineProperty(plugin, 'Config', { get: probe.config })
    return probe.namespace = { default: plugin }
  }})`)
})
await new Promise(done => server.listen(0, '127.0.0.1', done))
const origin = `http://127.0.0.1:${server.address().port}`
globalThis.location = new URL(origin)
globalThis.document = { querySelectorAll() { return [] } }
const runtime = resolve(process.argv[2] ?? 'clients/modules/lib')
const entry = { id: 'panel', url: '/panel.js?rev=inspect', rev: 'inspect', external: ['@fixture/inspection'] }
const graph = { rev: entry.rev, entries: [entry],
  batches: [{ phase: 'application', url: entry.url, rev: entry.rev, entries: [entry.id] }] }
async function readFactory(url) {
  const response = await fetch(new URL(url, origin))
  assert.equal(response.status, 200)
  return response.text()
}

let modules
try {
  const { bootClientModules, inspectClientModule } = await import(pathToFileURL(resolve(runtime, 'client.mjs')).href)
  modules = await bootClientModules({ graph, staticModules: { '@fixture/inspection': { factories: 0, starts: 0 } },
    async loadBundle(url) { runInThisContext(await readFactory(url), { filename: url }) } })
  const livePanel = modules.context.get('panel')
  assert.equal(livePanel.live, true)
  const liveFacade = globalThis.__ModuleLoader__
  const liveRegister = liveFacade.load

  const probe = { factories: 0, starts: 0 }
  const options = { graph, baseUrl: origin, specifier: 'panel/client', staticModules: { '@fixture/inspection': probe },
    async loadBundle(url, register) {
      assert.equal(globalThis.__ModuleLoader__, liveFacade)
      assert.equal(liveFacade.load, liveRegister)
      runInNewContext(await readFactory(url), { __ModuleLoader__: { load: register } }, { filename: url })
    } }
  const schema = { '~standard': { version: 1, vendor: 'example', validate() { throw new Error('validation ran during inspection') } } }
  probe.config = () => schema
  const found = await inspectClientModule(options)
  assert.equal(found.exports, probe.namespace)
  assert.equal(found.config.status, 'schema')
  assert.equal(found.config.schema, schema)
  delete probe.config
  assert.equal((await inspectClientModule(options)).config.status, 'absent')
  probe.config = () => ({ description: 'data without a Standard Schema declaration' })
  const unsupported = (await inspectClientModule(options)).config
  assert.equal(unsupported.status, 'unsupported')
  assert.ok(typeof unsupported.diagnostic === 'string' && unsupported.diagnostic.length > 0)
  probe.config = () => { throw new Error('inspection Config getter failed') }
  await assert.rejects(inspectClientModule(options), /inspection Config getter failed/)
  await assert.rejects(inspectClientModule({ ...options, specifier: 'missing/client' }))
  assert.equal(probe.factories, 4)
  assert.equal(probe.starts, 0)
  assert.equal(globalThis.__ModuleLoader__, liveFacade)
  assert.equal(liveFacade.load, liveRegister)
  assert.equal(modules.context.get('panel'), livePanel)
} finally {
  try { await modules?.dispose() } finally { await new Promise(done => server.close(done)) }
}
assert.equal(globalThis.__ModuleLoader__, undefined)
console.log('PASS client Config declaration inspection preserves namespace identity, live page registration and services')
