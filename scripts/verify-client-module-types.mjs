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
await writeFile(resolve(directory, 'management.ts'), `import { createManagementClient, type ManagementClient, type ManagementEvent, type ConfigurationOperation, type ConfigurationChange } from '@cordis-net/client-modules'
import type { RemoteClient as PluginManager } from '@cordis-net/client-modules/management'
import type { RemoteClient as Notifications } from '@cordis-net/client-modules/notifications'
export function edit(client: ManagementClient, operations: readonly ConfigurationOperation[], revision: string): Promise<ConfigurationChange> { return client.mutateConfiguration('root:plugin', operations, revision) }
export function modulesChanged(event: ManagementEvent): boolean { return event.kind === 'event' && event.event === 'client-modules/changed' }
export function connection(client: ManagementClient) { return client.connection.getSnapshot() }
export function readConfiguration(client: ManagementClient) { return client.readConfiguration('root:plugin') }
export function compatibility(client: PluginManager) { return client['pluginManager/listVersionExemptions']({}) }
export function install(client: PluginManager, input: Parameters<PluginManager['pluginManager/installBundle']>[0]) { return client['pluginManager/installBundle'](input) }
export function connect(notifications: Notifications) { return createManagementClient({ notifications }) }
`)
await writeFile(resolve(directory, 'shell.ts'), `import { bootClientModules } from '@cordis-net/client-modules'
const shared = { name: 'page-owned singleton' }
export function boot(graph: unknown, loadBundle: (url: string) => Promise<void>) {
  return bootClientModules({ graph, staticModules: { '@example/shell': shared, '@example/shell/tools': { part: shared } }, loadBundle })
}
`)
const program = ts.createProgram(['client.ts', 'management.ts', 'shell.ts'].map(name => resolve(directory, name)), {
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
