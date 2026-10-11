# Runtime ownership and platform adaptations

## Typert and bundle contract ownership, 2026-10-09

Module exports select plugin entries; they are separate from Cordis service provision. The CLR
adapter shares an assembly/load-context generation across the exports of one physical bundle,
while Loader retains each entry's configuration and Fiber. Complete bundle replacement reuses
Loader/HMR activation and recovery. An optional CLR artifact factory follows that same resolver;
it does not introduce a Core export table.

Composition owns the native Typert declaration model, explicit JSON codecs, Fiber-owned registry,
Loader artifact discovery and carrier-independent Remote dispatcher. A Roslyn generator shipped
in the Composition package adapts source analysis to the pinned .NET SDK. It emits typed delegates
and metadata factories without loading plugin assemblies or scanning their methods at runtime.
System.Text.Json source metadata supplies schemas and serialization; the same descriptors emit
TypeScript clients. ASP.NET Core supplies the authorized HTTP/NDJSON carrier above this layer.
Actual visible services determine routing; a descriptor alone cannot invoke a provider.

The native implementation preserves atomic registration, identity conflicts, exact-owner
withdrawal, descriptor history and stale-call fences for the implemented subset. Invocation
Context uses explicit providers and an asynchronous ambient call instead of JavaScript's rebound
`this.ctx`. Collectible typed metadata belongs to its bundle generation: replacing that generation
requires retiring and rebuilding its TypertLoader owner, or using host-shared DTO contracts.
Neither path supplies product admission, draining or business recovery policy.

This is an incremental implementation of the original application infrastructure scope. The
native carrier is not the complete fixed Typert wire protocol. The [scope reconciliation](development.md#application-infrastructure-scope-reconciliation-2026-10-09)
and [compatibility record](compatibility.md) distinguish verified paths from remaining type-graph,
transport and production-consumer work; earlier HTTP management acceptance remains historical.

## Current workflow decisions

The fixed DSH source remains the behavioral authority. Its existing comparison with the
native implementation is applied incrementally, rather than repeated as a new inventory.
The [scope reconciliation](development.md#application-infrastructure-scope-reconciliation-2026-10-09)
continues to own completeness. Maker is a consumer of that scope.

| Workflow | Fixed DSH responsibility | Native expression and owner | Observable boundary |
|---|---|---|---|
| Ordinary author and caller | Plugin provision, Fiber-owned effects, dependency activation and caller Context | Existing `Plugin`, `Provide`, `Reflect.Read<T>` and explicit `Service<TState>.CreateView` | Analysis selection does not introduce Remote registration or replace Cordis with container scopes. |
| Model and build | Authored declaration graph precedes codec, catalog and client projections | SDK Roslyn extracts selected declarations; Composition's MSBuild target publishes data-only models before `CoreCompile` | Definitions, constructed references and type-parameter ownership remain distinct. A backend's refusal does not remove analysis facts. |
| Settings view and write | Active live Entry forms, layers, redaction, optional view revision and ConfigEditor commit | Existing profile/configuration owner supplies layers and performs one complete candidate commit; a service-owned revision projection observes the form | Client view revision and file hash protect different conflicts. Replace resets live fields while retaining ordinary fields. Neither adds a whole-runtime transaction. |
| Management | Accepted install work belongs to PluginManager, not the waiting connection | Generated `PluginManager` calls the existing operations/session/toolchain and active request table | Inspection, hash checks, build approval and preparation share one admitted task. Disconnecting a waiter does not cancel installation. Explicit cancel joins work; updating installed code requires restart. |
| Remote and carrier | Context/reference ownership, invocation, streams, events and attachments | Existing Registry/Gateway retain authority; ASP.NET/HttpClient carry the supported native subset | JSON/NDJSON and base64 are current adaptations. Peer, uplink, event and attachment gaps require their own implementation and cross-end evidence. |
| Artifact update | Installed selection and current activation have separate lifetimes | Stable deployment directories, shared bundle/ALC and existing restart policy | Saving V2 does not change V1's running descriptors/codecs. Unload request, collection and deletion remain separate. |

Ordinary declaration models use a v2 artifact with `.cordis.model.json` extension. The supported
Remote wire projection uses the v1 `.cordis.typert.json` artifact. Both source entry points consume
the same declaration member, reference, default, attribute and provenance interpretation. Remote
selection and serializer naming are projections of those facts. Conflicting naming policies on
the same reachable DTO graph are refused. Historical artifact bytes are not a compatibility duty.
No provider is loaded for analysis.
External declarations identify their assembly and metadata name with `metadata-reference`
provenance. Their implementation members and source initializer bodies are not reconstructed.
Independent projections include a declaration catalog and selected ordinary .NET DTO/STJ data.
The same authored DTO can serve as an ordinary root and a Remote boundary. The two artifact entry
points remain: a saved v2 artifact cannot yet reconstruct a Remote service, and referenced-model
composition and generic DTO backends remain open.

Roslyn, STJ, MSBuild/NuGet and the existing transport mechanics remain the selected tools.
This continuation introduces no new RPC, schema or DI dependency. A complete type graph,
all model backends and rich protocol interoperability remain separate open responsibilities.

## ADR 001: one explicit execution domain

Cordis's first activation is scheduled after the current synchronous registration segment.
The .NET port uses a queue-backed SynchronizationContext behind `Context.RunAsync`. Plugin
callbacks and their captured continuations run there; awaited operations can interleave.
No monitor is held while awaiting user code. Ordinary thread-pool Task.Yield alone cannot
provide the same registration/dispose boundary. Ambient ExecutionContext is captured per
caller. `ConfigureAwait(false)` code must marshal back before manipulating Cordis.

The stable Context/Fiber pair owns effects, nested fibers, dependency snapshots and raw and
resolved configuration. `WaitAsync` joins current work without waiting for an absent future
provider. Root Fiber cleanup/restart is distinct from permanent .NET host Context disposal.
The historical scheduler was reused only after source inspection and fresh paired tests.

## ADR 002: explicit service views and data boundaries

Service identity is name plus realm. The generic type is a cast, not a second namespace.
`Get` is explicit lookup; injection-aware property access goes through Reflect. C# cannot
transparently proxy arbitrary mutable concrete objects in Native AOT. Typed service views
must forward to shared state, with an explicit caller context for effect ownership and the
provider context for shadowed dependency reads. The same mechanism is used in JIT and AOT.
No autogenerated proxy or reflection scanning is required.

Configuration in Core is an object supplied by the caller; Config delegates validate and
transform it. File data uses explicit mappings/lists and JsExpression nodes. YAML parsing
uses the low-level event API to avoid runtime type serialization. Loader resolves only entry
root configs after injection readiness; tree carrier rows and child caller-owned configs
preserve their own resolution boundaries. Missing keys and explicit null are separate.

## ADR 003: composition and deployment

Loader owns entry identities and live tree operations. Include owns original parsed input,
patch reapplication and durable file writes. Profile composition collects ordered sources
and anchors inserted relative modules at each source. None of these obtains code from NuGet
at runtime. StaticModuleResolver is explicit; packages ship normal assemblies and resources.

Core/Composition/Extensions are the static path. CLR, JavaScript and Hosting are separate
optional dependencies. CLR bundles use a collectible AssemblyLoadContext with explicit
shared-contract identity and complete shadow directories. Stop, replacement, unload request,
collection and file deletion are separate observations. Tests force collection only to
observe leaks; production does not make forced GC a lifecycle success condition.

HMR serializes config and code mutations, rejects nested transactions, and avoids waiting
on its own callback during teardown. A framework change invokes an explicit host restart
hook. File/config changes and code replacement remain separate operations.

## Application infrastructure ownership

Repository scope includes generic application infrastructure above Cordis, not only Core.
`PluginConfigurationOperations` owns profile management mutations; `ProfileSession` owns the
running Context, Loader, Include and HMR coordination. Host, client, transport and package
adapters reuse these owners. Products choose domain services, pages, identities and policies.
Reusable TypeScript client mechanisms can remain TypeScript; their location in a UI directory
does not exclude them. Scope does not imply that an unimplemented adapter is available.

`ConfigObject<T>` belongs to Composition and builds the existing `ConfigSchema<T>` contract.
It shares explicit field projections across reference binding, ordinary comparison, description
data and complete persistence without changing Core validation or update paths. Transparent
plugin wrappers forward `IConfigurationPlugin` as well as `IPlugin`. The wrapper owns forwarding
that optional declaration; the runtime does not infer missing declarations from the wrapped object.

Field edits use `ProfileSession.ConfigurationOperations`, its existing exclusive queue and
normal Loader validation/reconciliation. A content/activation hash fences stale writes;
`Saved` and `Applied` describe separate outcomes. Recovery restores source and attempts
normal reconciliation, without promising an atomic rollback of the entire live tree.
`Fiber.ConfigurationValues` exports only detached published reference snapshots; it does
not export or retain the plugin configuration object.

`SettingsPolicy` selects fields above Core. Ordered edits create one complete raw candidate,
validate through the existing Loader, and perform one source commit. Explicit descriptor annotations
carry detached data; Composition interprets form metadata and redacts settings. They do not add a
second validator. Schemastery and JSON Schema are separate projections with explicit limitations.

`DotnetPluginToolchain` owns NuGet acquisition and SDK preparation in Clr. Profile operations hold
the existing file/mutation lock, then enter the session queue for publication and reconciliation.
Build execution approval is independent of DSH version grants. The ASP.NET Core adapter and CLI
call these same operations. HTTP generations and content revisions fence stale requests; active
installation waiting supplies no durable receipt and never authorizes replay after an unknown response.

`ClientArtifact` still captures immutable bytes. `ClientModuleCatalog` adds selected dependencies
and graph identities; the TypeScript package reuses the fixed Cordis/Loader and client module
mechanisms. Compiled factory registrations are an optional browser authoring path; backend C#
authors do not need Node. Completed catalogs are published by the host, and failed builds must
retain their preceding complete catalog. Product pages and identities remain application-owned.

## Platform evidence

The same scenario executable is published and executed under Native AOT, with
RuntimeFeature.IsDynamicCodeSupported checked at startup. Dynamic DLL loading is isolated
and marked as requiring dynamic/unreferenced code. The Jint reflection bridge is not asserted
AOT-safe. See [Microsoft's Native AOT constraints](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
and [cooperative assembly unloading](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability).
