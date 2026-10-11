import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'
import { createServer } from 'node:http'
import { connect } from 'node:net'
import { once } from 'node:events'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const [origin, destination] = process.argv.slice(2)
const output = resolve(destination)
await mkdir(output, { recursive: true })
for (const name of ['shared', 'cordis-remote'])
  for (const extension of ['mjs', 'd.mts']) {
    const response = await fetch(origin + '/client-runtime/' + name + '.' + extension)
    assert.equal(response.status, 200)
    await writeFile(resolve(output, name + '.' + extension), await response.text())
  }
await writeFile(resolve(output, 'package.json'), '{"private":true,"type":"module"}\n')
await writeFile(resolve(output, 'SharedClient.ts'), await readFile(resolve(root, 'tests/fixtures/DeclarationModel/SharedClient.ts')))
const ts = createRequire(resolve(root, 'clients/modules/package.json'))('typescript')
const program = ts.createProgram([resolve(output, 'SharedClient.ts')], {
  strict: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts'], types: [], outDir: resolve(output, 'compiled'),
})
const diagnostics = ts.getPreEmitDiagnostics(program)
if (diagnostics.length) throw new Error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
  getCurrentDirectory: () => output, getCanonicalFileName: name => name, getNewLine: () => '\n',
}))
assert.equal(program.emit().emitSkipped, false)
for (const name of ['shared', 'cordis-remote'])
  await writeFile(resolve(output, 'compiled', name + '.mjs'), await readFile(resolve(output, name + '.mjs')))

// Node's browser-compatible WebSocket cannot set headers. Forward bytes unchanged with the fixture's admission header.
const target = new URL(origin)
const sockets = new Set()
const proxy = createServer()
proxy.on('upgrade', (request, socket, head) => {
  sockets.add(socket); socket.on('close', () => sockets.delete(socket))
  const upstream = connect(Number(target.port), target.hostname, () => {
    const headers = Object.entries(request.headers).filter(([key]) => key !== 'authorization').map(([key, value]) => key + ': ' + value)
    upstream.write('GET /remote/remote.mux HTTP/1.1\r\n' + headers.join('\r\n') + '\r\nauthorization: consumer-policy\r\n\r\n')
    if (head.length) upstream.write(head)
    socket.pipe(upstream); upstream.pipe(socket)
  })
  upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy())
  socket.on('close', () => upstream.destroy()); upstream.on('close', () => socket.destroy())
})
proxy.listen(0, '127.0.0.1'); await once(proxy, 'listening')
try {
  const { run } = await import(pathToFileURL(resolve(output, 'compiled/SharedClient.js')).href)
  await run(origin, 'ws://127.0.0.1:' + proxy.address().port)
  console.log('PASS strict generated TypeScript uplink caller over the actual Host mux')
} finally {
  for (const socket of sockets) socket.destroy()
  await new Promise(resolve => proxy.close(resolve))
}
