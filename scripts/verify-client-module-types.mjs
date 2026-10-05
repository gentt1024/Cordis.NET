import { createRequire } from 'node:module'
import { mkdtemp, mkdir, cp, readFile, writeFile } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const requireTools = createRequire(resolve(root, 'clients/modules/package.json'))
const ts = requireTools('typescript')
const directory = await mkdtemp(resolve(tmpdir(), 'cordis-client-author-'))
await mkdir(resolve(directory, 'node_modules/@cordis-net'), { recursive: true })
await cp(resolve(process.argv[2] ?? resolve(root, 'artifacts/client-modules')),
  resolve(directory, 'node_modules/@cordis-net/client-modules'), { recursive: true })
await writeFile(resolve(directory, 'package.json'), '{"private":true,"type":"module"}\n')
// A real external author resolves the built package, with no workspace paths or ambient Node types.
await writeFile(resolve(directory, 'client.ts'), await readFile(resolve(root, 'clients/modules/examples/provider/client.ts'), 'utf8'))
await writeFile(resolve(directory, 'management.ts'), `import type { ManagementClient, ManagementEvent, SettingsView } from '@cordis-net/client-modules'
export function secrets(view: SettingsView): boolean { return view.secrets.some(secret => secret.path.length > 0 && secret.set) }
export function modulesChanged(event: ManagementEvent): boolean { return event.kind === 'client-modules' }
export function connection(client: ManagementClient) { return client.connection.getSnapshot() }
export function readConfiguration(client: ManagementClient) { return client.readConfiguration('root:plugin') }
export function compatibility(client: ManagementClient) { return client.readCompatibility() }
`)
const program = ts.createProgram([resolve(directory, 'client.ts'), resolve(directory, 'management.ts')], {
  noEmit: true, strict: true, skipLibCheck: false, target: ts.ScriptTarget.ES2022,
  module: ts.ModuleKind.ESNext, moduleResolution: ts.ModuleResolutionKind.Bundler,
  lib: ['lib.es2022.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts'], types: [],
})
const diagnostics = ts.getPreEmitDiagnostics(program)
if (diagnostics.length) {
  console.error(ts.formatDiagnosticsWithColorAndContext(diagnostics, {
    getCurrentDirectory: () => directory, getCanonicalFileName: name => name, getNewLine: () => '\n',
  }))
  process.exitCode = 1
} else console.log('External strict author type consumption passed, including public Context/Service value imports.')
