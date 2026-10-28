# Cordis.NET browser client modules

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/clients/modules/README.zh.md)

Load browser plugin factories, reconcile entries and manage their Cordis effects using the fixed DSH module registry and Loader. The page supplies its renderer and authentication. Running the browser or an ordinary C# plugin does not require Node; source builds and author tools do.

## Obtain the browser runtime

Install the published archive for package imports and declarations:

```console
npm install https://github.com/gentt1024/Cordis.NET/releases/download/v0.2.1-alpha.1/cordis-net-client-modules-0.2.1-alpha.1.tgz
```

For direct browser loading, serve the complete built runtime directory at a same-origin path such as `/client-runtime`. Keep its modules, declarations and `LICENSES.txt` together. [ManagedApplication](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md) shows native Host artifact mapping. Runtime and author-tool archives have separate roles and are both on the [matching release](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1).

## Build a plugin for the Loader

Follow the [author-tool tutorial](https://github.com/gentt1024/Cordis.NET/blob/main/clients/tools/README.md) to create a plugin and produce `client.js` with its manifest. The classic registration script receives the registry's `require`, and its exported `apply` runs through the Loader. Register DOM, listeners and other cleanup with `ctx.effect`. Package-local dynamic chunks are unsupported by this build entry; independent `ClientArtifact` consumers retain their separate format.

Authors import `Context` and `Service` from `@cordis-net/client-modules` to share the page's Cordis instance. External requests match exactly, including subpaths; service-provider dependencies use `inject`. For watch, the Host consumes only successful `client-built` frames through its session queue, captures a new catalog and sends the module-change notification. The Host owns watcher startup and shutdown.

## Connect a page to its Host

The Host must expose generated management and notification contributions through `/remote`, with management state at `/cordis`. Serve the runtime at `/client-runtime`. The following module fragment belongs inside a page that supplies `renderConnection` and `renderProgress`; it is not an entire page shell:

```js
import { bootClientModules, createManagementClient } from '/client-runtime/client.mjs'
import { createEvents } from '/client-runtime/events.mjs'
import { TypertConnection } from '/client-runtime/cordis-remote.mjs'

const modules = await bootClientModules({ graph: { rev: 'initial', entries: [], batches: [] } })
const connection = new TypertConnection(location.origin + '/remote')
const events = createEvents(modules.context, connection)
const management = createManagementClient({ baseUrl: '/cordis', modules, events })
management.connection.subscribe(() => renderConnection(management.connection.getSnapshot()))
management.subscribe(event => renderProgress(event))
```

`createManagementClient` waits for the notification stream's readiness before reading full state and graph, including on reconnection. Configuration/module notifications invalidate these reads, and installation state/log notifications retain their arguments. Transient connection loss preserves page plugins and drafts. Notifications provide no replay or sequence-gap protocol.

Use the generated `/management` module for plugin/package operations. Installation sends the inspection's `contentHash` as `inspectedHash` and a caller-owned `requestId`. After losing a response, query that generation's active `waitForInstall`; null means unknown. The fetch carrier supplies authentication and `If-Cordis-Generation`. Raw configuration mutations require the connected generation and are never replayed. `ManagementHttpError` is a known HTTP refusal; `ManagementOutcomeUnknown` means the mutation outcome is unknown.

Use the generated Settings module for live forms with numeric revisions, optional expected revisions and secret-presence sidecars. Full configuration retains its separate hash fence. See [Settings](https://github.com/gentt1024/Cordis.NET/blob/main/docs/settings.md) and the [management contract](https://github.com/gentt1024/Cordis.NET/blob/main/docs/management-remote-migration.md).

## Supply modules and UI contributions

`bootClientModules({ graph, staticModules, loadBundle })` can use shell-owned module objects. `staticModules` keys are exact requests, including explicitly declared subpaths. Objects keep their identity across plugin withdrawal and reload. SDK modules are defaults, and explicit keys take precedence. Keep Cordis imports on the SDK runtime instance. The Host declares matching names through `ClientModuleCatalog.CaptureAsync(..., platformModules: names)`; names, not objects, enter the catalog. Undeclared suppliers are refused.

An optional `loadBundle(url)` must execute classic-script registration before resolving. The default uses the upstream script-element transport. Executable URLs must be same-origin and identify their declared revision. See [shell-owned modules](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.md#shell-owned-client-modules).

`SlotCore` supplies the generic contribution registry without a React runtime. Its declarations include licensed React/CSS type dependencies. Import and augment `SlotMap` through `@cordis-net/client-modules/slots`. The application owns rendering and session binding; a plugin owns its contribution disposer through `ctx.effect`. Factory-owned styles are released on failure or withdrawal without taking ownership of shell or nested-factory styles. SVG/PNG/WebP data URLs and inline-slot hints are supported; hints do not install a renderer.

## Handle release changes, recovery and shutdown

Catalogs contain `rev`, `entries`, `batches` and optional `release: { version, commit? }`. The application supplies release identity when capturing the catalog; changing it alone does not change artifact revisions. `modules.releaseChange` reports `{ page, previous, host }`. `dismissRelease()` hides the notice without admitting the new graph; the shell chooses whether to offer a reload. Version and optional commit must match the boot release before a graph is parsed or applied.

Initial bootstrap refuses failed imports, activation errors and missing-service Pending entries and releases its facade. Later synchronization reports failures and supports retry. A replacement download failure retains the old active Fiber. Explicitly syncing an earlier target loads that target; retry uses the latest desired target. This is not automatic rollback of application state. Successful provider replacement refreshes importing consumers.

Recovery owns handshake deadlines, backoff, offline suspension and `management.reconnect()`. Failed handshakes release their attempt, read cancellation is honored and late callbacks cannot publish readiness. Recovery never replays writes. Explicit `modules.disconnect()` invalidates pending activation and withdraws the roster. On page teardown, close management recovery, then dispose the event client and shared Connection before their borrowed module Context. Dispose the modules facade as well.

## Build from source

From the repository root, use Node and the SDK pinned in `global.json`; the build invokes the .NET ClientAssets generator:

```sh
npm ci --prefix reference --ignore-scripts
npm ci --prefix clients/modules --ignore-scripts
node scripts/build-client-modules.mjs artifacts/client-modules
node scripts/verify-client-module-types.mjs artifacts/client-modules
```

Source and license identities are recorded in `vendor/provenance.json`. Adapted files retain original and local hashes with reasons. For the browser transport check, start `node scripts/client-modules-browser-host.mjs` and open its printed origin at `/shared`. The injected-transport check is `node scripts/verify-client-shared-modules.mjs`; a Node result is not a browser result, and neither alone proves complete native Host composition.
