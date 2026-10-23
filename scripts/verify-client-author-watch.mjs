import { createHash } from 'node:crypto'
import { spawn } from 'node:child_process'
import { cp, mkdtemp, readFile, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { runInNewContext } from 'node:vm'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const builder = resolve(process.argv[2] ?? resolve(root, 'clients/tools/build.mjs'))
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-author-watch-'))
const author = resolve(directory, 'author')
await cp(resolve(root, 'clients/modules/examples/provider'), author, { recursive: true })
const entry = resolve(author, 'client.ts')
const images = {
  svg: { type: 'image/svg+xml', bytes: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"/>') },
  png: { type: 'image/png', bytes: Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQVDIGAACuAGcVHqFfAAAAAElFTkSuQmCC', 'base64') },
  webp: { type: 'image/webp', bytes: Buffer.from('UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAdQkUZ0pv+BiOh/AAA=', 'base64') }
}
await writeFile(entry, "import './watch.css'\n" + Object.keys(images)
  .map(extension => `export { default as ${extension} } from './watch.${extension}'\n`).join('') + await readFile(entry, 'utf8'))
for (const [extension, image] of Object.entries(images)) await writeFile(resolve(author, 'watch.' + extension), image.bytes)
await writeFile(resolve(author, 'watch.css'), '.provider { color: red; }')
const child = spawn(process.execPath, [builder,
  author, resolve(directory, 'artifacts'), '--watch'], { stdio: ['ignore', 'pipe', 'pipe'] })
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
  await writeFile(resolve(author, 'watch.css'), '.provider { color: blue; }')
  const styled = await frame('client-built')
  if (styled.revision === next.revision || !bytes.equals(await readFile(resolve(next.directory, 'client.js')))) {
    throw new Error('CSS-only change did not publish an independent generation')
  }
  const styledBytes = await readFile(resolve(styled.directory, 'client.js'))
  let previous = styled
  let previousBytes = styledBytes
  for (const [extension, image] of Object.entries(images)) {
    image.bytes = Buffer.concat([image.bytes, Buffer.from('\n')])
    await writeFile(resolve(author, 'watch.' + extension), image.bytes)
    const changed = await frame('client-built')
    const changedBytes = await readFile(resolve(changed.directory, 'client.js'))
    let registration
    runInNewContext(changedBytes.toString(), { __ModuleLoader__: { load(value) { registration = value } } })
    const exports = registration.factory(() => ({ Service: class {} }))
    if (changed.directory === previous.directory || changed.revision === previous.revision
      || hash(changedBytes) !== changed.revision || exports[extension] !== `data:${image.type};base64,${image.bytes.toString('base64')}`
      || !previousBytes.equals(await readFile(resolve(previous.directory, 'client.js')))) {
      throw new Error(extension + '-only change did not publish exact bytes in an independent immutable generation')
    }
    previous = changed
    previousBytes = changedBytes
  }
  const conflictingInput = resolve(directory, 'artifacts/value.ts')
  await writeFile(conflictingInput, 'export const value = 42')
  const validSource = await readFile(sourcePath, 'utf8')
  await writeFile(sourcePath, "export { value } from '../artifacts/value'\n" + validSource)
  const refused = await frame('build-error')
  if (!refused.diagnostic.includes('Output must not contain build input')
    || !previousBytes.equals(await readFile(resolve(previous.directory, 'client.js')))
    || await readFile(conflictingInput, 'utf8') !== 'export const value = 42'
    || frames.some(item => item.kind === 'client-built')) {
    throw new Error('A newly imported output input was not refused while preserving the previous generation')
  }
  await writeFile(sourcePath, validSource)
  const recovered = await frame('client-built')
  if (recovered.directory === previous.directory || recovered.revision !== previous.revision
    || !previousBytes.equals(await readFile(resolve(recovered.directory, 'client.js')))) {
    throw new Error('Removing the conflicting import did not restore a complete independent generation')
  }
  console.log('Author watch passed: complete success frames, compilation/input conflict failures preserve old generations, source, CSS and SVG/PNG/WebP-only changes publish new generations with exact image bytes, removed input conflict recovers.')
} finally { child.kill('SIGTERM') }
