# Standard management and notification contracts

[中文](management-remote-migration.zh.md)

The current source uses one generated `pluginManager` contract and the generated
`$events` stream. `PluginManager` shares the running profile's
`PluginConfigurationOperations`, toolchain and installation table. The maintained CLI and
ManagedApplication consume these contracts. This source change requires rebuilding clients;
it does not change a published package or persisted profile format.

## Public operations

| Responsibility | Generated methods |
|---|---|
| Inventory | `listPlugins`, `listBundles`, `sources`, `versions` |
| Persistent enablement | `setPluginEnabled`, `setBundleEnabled` |
| Package installation | `inspect`, `installBundle`, `waitForInstall`, `cancelInstall`, `removeBundle` |
| Exact runtime exemptions | `listVersionExemptions`, `setVersionExemption` |
| Selected host notifications | `$events` |

Installation uses an exact `PackageRequest { name, version, source }` and
`PluginInstallOptions { requestId, inspectedHash, enabled? }`. NuGet sources and SDK preparation
are .NET adaptations of the fixed package workflow. Build approval permits package code
execution; an exact-version exemption only changes runtime compatibility admission.

`PluginManagerChange.changed` compares profile manifest, patch and compatibility-file bytes,
matching the fixed workflow's persisted-change observation. A new exact-version grant reports
true even when manifest and patch are unchanged; repeating that grant reports false.
Runtime application remains a separate result field. The .NET workflow has no pnpm workspace
approval file; build authority is supplied by the host for each preparation.

Unreadable compatibility content remains unknown to the change observer. The existing
compatibility authority reports warnings and refuses grants and unsafe rewrites. This retains
the documented .NET startup/data-protection policy while returning a failed grant result;
manifest and patch reads remain required source observations.

The caller first inspects the archive, then submits its `contentHash` as `inspectedHash`.
`installBundle` registers the request ID before reinspection, hash comparison and host build
approval. These steps use the existing installation owner. Initial resource denial returns
`plugin-manager/forbidden`. An admitted hash mismatch returns a failed `PackageChange` with
`error: "inspection-changed"`; build denial returns `error: "build-not-approved"`. Neither
reaches SDK preparation. Cancellation and owner shutdown join admitted work, including an
asynchronous build decision. An unbounded host decision can therefore delay shutdown.

`PackageChange` separates installation, selection, application, restart and retained files.
`waitForInstall` observes only a task active at lookup. Its null result establishes no outcome.
`cancelInstall` waits for draining; its status can be `cancelled`, `failed`, `too-late` or
`not-running`. Losing a response does not authorize resubmitting a mutation. A saved installed
package update returns `restart-required` and leaves the running generation on its old code.

The host supplies resource and build authorization. `CordisManagement.MountPluginManagerAsync`
adapts an ASP.NET request identity and the host generation fence to the generated service.
Mutations require the current `If-Cordis-Generation`; reads can send it too. The host's endpoint
authorization still controls the physical `/remote` carrier. Required nullable DTO fields are
sent explicitly as null, so the generated .NET client validates the same contract the service emits.

## Generated client use

The build tool `tools/Cordis.ClientAssets` exports the actual runtime contributions as
`management.mjs`, `events.mjs`, `settings.mjs` and their declarations. The browser SDK
ships them through `/management`, `/events` and `/settings` package exports. It does not
keep handwritten PluginManager DTOs or method decoders.

```js
import { createRemote, TypertConnection } from '@cordis-net/client-modules/management'
const connection = new TypertConnection(new URL('/remote', location.href), authorizedFetch)
const manager = createRemote(connection)
const inspected = await manager['pluginManager/inspect']({ request })
if (!inspected.ok) throw inspected.error
const installed = await manager['pluginManager/installBundle']({
	request,
	options: { requestId, inspectedHash: inspected.value.contentHash },
})
```

`authorizedFetch` supplies the current host generation and the application's request identity.
Both generated clients return Remote results. C# uses `Task<RemoteResult<T>>` for `InspectAsync`,
`InstallBundleAsync` and `WaitForInstallAsync`; check `Ok` before reading `Value` or `Error`.
Assembly failures remain exceptions. Stream handles yield notification values and throw on
stream failure. Await client and Connection disposal; a client borrows its Connection.
Browser WebSocket authentication follows the application's admission policy, supplied through
the Connection socket factory when needed; fetch headers do not authenticate a browser socket.
Composition ships the selected service model at
`cordis/typert/Cordis.Composition.cordis.model.json`; the existing `emit-dotnet` command and
`CordisTypertClientModel` build property consume that model without loading a provider.

## Notification ownership

The host selects emit names and argument codecs with `TypertRemoteEvents.FromCordis`, then
registers one source with `TypertGateway.RegisterRemoteEvents`. The Gateway runs one source
pump and broadcasts to each subscriber. The fixed event stream first sends `{ type: "ready", clientId, host: { home } }`,
then `{ type: "emit", event, args }`. Subscribe before reading current state and graph.

Disconnect removes one observer. Source withdrawal closes all observers immediately, then
cancels and joins the source. Events have no replay, durable delivery or exactly-once promise.
Reconnection subscribes again and reads the current authorities. The browser recovery client
uses the existing connection controller and retains plugins and drafts during temporary loss.
`createManagementClient` takes a generated notification client in `options.events` and
an optional `transport.fetch`; `transport.openEvents` is removed.

Iterator read and disposal errors reach the owner. C# can surface an async iterator's finally
error through `MoveNextAsync`, so the registration retains unexpected read failures as cleanup
failures too. This can block replacement even when a failing source actually released its
resources. It avoids reporting unconfirmed cleanup as success. Cancellation is cooperative;
a source that does not settle can hold owner cleanup open.

## Removed paths and remaining scope

The handwritten HTTP plugin, bundle, source, inspection, installation, exemption and SSE
routes are removed. The browser management facade's matching methods and decoders are removed
with them. No legacy route or versioned Remote is retained. Native host state, raw configuration
and client artifact routes retain their independent duties and authorization.

Settings keeps its single [numeric revision contract](settings-contract-migration.md).
Maker adaptation belongs to its own session: regenerate clients, bind current host authorization,
subscribe before snapshot reads, and preserve unknown installation outcomes. This repository
does not modify Maker or add a permanent compatibility path for its old generated code.

The shared Connection uses the fixed unary envelope, unary multipart attachments and text WebSocket mux.
Per-method NDJSON and generator-embedded carrier code are removed. Host-selected emit notifications
do not complete typed uplink, reverse Context resolution, waterfalls or owned-value graphs. The original [scope ledger](development.md) and [validation record](validation.md)
retain those responsibilities and the exact platform evidence.

Typed emit and scoped waterfall authoring, reverse Context resolution and owned cleanup are described in [Remote events](remote-events.md). The former notification Remote and `/notifications` client export are removed; regenerate consumers using `/events` and `createEvents`.
