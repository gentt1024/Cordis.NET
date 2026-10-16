import {createServer} from 'node:http'
import {readFile} from 'node:fs/promises'
import {resolve} from 'node:path'
import {bundleSource} from '../clients/modules/examples/replacement/check.mjs'

// Serve the exact package installed by verify-client-replacement.mjs, without rebuilding it.
const runtime = resolve(process.argv[2])
const assets = new Map([
  ['/client.mjs', await readFile(resolve(runtime, 'client.mjs'))],
  ['/check.mjs', await readFile(new URL('../clients/modules/examples/replacement/check.mjs', import.meta.url))],
])
const page = `<!doctype html><meta charset="utf-8"><title>Browser target replacement</title>
<button id="run">Run replacement recovery</button><pre id="result">Ready</pre>
<script type="module">
import {bootClientModules} from '/client.mjs';
import {verifyReplacement} from '/check.mjs';
document.querySelector('#run').onclick = async () => {
  document.querySelector('#run').disabled = true;
  const result = document.querySelector('#result');
  try {
    const evidence = await verifyReplacement(bootClientModules, request => new Promise((resolve, reject) => {
      const script = document.createElement('script'); script.src = request;
      script.onload = () => {script.remove(); resolve()};
      script.onerror = () => {script.remove(); reject(new Error('Bundle request failed: ' + request))};
      document.head.append(script);
    }), false);
    result.textContent = 'PASS HTTP factories, failed replacement, explicit V1 sync, dependent exports, stable owner, retry, effects and DOM styles\\n' + JSON.stringify(evidence, null, 2);
  } catch (error) {result.textContent = 'FAIL ' + error.stack}
};
</script>`
const server = createServer((request, response) => {
  const path = new URL(request.url, 'http://localhost').pathname
  const body = path === '/' ? page : assets.get(path) ?? (['stable', 'subject', 'dependent'].some(id =>
    path === '/replacement/' + id + '.js') ? bundleSource(request.url) : undefined)
  if (body === undefined) {response.writeHead(404); response.end(); return}
  response.setHeader('content-type', path === '/' ? 'text/html' : 'text/javascript')
  response.setHeader('cache-control', 'no-store')
  response.end(body)
})
server.listen(0, '127.0.0.1', () => console.log('READY http://127.0.0.1:' + server.address().port))
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => {server.close(); server.closeAllConnections()})
