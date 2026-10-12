// Serve the already compiled independent package consumer through a same-origin browser carrier.
import { createServer, request as forward } from 'node:http'
import { createConnection } from 'node:net'
import { readFile } from 'node:fs/promises'
import { resolve } from 'node:path'

const [address, directory, mode = 'events'] = process.argv.slice(2)
if (!['events', 'outbound'].includes(mode)) throw new Error('Expected events or outbound mode')
const outbound = mode === 'outbound'
const host = new URL(address)
if (host.protocol !== 'http:' || !['127.0.0.1', 'localhost'].includes(host.hostname)) throw new Error('Expected the local package consumer Host')
const output = resolve(directory)
const assets = new Map([
  ['/Client.js', resolve(output, outbound ? 'compiled/SharedClient.js' : 'compiled/Client.js')],
  ...(outbound ? [['/shared.mjs', resolve(output, 'compiled/shared.mjs')]] : [
    ['/management.mjs', resolve(output, 'compiled/management.mjs')],
    ['/events.mjs', resolve(output, 'compiled/events.mjs')],
  ]),
  ['/cordis-remote.mjs', resolve(output, 'compiled/cordis-remote.mjs')],
  ['/cordis-context.mjs', resolve(output, 'cordis-context.mjs')],
])
for (const file of assets.values()) await readFile(file)
const page = `<!doctype html><html lang="en"><meta charset="utf-8"><title>Remote scoped package consumer</title>
<button id="run">Run scoped checks</button><pre id="result">Ready</pre>
<script type="module">
import { ${outbound ? 'scopedCalls' : 'scopedEvents'} } from '/Client.js'
import { Context } from '/cordis-context.mjs'
import { TypertConnection } from '/cordis-remote.mjs'
const button = document.querySelector('#run'), result = document.querySelector('#result')
button.onclick = async () => {
  button.disabled = true
  const connection = new TypertConnection(location.origin + '/remote', (input, init) => {
    const headers = new Headers(init?.headers); headers.set('Authorization', 'consumer-policy')
    return fetch(input, {...init, headers})
  }, url => { url.searchParams.set('consumer', 'consumer-policy'); return new WebSocket(url) })
  try {
    await ${outbound ? 'scopedCalls(connection, () => new Context())' : 'scopedEvents(location.origin, connection, () => new Context())'}
    result.textContent = ${JSON.stringify(outbound
      ? 'PASS generated outbound scoped: receiver and lookup, current adapter and identity, null/undefined, stream and withdrawal'
      : 'PASS generated package events: current adapter, borrowed Context, next/undefined/null, owned reply and cancelled-handler settlement')}
  } catch (error) { result.textContent = 'FAIL ' + error.stack }
  finally { await connection.dispose() }
}
</script></html>`
const sockets = new Set()
const server = createServer(async (request, response) => {
  const path = new URL(request.url, 'http://localhost').pathname
  if (path === '/') { response.setHeader('content-type', 'text/html'); response.end(page); return }
  if (assets.has(path)) {
    try { response.setHeader('content-type', 'text/javascript'); response.end(await readFile(assets.get(path))) }
    catch (error) { response.writeHead(500); response.end(String(error)) }
    return
  }
  const upstream = forward(new URL(request.url, host), {method: request.method, headers: request.headers}, remote => {
    response.writeHead(remote.statusCode, remote.headers); remote.pipe(response)
  })
  upstream.on('error', error => { if (!response.destroyed) { response.writeHead(502); response.end(String(error)) } })
  response.on('close', () => { if (!response.writableEnded) upstream.destroy() })
  request.pipe(upstream)
})
server.on('upgrade', (request, socket, head) => {
  if (new URL(request.url, host).pathname !== '/remote/remote.mux') { socket.destroy(); return }
  const upstream = createConnection({host: host.hostname, port: Number(host.port)})
  sockets.add(socket)
  socket.on('error', () => upstream.destroy())
  upstream.on('error', () => socket.destroy())
  socket.on('close', () => { sockets.delete(socket); upstream.destroy() })
  upstream.on('connect', () => {
    upstream.write(request.method + ' ' + request.url + ' HTTP/' + request.httpVersion + '\r\n' +
      request.rawHeaders.reduce((text, value, index, headers) => index % 2 ? text : text + value + ': ' + headers[index + 1] + '\r\n', '') +
      (outbound ? 'authorization: consumer-policy\r\n' : '') + '\r\n')
    if (head.length) upstream.write(head)
    socket.pipe(upstream).pipe(socket)
  })
})
server.listen(0, '127.0.0.1', () => console.log('READY http://127.0.0.1:' + server.address().port))
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {
  for (const socket of sockets) socket.destroy()
  server.close(); server.closeAllConnections()
})
