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

## Application infrastructure slices

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
