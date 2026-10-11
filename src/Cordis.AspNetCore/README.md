# Cordis.NET.AspNetCore

Optional ASP.NET Core management endpoints over a running Cordis profile. Package, configuration and lifecycle operations use the session's existing owners.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft.

The host supplies an authorization callback for every operation and an optional platform package toolchain. Build permission is checked separately from installation permission. The adapter does not provide accounts or login policy.

See `examples/ManagedApplication` for the maintained application example and `docs/authoring.md` for configuration authoring. This package is introduced in `0.2.0-alpha.3`; it is not included in the published `0.2.0-alpha.1` batch.

`Map` exposes host state, complete configuration edits and client artifacts. Full configuration and its schema use separate `configuration-read`/`configuration-write` permissions because raw data and defaults can contain secrets. Mount the generated `PluginManager` with `MountPluginManagerAsync` over the same profile operations and toolchain. Settings uses the generated Remote over `SettingsForms`; notifications use the host-selected emit source registered on the same Gateway. `MapCordisRemote` supplies their physical HTTP carrier with host authorization. See the [management](../../docs/management-remote-migration.md) and [Settings](../../docs/settings-contract-migration.md) migration guides; the matching handwritten routes are removed.

Every management mutation requires the generation returned by `/state`; reads may also supply `If-Cordis-Generation`. `MountPluginManagerAsync` applies this fence and parses the actual generated resource arguments before host policy. A replacement host refuses the old generation. HTTP JSON/NDJSON is a native carrier adaptation. Reconnect subscribes before reading current state and the full client graph; notifications have no durable delivery or replay promise.

Build authority permits one preparation, including dependency targets resolved from selected sources. The root archive hash binds inspection to preparation; it does not lock or approve every transitive dependency hash. Hosts can inspect the request source and root hash in the separate `build` permission callback.

`MapCordisService` resolves the current Cordis service on each request and uses explicit `JsonTypeInfo` contracts for AOT. A missing or withdrawn provider refuses new requests; a call already running follows that service's own cancellation contract.
