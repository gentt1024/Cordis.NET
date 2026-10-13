import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const [origin, directory] = process.argv.slice(2)
const output = resolve(directory)
const schema = JSON.parse(await readFile(resolve(output, 'remote.schema.json'), 'utf8')).$defs['referencedData/echo/parameters/value']
const defs = Object.values(schema.$defs)
const imported = defs.find(value => value.properties?.external_name)
assert.deepEqual(imported.required.sort(), ['Code', 'external_name'])
assert.equal(imported.properties.Count.type, 'integer')
assert.equal('default' in imported.properties.Count, false)
assert.equal(imported.properties.external_name.type, 'string')
assert.equal(imported.additionalProperties, false)
const requireTools = createRequire(resolve(root, 'clients/modules/package.json'))
const ts = requireTools('typescript')
await copyFile(resolve(root, 'tests/fixtures/DeclarationModel/ReferencedWebClient.ts'), resolve(output, 'ReferencedWebClient.ts'))
await writeFile(resolve(output, 'package.json'), '{"private":true,"type":"module"}\n')
const program = ts.createProgram([resolve(output, 'ReferencedWebClient.ts')], {
  strict: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts'], types: [], outDir: resolve(output, 'compiled'),
})
const diagnostics = ts.getPreEmitDiagnostics(program)
if (diagnostics.length) throw new Error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
  getCurrentDirectory: () => output, getCanonicalFileName: name => name, getNewLine: () => '\n',
}))
assert.equal(program.emit().emitSkipped, false)
await mkdir(resolve(output, 'compiled'), { recursive: true })
for (const name of ['remote.mjs', 'cordis-remote.mjs'])
  await copyFile(resolve(output, name), resolve(output, 'compiled', name))
const { run } = await import(pathToFileURL(resolve(output, 'compiled/ReferencedWebClient.js')).href)
// The Host still uses its original authored default; changing a saved input is not a Host deployment.
await run(origin, 11)
console.log('PASS saved referenced model Schema/TypeScript package over actual Host')
