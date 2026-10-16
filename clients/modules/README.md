# Browser client modules

This optional TypeScript package reuses the fixed DSH browser module registry, entry reconciliation, Cordis and Loader. It does not require Node in a deployed Host or in ordinary C# plugin development. The copied source and licenses are under `vendor`; `vendor/provenance.json` records the fixed upstream commit and source hashes.

The local `ClientEntries` correction keeps installed code identity separate from activation success. After a failed replacement, explicitly syncing an earlier graph loads that target; retry uses the current target. Download failure before withdrawal leaves the existing Fiber available. This is a bounded correction to a defect also present in the fixed source, not automatic rollback of application state. Provenance retains the original hash and records the local hash and reason separately; the normal build checks both unchanged and adapted file identities.

Build the runtime and its self-contained declarations:

```sh
npm ci --prefix clients/modules
node scripts/build-client-modules.mjs artifacts/client-modules
node scripts/verify-client-module-types.mjs artifacts/client-modules
```

Serve `client.mjs` as a same-origin ES module. Copy the complete output directory when distributing the TypeScript package; its declarations and `LICENSES.txt` accompany the runtime.

An author package has `client.ts`, a package name, and an explicit `dsh.client` declaration:

```json
{"name":"my-panel","dsh":{"client":{"platform":"web","external":["@cordis-net/client-modules"]}}}
```

Authors can import the public `Context` and `Service` values from `@cordis-net/client-modules`. The author build preserves that request, and the browser supplies the same Cordis instance as the page root. Declare cross-package imports in `external`, including `/client` requests, and service-provider package dependencies in `inject`.

```sh
node scripts/build-client-modules.mjs artifacts/client-modules path/to/author-package artifacts/my-panel
```

Add `--watch` as the final argument for optional author development. Each successful build publishes a complete immutable generation directory and a JSON stdout frame `{kind:"client-built",name,revision,directory}`. Compilation failure emits `{kind:"build-error",name,diagnostic}` and preserves earlier directories. A later success publishes a new directory. Changes to the declared external requests require restarting the watch. The Host consumes only successful frames through its session queue, captures a new catalog, and sends its `client-modules` event. Host-owned process startup, cancellation and shutdown remain integration responsibilities; this compiler command does not manage those tasks.

The output is a single classic factory-registration script, `client.js`, and its package manifest. Its factory receives the fixed module registry's `require`; its exported `apply` runs through the existing Cordis Loader. Register DOM, listeners and other contributions with `ctx.effect` so Cordis releases them when the plugin is withdrawn. Package-local dynamic chunks are not supported by this build entry. Existing independent `ClientArtifact` consumers keep their own format contract.

The native Host's `ClientModuleCatalog` selects immutable bytes, dependency rows and revisioned URLs. Its graph contains `rev`, `entries` and `batches`. The browser rejects executable artifact URLs that are outside the page origin or do not identify their declared revision. Successful provider replacement refreshes consumers that imported its previous exports. A failed replacement download retains the active old fiber and exposes a synchronization failure; retry uses the latest desired graph. Explicit `modules.disconnect()` invalidates pending activation and withdraws the roster through the fixed entry mechanism. Initial bootstrap uses the fixed web activation audit: failed imports, failed activation and missing-service Pending entries reject startup and release its facade. Later synchronization retains its diagnostic/retry behavior.

The page shell may supply additional module objects through `bootClientModules({ graph, staticModules, loadBundle })`.
`staticModules` maps **exact requests** to shell-owned objects, including separately declared subpaths. Values keep their identity
across plugin withdrawal and reload. SDK modules are supplied by default; explicit keys take precedence. Keep Cordis imports
on the SDK's runtime instance when providing application services. The native Host declares the same additional names with
`ClientModuleCatalog.CaptureAsync(..., platformModules: names)`; the existing capture entry remains available.
The catalog records names, never serializes module objects, and rejects requests without a declared supplier. Authors list
these requests in `dsh.client.external`. The author build externalizes only exact requests and bundles ordinary dependencies.
An optional `loadBundle(url)` must execute the classic script registration before its promise resolves; omitting it uses
the fixed upstream script-element transport. Artifact URLs still obey the same-origin revision boundary.
See the matching [English](../../docs/development.md#shell-owned-client-modules) and [Chinese](../../docs/development.zh.md#外壳持有的客户端模块) integration examples.

For a Host mapped at `/cordis`, a browser entry can use:

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

The management client waits for the generated notification stream's readiness, then reads full state and graph on connection and reconnection. Selected configuration and client-module notifications invalidate those reads; installation state and log notifications reach subscribers with their original arguments. Notifications have no replay or sequence-gap protocol. Transient connection loss preserves page plugins and drafts. Identical artifacts retain their instances across Host reconnection; actual roster withdrawal, explicit disconnect and page close release effects. Raw configuration mutations require a connected Host generation and are never automatically replayed. `ManagementHttpError` represents a known HTTP refusal, while `ManagementOutcomeUnknown` means the caller cannot infer whether a raw configuration mutation happened.

Plugin and package operations use the actual generated `/management` module. Installation submits the inspection's `contentHash` as `inspectedHash` plus a caller-owned `requestId`; after a lost response the caller queries the same generation's active `waitForInstall`. A null result remains unknown. The shell's fetch carrier supplies authentication and `If-Cordis-Generation`; approval policy belongs to the native Host. See the [standard management migration guide](../../docs/management-remote-migration.md) for public calls and removed handwritten routes and methods.

Settings is consumed through the generated standard Remote module, not handwritten management methods. It uses numeric form revisions and optional expected revisions and exposes secret availability as `{path,set}` sidecars. Full-configuration management methods retain their independent hash fence. Regenerate consumers using the [Settings migration guide](../../docs/settings-contract-migration.md).

`SlotCore` is the fixed generic contribution registry, exported beside the module client. It does not bring a React runtime into the bundle. Its type declarations retain the upstream renderer contracts and ship their licensed React/CSS type dependencies. Import types and augment `SlotMap` through the public `@cordis-net/client-modules/slots` subpath. A host can declare root and child seats; a plugin registers a component and owns the returned disposer with `ctx.effect`. The examples demonstrate a provider declaring a list seat and a separate panel registering into it. Replacement clears the previous registry's entries, and withdrawal removes the panel's contribution. Application-specific renderer and session binding remain outside this package.

`createManagementClient` requires the generated notification client in `events` and accepts optional `transport.fetch` and `recovery`. The carrier is native HTTP envelopes and WebSocket mux. The fixed upstream controller owns handshake deadlines, continuous backoff, browser offline suspension and manual `management.reconnect()`. Failed handshakes close their subscription attempt and retry even when the stream remained open. Injected fetch carriers honor read cancellation; the generated notification client releases its observer when that attempt is aborted. Late callbacks cannot publish readiness. Recovery never replays writes.

`management.close()` closes recovery and withdraws modules; the page still owns `modules.dispose()` and `events.dispose()`. Release all three when disposing the page. The isolated `client-modules-browser-host.mjs` carrier exercises factory scripts in a browser, but does not prove native management, configuration or Host composition.

Run `node scripts/verify-client-shared-modules.mjs` for an independent built-plugin check of the public bootstrap with an injected Node transport.
For real browser transport checks, start `node scripts/client-modules-browser-host.mjs` and open its printed origin at `/shared`.
The page checks default/custom loading, exact subpath refusal, and singleton identity after withdrawal/reload. Browser and Node results are separate evidence.
