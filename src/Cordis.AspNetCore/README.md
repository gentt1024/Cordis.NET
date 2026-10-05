# Cordis.NET.AspNetCore

Optional ASP.NET Core management endpoints over a running Cordis profile. Package, configuration and lifecycle operations use the session's existing owners.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft.

The host supplies an authorization callback for every operation, a settings visibility policy per entry and an optional platform package toolchain. Build permission is checked separately from installation permission. The adapter does not provide accounts or login policy.

See `examples/ManagedApplication` for the maintained application example and `docs/authoring.md` for configuration authoring. This package is available from current source; it is not included in the published `0.2.0-alpha.1` batch.

`Map` exposes management reads, settings and complete configuration edits, package progress and active installation queries. Full configuration and its schema use separate `configuration-read`/`configuration-write` permissions because raw data and defaults can contain secrets. Settings reads and exports use the host's selection/redaction policy.

Every mutation requires the generation returned by `/state`; reads may also supply `If-Cordis-Generation`. A replacement host refuses the old generation. HTTP/SSE is a native contract, not a Typert wire implementation. Reconnect reads current state and the full client graph; events are bounded notifications, not a durable log.

Build authority permits one preparation, including dependency targets resolved from selected sources. The root archive hash binds inspection to preparation; it does not lock or approve every transitive dependency hash. Hosts can inspect the request source and root hash in the separate `build` permission callback.

`MapCordisService` resolves the current Cordis service on each request and uses explicit `JsonTypeInfo` contracts for AOT. A missing or withdrawn provider refuses new requests; a call already running follows that service's own cancellation contract.
