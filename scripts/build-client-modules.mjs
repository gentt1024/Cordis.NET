import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { dirname, relative, resolve } from 'node:path'
import { cp, mkdir, readFile, readdir, writeFile } from 'node:fs/promises'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'

if (process.argv.length > 3) {
  throw new Error('This entry builds the runtime only. Use cordis-client-build for author packages.')
}

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const source = resolve(root, 'clients/modules')
const provenance = JSON.parse(await readFile(resolve(source, 'vendor/provenance.json'), 'utf8'))
const lock = JSON.parse(await readFile(resolve(root, 'upstream.lock.json'), 'utf8'))
if (provenance.commit !== lock.harness.commit) throw new Error('Browser source baseline differs from upstream.lock.json')
for (const file of provenance.files) {
  const digest = createHash('sha256').update(await readFile(resolve(source, 'vendor', file.file))).digest('hex')
  if (file.localSha256 !== undefined && !file.adaptation) throw new Error('Missing source adaptation: ' + file.file)
  if (digest !== (file.localSha256 ?? file.sha256)) throw new Error('Browser source identity mismatch: ' + file.file)
}
const declarationsOnly = process.argv[2] === '--types-only'
const output = resolve(declarationsOnly ? resolve(source, 'lib') : process.argv[2] ?? resolve(source, 'lib'))
const standard = resolve(source, '.types/standard')
const generateStandard = spawnSync(process.env.DOTNET_HOST_PATH ?? 'dotnet', [
  'run', '--project', resolve(root, 'tools/Cordis.ClientAssets/Cordis.ClientAssets.csproj'),
  '-c', 'Release', '--', '--output', standard,
], { stdio: 'inherit' })
if (generateStandard.status !== 0) throw new Error('Standard generated client assets failed')
let requireTools = createRequire(resolve(source, 'package.json'))
let build
try { ({ build } = requireTools('esbuild')) }
catch { requireTools = createRequire(resolve(root, 'reference/package.json')); ({ build } = requireTools('esbuild')) }
const alias = {
  '@deepseek-ai/cordis': resolve(source, 'vendor/cordis/src/index.ts'),
  '@deepseek-ai/cosmokit': resolve(source, 'vendor/cosmokit/src/index.ts'),
  '@deepseek-ai/cordis-plugin-loader': resolve(source, 'vendor/loader/src/index.ts'),
  '@cordis-net/connection': resolve(source, 'vendor/connection/client/connection.ts'),
  '@cordis-net/web/boot-client': resolve(source, 'vendor/web/boot-client.ts'),
  '@cordis-net/modules/system': resolve(source, 'vendor/modules/system.ts'),
  '@cordis-net/modules/manifest': resolve(source, 'vendor/modules/manifest.ts'),
  '@cordis-net/modules/entries': resolve(source, 'vendor/modules/entries.ts'),
  '@cordis-net/modules/slots': resolve(source, 'vendor/slots/index.ts'),
  'node:module': resolve(source, 'vendor/contracts/node-module-stub.ts'),
}
const define = { 'process.versions.node': '"0.0.0"', 'process.execArgv': '[]', 'process.env.CORDIS_SHARED': 'undefined' }
for (const config of ['tsconfig.vendor.json', 'tsconfig.json']) {
  const result = spawnSync(process.execPath, [requireTools.resolve('typescript/bin/tsc'), '-p', resolve(source, config)], { stdio: 'inherit' })
  if (result.status !== 0) throw new Error(`Client type declaration build failed: ${config}`)
}
if (declarationsOnly) process.exit(0)
await mkdir(output, { recursive: true })
await cp(resolve(source, 'vendor/provenance.json'), resolve(output, 'provenance.json'))
await build({ entryPoints: [resolve(source, 'src/index.ts')], bundle: true, format: 'esm', platform: 'browser',
  outfile: resolve(output, 'client.mjs'), alias, define, legalComments: 'eof' })
// A normal deployment can copy this directory; it does not require the author sources or Node.
const licenses = await Promise.all(['cordis', 'cosmokit', 'loader', 'modules', 'slots', 'web', 'connection'].map(async name =>
  `${name}\n${await readFile(resolve(source, 'vendor', name, 'LICENSE'), 'utf8')}`))
const standardSchema = dirname(dirname(requireTools.resolve('@standard-schema/spec')))
licenses.push(`@standard-schema/spec 1.0.0\n${await readFile(resolve(standardSchema, 'LICENSE'), 'utf8')}`)
await writeFile(resolve(output, 'LICENSES.txt'), licenses.join('\n'))
await cp(resolve(source, '.types/vendor'), resolve(output, 'types/vendor'), { recursive: true })
await cp(standard, resolve(output, 'types/standard'), { recursive: true })
await cp(resolve(standard, 'cordis-remote.mjs'), resolve(output, 'cordis-remote.mjs'))
await cp(resolve(standard, 'cordis-remote.d.mts'), resolve(output, 'cordis-remote.d.mts'))
for (const name of ['management', 'events', 'settings']) {
  await cp(resolve(standard, name + '.mjs'), resolve(output, name + '.mjs'))
  await cp(resolve(standard, name + '.d.ts'), resolve(output, name + '.d.ts'))
}
await cp(resolve(standardSchema, 'dist/index.d.ts'), resolve(output, 'types/vendor/contracts/standard-schema.d.ts'))
await cp(resolve(source, 'vendor/contracts/schema-metadata.d.ts'), resolve(output, 'types/vendor/contracts/schema-metadata.d.ts'))
for (const [name, files] of Object.entries({ '@types/react': ['index.d.ts', 'global.d.ts'], '@types/prop-types': ['index.d.ts'], csstype: ['index.d.ts'] })) {
  const packageRoot = dirname(requireTools.resolve(name + '/package.json'))
  const target = resolve(output, 'types/vendor/contracts', name.replace('@types/', ''))
  await mkdir(target, { recursive: true })
  for (const file of files) await cp(resolve(packageRoot, file), resolve(target, file))
  await writeFile(resolve(target, 'package.json'), '{"type":"commonjs"}\n')
  licenses.push(`${name}\n${await readFile(resolve(packageRoot, 'LICENSE'), 'utf8')}`)
}
await writeFile(resolve(output, 'LICENSES.txt'), licenses.join('\n'))
if (output !== resolve(source, 'lib')) await cp(resolve(source, 'lib/types/src'), resolve(output, 'types/src'), { recursive: true })
// Published declarations resolve locally, without private source aliases or upstream npm packages.
const typeAliases = {
  '@deepseek-ai/cordis': 'vendor/cordis/src/index.d.ts',
  '@deepseek-ai/cosmokit': 'vendor/cosmokit/src/index.d.ts',
  '@deepseek-ai/cordis-plugin-loader': 'vendor/loader/src/index.d.ts',
  '@deepseek-ai/dsh-client-store': 'vendor/contracts/store.d.ts',
  '@deepseek-ai/dsh-client-modules/client': 'vendor/modules/manifest.d.ts',
  '@cordis-net/web/boot-client': 'vendor/web/boot-client.d.ts',
  '@cordis-net/connection': 'vendor/connection/client/connection.d.ts',
  '@deepseek-ai/dsh-package-manifest': 'vendor/contracts/package-manifest.d.ts',
  '@cordis-net/modules/system': 'vendor/modules/system.d.ts',
  '@cordis-net/modules/manifest': 'vendor/modules/manifest.d.ts',
  '@cordis-net/modules/entries': 'vendor/modules/entries.d.ts',
  '@cordis-net/modules/slots': 'vendor/slots/index.d.ts',
  '@deepseek-ai/schemastery': 'vendor/contracts/schema-metadata.d.ts',
  '@standard-schema/spec': 'vendor/contracts/standard-schema.d.ts',
  'node:module': 'vendor/contracts/node-module-stub.d.ts',
  'react': 'vendor/contracts/react/index.d.ts',
  'prop-types': 'vendor/contracts/prop-types/index.d.ts',
  'csstype': 'vendor/contracts/csstype/index.d.ts',
  '../.types/standard/events.js': 'standard/events.d.ts',
}
async function rewriteTypes(directory) {
  for (const item of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, item.name)
    if (item.isDirectory()) { await rewriteTypes(path); continue }
    if (!item.name.endsWith('.d.ts')) continue
    let text = await readFile(path, 'utf8')
    for (const [name, target] of Object.entries(typeAliases)) {
      const local = './' + relative(dirname(path), resolve(output, 'types', target)).replaceAll('\\', '/').replace(/\.d\.ts$/, '.js')
      for (const quote of ["'", '"']) text = text.replaceAll(`${quote}${name}${quote}`, `${quote}${local}${quote}`)
    }
    // Browser declarations retain the fixed source types without requiring Node globals.
    text = text.replaceAll('NodeJS.Signals', 'string').replace(/(['"])(\.[^'"\r\n]+)(?<!\.d)\.ts\1/g, '$1$2.js$1')
    await writeFile(path, text)
  }
}
await rewriteTypes(resolve(output, 'types'))
await writeFile(resolve(output, 'package.json'), JSON.stringify({ name: '@cordis-net/client-modules',
  private: true, type: 'module', exports: { '.': { types: './types/src/index.d.ts', default: './client.mjs' },
    './slots': { types: './types/vendor/slots/index.d.ts', default: './client.mjs' },
    ...Object.fromEntries(['management', 'events', 'settings'].map(name => [
      './' + name, { types: './types/standard/' + name + '.d.ts', default: './' + name + '.mjs' },
    ])) } }, null, 2) + '\n')
