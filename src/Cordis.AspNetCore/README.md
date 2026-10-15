# Cordis.NET.AspNetCore

Choose the exact version and feed for this package batch; see [getting started](../../docs/getting-started.md) for package-only use and ownership. Published packages and unreleased candidate feeds are distinct.

Authorized HTTP unary and WebSocket mux hosting for generated Remote services, plus management endpoints over a running Cordis profile. Package, configuration and lifecycle operations use the session's existing owners.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft.

The host supplies an authorization callback for every operation and an optional platform package toolchain. Build permission is checked separately from installation permission. The adapter does not provide accounts or login policy.

See the [managed application](../../examples/ManagedApplication/README.md) and [authoring guide](../../docs/authoring.md).

`Map` exposes host state, complete configuration edits and client artifacts. Full configuration and its schema use separate `configuration-read`/`configuration-write` permissions because raw data and defaults can contain secrets. Mount the generated `PluginManager` with `MountPluginManagerAsync` over the same profile operations and toolchain. Settings uses the generated Remote over `SettingsForms`; notifications use the host-selected emit source registered on the same Gateway. `MapCordisRemote` supplies their physical HTTP carrier with host authorization. See the [management](../../docs/management-remote-migration.md) and [Settings](../../docs/settings-contract-migration.md) migration guides; the matching handwritten routes are removed.

Every management mutation requires the generation returned by `/state`; reads may also supply `If-Cordis-Generation`. `MountPluginManagerAsync` applies this fence and parses the actual generated resource arguments before host policy. A replacement host refuses the old generation. Remote calls use the fixed HTTP envelope and streams use WebSocket mux. Reconnect subscribes before reading current state and the full client graph; notifications have no durable delivery or replay promise.

Build authority permits one preparation, including dependency targets resolved from selected sources. The root archive hash binds inspection to preparation; it does not lock or approve every transitive dependency hash. Hosts can inspect the request source and root hash in the separate `build` permission callback.

`MapCordisService` resolves the current Cordis service on each request and uses explicit `JsonTypeInfo` contracts for AOT. A missing or withdrawn provider refuses new requests; a call already running follows that service's own cancellation contract.
