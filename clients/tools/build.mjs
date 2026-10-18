#!/usr/bin/env node
import { build, context } from 'esbuild'
import { transform } from 'lightningcss'
import { createHash, randomUUID } from 'node:crypto'
import { readFile, writeFile, mkdir, mkdtemp, rename } from 'node:fs/promises'
import { basename, dirname, extname, isAbsolute, relative, resolve, sep } from 'node:path'

const usage = 'cordis-client-build <author-package> <output-directory> [--entry <relative-source>] [--watch]'
const report = value => process.stdout.write(JSON.stringify(value) + '\n')
const outside = path => path === '..' || path.startsWith('..' + sep) || isAbsolute(path)

async function main(args) {
  if (args.length === 1 && args[0] === '--help') { process.stdout.write(usage + '\n'); return }
  if (args.length < 2 || args[0].startsWith('--') || args[1].startsWith('--')) throw new Error(usage)
  const author = resolve(args[0])
  const destination = resolve(args[1])
  const ancestor = relative(destination, author)
  if (!outside(ancestor)) {
    throw new Error('Output must not contain the author directory')
  }
  let entry = 'client.ts'
  let watching = false
  for (let index = 2; index < args.length; index++) {
    if (args[index] === '--watch' && !watching) watching = true
    else if (args[index] === '--entry' && args[index + 1] && !args[index + 1].startsWith('--')) entry = args[++index]
    else throw new Error(usage)
  }
  const entryPath = resolve(author, entry)
  const entryRelative = relative(author, entryPath)
  if (outside(entryRelative)) throw new Error('Entry must be inside the author package')
  const protectInputs = inputs => {
    for (const input of inputs) {
      const path = resolve(author, input)
      if (!outside(relative(destination, path))) throw new Error('Output must not contain build input: ' + path)
    }
  }
  protectInputs([entryPath])
  const manifestPath = resolve(author, 'package.json')
  const readManifest = async () => {
    const manifest = JSON.parse(await readFile(manifestPath, 'utf8'))
    const client = manifest.dsh?.client
    if (typeof manifest.name !== 'string' || !manifest.name || client?.platform !== 'web'
      || (client.external !== undefined && (!Array.isArray(client.external)
        || client.external.some(value => typeof value !== 'string')))) {
      throw new Error('Author package requires a name, web dsh.client, and a string array when external is provided')
    }
    return manifest
  }
  let manifest = await readManifest()
  const failure = error => report({ kind: 'build-error', name: manifest.name, diagnostic: String(error) })
  const requests = new Set(['@cordis-net/client-modules', '@cordis-net/client-modules/slots',
    '@deepseek-ai/cordis', '@deepseek-ai/cosmokit', ...(manifest.dsh.client.external ?? [])])
  const mode = process.env.NODE_ENV ?? 'production'
  const publish = async bundle => {
    protectInputs([manifestPath, ...Object.keys(bundle.metafile.inputs)
      .map(input => input.replace(/^css-(?:text|style):/, ''))])
    if (bundle.outputFiles.length !== 1) throw new Error('The factory artifact must have one JavaScript output')
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
      try { await rename(destination, destination + '.retired-' + randomUUID()) }
      catch (error) { if (error.code !== 'ENOENT') throw error }
    }
    await rename(staged, directory)
    report({ kind: 'client-built', name: manifest.name, revision, directory })
  }
  const settings = {
    absWorkingDir: author, entryPoints: [entryPath], bundle: true, format: 'cjs', platform: 'browser',
    write: false, metafile: true, logLevel: 'silent', legalComments: 'eof',
    conditions: [mode === 'development' ? 'development' : 'production', 'browser'],
    define: { 'process.versions.node': '"0.0.0"', 'process.execArgv': '[]', 'process.env.CORDIS_SHARED': 'undefined',
      'process.env.NODE_ENV': JSON.stringify(mode), 'import.meta.env.MODE': JSON.stringify(mode),
      'import.meta.env': JSON.stringify({ MODE: mode }) },
    plugins: [{ name: 'client-inputs', setup(plugin) {
      plugin.onResolve({ filter: /.*/ }, args => requests.has(args.path) ? { path: args.path, external: true } : undefined)
      plugin.onResolve({ filter: /\.css(?:\?inline)?$/ }, async args => {
        if (args.pluginData?.resolvedCss) return
        const inline = args.path.endsWith('?inline')
        const path = inline ? args.path.slice(0, -'?inline'.length) : args.path
        const result = await plugin.resolve(path, { importer: args.importer, resolveDir: args.resolveDir,
          kind: args.kind, pluginData: { resolvedCss: true } })
        if (result.errors.length) return { errors: result.errors }
        return { path: result.path, namespace: inline ? 'css-text' : 'css-style', sideEffects: !inline }
      })
      for (const namespace of ['css-text', 'css-style']) plugin.onLoad({ filter: /.*/, namespace }, async args => {
        const source = await readFile(args.path)
        const modules = namespace === 'css-style' && args.path.endsWith('.module.css')
        const transformed = transform({ filename: args.path, code: source, minify: true,
          ...(modules ? { cssModules: { pattern: '[hash]_[local]' } } : {}) })
        const css = transformed.code.toString()
        const classes = modules ? Object.fromEntries(Object.entries(transformed.exports ?? {})
          .sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0).map(([name, value]) => [name, value.name])) : undefined
        const contents = namespace === 'css-text' ? `export default ${JSON.stringify(css)};`
          : styleModule(manifest.name, args.path, css, classes)
        return { contents, loader: 'js', watchFiles: [args.path] }
      })
      if (watching) {
        plugin.onStart(async () => {
          const next = await readManifest()
          if (next.name !== manifest.name) throw new Error('Package identity changed; restart the author watch')
          if (JSON.stringify(next.dsh.client.external ?? []) !== JSON.stringify(manifest.dsh.client.external ?? [])) {
            throw new Error('External requests changed; restart the author watch with the new manifest')
          }
          manifest = next
        })
        plugin.onLoad({ filter: /\.[cm]?[jt]sx?$/ }, async args => {
          if (args.path !== entryPath) return
          const extension = extname(args.path).slice(1)
          const loader = extension === 'mts' || extension === 'cts' ? 'ts'
            : extension === 'mjs' || extension === 'cjs' ? 'js' : extension
          return { contents: await readFile(args.path, 'utf8'), loader, resolveDir: dirname(args.path),
            watchFiles: [manifestPath] }
        })
        plugin.onEnd(async result => {
          if (result.errors.length) { failure(result.errors.map(error => error.text).join('; ')); return }
          try { await publish(result) } catch (error) { failure(error) }
        })
      }
    } }]
  }
  if (!watching) {
    try { await publish(await build(settings)) } catch (error) { failure(error); process.exitCode = 1 }
    return
  }
  const compiler = await context(settings)
  await compiler.watch()
  for (const signal of ['SIGINT', 'SIGTERM']) process.once(signal, async () => { await compiler.dispose() })
}

function styleModule(id, file, css, classes) {
  const tag = id + '/' + basename(file)
  return `const css=${JSON.stringify(css)};const tagId=${JSON.stringify(tag)};
if(typeof document!=='undefined'&&document.querySelector('style[data-plugin-css='+JSON.stringify(tagId)+']')===null){
const tag=document.createElement('style');tag.dataset.plugin=${JSON.stringify(id)};tag.dataset.pluginCss=tagId;
tag.textContent=css;document.head.appendChild(tag);}
${classes === undefined ? 'export {};' : `export default ${JSON.stringify(classes)};`}`
}

try { await main(process.argv.slice(2)) }
catch (error) { process.stderr.write(String(error) + '\n'); process.exitCode = 1 }
