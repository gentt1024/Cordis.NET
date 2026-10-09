import assert from 'node:assert/strict'
import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'
import { spawnSync } from 'node:child_process'

const [base, directory, compiler] = process.argv.slice(2)
const first = await import(pathToFileURL(join(directory, 'first.client.mjs')).href)
const second = await import(pathToFileURL(join(directory, 'second.client.mjs')).href)
const clients = [first.createRemote(base), second.createRemote(base)]
try {
  assert.deepEqual(await clients[0]['first/Echo']({ request: { delta: 2 } }), { ok: true, value: 109 })
  assert.deepEqual(await clients[1]['second/Echo']({ request: { delta: 2 } }), { ok: true, value: 113 })
  const invalid = await clients[0]['first/Echo']({ request: { delta: 'wrong' } })
  assert.equal(invalid.ok, false)
  assert.equal(invalid.error.code, 'gateway/input-invalid')
  const validSource = `import {createRemote as first} from './first.client.mjs';
import {createRemote as second} from './second.client.mjs';
first('http://example.invalid')['first/Echo']({request:{delta:2}});
second('http://example.invalid')['second/Echo']({request:{delta:2}});
`
  const source = join(directory, 'consumer.mts')
  await writeFile(source, validSource)
  const arguments_ = [compiler, '--strict', '--noEmit', '--module', 'NodeNext', '--target', 'ES2022', source]
  const valid = spawnSync(process.execPath, arguments_, { encoding: 'utf8' })
  assert.equal(valid.status, 0, valid.stdout + valid.stderr)
  await writeFile(source, validSource + `first('http://example.invalid')['first/Echo']({request:{delta:'wrong'}});
`)
  const invalidTypes = spawnSync(process.execPath, arguments_, { encoding: 'utf8' })
  assert.notEqual(invalidTypes.status, 0, 'The generated client accepted a string DTO delta')
  assert.match(invalidTypes.stdout, /TS2322/, invalidTypes.stdout + invalidTypes.stderr)
  console.log('Generated multi-entry Remote clients passed (two actual HTTP calls, DTO type rejection, bad arguments).')
} finally {
  for (const client of clients) client.dispose()
}
