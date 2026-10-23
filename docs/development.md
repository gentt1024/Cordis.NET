# Development and verification

[中文](development.zh.md)

## Current support and release status

This source targets `0.2.0-alpha.5.2`; the [matching release](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.0-alpha.5.2) records distribution and publication. The original alpha.5 archive remains historical and is not a publication candidate. [Validation](validation.md#released-version-evidence) identifies tested source and package batches. [Publication readiness](publication-readiness.md) owns release gates. Historical decisions are linked separately below.

| Area | Current use and boundary |
|---|---|
| Runtime/composition | Context, Fiber, services, static modules, profiles and coordinated edits. Ordinary C# services need no Remote. |
| Settings/management | Numeric-revision Settings, generated PluginManager and selected typed events reuse configuration operations and installation tasks. Hosts own authorization and document-opening policy. |
| Declarations | One common model, explicit references, normal build/pack and first design-time generation. The backend table below lists supported and refused shapes. |
| Remote | Shared unary/multipart and text mux Connection, scoped clients, reverse Context/owned values, Peer routing and fixed events. Interoperability is bounded, not all DSH SDK transports. |
| CLR | Prepared/published receipts, stable LoadDirectory, restart upgrades and retained files after logical removal. Ordinary runtime only; business drain/readiness stays application-owned. |
| Consumption status | Prepared-candidate contract access, unary local transport cause and bounded Web-number metadata are implemented in this candidate. [Validation](validation.md#released-version-evidence) separates local consumption, complete CI and release status; external applications verify their affected calls against one matching package/tool batch. Generic DTO/static backend limits remain explicit; named UI/platform acceptance requires its own evidence. |

## Maintenance navigation

Every upstream path below uses [upstream.lock.json](../upstream.lock.json). This short map locates the owner and primary check; [compatibility](compatibility.md) records adaptations. Packaging/transport checks protect their own wiring, not duplicate every owner assertion.

| Responsibility | Fixed upstream / .NET adaptation | Source owner | Primary check and distinct consumer |
|---|---|---|---|
| Execution and service views | [vendor/cordis/src](https://github.com/deepseek-ai/deepseek-harness/tree/639ed015397290b3745d163aafe02ffee4aa3f84/vendor/cordis/src); typed views replace contextual JS rebinding | [Core](../src/Cordis.Core): Context, Fiber, ReflectService | [CoreTests](../tests/Cordis.Core.Tests/CoreTests.cs); isolated package consumer |
| Configuration / Settings | packages/boot and packages/api; typed fields and STJ over the existing writer | [Composition](../src/Cordis.Composition): configuration operations, SettingsForms | [ConfigurationEditTests](../tests/Cordis.Composition.Tests/ConfigurationEditTests.cs); [Settings caller](../scripts/verify-typert-dotnet.py) |
| Installation | packages/boot; NuGet/SDK preparation retains operation/task ownership | [Extensions](../src/Cordis.Extensions), [Clr](../src/Cordis.Clr) toolchain | [ConfigurationSessionTests](../tests/Cordis.Extensions.Tests/ConfigurationSessionTests.cs); [deployment](../scripts/verify-clr-deployment.py) |
| Model / backends | packages/typert/generator; Roslyn and STJ replace TS analysis/codecs | [Model](../tools/Cordis.Typert.Generator/Model), [Generation](../src/Cordis.Composition/Generation), MSBuild targets | [Independent callers](../scripts/verify-typert-dotnet.py), including build/pack/design-time wiring |
| Remote / events | packages/api/gateway and packages/typert/protocol; async and disposal adapt ownership | [Composition](../src/Cordis.Composition) Registry/Gateway/Connection, [AspNetCore](../src/Cordis.AspNetCore) | [Registry](../tests/Cordis.Composition.Tests/TypertRegistryTests.cs), [events](../tests/Cordis.Composition.Tests/RemoteEventsGatewayTests.cs); [real clients](../scripts/verify-typert.py) |
| CLR lifecycle | packages/boot/hmr; collectible ALC and stable files adapt loading/retirement | [ClrModuleResolver](../src/Cordis.Clr/ClrModuleResolver.cs), Loader | [ClrTests](../tests/Cordis.Platform.Tests/ClrTests.cs); [multi-entry consumer](../scripts/verify-clr-multi-entry.py) |

The default JIT package-consumer path is `python scripts/verify-typert-dotnet.py --packages <validated-batch> --output <new-empty-directory>`. Select at most one of its five `--…-only` options; omitting them runs the default path, and `--aot` remains independent. Existing negative checks must identify their intended refusal: caller DTO projection for `recordStruct`, `gateway/input-invalid` for illegal Web inputs before business calls, and the compiler's missing-file diagnostic for the deleted explicit event model. A missing file is different from an omitted reference model.

This consumer maps `Cordis.NET.*` to the validated batch, its `Independent*` fixture packages to the temporary author feed, and public dependencies to nuget.org. It clears fallback folders and uses a private cache. Existing assets checks verify that cache and the selected Cordis package version; a public package with the same version does not establish candidate provenance.

## Model pipeline contract

Ordinary and Remote roots share one declaration fact model. An incomplete port cannot define the next architecture: correct missing facts and stage responsibilities before creating another public protocol. Published alpha.5.2 is the public-use baseline. Corrections require explicit impact and regeneration guidance; historical experiments do not independently define the target contract.

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

## Alpha5 independent backend disposition

The fixed generator's declaration graph and face/event emitters establish the responsibility to preserve a selected boundary. Roslyn facts and STJ metadata are the .NET expression of that responsibility. The following dispositions concern saved-model DTO/STJ and Web generation; they do not remove Host capabilities or create a compatibility obligation for experimental outputs.

| Shape and basis | Facts and actual Host behavior | Backend/standard consumer disposition | Public-use consequence |
|---|---|---|---|
| Default STJ System.Guid scalar | Common facts retain framework identity; Host reads/writes JSON string with string/uuid Schema. | Required alpha5 .NET adaptation. Shared classification feeds ordinary data, Remote and event projections. Guid?, arrays, supported collections and string-key dictionary values compose normally. | C# remains Guid; Web is string. No source model or mapping annotation for the framework type. Custom converters, Guid keys and ID wrappers are not added. |
| Parameters, results, omission/null, required/default and directional properties | Common facts retain annotations, constructor parameters, constants and JSON attributes. Host uses actual STJ admission/serialization. | Required in alpha5 for ordinary methods and events, including Settings/management and their independent callers. Reply is input; request/arguments/next are output. | Correct existing generated promises now; no permissive `object` substitute. |
| Recursive DTOs, arrays, supported framework collections and string-key dictionaries; explicit referenced definitions | Common facts retain complete identity and element annotations; Host uses the actual registered codec. | Required, supported by current .NET/Web projections and existing independent reference consumers. Missing/conflicting references remain errors. | Same primary/reference inputs and build/pack assets; references never expand selected roots. |
| Closed authored generic containers, e.g. `Page<Row>` | Definition/argument/parameter-owner facts exist. Host can bind a closed STJ type; independent substitution and generated naming are not implemented. | Incremental unsupported DTO/STJ and Web shape. Standard result/stream/optional wrappers are interpreted protocol shapes, not arbitrary generic DTO support. Current Settings/management/event routes do not require an authored generic container. | Future substitution fits the existing declaration and backend entry; it need not change already supported callers. It is not complete generic emission. |
| Data inheritance and polymorphism | Base/interface and JSON attributes are retained. Host STJ can serialize inherited members or a configured discriminator. | Separately unsupported: inherited member flattening/override collision resolution, and polymorphic discriminator/union projection. Neither is required by the current standard DTO routes. | Add support to currently refused declarations through the same inputs; do not silently drop inherited fields or emit a base-only contract. |
| Nested DTO names, non-string dictionary keys, multidimensional arrays | Type identity/containing type/key/rank facts exist; Host support depends on STJ's concrete type. | Incremental unsupported shapes in independent projection. Framework collection support does not imply these shapes. | Current supported declarations retain their representation; future support needs its own precise codec and naming evidence. |
| Binary attachments | Host codecs and the Connection preserve attachment metadata and ownership. Saved Web JSON projection currently refuses byte-array attachment metadata. | The existing Host-generated client route supports the carrier; independent saved Web attachment emission remains unsupported and is not needed by the standard Settings/management/event DTOs. | No planned replacement of the Connection or author declaration; future static attachment emission must retain the existing carrier contract. This is not proof that every backend supports binary values. |
| Read-only members, extension data, non-public JsonInclude, SetsRequiredMembers and non-default serializer policies | Facts are retained where selected; Host uses executable author metadata. Some schema/data projections remain unimplemented and reject explicitly. | Incremental unsupported static shapes unless a required standard consumer demonstrates a dependency. Non-public included data that analysis omitted is diagnosed, never erased. | Do not advertise a generated contract that loses a field. Extending support must preserve already supported directional contracts. |
| Arbitrary converters, accessor/constructor business and non-constant initializers | A type/attribute name or expression text does not establish its executable behavior. Host continues to run the actual implementation and STJ metadata. | Not reproducible from saved facts alone. Independent backends refuse rather than load the Provider or invent defaults. Supported constants and empty collection defaults remain supported. | No new converter registry, duplicate author contract or format generation. A future required use must first specify its actual wire contract. |
| Metadata-only serializer context; conflicting JSON policies | Metadata does not supply the source declaration closure; incompatible naming cannot be merged for one projected type. | Explicit source-reference models are required. Select independently where policies differ; keep current precise diagnostics. | No reflection fallback, silent policy choice or implementation-DLL repair. |

These are dispositions of known backend limits, not a promise that every C# shape is supported. No identified alpha5 standard consumer requires a later replacement of author selection, model/reference inputs, Connection construction, mounting/scoping/disposal, result/error/stream/event contracts, Settings/management task ownership or stable CLR artifact loading. Saved Web event generation closes a missing output of that route. Candidate-specific package, browser, author-experience and HMR risk evidence still determines release readiness; this table cannot substitute for it.

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

PR and main-branch CI run the .NET verification and fixed upstream source tests in parallel on both platforms. Four isolated upstream jobs split the 4,800 linked-resolution matrix cases; each group remains sequential, and the first job also runs the remaining original suites. Each job discovers the complete original case list. The final `verify (windows-latest)` and `verify (ubuntu-24.04)` checks require all runtime jobs and all shards, identical source/toolchain identities within each platform, and an exact union of the discovered cases. Missing, duplicate, failed, or unexpectedly skipped cases fail the gate. The known upstream filesystem-capability skip remains explicit. Filtered-out matrix cases are not counted as executions.

CI uses `verify.py --defer-upstream-source-tests` only with those required shard gates. That option retains the current .NET/DSH trace comparisons but does not independently establish original-source test coverage. The default local command and release workflow still run the full original suites. No test results are reused across CI runs. A shard can be reproduced in a fresh checkout with `python scripts/verify_upstream.py run --dsh <fixed-dsh> --origin <fixed-cordis> --shard 1 --run-id <batch-id>` (use 1 through 4); preserve each job's `artifacts/upstream-shard-*` JSON and logs for aggregation. Supply the same unique `--run-id` to all four local shards and `aggregate`. Hosted runs bind the workflow run and attempt automatically. Aggregation rejects receipts from another batch or different source bytes.

The verification-script regression suite also exercises the candidate collector. Before running `python -m unittest discover -s scripts/tests`, install its fixed tools with `npm ci --prefix reference/candidate-tools --ignore-scripts`. Installing those tools does not select a candidate behavior baseline.
