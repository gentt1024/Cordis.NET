# Development and verification

English | [中文](development.zh.md)

## Current support and release status

The published preview is [0.2.1-alpha.1](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1), released on 2026-10-27 from tag commit `a2728c7e929a01a586fc714d9b397eaf9549e153`. It uses the fixed DSH 0.2.1-rc.2 baseline and recorded Windows adaptation. [Release evidence](validation.md#released-version-evidence) identifies its formal validation and downloadable artifacts.

Current main also contains two fixes that have **not been published**: [PR #28](https://github.com/gentt1024/Cordis.NET/pull/28) skips unchanged Typert generation and restores invalidated outputs; [PR #29](https://github.com/gentt1024/Cordis.NET/pull/29) composes a referenced DTO's owning model into generated Service input defaults. To use these fixes before a new release, build matching packages and tools from one source revision. Installing the published alpha.1 packages does not include them, even though the source version property has not changed. A passing PR is not a new release; [publication readiness](publication-readiness.md) describes the release procedure.

| Area | Current use and boundary |
|---|---|
| Runtime/composition | Context, Fiber, services, static modules, profiles and coordinated edits. Ordinary C# services need no Remote. |
| Settings/management | Numeric-revision Settings, generated PluginManager and selected typed events reuse configuration operations and installation tasks. Hosts own authorization and document-opening policy. |
| Declarations | One common model, explicit references, normal build/pack and first design-time generation. The backend table below lists supported and refused shapes. |
| Remote | Shared unary/multipart and text mux Connection, scoped clients, reverse Context/owned values, Peer routing and fixed events. Interoperability is bounded, not all DSH SDK transports. |
| CLR | Prepared/published receipts, stable LoadDirectory, restart upgrades and retained files after logical removal. Ordinary runtime only; business drain/readiness stays application-owned. |
| Platforms and evidence | Windows/Ubuntu have full runtime, package and AOT validation for the published release. macOS has named authoring and browser consumers, not full runtime, application or AOT acceptance. See [validation](validation.md) for the exact source identities, failures and limits. |

## Maintenance navigation

Every upstream path below uses [upstream.lock.json](../upstream.lock.json). This short map locates the owner and primary check; [compatibility](compatibility.md) records adaptations. Packaging/transport checks protect their own wiring, not duplicate every owner assertion.

| Responsibility | Fixed upstream / .NET adaptation | Source owner | Primary check and distinct consumer |
|---|---|---|---|
| Execution and service views | [vendor/cordis/src](https://github.com/deepseek-ai/deepseek-harness/tree/687f282e8625c6b8d11ff58211a90524be2e4317/vendor/cordis/src); typed views replace contextual JS rebinding | [Core](../src/Cordis.Core): Context, Fiber, ReflectService | [CoreTests](../tests/Cordis.Core.Tests/CoreTests.cs); isolated package consumer |
| Configuration / Settings | packages/boot and packages/api; typed fields and STJ over the existing writer | [Composition](../src/Cordis.Composition): configuration operations, SettingsForms | [ConfigurationEditTests](../tests/Cordis.Composition.Tests/ConfigurationEditTests.cs); [Settings caller](../scripts/verify-typert-dotnet.py) |
| Installation | packages/boot; NuGet/SDK preparation retains operation/task ownership | [Extensions](../src/Cordis.Extensions), [Clr](../src/Cordis.Clr) toolchain | [ConfigurationSessionTests](../tests/Cordis.Extensions.Tests/ConfigurationSessionTests.cs); [deployment](../scripts/verify-clr-deployment.py) |
| Model / backends | packages/typert/generator; Roslyn and STJ replace TS analysis/codecs | [Model](../tools/Cordis.Typert.Generator/Model), [Generation](../src/Cordis.Composition/Generation), MSBuild targets | [Independent callers](../scripts/verify-typert-dotnet.py), including build/pack/design-time wiring |
| Remote / events | packages/api/gateway and packages/typert/protocol; async and disposal adapt ownership | [Composition](../src/Cordis.Composition) Registry/Gateway/Connection, [AspNetCore](../src/Cordis.AspNetCore) | [Registry](../tests/Cordis.Composition.Tests/TypertRegistryTests.cs), [events](../tests/Cordis.Composition.Tests/RemoteEventsGatewayTests.cs); [real clients](../scripts/verify-typert.py) |
| CLR lifecycle | packages/boot/hmr; collectible ALC and stable files adapt loading/retirement | [ClrModuleResolver](../src/Cordis.Clr/ClrModuleResolver.cs), Loader | [ClrTests](../tests/Cordis.Platform.Tests/ClrTests.cs); [multi-entry consumer](../scripts/verify-clr-multi-entry.py) |

The default JIT package-consumer path is `python scripts/verify-typert-dotnet.py --packages <validated-batch> --output <new-empty-directory>`. Select at most one of its five `--…-only` options; omitting them runs the default path, and `--aot` remains independent. Existing negative checks must identify their intended refusal: caller DTO projection for `recordStruct`, `gateway/input-invalid` for illegal Web inputs before business calls, and the compiler's missing-file diagnostic for the deleted explicit event model. A missing file is different from an omitted reference model.

This consumer maps `Cordis.NET.*` to the validated batch, its `Independent*` fixture packages to the temporary author feed, and public dependencies to nuget.org. It clears fallback folders and uses a private cache. Existing assets checks verify that cache and the selected Cordis package version; a public package with the same version does not establish candidate provenance.

Typert MSBuild targets check content hashes for the evaluated source/model/reference lists, reference XML documentation, compiler and selected SDK Roslyn files, generation options and owned outputs. An unchanged build skips the compiler process while still registering `Compile`, `AdditionalFiles` and `FileWrites`. Missing or changed output, changed inputs/options, or a missing/invalid success receipt triggers generation. Receipts are invalidated before starting the compiler and written only after successful generation; `Clean` removes them. This is local build state, not a model format or a second declaration authority. Normal and design-time outputs remain separate. The existing declaration and referenced-model package consumers check no-change generation alongside their content, timestamp, failure and cold-rebuild checks.

## Model pipeline contract

Ordinary and Remote roots share one declaration fact model. Extraction records author facts; each backend interprets those facts for its own supported output. When required facts change, re-extract the owning models and rebuild affected providers and clients with matching tools. Fixed upstream behavior and documented platform adaptations determine the target contract; historical experimental formats do not create additional compatibility requirements.

| Stage | Input → output | Owner | Refusal boundary | Exit path |
|---|---|---|---|---|
| Select and extract | Effective compiler input plus explicit ordinary roots and/or service selection → declaration facts, referenced graph and diagnostics | Source reader and `extract-model` | Invalid root, unusable syntax, duplicate identity or missing required facts; analysis diagnostics do not imply every backend must refuse | One `$(AssemblyName).cordis.model.json`; ordinary roots do not require a Remote declaration |
| Validate and compose | Current saved facts and optional explicit reference models → validated identities and reachable definitions | Model reader/composition | Incomplete or inconsistent input, identity mismatch, missing/conflicting definition; references do not add primary roots | Re-extract rejected inputs; no old-format reader, conversion or DLL-based fact repair |
| Interpret for a purpose | Common facts plus named backend policy → a recomputable internal projection | Declaration interpreter and the selected backend | Unsupported behavior is refused by the consuming backend; it is not silently removed from analysis | Internal projections are recomputed for the selected backend, not persisted as another contract |
| Bind Host | Remote facts plus actual author implementation and explicit STJ metadata → typed bindings and runtime descriptors | Remote generator; Registry/Gateway own publication and invocation lifetime | Existing Host contract, declaration drift and unavailable runtime capability; standalone-caller restrictions do not automatically disable Host | Bindings retain cancellation and withdrawal; declaration drift is checked against the current compilation |
| Generate consumers | Same saved facts plus selected catalog/data/client policy → catalog, DTO/STJ or .NET/TS output within that backend's supported scope | Respective emitter | A caller that cannot reproduce required data/serializer behavior refuses that projection; it never copies author business code | Preserve pre-`CoreCompile` generation and provider-independent callers; ordinary roots require no Remote registration |
| Build and deliver | Current sources, models and generated output → package and consumer evidence | MSBuild targets, package checks and existing consumer gates | Failed generation removes owned stale output; Clean and incremental builds remain observable contracts | Pack only `cordis/typert/$(AssemblyName).cordis.model.json`; explicit model/reference inputs determine generation |

Current supported shapes are listed below; completed checks are indexed in [validation](validation.md#released-version-evidence). Historical reconstruction checkpoints do not define current remaining work.

MSBuild supplies reference XML documentation to the generator under each resolved assembly identity. This preserves the same documentation facts when extraction reads an implementation assembly and the C# compiler binds its reference assembly. XML text is an incremental analyzer input and is parsed only when a referenced declaration needs it. The generator still uses compiler symbols to locate implementation members and emit compilable C#; invocation semantics come from the common declaration contract.

Null attribute arrays and reference documentation are retained through extraction, model reading and Host binding.

<a id="alpha5-independent-backend-disposition"></a>

## Generation backend support

Choose a backend by the artifact you need. Extraction records common declaration facts; it does not promise that every backend can execute the author's serializer or generate every C# shape. A Host uses the actual implementation, registered STJ metadata and codec; independent consumers use saved facts and explicit reference models. Runtime Schema projection and input admission have their own diagnostics even when STJ itself can serialize a value.

| Declaration or use | Host binding / runtime export | Independent C# Remote client | Ordinary C# data (`emit-dotnet-data`) | Independent Web (`emit-web`) |
|---|---|---|---|---|
| Public DTOs, arrays, supported collections and string-key dictionaries | Uses the selected codec and real author metadata. | Supported within the declaration projection. | Public top-level non-generic classes, public readable properties with public set/init, no inheritance/interfaces or parameterized constructor. | Supported public DTO shapes with directional input/output contracts. |
| Explicit referenced source models | Host model composition preserves Service drift checks; referenced initializer facts require the owning source model. | Exact assembly-qualified identities; missing/conflicting source facts refuse generation. | Same identity and source requirements; references do not add roots. | Same identity and source requirements; no provider DLL loading. |
| Value-type structs and readonly record structs | Depends on registered metadata/codec. | Supported data carriers, including constructor-backed readonly members. | Refused. | Refused by the current DTO projection. |
| Closed authored generics, e.g. Response<Row> | Can bind a closed registered type within its codec support. | Supported unconstrained data definitions with actual closed arguments; not arbitrary open generic endpoints or constrained execution. | Authored generic DTOs refused. | Authored generic DTOs refused. |
| Getter-only / readonly members | Output follows actual metadata and direction. | Generated JSON constructor parameters can receive readonly wire data; no accessor body is copied. | Requires public set/init properties; fields/getter-only carriers refused. | Supported directional member facts only; no accessor execution. |
| System.Guid | Default STJ UUID string contract. | Native Guid, including supported nullable/container uses. | Native Guid in the supported data subset. | String with UUID Schema; not arbitrary ID wrappers or Guid dictionary keys. |
| Public source enums | Uses actual registered serializer. | Numeric enums and the recognized default generic JsonStringEnumConverter<TEnum> policy. | Numeric enums and the same recognized generic converter. | Numeric enums; string-converted enums refused. Check the documented numeric precision boundary. |
| Static DTO member defaults | Uses declared input defaults and the real serializer; output does not apply input defaults. | Constants and supported finite record/collection data construction; unsupported behavioral expressions refuse input projection. | Constant/null/enum initializers in its narrower property subset. | Does not execute author initializers or constructors; do not assume C# Host-default execution from its catalog. |
| Inheritance, polymorphism, nested DTO declarations, non-string dictionary keys and multidimensional arrays | Depends on explicit metadata and codec support. | Not general supported projection shapes. | Refused. | Refused. |
| Non-public JsonInclude / omitted required source facts | Actual metadata remains Host-owned. | Missing analyzed included data is diagnosed, not silently dropped. | Same refusal principle. | Same refusal principle. |
| Arbitrary converters, constructor/accessor business, metadata-only serializer contexts | Uses real code where its codec supports it; export can still refuse. | No replay or provider loading; unsupported behavior and insufficient context facts refuse precisely. | No replay or reflection recovery. | No replay or reflection recovery. |
| Binary attachment metadata | Supported runtime carrier/export route retains attachment and lifetime rules. | Do not infer arbitrary attachment policy from saved DTO facts. | Ordinary JSON data generation is not attachment generation. | Saved JSON projection refuses attachment metadata. Ordinary byte[] JSON is separate from an attachment policy. |

The C# Remote projection also retains recognized ImmutableArray<T> and DateTimeOffset mappings. A known framework mapping does not enable arbitrary BCL or authored generic types. Enum aliases, flags and declaration order retain the supported STJ behavior; arbitrary converter implementations remain refused.

The nullability/default query graph reuses ordinary recursive uses. Its 256 budget counts active uses of the same generic declaration on one path, not all sibling nodes, and can refuse a finite path. This is separate from backend limits: ordinary data projection still bounds reachable definitions at 256 and has its own traversal/type restrictions. No backend promises arbitrary C# graphs.

When static generation cannot reproduce a contract, runtime `TypertArtifacts.GenerateClient(contribution)` can export the actual Host descriptors through the shared emitter where supported. That is an explicit runtime route, not automatic reflection fallback or a second model format. Preserve one primary model, explicit references and normal build/pack inputs; regenerate with matching packages/tools when facts change. [Authoring](authoring.md#generate-models-and-independent-clients) gives the inputs and [compatibility](compatibility.md) records platform adaptations.

Published alpha.1 contains the C# struct/generic/default projection support above. Main's later incremental-generation and referenced-assembly Host-default fixes remain unpublished; see [release and source status](#current-support-and-release-status). Support statements do not replace the [actual validation evidence](validation.md) or prove complete DSH product/SDK interoperability.

## Fast loop

The package version for source builds is defined once in [Directory.Build.props](../Directory.Build.props). Projects inherit it; package verification scripts read it for their isolated consumers. When an exact local package version is needed, query that same value:

```console
dotnet msbuild src/Cordis.Core/Cordis.Core.csproj -getProperty:Version
```

Ordinary commits do not require a package version bump. The source version does not establish NuGet availability; use the [installation guide](getting-started.md) for published packages and the [authoring guide](authoring.md) for authoring API availability and source examples.

Third-party dependency versions are declared in [Directory.Packages.props](../Directory.Packages.props); projects keep their own `PackageReference` items and metadata. Each project's `packages.lock.json` records its resolved dependency graph, and validation continues to use `--locked-mode`. Transitive pinning is not enabled. SDK-provided implicit packages remain controlled by the selected SDK.

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

## Full gate

```console
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --aot --package --package-output artifacts/upgrade-packages
python scripts/verify-authoring.py --aot --packages artifacts/upgrade-packages
```

The full gate checks the locked SDK and dependencies, analyzers, tests, upstream source execution, differential scenarios, Native AOT, API shape, packages, and isolated consumers. Run it on Windows x64 and actual Linux x64 for release candidates. See [validation](validation.md) for the latest completed evidence and [compatibility](compatibility.md) for what that evidence means.

Generated outputs and raw logs may contain local paths or user names. Keep them outside the public tree; commit only redacted summaries and stable machine-readable evidence.

## Shell-owned client modules

The optional [browser module package](../clients/modules/README.md) exposes the fixed DSH static module table and optional transport through its public bootstrap. The shell owns shared module objects; plugin effects remain owned by Cordis.

```js
import { bootClientModules } from '/client-runtime/client.mjs'
const shell = { name: 'application shell' }
const tools = { format: value => String(value) }
const modules = await bootClientModules({
  graph: await fetch('/client/graph').then(response => response.json()),
  staticModules: { '@my-app/shell': shell, '@my-app/shell/tools': tools },
})
```

On the native Host, pass those same names to the additional `ClientModuleCatalog.CaptureAsync` overload through `platformModules: ["@my-app/shell", "@my-app/shell/tools"]`. Host declaration and browser supply must agree. Plugin authors list their exact imports in `dsh.client.external`; a root request does not supply subpaths. The author build bundles undeclared ordinary dependencies instead of implicitly making their subpaths external. The shell supplies types and module values; neither catalog nor bootstrap discovers npm packages.

`staticModules` extends the SDK defaults, with explicit keys taking precedence. Values are retained without cloning and survive plugin withdrawal/reload. Use the SDK's runtime instance for Cordis-related services. Optional `loadBundle(url)` must execute factory registration before resolving; the fixed script-element transport remains the default. Both transports receive the validated same-origin revisioned URL. Disposal releases plugin effects and the page facade, not shell objects.

The existing verification flow runs `verify-client-shared-modules.mjs` through the built public package with an independent plugin and injected Node transport. The existing browser carrier's `/shared` page separately checks actual default/custom script transport and missing suppliers. These results do not replace native catalog tests, upstream assertions or hosted verification.


Initial browser startup calls the fixed web activation audit; failed imports, activation and missing-service Pending entries reject and clean up. Transient management disconnects preserve plugins and drafts. The public client accepts a generated notification client in `options.events`, `transport.fetch`, `recovery` and manual `reconnect()`. The fixed connection controller owns handshake deadlines, retry/backoff and offline/online handling over the fixed `$events` mux subscription. Subscription readiness precedes full state and graph reads. Closing the owner rejects new writes, cancels reads and awaits withdrawal. Recovery never replays uncertain mutations.

## Application invocation

```console
cordis run ./profile --resume abc --source app-owned-value
cordis run ./profile --source ./feed --url http://127.0.0.1:5080 --authorization-env CORDIS_TOKEN -- --help
```

The launcher parses its own options until `--` or the first unrecognized token; the remaining arguments are copied verbatim into `CommandLineArguments` under `cmdlineArgs`. The application owns parsing, help and errors. The first command's `--source` belongs to the application. The second selects a package feed before the separator. Without `--url`, Generic Host opens no HTTP listener. An empty feed whitelist can run installed packages; acquisition still requires an explicitly selected source.

Before entries mount, the CLI also provides `IApplicationReady` as `appReady` and `ApplicationExit` as `appExit`. Register readiness subscriptions with a Context effect. Readiness commits after tree and host startup; late subscribers run immediately. The application may request exit during activation (for help) or after readiness. The first request starts the fixed upstream five-second grace, including during startup; the CLI awaits orderly tree disposal and forces process exit with that code if grace expires. Ordinary startup has no five-second deadline. Startup/readiness exceptions return failure. Cordis reports effect cleanup errors without rewriting the requested code. Existing HMR uses the same readiness result. Process signal handling remains the .NET Host's responsibility.

Library hosts can provide these Composition contracts through `ProfileSession.StartAsync(prepare: ...)` and own their exit policy; they do not need the CLI or another runtime. The verification flow compiles an independent CLR application and runs the real CLI for argument boundaries, help/error, readiness and cleanup. Controlled carrier checks, browser execution and native management tests are separate evidence.





## Remaining support boundaries

The backend table above remains the support inventory. WPF, Avalonia, Godot .NET and Blazor are intended consumers with outstanding named integration evidence; they were deferred as alpha5 release blockers, not removed from scope. Rider acceptance does not prove other IDEs. Windows document-opening evidence proves system handoff, not editor contents or other desktop platforms. See [validation](validation.md#accepted-risks-and-evidence-boundaries) for HMR and platform limits.

## Historical decisions

[Pre-release checkpoints](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/development.md) retain the original decisions, failures and pending statements. Use them to trace a change, not as the current task list.

## Contributor tooling and CI

### C# style and lint

The root `.editorconfig` is the style authority. The pinned SDK's compiler and Roslyn analyzers own correctness, nullable, async, lifetime, interop, performance, and API-usage diagnostics. Build warnings are errors. Formatting does not apply semantic fixes.

```console
python scripts/format.py
python scripts/format.py --check
```

The script restores the repository-local JetBrains ReSharper GlobalTools version from `dotnet-tools.json` and the solution's locked dependencies. It runs `jb cleanupcode` with `Built-in: Reformat Code` on repository C# files, then `dotnet format whitespace` in folder and solution modes, then CleanupCode again. Solution mode covers conditional compilation branches. Every stage uses the same explicit C# file list, including fixtures and tools outside the solution. Each later stage must leave the previous output unchanged.

`--check` copies current tracked and non-ignored new files, including configuration and uncommitted edits, to a temporary directory. Restore and formatting run there; nothing is written back to the working tree. Any stage changing the copied C# files fails the check. Files resolving outside the repository are rejected. CI runs this check on Windows and Linux. Git and Python are required.

Roslyn defines braces, newlines, spacing, and indentation; single-line blocks and embedded statements are expanded. JetBrains adds wrapping for signatures, arguments, call chains, and initializers at a target width of 120 columns. Unbreakable tokens and string contents may exceed that width. GlobalTools is a development tool, not a dependency of any `Cordis.NET.*` package. CSharpier is not used.

To keep both formatters stable, nested loops are indented, `for` semicolons have no padding, and explicit line breaks are preserved, including before a call after a multiline constructor. Existing lifecycle exceptions have narrow, commented analyzer suppressions; formatting must not change cleanup or cancellation timing.

Rider and Visual Studio read `.editorconfig` automatically. In Rider or Visual Studio with ReSharper, use **Reformat Code**, with Roslyn analyzers enabled. Visual Studio's built-in formatter handles the Roslyn rules; run the script before submitting changes to apply width-aware wrapping too. The pinned CLI output is canonical. Avoid Full Cleanup for formatting-only changes because it can rewrite code. See the [CleanupCode reference](https://www.jetbrains.com/help/resharper/CleanupCode.html) and [wrapping settings](https://www.jetbrains.com/help/resharper/EditorConfig_CSHARP_LineBreaksPageSchema.html).

### Build and tests

Install the exact SDK in `global.json` and select that installation in Rider/Visual Studio and your CLI. SDK patch roll-forward is disabled to keep SDK-supplied dependencies, including ILLink, aligned with CI and the lock files. Regenerate dependency locks only with that SDK; normal validation uses locked restore.

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

For focused maintenance, Core's `InertiaSerializesLoadAndUnload` owns load/unload serialization and `AsyncYieldExactSequence` owns normal completion and distinct abort positions. Composition's `MaintenanceTests.EnablementRejectsMalformedFileWithoutOverwrite` owns malformed configuration refusal before writing; `LoaderOriginalTests.LoaderInterceptReadiness` owns the state observations before and after readiness release.

```console
dotnet test tests/Cordis.Core.Tests -c Release --no-build --no-restore --filter "FullyQualifiedName~InertiaSerializesLoadAndUnload|FullyQualifiedName~AsyncYieldExactSequence"
dotnet test tests/Cordis.Composition.Tests -c Release --no-build --no-restore --filter "FullyQualifiedName~EnablementRejectsMalformedFileWithoutOverwrite|FullyQualifiedName~LoaderInterceptReadiness"
python scripts/test-map.py
```

Parameterized cases should exercise a different path or observation. Prefer a lightweight owner check over an equivalent fixture whose extra setup is unused. Release test-owned barriers in `finally` when failed assertions could otherwise leave teardown waiting on them. Focused test results do not establish execution of the full upstream map.

Run `python scripts/verify.py` for runtime, packaging, compatibility, or release changes. Documentation-only changes need the documentation check and a focused review; they do not require every Windows, Linux, or AOT gate.

PR and main-branch CI run the .NET verification and fixed upstream source tests in parallel on both platforms. Four isolated upstream jobs split the 4,800 linked-resolution matrix cases; each group remains sequential, and the first job also runs the remaining original suites. Each job discovers the complete original case list. The final `verify (windows-latest)` and `verify (ubuntu-24.04)` checks require all runtime jobs and all shards, identical source/toolchain identities within each platform, and an exact union of the discovered cases. Missing, duplicate, failed, or unexpectedly skipped cases fail the gate. Only the platform-specific skips recorded in the locked source manifest are accepted; all other skips fail the gate. Filtered-out matrix cases are not counted as executions.

CI and release both call `.github/workflows/upstream.yml` for the locked source discovery, four shards on each platform, and strict unions. The existing `verify (windows-latest)` and `verify (ubuntu-24.04)` checks require native/runtime, portable-authoring and source success. `verify.py --defer-upstream-source-tests` retains native/source trace comparisons and is used only with that independent required gate. Without deferral, `verify.py --dsh ... --origin ...` discovers and executes the same complete four-shard source scope locally. No receipts are reused across runs. For a focused reproduction, use the existing `scripts/verify_candidate_upstream.py` discover/run/aggregate commands with one fixed inventory and a shared `--run-id`; the filename remains from S10, but its source identity is now checked against the formal lock.

The verification-script regression suite also exercises the candidate collector. Before running `python -m unittest discover -s scripts/tests`, install its fixed tools with `npm ci --prefix reference/candidate-tools --ignore-scripts`. Installing those tools does not select a candidate behavior baseline.
