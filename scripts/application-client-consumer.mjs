import assert from 'node:assert/strict'
import { appendFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { createHash } from 'node:crypto'

const [origin, packageDirectory] = process.argv.slice(2)
const metadata = await (await fetch(origin + '/client')).json()
const resource = await fetch(origin + metadata.entry)
assert.equal(resource.status, 200)
const code = await resource.text()
assert.equal(createHash('sha256').update(code).digest('hex'), metadata.revision, 'client content hash binds captured bytes')
assert.equal(resource.headers.get('etag'), '"' + metadata.revision + '"')
const client = await import('data:text/javascript;base64,' + Buffer.from(code).toString('base64'))
assert.equal(await client.runApplicationClient(origin), 'application client scenario passed')
const head = await fetch(origin + metadata.entry, { method: 'HEAD' })
assert.equal(head.status, 200)
assert.equal(await head.text(), '')
assert.equal(head.headers.get('content-length'), resource.headers.get('content-length'))
assert.equal((await fetch(origin + metadata.entry, { headers: { 'If-None-Match': resource.headers.get('etag') } })).status, 304)
await appendFile(resolve(packageDirectory, 'client.mjs'), '\nexport const artifactGeneration = 2;\n')
// The old version remains immutable before the host accepts a new content generation.
assert.equal(await (await fetch(origin + metadata.entry)).text(), code, 'old client URL never serves new bytes')
const next = await (await fetch(origin + '/client')).json()
assert.notEqual(next.entry, metadata.entry, 'client generation changes with bytes')
assert.equal((await fetch(origin + metadata.entry)).status, 404, 'retired generation is unavailable in the minimal host')
const replacementCode = await (await fetch(origin + next.entry)).text()
const replacement = await import('data:text/javascript;base64,' + Buffer.from(replacementCode).toString('base64'))
assert.equal(replacement.artifactGeneration, 2, 'replacement client actually executes')
assert.equal((await fetch(origin + '/client/unknown.mjs')).status, 404)
assert.equal((await fetch(origin + '/client/..%2Fpackage.json')).status, 404)
console.log(JSON.stringify({ status: 'application-client-passed', initialRevision: metadata.revision, nextRevision: next.revision,
  consumer: 'actual bundled unchanged fixed DSH form-model and client-store over native HTTP', execution: 'Node ESM client; browser UI rendering not asserted' }))
