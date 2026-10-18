import assert from 'node:assert/strict'
import { execFileSync, spawn } from 'node:child_process'
import { existsSync } from 'node:fs'
import { createHash } from 'node:crypto'
import { createServer } from 'node:http'
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import { runInNewContext } from 'node:vm'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const runtime = resolve(process.argv[2])
const output = resolve(process.argv[3])
const serve = process.argv.includes('--serve')
const headless = process.argv.includes('--headless')
await mkdir(output, { recursive: true })
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-installed-author-'))
const author = resolve(directory, '..author')
await mkdir(resolve(author, 'src'), { recursive: true })
const npm = (args, cwd) => {
  const windows = process.platform === 'win32'
  const command = windows ? process.execPath : 'npm'
  const parameters = windows ? [resolve(dirname(process.execPath), 'node_modules/npm/bin/npm-cli.js'), ...args] : args
  return execFileSync(command, parameters, { cwd, encoding: 'utf8',
    windowsHide: true, maxBuffer: 4 * 1024 * 1024 })
}
const supplied = process.env.CORDIS_CLIENT_TOOLS_TGZ
const packed = supplied ? resolve(supplied) : resolve(output, JSON.parse(npm(
  ['pack', '--json', '--pack-destination', output], resolve(root, 'clients/tools')))[0].filename)
const manifest = { name: 'independent-author', private: true, type: 'module',
  dsh: { client: { platform: 'web', external: ['@example/shared'] } },
  devDependencies: { '@cordis-net/client-tools': 'file:' + packed.replaceAll('\\', '/') } }
await writeFile(resolve(author, 'package.json'), JSON.stringify(manifest))
await writeFile(resolve(author, 'src/client.tsx'), `
import classes from './surface.module.css'
import './global.css'
import inline from './owned.css?inline'
import { marker } from '@example/shared'
export const observed = { classes, inline, marker }
export function apply(ctx) {
  ctx.provide('fixture', observed)
  ctx.effect(() => {
    const sheet = document.createElement('style')
    sheet.dataset.inlineOwner = 'fixture'
    sheet.textContent = inline
    document.head.appendChild(sheet)
    const element = document.createElement('div')
    element.id = 'author-element'
    element.className = classes.panel + ' owned'
    element.textContent = marker.value
    document.body.appendChild(element)
    return () => {
      globalThis.authorCleanup.push(document.querySelectorAll('style[data-plugin="independent-author"]').length)
      element.remove(); sheet.remove()
    }
  })
}
`)
await writeFile(resolve(author, 'src/surface.module.css'), '.panel { color: rgb(1, 2, 3); }')
await writeFile(resolve(author, 'src/global.css'), 'body { --author-global: ready; }')
await writeFile(resolve(author, 'src/owned.css'), '.owned { font-weight: 700; }')
await writeFile(resolve(output, 'install.log'), npm(['install', '--ignore-scripts', '--no-audit', '--no-fund'], author))
// npm exec must find the installed bin; no source checkout paths or .NET invocation are supplied.
const log = npm(['exec', '--offline', '--', 'cordis-client-build', '.', 'dist', '--entry', 'src/client.tsx'], author)
await writeFile(resolve(output, 'build.log'), log)
assert.throws(() => npm(['exec', '--offline', '--', 'cordis-client-build', '.', directory,
  '--entry', 'src/client.tsx'], author), error =>
  error.status !== 0 && String(error.stderr).includes('Output must not contain the author directory'))
assert.match(await readFile(resolve(author, 'src/client.tsx'), 'utf8'), /export function apply/)
const frame = log.trim().split(/\r?\n/).map(line => JSON.parse(line)).find(item => item.kind === 'client-built')
assert.ok(frame)
const artifact = resolve(frame.directory, 'client.js')
const bytes = await readFile(artifact)
assert.equal(createHash('sha256').update(bytes).digest('hex'), frame.revision)
let registered
const marker = { value: 'shell-owned singleton' }
runInNewContext(bytes.toString(), { __ModuleLoader__: { load(value) { registered = value } } })
assert.equal(registered.id, manifest.name)
let requests = 0
const plugin = registered.factory(request => { requests++; assert.equal(request, '@example/shared'); return { marker } })
assert.equal(requests, 1)
assert.equal(plugin.observed.marker, marker)
assert.match(plugin.observed.classes.panel, /_panel$/)
assert.match(plugin.observed.inline, /font-weight:700/)
const snapshot = { rev: frame.revision, entries: [{ id: manifest.name, rev: frame.revision,
  url: '/plugin.js?rev=' + frame.revision, inject: [], external: ['@example/shared'] }],
  batches: [{ phase: 'application', url: '/plugin.js?rev=' + frame.revision,
    rev: frame.revision, entries: [manifest.name] }] }
const receipt = { status: 'passed', toolArchive: packed, toolSha256: createHash('sha256').update(await readFile(packed)).digest('hex'),
  author, artifact, artifactSha256: frame.revision, installedBin: true,
  exactExternalIdentity: true, cssModuleExport: true, inlineCssExport: true,
  browser: serve || headless ? 'pending' : 'not requested' }
await writeFile(resolve(output, 'result.json'), JSON.stringify(receipt, null, 2) + '\n')
if (!serve && !headless) {
  console.log('PASS installed author package: independent npm bin, TSX entry, exact external identity, CSS module and inline outputs')
} else {
  const html = `<!doctype html><meta charset="utf-8"><title>Installed Cordis author consumer</title>
<h1>Installed author package</h1><pre id="result">Running real browser lifecycle checks</pre>
<script type="module">
import { bootClientModules } from '/runtime/client.mjs'
const results=[];let client
function check(condition,message){if(!condition)throw new Error(message);results.push(message)}
globalThis.authorCleanup=[]
try {
  const marker={value:'shell-owned singleton'}
  const graph=${JSON.stringify(snapshot)}
  client=await bootClientModules({graph,staticModules:{'@example/shared':{marker}}})
  check(client.state.getSnapshot().failures.length===0,'plugin activated')
  check(client.context.get('fixture').marker===marker,'external singleton retained')
  const styles=()=>document.querySelectorAll('style[data-plugin="independent-author"]').length
  check(styles()===2,'module and global styles owned by plugin')
  check(getComputedStyle(document.querySelector('#author-element')).color==='rgb(1, 2, 3)','CSS module applied')
  check(getComputedStyle(document.querySelector('#author-element')).fontWeight==='700','inline CSS effect applied')
  check(getComputedStyle(document.body).getPropertyValue('--author-global').trim()==='ready','global CSS applied')
  await client.disconnect()
  check(globalThis.authorCleanup[0]===2,'effect cleanup precedes loader style removal')
  check(styles()===0&&!document.querySelector('#author-element')&&!document.querySelector('[data-inline-owner]'),'withdrawal releases styles and DOM')
  await client.sync(graph)
  check(styles()===2&&document.querySelector('#author-element'),'reload restores owned styles and DOM')
  await client.dispose()
  check(styles()===0&&globalThis.authorCleanup.length===2&&!document.querySelector('#author-element'),'final disposal releases all contributions')
  document.querySelector('#result').textContent='PASS\\n'+results.join('\\n')
  await fetch('/result',{method:'POST',body:JSON.stringify({status:'passed',checks:results})})
} catch(error) {
  document.querySelector('#result').textContent='FAIL '+String(error)
  await client?.dispose()
  await fetch('/result',{method:'POST',body:JSON.stringify({status:'failed',error:String(error),checks:results})})
}
</script>`
  let browser
  let deadline
  const server = createServer(async (request, response) => {
    try {
      const path = new URL(request.url, 'http://localhost').pathname
      if (path === '/result' && request.method === 'POST') {
        let body = ''; for await (const bytes of request) body += bytes
        const result = JSON.parse(body)
        await writeFile(resolve(output, 'browser-result.json'), JSON.stringify(result, null, 2) + '\n')
        await writeFile(resolve(output, 'result.json'), JSON.stringify({ ...receipt, browser: result.status }, null, 2) + '\n')
        response.end('recorded'); console.log(JSON.stringify(result)); server.close()
        clearTimeout(deadline)
        browser?.kill()
        if (result.status !== 'passed') process.exitCode = 1
      } else if (path === '/') { response.setHeader('content-type', 'text/html'); response.end(html) }
      else if (path === '/plugin.js') { response.setHeader('content-type', 'text/javascript'); response.end(bytes) }
      else if (/^\/runtime\/[a-zA-Z0-9._-]+\.mjs$/.test(path)) {
        response.setHeader('content-type', 'text/javascript'); response.end(await readFile(resolve(runtime, path.slice('/runtime/'.length))))
      } else { response.writeHead(404); response.end() }
    } catch (error) { response.writeHead(500); response.end(String(error)) }
  })
  server.listen(0, '127.0.0.1', () => {
    const url = 'http://127.0.0.1:' + server.address().port + '/'
    void writeFile(resolve(output, 'browser-url.txt'), url)
    console.log('READY ' + url)
    if (headless) {
      const candidates = [process.env.CORDIS_TEST_BROWSER, '/usr/bin/google-chrome', '/usr/bin/chromium',
        '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
        'C:/Program Files/Google/Chrome/Application/chrome.exe',
        'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe']
      const executable = candidates.find(path => path && existsSync(path))
      if (!executable) { server.close(); throw new Error('Set CORDIS_TEST_BROWSER to an installed Chromium browser') }
      browser = spawn(executable, ['--headless=new', '--no-first-run', '--disable-default-apps',
        '--user-data-dir=' + resolve(directory, 'browser-profile'), url], { stdio: 'ignore', windowsHide: true })
      deadline = setTimeout(() => {
        browser.kill(); server.closeAllConnections(); server.close(); process.exitCode = 1
        void writeFile(resolve(output, 'browser-result.json'), JSON.stringify({ status: 'failed', error: 'Browser result timed out' }))
      }, 60000)
      browser.once('error', error => { clearTimeout(deadline); server.close(); throw error })
    }
  })
}
