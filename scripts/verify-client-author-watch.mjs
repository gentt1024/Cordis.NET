import { createHash } from 'node:crypto'
import { spawn } from 'node:child_process'
import { cp, mkdtemp, readFile, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-author-watch-'))
const author = resolve(directory, 'author')
await cp(resolve(root, 'clients/modules/examples/provider'), author, { recursive: true })
const child = spawn(process.execPath, [resolve(root, 'scripts/build-client-modules.mjs'),
  resolve(directory, 'runtime'), author, resolve(directory, 'artifacts'), '--watch'], { stdio: ['ignore', 'pipe', 'pipe'] })
const frames = []
let output = ''
let errors = ''
child.stderr.on('data', bytes => { errors += bytes.toString() })
child.stdout.on('data', bytes => {
  output += bytes.toString()
  for (;;) {
    const newline = output.indexOf('\n')
    if (newline < 0) break
    const line = output.slice(0, newline); output = output.slice(newline + 1)
    try { frames.push(JSON.parse(line)) } catch { /* Compiler prose is not a success frame. */ }
  }
})
async function frame(kind) {
  const deadline = Date.now() + 45000
  while (Date.now() < deadline) {
    const index = frames.findIndex(item => item.kind === kind)
    if (index >= 0) return frames.splice(index, 1)[0]
    if (child.exitCode !== null) throw new Error(`Author watch exited ${child.exitCode}: ${errors}`)
    await new Promise(resolve => setTimeout(resolve, 50))
  }
  throw new Error(`No ${kind} frame: ${errors}`)
}
const hash = bytes => createHash('sha256').update(bytes).digest('hex')
try {
  const first = await frame('client-built')
  const original = await readFile(resolve(first.directory, 'client.js'))
  const manifest = JSON.parse(await readFile(resolve(first.directory, 'package.json'), 'utf8'))
  if (first.name !== manifest.name || hash(original) !== first.revision || manifest.exports['./client'] !== './client.js') {
    throw new Error('Initial watch frame does not identify a complete matching artifact')
  }
  const sourcePath = resolve(author, 'client.ts')
  const source = await readFile(sourcePath, 'utf8')
  await writeFile(sourcePath, 'export {')
  await frame('build-error')
  if (!original.equals(await readFile(resolve(first.directory, 'client.js'))) || frames.some(item => item.kind === 'client-built')) {
    throw new Error('Failed compilation published or changed a valid generation')
  }
  await writeFile(sourcePath, source.replace('Provider one', 'Provider three'))
  const next = await frame('client-built')
  const bytes = await readFile(resolve(next.directory, 'client.js'))
  if (next.directory === first.directory || next.revision === first.revision || hash(bytes) !== next.revision
    || !original.equals(await readFile(resolve(first.directory, 'client.js')))) throw new Error('Rebuild did not publish an independent immutable generation')
  console.log('Author watch passed: complete success frames, failure preserves old generation, recovery publishes a new generation.')
} finally { child.kill('SIGTERM') }
