import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { closeSync, existsSync, openSync } from 'node:fs'
import { createServer } from 'node:http'
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { resolve } from 'node:path'

const runtime = resolve(process.argv[2] ?? 'artifacts/client-modules')
const output = resolve(process.argv[3] ?? 'artifacts/client-safety')
const serve = process.argv.includes('--serve')
await mkdir(output, { recursive: true })
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-client-safety-'))
const scripts = {
  '/parent.js': `
__ModuleLoader__.load({ id: 'child', factory() {
  const style = document.createElement('style'); style.id = 'child-style'
  style.textContent = 'body { --child: ready; }'; document.head.append(style)
  globalThis.factoryOrder.push('child')
  return { apply(ctx) { ctx.effect(() => () => globalThis.cleanup.push(['child', style.dataset.plugin])) } }
} })
__ModuleLoader__.load({ id: 'parent', factory(require) {
  const style = document.createElement('style'); style.id = 'parent-style'
  style.textContent = 'body { --parent: ready; }'; document.head.append(style)
  const child = require('child')
  globalThis.nestedOwners = [style.getAttribute('data-plugin'), document.querySelector('#child-style').dataset.plugin]
  const preset = document.createElement('style'); preset.id = 'preset-style'
  preset.dataset.plugin = 'parent'; preset.dataset.pluginCss = 'preset.css'
  preset.textContent = 'body { --preset: ready; }'; document.head.append(preset)
  globalThis.factoryOrder.push('parent')
  return { child, apply(ctx) { ctx.effect(() => () => globalThis.cleanup.push(['parent', style.dataset.plugin, preset.dataset.pluginCss])) } }
} })`,
  '/child.js': '',
  '/failure.js': `__ModuleLoader__.load({ id: 'failure', factory() {
  const style = document.createElement('style'); style.id = 'failed-untagged-style'
  style.textContent = 'body { --failed: leaked; }'; document.head.append(style)
  const preset = document.createElement('style'); preset.id = 'failed-preset-style'
  preset.dataset.plugin = 'failure'; preset.dataset.pluginCss = 'failed.css'; document.head.append(preset)
  throw new Error('intentional factory failure')
} })`,
}
const releaseA = { version: 'application-1', commit: 'build-a' }
const releaseB = { version: 'application-1', commit: 'build-b' }
const releaseC = { version: 'application-2', commit: 'build-c' }
for (const name of ['a', 'a2', 'b', 'c']) scripts['/release-' + name + '.js'] = `
__ModuleLoader__.load({ id: 'release-panel', factory() {
  const style = document.createElement('style'); style.id = 'release-style'
  style.textContent = 'body { --release: ${name}; }'; document.head.append(style)
  return { apply(ctx) { ctx.effect(() => {
    const panel = document.createElement('div'); panel.id = 'release-panel'; panel.textContent = '${name}'
    document.body.append(panel)
    return () => panel.remove()
  }) } }
} })`
const releaseGraph = name => {
  const release = name.startsWith('b') ? releaseB : name === 'c' ? releaseC : releaseA
  if (name === 'b-future') return { release, futureGraphFormat: true }
  const row = { id: 'release-panel', url: '/release-' + name + '.js?rev=' + name, rev: name, external: [], inject: [] }
  return { release, rev: name, entries: [row], batches: [{ phase: 'application', url: row.url, rev: row.rev, entries: [row.id] }] }
}
const requests = []
const html = `<!doctype html><meta charset="utf-8"><title>Cordis client safety</title>
<style id="shell-style">body { --shell: retained; }</style>
<pre id="result">Running real browser checks</pre>
<script>
globalThis.reportSafetyProgress = (stage, detail) => navigator.sendBeacon('/diagnostic', JSON.stringify({ stage, detail }))
addEventListener('error', event => reportSafetyProgress('page-error', event.message || event.target?.src), true)
addEventListener('unhandledrejection', event => reportSafetyProgress('unhandled-rejection', String(event.reason)))
reportSafetyProgress('page-script-ready')
</script>
<script type="module">
import { bootClientModules } from '/runtime/client.mjs'
reportSafetyProgress('module-ready')
const checks = []; let client
const check = (condition, message) => { if (!condition) throw new Error(message); checks.push(message); reportSafetyProgress('check-passed', message) }
const equal = (actual, expected, message) => check(JSON.stringify(actual) === JSON.stringify(expected), message + ': ' + JSON.stringify(actual))
const row = (id, url) => ({ id, rev: 'v1', url: url + '?rev=v1', external: [], inject: [] })
const graph = entries => ({ rev: 'graph-v1', entries, batches: entries.map(entry => ({ phase: 'application', url: entry.url, rev: entry.rev, entries: [entry.id] })) })
const parent = row('parent', '/parent.js'); const child = row('child', '/child.js')
const shell = document.querySelector('#shell-style')
const shellIntact = () => shell.isConnected && !shell.hasAttribute('data-plugin') && getComputedStyle(document.body).getPropertyValue('--shell').trim() === 'retained'
const owned = () => [...document.querySelectorAll('style[data-plugin]')].map(style => [style.id, style.dataset.plugin, style.dataset.pluginCss ?? null])
globalThis.factoryOrder = []; globalThis.cleanup = []
try {
  client = await bootClientModules({ graph: graph([parent]) })
  equal(globalThis.factoryOrder, ['child', 'parent'], 'nested require materializes child inside parent factory')
  equal(globalThis.nestedOwners, [null, 'child'], 'child claims its own new style and preserves the untagged parent style')
  equal(owned(), [['parent-style', 'parent', null], ['child-style', 'child', null], ['preset-style', 'parent', 'preset.css']], 'factories own exactly their new and preset styles')
  check(shellIntact(), 'boot preserves shell style identity, ownership and computed CSS')
  equal(['--parent', '--child', '--preset'].map(name => getComputedStyle(document.body).getPropertyValue(name).trim()), ['ready', 'ready', 'ready'], 'factory and preset CSS all apply')
  await client.sync(graph([parent, child]))
  equal(client.state.getSnapshot().failures, [], 'nested dependency becomes a managed entry')
  await client.disconnect()
  equal(globalThis.cleanup.map(item => item[0]).sort(), ['child', 'parent'], 'disconnect runs both plugin cleanups')
  equal(globalThis.cleanup.find(item => item[0] === 'parent'), ['parent', 'parent', 'preset.css'], 'parent cleanup sees its style ownership and preset inventory before removal')
  equal(globalThis.cleanup.find(item => item[0] === 'child'), ['child', 'child'], 'child cleanup retains its separate style ownership')
  equal(owned(), [], 'disconnect removes every managed factory style')
  check(shellIntact(), 'disconnect preserves shell CSS')
  await client.sync(graph([parent]))
  await client.sync(graph([parent, child]))
  equal(globalThis.factoryOrder, ['child', 'parent', 'child', 'parent'], 'reconnect evaluates fresh nested factories')
  check(owned().length === 3, 'reconnect restores exactly the factory styles')
  await client.dispose(); client = undefined
  equal(owned(), [], 'disposal removes every managed factory style')
  check(shellIntact(), 'disposal preserves shell CSS')
  let failure
  try { client = await bootClientModules({ graph: graph([row('failure', '/failure.js')]) }) }
  catch (error) { failure = String(error) }
  check(failure?.includes('intentional factory failure'), 'throwing factory propagates its failure through public boot')
  check(!document.querySelector('#failed-untagged-style') && !document.querySelector('#failed-preset-style'), 'throwing factory removes both new untagged and preset styles')
  check(shellIntact(), 'failed factory preserves shell CSS')
  client = await bootClientModules({ graph: graph([]) })
  await client.dispose(); client = undefined
  check(globalThis.__ModuleLoader__ === undefined, 'failed boot leaves the page available for a fresh lifecycle')
  const readGraph = async name => (await fetch('/release-graph?name=' + name)).json()
  client = await bootClientModules({ graph: await readGraph('a') })
  const releaseState = client.releaseChange
  const notices = []
  const unsubscribe = releaseState.subscribe(() => notices.push(releaseState.getSnapshot()))
  const panelA = document.querySelector('#release-panel')
  const styleA = document.querySelector('#release-style')
  await client.sync(await readGraph('b'))
  equal(releaseState.getSnapshot(), { page: ${JSON.stringify(releaseA)}, previous: ${JSON.stringify(releaseA)}, host: ${JSON.stringify(releaseB)} }, 'same-version different-commit Host is announced')
  check(document.querySelector('#release-panel') === panelA && document.querySelector('#release-style') === styleA,
    'foreign release preserves the exact visible plugin and style nodes')
  check(getComputedStyle(document.body).getPropertyValue('--release').trim() === 'a', 'foreign release cannot change applied CSS')
  client.dismissRelease()
  check(releaseState.getSnapshot() === undefined, 'dismiss hides only the release notice')
  await client.sync(await readGraph('b-future'))
  await client.retry()
  await client.sync(await readGraph('b-future'))
  check(releaseState.getSnapshot() === undefined && document.querySelector('#release-panel') === panelA,
    'dismissed future graph is rejected before parsing and retry retains the accepted page graph')
  await client.sync(await readGraph('c'))
  equal(releaseState.getSnapshot(), { page: ${JSON.stringify(releaseA)}, previous: ${JSON.stringify(releaseB)}, host: ${JSON.stringify(releaseC)} }, 'another Host release announces the preceding observed identity')
  await client.sync(await readGraph('a2'))
  check(releaseState === client.releaseChange && releaseState.getSnapshot() === undefined, 'return to boot release clears the stable observable')
  check(!panelA.isConnected && !styleA.isConnected && document.querySelector('#release-panel').textContent === 'a2',
    'boot-release return replaces the old plugin through its lifecycle')
  check(getComputedStyle(document.body).getPropertyValue('--release').trim() === 'a2', 'boot-release replacement applies its own CSS')
  await client.sync(await readGraph('b'))
  await client.disconnect()
  check(!document.querySelector('#release-panel') && !document.querySelector('#release-style'), 'disconnect withdraws resources while Host release is mismatched')
  await client.sync(await readGraph('a'))
  await client.sync(await readGraph('b'))
  await client.dispose(); client = undefined; unsubscribe()
  check(!document.querySelector('#release-panel') && !document.querySelector('#release-style'), 'dispose withdraws resources while Host release is mismatched')
  const requested = await (await fetch('/requests')).json()
  check(!requested.some(path => path === '/release-b.js' || path === '/release-c.js'), 'foreign releases never fetch or evaluate their factory scripts')
  check(notices.length > 0 && shellIntact(), 'release transitions preserve page shell styles and publish diagnostics')
  document.querySelector('#result').textContent = 'PASS\\n' + checks.join('\\n')
  await fetch('/result', { method: 'POST', body: JSON.stringify({ status: 'passed', checks }) })
} catch (error) {
  reportSafetyProgress('caught-error', String(error))
  await client?.dispose().catch(() => {})
  document.querySelector('#result').textContent = 'FAIL ' + String(error)
  await fetch('/result', { method: 'POST', body: JSON.stringify({ status: 'failed', error: String(error), checks }) })
}
</script>`
let browser
let deadline
let finished = false
const diagnostics = { startedAt: new Date().toISOString(), requests: [], progress: [] }
const saveDiagnostics = () => writeFile(resolve(output, 'browser-diagnostics.json'), JSON.stringify(diagnostics, null, 2) + '\n')
const server = createServer(async (request, response) => {
  try {
    const path = new URL(request.url, 'http://localhost').pathname
    if (path === '/diagnostic' && request.method === 'POST') {
      let body = ''; for await (const bytes of request) body += bytes
      if (diagnostics.progress.length < 256) diagnostics.progress.push({ at: new Date().toISOString(), ...JSON.parse(body) })
      response.end('recorded'); return
    }
    if (diagnostics.requests.length < 256) diagnostics.requests.push({ at: new Date().toISOString(), path })
    if (path === '/result' && request.method === 'POST') {
      let body = ''; for await (const bytes of request) body += bytes
      const result = JSON.parse(body)
      assert.ok(['passed', 'failed'].includes(result.status))
      await writeFile(resolve(output, 'browser-result.json'), JSON.stringify(result, null, 2) + '\n')
      diagnostics.result = result.status
      await saveDiagnostics()
      finished = true
      response.end('recorded'); console.log(JSON.stringify(result))
      clearTimeout(deadline); server.close(); browser?.kill()
      if (result.status !== 'passed') process.exitCode = 1
    } else if (path === '/') {
      response.setHeader('content-type', 'text/html'); response.end(html)
    } else if (path === '/release-graph') {
      const name = new URL(request.url, 'http://localhost').searchParams.get('name')
      assert.ok(['a', 'a2', 'b', 'b-future', 'c'].includes(name))
      response.setHeader('content-type', 'application/json'); response.end(JSON.stringify(releaseGraph(name)))
    } else if (path === '/requests') {
      response.setHeader('content-type', 'application/json'); response.end(JSON.stringify(requests))
    } else if (Object.hasOwn(scripts, path)) {
      requests.push(path)
      response.setHeader('content-type', 'text/javascript'); response.end(scripts[path])
    } else if (/^\/runtime\/[a-zA-Z0-9._-]+\.mjs$/.test(path)) {
      response.setHeader('content-type', 'text/javascript')
      response.end(await readFile(resolve(runtime, path.slice('/runtime/'.length))))
    } else { response.writeHead(404); response.end() }
  } catch (error) {
    diagnostics.serverError = String(error)
    response.writeHead(500); response.end(String(error))
  }
})
server.listen(0, '127.0.0.1', () => {
  const url = 'http://127.0.0.1:' + server.address().port + '/'
  console.log('READY ' + url)
  if (serve) return
  const executable = [process.env.CORDIS_TEST_BROWSER, '/usr/bin/google-chrome', '/usr/bin/chromium',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    'C:/Program Files/Google/Chrome/Application/chrome.exe',
    'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(path => path && existsSync(path))
  if (!executable) { server.close(); throw new Error('Set CORDIS_TEST_BROWSER to an installed Chromium browser') }
  diagnostics.executable = executable
  const fail = async error => {
    if (finished) return
    finished = true; clearTimeout(deadline)
    browser?.kill(); server.closeAllConnections(); server.close(); process.exitCode = 1
    await writeFile(resolve(output, 'browser-result.json'), JSON.stringify({ status: 'failed', error }, null, 2) + '\n')
    diagnostics.failure = error
    await saveDiagnostics()
    console.error(error)
  }
  const browserLog = openSync(resolve(output, 'browser-process.log'), 'w')
  try {
    browser = spawn(executable, ['--headless=new', '--no-first-run', '--disable-default-apps',
      '--user-data-dir=' + resolve(directory, 'browser-profile'), url], { stdio: ['ignore', browserLog, browserLog], windowsHide: true })
  } finally { closeSync(browserLog) }
  diagnostics.pid = browser.pid
  deadline = setTimeout(() => { void fail('Browser result timed out') }, 60000)
  browser.once('error', error => { void fail(String(error)) })
  browser.once('exit', (code, signal) => {
    diagnostics.exit = { code, signal }
    if (!finished) void fail('Browser exited before reporting a result: ' + code + '/' + signal)
  })
})
