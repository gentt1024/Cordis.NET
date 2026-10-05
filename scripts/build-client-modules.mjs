import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { dirname, relative, resolve } from 'node:path'
import { cp, mkdir, mkdtemp, readFile, readdir, rename, writeFile } from 'node:fs/promises'
import { spawnSync } from 'node:child_process'
import { createHash, randomUUID } from 'node:crypto'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const source = resolve(root, 'clients/modules')
const output = resolve(process.argv[2] ?? resolve(source, 'lib'))
let requireTools = createRequire(resolve(source, 'package.json'))
let build
let buildContext
try { ({ build, context: buildContext } = requireTools('esbuild')) }
catch { requireTools = createRequire(resolve(root, 'reference/package.json')); ({ build, context: buildContext } = requireTools('esbuild')) }
const alias = {
  '@deepseek-ai/cordis': resolve(source, 'vendor/cordis/src/index.ts'),
  '@deepseek-ai/cosmokit': resolve(source, 'vendor/cosmokit/src/index.ts'),
  '@deepseek-ai/cordis-plugin-loader': resolve(source, 'vendor/loader/src/index.ts'),
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
await mkdir(output, { recursive: true })
await build({ entryPoints: [resolve(source, 'src/index.ts')], bundle: true, format: 'esm', platform: 'browser',
  outfile: resolve(output, 'client.mjs'), alias, define, legalComments: 'eof' })
// A normal deployment can copy this directory; it does not require the author sources or Node.
const licenses = await Promise.all(['cordis', 'cosmokit', 'loader', 'modules', 'slots'].map(async name =>
  `${name}\n${await readFile(resolve(source, 'vendor', name, 'LICENSE'), 'utf8')}`))
const standardSchema = dirname(dirname(requireTools.resolve('@standard-schema/spec')))
licenses.push(`@standard-schema/spec 1.0.0\n${await readFile(resolve(standardSchema, 'LICENSE'), 'utf8')}`)
await writeFile(resolve(output, 'LICENSES.txt'), licenses.join('\n'))
await cp(resolve(source, '.types/vendor'), resolve(output, 'types/vendor'), { recursive: true })
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
    './slots': { types: './types/vendor/slots/index.d.ts', default: './client.mjs' } } }, null, 2) + '\n')

// Optional author build: preserve declared cross-package requests instead of bundling dependency copies.
// Usage: node scripts/build-client-modules.mjs <runtime-output> <author-package> <plugin-output>
if (process.argv[3] !== undefined) {
  const author = resolve(process.argv[3])
  const destination = resolve(process.argv[4] ?? resolve(author, 'dist'))
  if (destination === output) throw new Error('Author artifacts require a separate output directory')
  const watching = process.argv[5] === '--watch'
  const readManifest = async () => {
    const manifest = JSON.parse(await readFile(resolve(author, 'package.json'), 'utf8'))
    const client = manifest.dsh?.client
    if (typeof manifest.name !== 'string' || client?.platform !== 'web'
      || !Array.isArray(client.external) || client.external.some(value => typeof value !== 'string')) {
      throw new Error('Author package requires a name, web dsh.client, and explicit external requests')
    }
    return manifest
  }
  let manifest = await readManifest()
  const publish = async (bundle) => {
    const code = bundle.outputFiles[0].text
    const registration = `globalThis.__ModuleLoader__.load({id:${JSON.stringify(manifest.name)},factory(require){const module={exports:{}};const exports=module.exports;\n${code}\nreturn module.exports;}});\n`
    const revision = createHash('sha256').update(registration).digest('hex')
    const parent = watching ? resolve(destination, 'generations') : dirname(destination)
    await mkdir(parent, { recursive: true })
    const staged = await mkdtemp(resolve(parent, '.client-pending-'))
    await writeFile(resolve(staged, 'client.js'), registration)
    await writeFile(resolve(staged, 'package.json'), JSON.stringify({ ...manifest,
      exports: { ...manifest.exports, './client': './client.js' } }, null, 2) + '\n')
    const directory = watching ? resolve(parent, revision + '-' + randomUUID()) : destination
    if (!watching) {
      // Preserve the prior complete directory; never overwrite files a capture may be reading.
      try { await rename(destination, destination + '.retired-' + randomUUID()) }
      catch (error) { if (error.code !== 'ENOENT') throw error }
    }
    await rename(staged, directory)
    console.log(JSON.stringify({ kind: 'client-built', name: manifest.name, revision, directory }))
  }
  const reportFailure = error => console.log(JSON.stringify({ kind: 'build-error', name: manifest.name, diagnostic: String(error) }))
  // DSH selects externals by the complete request. esbuild's external package patterns also
  // match every subpath, which would silently ask the shell for undeclared module identities.
  const requests = new Set(['@cordis-net/client-modules', '@cordis-net/client-modules/slots',
    '@deepseek-ai/cordis', '@deepseek-ai/cosmokit', ...manifest.dsh.client.external])
  const settings = { entryPoints: [resolve(author, 'client.ts')], bundle: true, format: 'cjs', platform: 'browser',
    write: false, plugins: [{ name: 'exact-client-externals', setup(plugin) {
      plugin.onResolve({ filter: /.*/ }, args => requests.has(args.path) ? { path: args.path, external: true } : undefined)
    } }],
    define, legalComments: 'eof' }
  if (!watching) {
    try { await publish(await build(settings)) } catch (error) { reportFailure(error); process.exitCode = 1 }
  } else {
    const compiler = await buildContext({ ...settings, plugins: [...settings.plugins, { name: 'complete-client-artifact', setup(plugin) {
      plugin.onStart(async () => {
        const next = await readManifest()
        if (next.name !== manifest.name) throw new Error('Package identity changed; restart the author watch')
        if (JSON.stringify(next.dsh.client.external) !== JSON.stringify(manifest.dsh.client.external)) {
          throw new Error('External requests changed; restart the author watch with the new manifest')
        }
        manifest = next
      })
      plugin.onLoad({ filter: /client\.ts$/ }, async args => {
        if (args.path !== resolve(author, 'client.ts')) return
        return { contents: await readFile(args.path, 'utf8'), loader: 'ts', resolveDir: dirname(args.path),
          watchFiles: [resolve(author, 'package.json')] }
      })
      plugin.onEnd(async result => {
        if (result.errors.length > 0) { reportFailure(result.errors.map(error => error.text).join('; ')); return }
        try { await publish(result) } catch (error) { reportFailure(error) }
      })
    } }] })
    await compiler.watch()
    for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, async () => { await compiler.dispose() })
  }
}
