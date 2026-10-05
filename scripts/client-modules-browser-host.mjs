import { createServer } from 'node:http'
import { createHash } from 'node:crypto'
import { execFileSync } from 'node:child_process'
import { mkdir, readFile, copyFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// Browser-runtime verification carrier. Native catalog/Host integration has separate evidence.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const output = resolve(process.argv[2] ?? resolve(root, 'artifacts/client-modules-browser'))
const runtime = resolve(output, 'runtime')
const build = resolve(root, 'scripts/build-client-modules.mjs')
const modules = new Map()
for (const directory of ['provider', 'provider-next', 'panel', 'panel-broken', 'shared']) {
  const target = resolve(output, directory)
  execFileSync(process.execPath, [build, runtime, resolve(root, 'clients/modules/examples', directory), target], { stdio: 'inherit' })
  const manifest = JSON.parse(await readFile(resolve(target, 'package.json'), 'utf8'))
  const bytes = await readFile(resolve(target, 'client.js'))
  const rev = createHash('sha256').update(bytes).digest('hex')
  modules.set(directory, { id: manifest.name, inject: manifest.dsh.client.inject ?? [],
    external: manifest.dsh.client.external, rev, bytes,
    url: '/client/artifacts/' + encodeURIComponent(manifest.name) + '?rev=' + rev })
}
await mkdir(output, { recursive: true })
await copyFile(resolve(root, 'clients/modules/examples/index.html'), resolve(output, 'index.html'))
let roster = ['provider', 'panel']
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost')
    if (url.pathname === '/client/graph' || url.pathname === '/client/shared-graph') {
      const action = url.searchParams.get('action')
      if (action === 'load' || action === 'reconnect') roster = ['provider', 'panel']
      else if (action === 'update') roster = ['provider-next', 'panel']
      else if (action === 'withdraw') roster = [roster[0]]
      else if (action === 'broken') roster = [roster[0], 'panel-broken']
      const entries = (url.pathname === '/client/shared-graph' ? ['shared'] : roster).map(name => {
        const { bytes, ...entry } = modules.get(name)
        return entry
      })
      const batches = entries.map(row => ({ phase: 'application', url: row.url, rev: row.rev, entries: [row.id] }))
      const rev = createHash('sha256').update(JSON.stringify({ entries, batches })).digest('hex')
      response.writeHead(200, { 'content-type': 'application/json', 'cache-control': 'no-store' })
      response.end(JSON.stringify({ rev, entries, batches }))
      return
    }
    if (url.pathname.startsWith('/client/artifacts/')) {
      const id = decodeURIComponent(url.pathname.slice('/client/artifacts/'.length))
      const artifact = [...roster, 'shared'].map(name => modules.get(name)).find(row => row.id === id && row.rev === url.searchParams.get('rev'))
      if (artifact === undefined) { response.writeHead(404); response.end(); return }
      response.writeHead(200, { 'content-type': 'text/javascript', 'etag': '"' + artifact.rev + '"',
        'content-length': artifact.bytes.length, 'cache-control': 'public, max-age=31536000, immutable' })
      response.end(request.method === 'HEAD' ? undefined : artifact.bytes)
      return
    }
    const file = url.pathname === '/' ? resolve(output, 'index.html')
      : url.pathname === '/shared' ? resolve(root, 'clients/modules/examples/shared/index.html')
      : url.pathname === '/shared-check.mjs' ? resolve(root, 'clients/modules/examples/shared/check.mjs')
      : url.pathname === '/client-runtime/client.mjs' ? resolve(runtime, 'client.mjs') : undefined
    if (file === undefined) { response.writeHead(404); response.end(); return }
    response.writeHead(200, { 'content-type': ['/', '/shared'].includes(url.pathname) ? 'text/html' : 'text/javascript' })
    response.end(await readFile(file))
  } catch (error) { response.writeHead(500); response.end(String(error)) }
})
server.listen(0, '127.0.0.1', () => {
  console.log(JSON.stringify({ origin: 'http://127.0.0.1:' + server.address().port,
    evidence: 'Browser carrier ready; browser interactions have not run' }))
})
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => { server.close(); server.closeAllConnections() })
