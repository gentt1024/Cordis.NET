import assert from 'node:assert/strict'
import { createRequire } from 'node:module'
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const [origin, destination, savedEvents] = process.argv.slice(2)
const output = resolve(destination)
await mkdir(output, { recursive: true })
for (const name of ['collections', 'decision']) {
  for (const extension of ['mjs', 'd.mts']) {
    await writeFile(resolve(output, name + '.' + extension), await readFile(resolve(savedEvents, name + '.' + extension)))
  }
}
for (const name of ['management', 'settings', 'events']) {
  for (const extension of ['mjs', 'd.ts']) {
    const response = await fetch(origin + '/client-runtime/' + name + '.' + extension)
    assert.equal(response.status, 200, 'The actual host must publish its generated contract')
    await writeFile(resolve(output, name + (extension === 'd.ts' ? '.d.mts' : '.mjs')), await response.text())
  }
}
for (const extension of ['mjs', 'd.mts']) {
  const response = await fetch(origin + '/client-runtime/cordis-remote.' + extension)
  assert.equal(response.status, 200, 'Host must publish the shared Connection runtime')
  await writeFile(resolve(output, 'cordis-remote.' + extension), await response.text())
}
await writeFile(resolve(output, 'package.json'), '{"private":true,"type":"module"}\n')
await writeFile(resolve(output, 'Client.ts'), await readFile(resolve(root, 'tests/fixtures/ManagementDotnet/Client.ts'), 'utf8'))
const requireTools = createRequire(resolve(root, 'clients/modules/package.json'))
const ts = requireTools('typescript')
const program = ts.createProgram([resolve(output, 'Client.ts')], {
  strict: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts', 'lib.esnext.disposable.d.ts'], types: [], outDir: resolve(output, 'compiled'),
})
const diagnostics = ts.getPreEmitDiagnostics(program)
if (diagnostics.length) throw new Error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
  getCurrentDirectory: () => output, getCanonicalFileName: name => name, getNewLine: () => '\n',
}))
const emitted = program.emit()
assert.equal(emitted.emitSkipped, false, 'Independent strict TypeScript caller was not emitted')
for (const name of ['management', 'settings', 'events', 'collections', 'decision', 'cordis-remote']) {
  await writeFile(resolve(output, 'compiled', name + '.mjs'), await readFile(resolve(output, name + '.mjs')))
}
const { run } = await import(pathToFileURL(resolve(output, 'compiled/Client.js')).href)
const contextFile = resolve(output, 'cordis-context.mjs')
await requireTools('esbuild').build({
  entryPoints: [resolve(root, 'clients/modules/vendor/cordis/src/index.ts')],
  bundle: true, format: 'esm', platform: 'browser', outfile: contextFile, legalComments: 'eof',
  alias: { '@deepseek-ai/cosmokit': resolve(root, 'clients/modules/vendor/cosmokit/src/index.ts') },
})
const { Context } = await import(pathToFileURL(contextFile).href)
await run(origin, () => new Context())
console.log('PASS actual generated TypeScript PluginManager and typed event package clients')
