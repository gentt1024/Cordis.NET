# Development and verification

[中文](development.zh.md)

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


Initial browser startup calls the fixed web activation audit; failed imports, activation and missing-service Pending entries reject and clean up. Transient management disconnects preserve plugins and drafts. The public client accepts `transport.fetch`, `transport.openEvents`, `recovery` and manual `reconnect()`. The fixed connection controller owns handshake deadlines, retry/backoff and offline/online handling over the native HTTP/SSE carrier. Closing the owner rejects new writes, cancels reads and awaits withdrawal. Recovery never replays uncertain mutations.

## Application invocation

```console
cordis run ./profile --resume abc --source app-owned-value
cordis run ./profile --source ./feed --url http://127.0.0.1:5080 --authorization-env CORDIS_TOKEN -- --help
```

The launcher parses its own options until `--` or the first unrecognized token; the remaining arguments are copied verbatim into `CommandLineArguments` under `cmdlineArgs`. The application owns parsing, help and errors. The first command's `--source` belongs to the application. The second selects a package feed before the separator. Without `--url`, Generic Host opens no HTTP listener. An empty feed whitelist can run installed packages; acquisition still requires an explicitly selected source.

Before entries mount, the CLI also provides `IApplicationReady` as `appReady` and `ApplicationExit` as `appExit`. Register readiness subscriptions with a Context effect. Readiness commits after tree and host startup; late subscribers run immediately. The application may request exit during activation (for help) or after readiness. The first request starts the fixed upstream five-second grace, including during startup; the CLI awaits orderly tree disposal and forces process exit with that code if grace expires. Ordinary startup has no five-second deadline. Startup/readiness exceptions return failure. Cordis reports effect cleanup errors without rewriting the requested code. Existing HMR uses the same readiness result. Process signal handling remains the .NET Host's responsibility.

Library hosts can provide these Composition contracts through `ProfileSession.StartAsync(prepare: ...)` and own their exit policy; they do not need the CLI or another runtime. The verification flow compiles an independent CLR application and runs the real CLI for argument boundaries, help/error, readiness and cleanup. Controlled carrier checks, browser execution and native management tests are separate evidence.

## Initial application infrastructure slices (historical acceptance)

Work follows the completed application-scope matrix, using the fixed DSH revision in `upstream.lock.json`. Do not reopen a whole-repository review. Core remains domain-independent; management, authoring and platform integration reuse existing owners above it.

| Slice | State | Acceptance |
|---|---|---|
| A: complete typed field composition | Implemented; Windows/Linux JIT/AOT and independent package checks passed | Real manual/composed consumer, transparent wrapper, live and ordinary updates, full save/default round trip, replacement, collectible controls, JIT/AOT and isolated packages |
| B: one field-edit operation | Implemented; independent review repairs and Windows/Linux consumer checks passed | Existing operations/session ownership, validation, revision, saved/effective failure outcomes |
| C: one settings view | Implemented subset; Windows/Linux source and isolated-package JIT/AOT consumption passed | Explicit live-only contract, redaction and revision, real TypeScript consumer; no claim that Core's graph is Schemastery or JSON Schema |
| D: one client artifact delivery | Implemented subset; Windows/Linux immutable delivery, path and generation checks passed | Explicit manifest/entry contract, host delivery, actual Node ESM execution and generation invalidation |

The bounded rules for A are complete explicit plain-data projections, unchanged validator authority, no inferred defaults and no global callback/type caches. Existing semantics are checked through the fixed-source differential and established native regressions; the new author helper is checked through actual consumers. The authoring gate runs unchanged assertions against helper mutations that remove a live binding, ignore ordinary effective changes or omit a saved field. A mutant must compile and fail for its designated runtime reason. Never relax assertions or the formal behavior lock to obtain a pass.

Each slice requires implementation, an independent read-only review citing rules/source/reproduction, repairs and re-verification. Rule corrections are reviewed independently and applied between batches. Capture pre-transition values when asserting retired values: the native Fiber itself is reused. Keep commits, command results, unexecuted checks and source hashes in the existing validation record and private evidence outputs. No new publication is implied. These principles borrow from the [migration article](https://claude.com/blog/ai-code-migration) and [kit README/operating manual](https://github.com/anthropics/code-migration-kit-with-claude-code), without importing their queue, tool bans or approval gates.

B preserves source ownership, raw opacity and the existing session queue. Independent review corrected flow-row deletion, user-insert inheritance, ancestor live admission, restart outcomes and caller-owned maps. YAML round-trip checks refuse non-persistable candidates before writing. C restricts both reads and submissions with host policy, keeps draft revisions tied to their original host hash and refuses unsupported reset/multi-edit. D captures immutable bytes through deployment package routing; both lexical and linked escape paths are rejected. These rules were calibrated before the final batch, without changing the formal lock. The existing authoring verifier includes compiled production/client mutations and unchanged end-to-end controls for these contracts; correct source and isolated packages run the actual native host and TypeScript client.

The final batch exposed truncated Node data-URL error output. The consumer now reports the original error message and a nonzero exit code; independent review confirmed that assertions and the verifier's designated-rejection checks are unchanged. Both platforms reran the authoring gate successfully. The source-ZIP build also exposed a separate symbol-packaging limitation: without Git metadata, the SDK emitted no SourceLink record and the strict inspector rejected the packages. Full symbol/package checks passed from Git checkouts; the no-Git export path remains open. See the current validation record, rather than treating archive creation as acceptance.

The no-Git export limitation described above was subsequently repaired by binding exported sources to their real commit. The isolated no-Git `verify.py --aot --package` run passed for that build change, including strict symbols and independent consumers. This result does not validate later application-infrastructure changes or remote SourceLink availability.

Current source extends those initial slices with ordered configuration edits/reset, nested Settings and independent declaration exports, NuGet/SDK package operations, HTTP/SSE management, CLI operations and browser module delivery. The initial restrictions above describe the earlier acceptance batch. Each new path requires independent review and combined validation; its presence does not reuse the earlier batch as proof.
