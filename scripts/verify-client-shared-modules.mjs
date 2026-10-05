import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { runInThisContext } from 'node:vm'
import { verifySharedModules } from '../clients/modules/examples/shared/check.mjs'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-shared-modules-'))
const runtime = resolve(process.argv[2] ?? directory, process.argv[2] ? '' : 'runtime')
const plugin = resolve(directory, 'plugin')
execFileSync(process.execPath, [resolve(root, 'scripts/build-client-modules.mjs'), runtime,
  resolve(root, 'clients/modules/examples/shared'), plugin], { stdio: 'inherit' })
const bytes = await readFile(resolve(plugin, 'client.js'))
const revision = createHash('sha256').update(bytes).digest('hex')
const url = 'http://localhost/client/shared?rev=' + revision
globalThis.location = new URL('http://localhost/')
// No browser emulation: the injected transport executes the real classic factory artifact in Node.
globalThis.document = { querySelectorAll() { return [] } }
const { bootClientModules } = await import(pathToFileURL(resolve(runtime, 'client.mjs')).href)
const graph = { rev: revision, entries: [{ id: 'example-shared-consumer', url, rev: revision,
  external: ['@example/shell', '@example/shell/tools'] }],
  batches: [{ phase: 'application', url, rev: revision, entries: ['example-shared-consumer'] }] }
const requests = []
const loadBundle = async request => {
  requests.push(request)
  assert.equal(request, url)
  runInThisContext(bytes.toString('utf8'), { filename: 'independent-client.js' })
}
console.log(await verifySharedModules(bootClientModules, graph, loadBundle))
assert.ok(requests.length > 0, 'The public bootstrap ignored the custom transport')
console.log(await verifySharedModules(bootClientModules, graph, loadBundle, true))

// A declared package root must not externalize an undeclared subpath. This ordinary
// dependency is bundled, so the consumer succeeds without a shell supplier for that subpath.
const author = resolve(directory, 'embedded-author')
const dependency = resolve(author, 'node_modules/@example/shell')
await mkdir(dependency, { recursive: true })
await writeFile(resolve(dependency, 'package.json'), JSON.stringify({ name: '@example/shell', type: 'module',
  exports: { './embedded': './embedded.js' } }))
await writeFile(resolve(dependency, 'embedded.js'), 'export const value = 7\n')
await writeFile(resolve(author, 'package.json'), JSON.stringify({ name: 'example-embedded-consumer',
  dsh: { client: { platform: 'web', external: ['@example/shell'] } } }))
await writeFile(resolve(author, 'client.ts'), `import { value } from '@example/shell/embedded'
export function apply(ctx) { ctx.provide('embedded-value', value) }
`)
const embeddedPlugin = resolve(directory, 'embedded-plugin')
execFileSync(process.execPath, [resolve(root, 'scripts/build-client-modules.mjs'), runtime, author, embeddedPlugin], { stdio: 'inherit' })
const embeddedBytes = await readFile(resolve(embeddedPlugin, 'client.js'))
const embeddedRevision = createHash('sha256').update(embeddedBytes).digest('hex')
const embeddedUrl = 'http://localhost/client/embedded?rev=' + embeddedRevision
const embeddedClient = await bootClientModules({
  graph: { rev: embeddedRevision, entries: [{ id: 'example-embedded-consumer', url: embeddedUrl,
    rev: embeddedRevision, external: ['@example/shell'] }],
    batches: [{ phase: 'application', url: embeddedUrl, rev: embeddedRevision, entries: ['example-embedded-consumer'] }] },
  staticModules: { '@example/shell': {} },
  async loadBundle(request) {
    assert.equal(request, embeddedUrl)
    runInThisContext(embeddedBytes.toString('utf8'), { filename: 'independent-embedded-client.js' })
  },
})
try {
  assert.deepEqual(embeddedClient.state.getSnapshot().failures, [])
  assert.equal(embeddedClient.context.get('embedded-value'), 7)
  console.log('PASS undeclared subpath stays an ordinary bundled dependency')
} finally { await embeddedClient.dispose() }

// Failure while obtaining shell inputs must not reserve the page's one bootstrap facade.
const emptyGraph = { rev: 'empty', entries: [], batches: [] }
await assert.rejects(bootClientModules({ graph: emptyGraph, staticModules: {
  get unavailable() { throw new Error('Shell supplier unavailable') },
} }), /Shell supplier unavailable/)
assert.equal(globalThis.__ModuleLoader__, undefined, 'Failed bootstrap retained its global facade')
const retry = await bootClientModules({ graph: emptyGraph })
await retry.dispose()
console.log('PASS failed shell input leaves the page available for bootstrap retry')
