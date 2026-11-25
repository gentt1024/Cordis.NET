import { createRequire } from 'node:module'
import { fileURLToPath } from 'node:url'
import { dirname, relative, resolve } from 'node:path'
import { cp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises'
import { spawnSync } from 'node:child_process'
import { createHash } from 'node:crypto'

if (process.argv.length > 3) {
  throw new Error('This entry builds the runtime only. Use cordis-client-build for author packages.')
}
const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const source = resolve(root, 'clients/modules')
const readJson = async path => JSON.parse(await readFile(path, 'utf8'))
const provenance = await readJson(resolve(source, 'vendor/provenance.json'))
const lock = await readJson(resolve(root, 'upstream.lock.json'))
if (provenance.commit !== lock.harness.commit) throw new Error('Browser source baseline differs from upstream.lock.json')
for (const file of provenance.files) {
  const digest = createHash('sha256').update(await readFile(resolve(source, 'vendor', file.file))).digest('hex')
  if (file.localSha256 !== undefined && !file.adaptation) throw new Error('Missing source adaptation: ' + file.file)
  if (digest !== (file.localSha256 ?? file.sha256)) throw new Error('Browser source identity mismatch: ' + file.file)
}

const declarationsOnly = process.argv[2] === '--types-only'
const output = resolve(declarationsOnly ? resolve(source, 'lib') : process.argv[2] ?? resolve(source, 'lib'))
if (output === source || output === root) throw new Error('Choose a build output directory')
const standard = resolve(source, '.types/standard')
const generateStandard = spawnSync(process.env.DOTNET_HOST_PATH ?? 'dotnet', [
  'run', '--project', resolve(root, 'tools/Cordis.ClientAssets/Cordis.ClientAssets.csproj'),
  '-c', 'Release', '--', '--output', standard,
], { stdio: 'inherit' })
if (generateStandard.status !== 0) throw new Error('Standard generated client assets failed')

const requireTools = createRequire(resolve(source, 'package.json'))
for (const config of ['tsconfig.vendor.json', 'tsconfig.json']) {
  const result = spawnSync(process.execPath, [requireTools.resolve('typescript/bin/tsc'), '-p', resolve(source, config)], { stdio: 'inherit' })
  if (result.status !== 0) throw new Error(`Client type declaration build failed: ${config}`)
}
await mkdir(output, { recursive: true })
const previousPackage = await readJson(resolve(output, 'package.json')).catch(error => {
  if (error.code !== 'ENOENT') throw error
})
if (previousPackage?.name === '@cordis-net/client-modules') {
  // Remove obsolete declarations/chunks only from a previous generated SDK.
  await rm(resolve(output, 'types'), { recursive: true, force: true })
  if (!declarationsOnly) for (const name of await readdir(output)) {
    if (/^shared-[A-Z0-9]{8}\.mjs$/.test(name)) await rm(resolve(output, name))
  }
}

const moduleNames = ['entries', 'system', 'manifest', 'release-change']
const runtimePackages = new Set()
if (!declarationsOnly) {
  const { build } = requireTools('esbuild')
  const alias = Object.fromEntries(moduleNames.map(name => [`@cordis-net/modules/${name}`, resolve(source, 'vendor/modules', name + '.ts')]))
  // The public node condition supplies named ESM exports; the browser condition supplies a DSH registration script.
  alias['@deepseek-ai/dsh-client-connection/client'] = requireTools.resolve('@deepseek-ai/dsh-client-connection/client')
  alias['node:module'] = resolve(source, 'vendor/contracts/node-module-stub.ts')
  const generatedAccess = { name: 'generated-access-remote', setup(builder) {
    builder.onResolve({ filter: /^\.\.\/\.types\/standard\/access-remote\.js$/ }, () => ({ path: './access-remote.mjs', external: true }))
  } }
  const { metafile } = await build({
    absWorkingDir: source,
    entryPoints: { client: 'src/index.ts', access: 'src/access.ts', slots: requireTools.resolve('@deepseek-ai/dsh-client-ui-slots') },
    bundle: true, format: 'esm', platform: 'browser', splitting: true, chunkNames: 'shared-[hash]',
    outdir: output, outExtension: { '.js': '.mjs' }, legalComments: 'eof', metafile: true,
    alias, plugins: [generatedAccess],
    define: { 'process.versions.node': '"0.0.0"', 'process.execArgv': '[]', 'process.env.CORDIS_SHARED': 'undefined' },
  })
  for (const artifact of Object.values(metafile.outputs)) for (const [input, contribution] of Object.entries(artifact.inputs)) {
    const name = /(?:^|\/)node_modules\/((?:@[^/]+\/)?[^/]+)\//.exec(input)?.[1]
    if (name && contribution.bytesInOutput) runtimePackages.add(name)
  }
}

// Keep one physical declaration module per package inside the standalone browser SDK.
// In particular the /slots export targets the original lexical owner of SlotMap.
const typeAliases = Object.fromEntries(moduleNames.map(name => [`@cordis-net/modules/${name}`, `vendor/modules/${name}.d.ts`]))
Object.assign(typeAliases, {
  'node:module': 'vendor/contracts/node-module-stub.d.ts',
  '../.types/standard/events.js': 'standard/events.d.ts',
  '../.types/standard/access-remote.js': 'standard/access-remote.d.ts',
})
const typePackages = new Set()
const copyTypes = async (name, directory, files, { request = name, entry = 'index.d.ts', commonjs = false } = {}) => {
  const installed = resolve(source, 'node_modules', name, directory)
  const destination = resolve(output, 'types/npm', name)
  await mkdir(destination, { recursive: true })
  if (files) {
    for (const file of files) await cp(resolve(installed, file), resolve(destination, file))
  } else {
    await cp(installed, destination, { recursive: true, filter: (from) => !/\.(?:map|js|cjs|mjs)$/.test(from) })
  }
  if (commonjs) await writeFile(resolve(destination, 'package.json'), '{"type":"commonjs"}\n')
  typeAliases[request] = 'npm/' + name + '/' + entry
  typePackages.add(name)
}
for (const name of ['cordis', 'cosmokit', 'cordis-plugin-loader', 'schemastery', 'dsh-client-ui-slots', 'dsh-client-store', 'dsh-package-manifest']) {
  await copyTypes('@deepseek-ai/' + name, 'lib/types')
}
// ConnectionRecoveryConfig is the only public upstream type in the native management facade.
await copyTypes('@deepseek-ai/dsh-client-connection', 'lib/types', ['recovery-config.d.ts'], {
  request: '@deepseek-ai/dsh-client-connection/client', entry: 'recovery-config.d.ts',
})
await copyTypes('@standard-schema/spec', 'dist', ['index.d.ts'])
await copyTypes('@types/react', '', ['index.d.ts', 'global.d.ts'], { request: 'react', commonjs: true })
await copyTypes('@types/prop-types', '', ['index.d.ts'], { request: 'prop-types', commonjs: true })
await copyTypes('csstype', '', ['index.d.ts'], { commonjs: true })

await cp(resolve(source, '.types/vendor/modules'), resolve(output, 'types/vendor/modules'), { recursive: true })
await cp(resolve(source, '.types/vendor/contracts/node-module-stub.d.ts'), resolve(output, 'types/vendor/contracts/node-module-stub.d.ts'))
const authored = resolve(source, '.types/sdk/src')
const publicDeclarations = new Set(['index.d.ts', 'access.d.ts'])
const ts = requireTools('typescript')
for (const file of publicDeclarations) {
  const declaration = await readFile(resolve(authored, file), 'utf8')
  for (const { fileName } of ts.preProcessFile(declaration).importedFiles) {
    if (fileName.startsWith('./') && /\.[jt]s$/.test(fileName)) {
      publicDeclarations.add(relative(authored, resolve(authored, dirname(file), fileName.replace(/\.[jt]s$/, '.d.ts'))))
    }
  }
  await cp(resolve(authored, file), resolve(output, 'types/src', file))
}
await cp(standard, resolve(output, 'types/standard'), { recursive: true })
await cp(resolve(standard, 'cordis-remote.mjs'), resolve(output, 'cordis-remote.mjs'))
await cp(resolve(standard, 'cordis-remote.d.mts'), resolve(output, 'cordis-remote.d.mts'))
for (const name of ['management', 'events', 'settings', 'access-remote']) {
  await cp(resolve(standard, name + '.mjs'), resolve(output, name + '.mjs'))
  await cp(resolve(standard, name + '.d.ts'), resolve(output, name + '.d.ts'))
}

async function rewriteTypes(directory) {
  for (const item of await readdir(directory, { withFileTypes: true })) {
    const path = resolve(directory, item.name)
    if (item.isDirectory()) { await rewriteTypes(path); continue }
    if (!item.name.endsWith('.d.ts')) continue
    let text = await readFile(path, 'utf8')
    for (const [name, target] of Object.entries(typeAliases)) {
      const local = './' + relative(dirname(path), resolve(output, 'types', target)).replaceAll('\\', '/').replace(/\.d\.ts$/, '.js')
      for (const quote of ['"', "'"]) text = text.replaceAll(`${quote}${name}${quote}`, `${quote}${local}${quote}`)
    }
    // The optional Node loader is unavailable in the browser.
    text = text.replaceAll('NodeJS.Signals', 'string').replace(/(['"])(\.[^'"\r\n]+)(?<!\.d)\.ts\1/g, '$1$2.js$1')
      .replace(/^\/\/# sourceMappingURL=.*$/gm, '')
    await writeFile(path, text)
  }
}
await rewriteTypes(resolve(output, 'types'))
if (declarationsOnly) process.exit(0)
const packageLock = await readJson(resolve(source, 'package-lock.json'))
const packages = []
const licenses = ['modules\n' + await readFile(resolve(source, 'vendor/modules/LICENSE'), 'utf8')]
for (const name of [...new Set([...runtimePackages, ...typePackages])].sort()) {
  const { version, integrity } = packageLock.packages['node_modules/' + name]
  const installed = resolve(source, 'node_modules', name)
  if (!integrity || (await readJson(resolve(installed, 'package.json'))).version !== version) throw new Error('Unverified build input: ' + name)
  packages.push({ name, version, integrity, runtime: runtimePackages.has(name), declarations: typePackages.has(name) })
  licenses.push(`${name} ${version}\n${await readFile(resolve(installed, 'LICENSE'), 'utf8')}`)
}
await writeFile(resolve(output, 'provenance.json'), JSON.stringify({ ...provenance, packages }, null, 2) + '\n')
await writeFile(resolve(output, 'LICENSES.txt'), licenses.join('\n'))
const manifest = await readJson(resolve(source, 'package.json'))
const exports = Object.fromEntries(Object.entries(manifest.exports).map(([key, entry]) => [
  key, Object.fromEntries(Object.entries(entry).map(([condition, path]) => [condition, path.replace('./lib/', './')])),
]))
await writeFile(resolve(output, 'package.json'), JSON.stringify({ name: manifest.name, private: true, type: 'module', exports }, null, 2) + '\n')
