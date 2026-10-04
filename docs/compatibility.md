# Compatibility and evidence

[中文](compatibility.zh.md)

Cordis.NET targets the pinned DeepSeek Harness vendored Cordis behavior described in [upstream](upstream.md). Compatibility is assessed per observable contract, not by package name or test count alone.

## Status vocabulary

- **Implemented:** the .NET behavior exists.
- **Executed:** a named .NET test, upstream source test, or paired scenario ran.
- **Assertion reviewed:** the upstream setup, helpers, and assertions were individually matched or given an explicit adaptation.
- **Not supported:** the behavior is outside the product or platform boundary.

The immutable historical inventory targets DSH `ddefc45fbc7f8e46dd73185e68295696d1297887` and contains 631 upstream candidate instances. It does not describe assertion closure for the new baseline. The current upgrade scope, behavior mapping and platform adaptations are recorded in [the upgrade matrix](upgrade-0.2.0-rc.2.md). Current disposition records 323 as implementation complete. Of those, 36 have a complete assertion review and 287 have not received a complete assertion review. The remaining 308 are explicitly non-complete, usually because they concern JavaScript or DeepSeek Harness product behavior outside Cordis.NET. These figures do not claim a compatibility percentage.

## Deployment boundaries

| Path | Supported behavior | Boundary |
|---|---|---|
| Static Core and Composition | JIT and Native AOT; static module registration, lifecycle, configuration, profiles and selected extensions | New plugin code requires republishing the application |
| CLR adapter | Runtime loading, replacement, rollback, and cooperative unload of managed assemblies | Ordinary runtime only; retained CLR references can prevent collection |
| JavaScript adapter | Actual `!!js` evaluation and a controlled context bridge through Jint | Trusted configuration only; no Node module environment, sandbox, or Native AOT claim |

Cordis.NET does not execute arbitrary server-side TypeScript plugins. JavaScript prototypes, thrown primitives, cyclic object graphs and Node resolution are not general platform compatibility promises. Agent/LLM business behavior, RSI goals and FSM domain logic belong to products. Reusable client/UI, diagnostics and installation mechanisms remain repository scope; their protocols and Node implementations require explicit adaptations above Core. A historical inventory describes its recorded coverage, not permanent repository exclusions. An in-scope gap is not an available feature.

`ConfigObject<T>` is a Composition authoring adapter over the existing `ConfigSchema<T>`, not a second validator. It combines explicit complete data-field projections into live bindings, strict ordinary comparison and complete persistence. The author retains responsibility for defaults, validation, raw round trips and complete field coverage. This source addition is not available in the already published `0.2.0-alpha.1` packages.

Field editing reuses fixed ConfigEditor responsibilities: ownership, normal hooks/validation, complete raw user overrides, overlay refusal, durable write and cooperative recovery. It keeps YAML flow roots when needed to preserve unrelated comments. Revisions are native content/activation hashes; non-round-trippable `Undefined` SET values are refused without changing runtime sentinel semantics. Deployment restart requirements refuse edits before persistence. Settings currently exposes only selected top-level primitive live snapshots and enforces selection/redaction on submission too. User-source presence includes uniquely inserted source rows as an explicit native adaptation; it is not inferred by comparing effective values. Reset, multi-field atomic settings submissions, full Schemastery and JSON Schema export remain gaps. Host secret policy is explicit because Core does not infer secret metadata.

Client artifacts reuse `dsh.client.platform=web` and `exports['./client']` through deployment package routing. Canonical containment covers directory links. Self-contained ESM, SHA-256 URLs, ETag and polling invalidation are the supported example adaptation; external imports, the full DSH module graph, RPC and UI rendering are not asserted. Correct and targeted broken implementations are exercised by the existing authoring gate. Node ESM execution evidence is separate from browser rendering and upstream source tests. None of these additions changes the formal behavior lock or the published package batch.

Deployment generations accept explicit external linked roots alongside the existing package and local maps. A generation captures each link's canonical target; removing a root stops routing its importers without unloading retained modules. Re-adding that name with another target requires a restart, including after an intervening removal. Linked peers are read from current ancestor manifests; private lookups remain host-provided, and CLR exports remain explicit mappings. This does not install a Node loader or emulate Node exports, package self-references, CommonJS/ESM differences, or worker propagation.

HMR awaits candidate lifecycle settlement, which may leave a fiber Pending until its services appear. Activation failure restores the previous plugin; secondary cleanup, recovery, and diagnostic observer failures do not replace the original error. Retained configuration references freeze at their generation's last committed value, while replacement and recovery fibers receive fresh references. A reference whose generic value type belongs to a collectible bundle can retain that bundle until the host releases the reference. CLR bundles resolve private managed dependencies within their shadow copies or through explicitly shared host contracts; an unrelated default-context assembly cannot satisfy a missing private dependency. Framework assemblies still use the runtime, and native-library lookup retains the CLR/OS loading rules.

Cooperative unload observer errors are exposed as text snapshots in `ClrUnloadObservation.UnloadError`, without retaining the exception instance or its collectible type. `UnloadRequested` records an attempt; `IsCollected` observes the managed load-context wrapper. An `Unloading` handler that throws can interrupt physical release even after that wrapper is collected, leaving shadow DLLs locked. The host must repair or remove the failing handler and explicitly request `Unload()` again while it still holds the actual context; the adapter does not retry automatically. Shadow deletion is a separate result and can remain blocked by retained references or interrupted release.

Patch source loading, field editing and Settings attribution treat falsey `insert` values (including `null` and `false`) as ordinary overrides, following the pinned Include application rule. The pinned ConfigEditor instead uses undefined or field-presence checks; inheritance restoration here deliberately follows Include so returning to the inherited configuration removes the effective override and permits later base updates. Real inserts, unrelated fields and source comments retain the existing editing contract.

## Evidence

The latest completed run is summarized in [validation](validation.md). `docs/upstream-tests.json` is the immutable candidate inventory; `docs/test-map.json` contains current dispositions; `docs/scenario-map.json` records differential scenarios. Upstream source execution, .NET tests, paired traces, and manual assertion review remain separate evidence categories.
