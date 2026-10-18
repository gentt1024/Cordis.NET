import { createServer } from 'node:http'
import { createHash } from 'node:crypto'
import { createConnection } from 'node:net'
import { createInterface } from 'node:readline'
import { once } from 'node:events'
import { spawn, execFileSync } from 'node:child_process'
import { mkdir, readFile, copyFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

// Browser module graph fixture; notifications use the production ASP.NET mux.
// Native catalog/package integration retains its separate evidence.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const output = resolve(process.argv[2] ?? resolve(root, 'artifacts/client-modules-browser'))
const runtime = resolve(output, 'runtime')
execFileSync(process.execPath, [resolve(root, 'scripts/build-client-modules.mjs'), runtime], { stdio: 'inherit' })
const build = resolve(root, 'clients/tools/build.mjs')
const modules = new Map()
for (const directory of ['provider', 'provider-next', 'panel', 'panel-broken', 'shared']) {
  const target = resolve(output, directory)
  execFileSync(process.execPath, [build, resolve(root, 'clients/modules/examples', directory), target], { stdio: 'inherit' })
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
let managementWithdrawn = false
let stateFailures = 0
const streams = new Set()
function sharedGraph() {
  const { bytes, ...entry } = modules.get('shared')
  return managementWithdrawn ? { rev: 'empty', entries: [], batches: [] }
    : { rev: entry.rev, entries: [entry], batches: [{ phase: 'application', url: entry.url, rev: entry.rev, entries: [entry.id] }] }
}
const hostDirectory = resolve(output, 'host')
await mkdir(hostDirectory, { recursive: true })
await copyFile(resolve(root, 'tests/fixtures/ClientModuleBrowser/Host.cs'), resolve(hostDirectory, 'Program.cs'))
const project = resolve(hostDirectory, 'BrowserHost.csproj')
await writeFile(project, `<Project Sdk="Microsoft.NET.Sdk.Web"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><IsPackable>false</IsPackable></PropertyGroup><ItemGroup><ProjectReference Include="${resolve(root, 'src/Cordis.AspNetCore/Cordis.AspNetCore.csproj')}" /></ItemGroup></Project>`)
const dotnet = process.env.DOTNET_HOST_PATH ?? 'dotnet'
execFileSync(dotnet, ['build', project, '-c', 'Release'], { stdio: 'inherit' })
const host = spawn(dotnet, [resolve(hostDirectory, 'bin/Release/net10.0/BrowserHost.dll')], { stdio: ['ignore', 'pipe', 'inherit'], windowsHide: true })
const hostLines = createInterface({ input: host.stdout })
const hostAddress = await new Promise((accept, reject) => {
  hostLines.on('line', line => { if (line.startsWith('READY ')) accept(new URL(line.slice(6))) })
  host.once('exit', code => reject(new Error('Browser notification Host exited: ' + code)))
})
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost')
    if (url.pathname === '/cordis/state') {
      if (stateFailures > 0) { stateFailures--; response.writeHead(503); response.end('temporary unavailable'); return }
      response.writeHead(200, { 'content-type': 'application/json' })
      response.end(JSON.stringify({ protocol: 1, generation: 'browser-host', restartRequired: false,
        selectedBundles: [], loadedBundles: [] }))
      return
    }
    if (url.pathname === '/cordis/client/graph') {
      response.writeHead(200, { 'content-type': 'application/json' })
      response.end(JSON.stringify(sharedGraph()))
      return
    }
    if (url.pathname.startsWith('/test/') && request.method === 'POST') {
      if (url.pathname === '/test/loss') for (const stream of streams) stream.destroy()
      else if (url.pathname === '/test/fail-state') stateFailures = 1
      else if (url.pathname === '/test/withdraw') { managementWithdrawn = true; await fetch(new URL('/changed', hostAddress), { method: 'POST' }) }
      else if (url.pathname === '/test/reset') { managementWithdrawn = false; stateFailures = 0 }
      else { response.writeHead(404); response.end(); return }
      response.end('ok')
      return
    }
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
      : url.pathname === '/recovery' ? resolve(root, 'clients/modules/examples/recovery.html')
      : url.pathname === '/shared' ? resolve(root, 'clients/modules/examples/shared/index.html')
      : url.pathname === '/shared-check.mjs' ? resolve(root, 'clients/modules/examples/shared/check.mjs')
      : url.pathname === '/client-runtime/client.mjs' ? resolve(runtime, 'client.mjs')
      : url.pathname === '/client-runtime/cordis-remote.mjs' ? resolve(runtime, 'cordis-remote.mjs')
      : url.pathname === '/client-runtime/events.mjs' ? resolve(runtime, 'events.mjs') : undefined
    if (file === undefined) { response.writeHead(404); response.end(); return }
    response.writeHead(200, { 'content-type': ['/', '/shared', '/recovery'].includes(url.pathname) ? 'text/html' : 'text/javascript' })
    response.end(await readFile(file))
  } catch (error) { response.writeHead(500); response.end(String(error)) }
})
// Forward an upgrade byte-for-byte. This fixture does not implement a second mux.
server.on('upgrade', (request, socket, head) => {
  if (request.url !== '/remote/remote.mux') { socket.destroy(); return }
  const upstream = createConnection({ host: hostAddress.hostname, port: Number(hostAddress.port) })
  streams.add(socket)
  socket.on('error', () => upstream.destroy())
  upstream.on('error', () => socket.destroy())
  socket.on('close', () => { streams.delete(socket); upstream.destroy() })
  upstream.on('connect', () => {
    upstream.write(`${request.method} ${request.url} HTTP/${request.httpVersion}\r\n` +
      request.rawHeaders.reduce((text, value, index, headers) => index % 2 ? text : text + `${value}: ${headers[index + 1]}\r\n`, '') + '\r\n')
    if (head.length) upstream.write(head)
    socket.pipe(upstream).pipe(socket)
  })
})
server.listen(0, '127.0.0.1', () => {
  console.log(JSON.stringify({ origin: 'http://127.0.0.1:' + server.address().port,
    evidence: 'Browser carrier ready; browser interactions have not run' }))
})
let closing = false
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, async () => {
  if (closing) return
  closing = true
  for (const stream of streams) stream.destroy()
  server.close()
  server.closeAllConnections()
  const exited = once(host, 'exit')
  await fetch(new URL('/stop', hostAddress), { method: 'POST' }).catch(() => host.kill())
  await exited
  hostLines.close()
})
process.on('exit', () => { if (host.exitCode === null) host.kill() })
