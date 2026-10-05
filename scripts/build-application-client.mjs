import { build } from '../reference/node_modules/esbuild/lib/main.js'
import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'

const output = resolve(process.argv[2] ?? 'artifacts/application-client')
const source = resolve(process.argv[3] ?? 'examples/Probes/client')
await mkdir(output, { recursive: true })
await build({ entryPoints: [resolve(source, 'application.ts')], bundle: true, format: 'esm', platform: 'browser',
  outfile: resolve(output, 'client.mjs'), nodePaths: [resolve('reference/node_modules')],
  alias: { '@deepseek-ai/dsh-client-store': resolve(source, 'vendor/store/index.ts') },
  define: { 'process.env.NODE_ENV': '"production"' } })
await writeFile(resolve(output, 'package.json'), JSON.stringify({ name: 'demo-client', version: '1', type: 'module',
  dsh: { client: { platform: 'web' } }, exports: { './client': './client.mjs' } }, null, 2) + '\n')
