import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const [origin, destination] = process.argv.slice(2)
const output = resolve(destination)
await mkdir(output, { recursive: true })
for (const name of ['management', 'notifications']) {
  for (const extension of ['mjs', 'd.ts']) {
    const response = await fetch(origin + '/client-runtime/' + name + '.' + extension)
    assert.equal(response.status, 200, 'The actual host must publish its generated contract')
    await writeFile(resolve(output, name + (extension === 'd.ts' ? '.d.mts' : '.mjs')), await response.text())
  }
}
await writeFile(resolve(output, 'package.json'), '{"private":true,"type":"module"}\n')
await writeFile(resolve(output, 'Client.ts'), await readFile(resolve(root, 'tests/fixtures/ManagementDotnet/Client.ts'), 'utf8'))
const requireTools = createRequire(resolve(root, 'clients/modules/package.json'))
const ts = requireTools('typescript')
const program = ts.createProgram([resolve(output, 'Client.ts')], {
  strict: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts'], types: [], outDir: resolve(output, 'compiled'),
})
const diagnostics = ts.getPreEmitDiagnostics(program)
if (diagnostics.length) throw new Error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
  getCurrentDirectory: () => output, getCanonicalFileName: name => name, getNewLine: () => '\n',
}))
const emitted = program.emit()
assert.equal(emitted.emitSkipped, false, 'Independent strict TypeScript caller was not emitted')
for (const name of ['management', 'notifications']) {
  await writeFile(resolve(output, 'compiled', name + '.mjs'), await readFile(resolve(output, name + '.mjs')))
}
const { run } = await import(pathToFileURL(resolve(output, 'compiled/Client.js')).href)
await run(origin)
console.log('PASS actual generated TypeScript PluginManager and RemoteNotifications package clients')
