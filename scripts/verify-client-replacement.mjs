import assert from 'node:assert/strict'
import {execFileSync} from 'node:child_process'
import {createHash} from 'node:crypto'
import {cp, mkdir, mkdtemp, readFile, readdir, writeFile} from 'node:fs/promises'
import {tmpdir} from 'node:os'
import {createRequire} from 'node:module'
import {dirname, resolve} from 'node:path'
import {pathToFileURL} from 'node:url'
import {runInThisContext} from 'node:vm'
import {bundleSource, verifyReplacement} from '../clients/modules/examples/replacement/check.mjs'

const runtime = resolve(process.argv[2] ?? 'clients/modules/lib')
const output = process.argv[3] ? resolve(process.argv[3]) : await mkdtemp(resolve(tmpdir(), 'cordis-replacement-'))
await mkdir(output, {recursive:true})
if ((await readdir(output)).length) throw new Error('Preserve previous evidence and choose an empty output directory')
const distribution = resolve(output, 'distribution')
await cp(runtime, distribution, {recursive:true})
const metadata = JSON.parse(await readFile(resolve(distribution, 'package.json'), 'utf8'))
metadata.version = process.argv[4] ?? '0.0.0-replacement-verification'
await writeFile(resolve(distribution, 'package.json'), JSON.stringify(metadata, null, 2))
let npm
try { npm = resolve(dirname(createRequire(process.execPath).resolve('npm/package.json')), 'bin/npm-cli.js') }
catch { npm = resolve(dirname(process.execPath), '../lib/node_modules/npm/bin/npm-cli.js') }
const options = {encoding:'utf8', windowsHide:true}
const packed = JSON.parse(execFileSync(process.execPath, [npm, 'pack', '--ignore-scripts', '--json', '--pack-destination', output],
  {...options, cwd:distribution}))
const archive = resolve(output, packed[0].filename)
const consumer = resolve(output, 'consumer')
await mkdir(consumer)
execFileSync(process.execPath, [npm, 'install', '--ignore-scripts', '--offline', '--no-audit', '--no-fund', '--prefix', consumer, archive], options)
const installed = resolve(consumer, 'node_modules/@cordis-net/client-modules')
assert.deepEqual(await readFile(resolve(installed, 'client.mjs')), await readFile(resolve(runtime, 'client.mjs')))
globalThis.location = new URL('http://localhost/')
globalThis.document = {querySelectorAll() { return [] }}
const entry = createRequire(resolve(consumer, 'consumer.mjs')).resolve('@cordis-net/client-modules')
const {bootClientModules} = await import(pathToFileURL(entry).href)
const result = {archive, sha256:createHash('sha256').update(await readFile(archive)).digest('hex'), installed}
try {
  result.scenarios = await verifyReplacement(bootClientModules, async request => {
    runInThisContext(bundleSource(request), {filename:request})
  })
  result.status = 'passed'
  console.log('PASS installed tgz: failed target replacement, explicit restoration, same-version retry, dependency exports and late completion')
} finally {
  await writeFile(resolve(output, 'results.json'), JSON.stringify(result, null, 2))
}
