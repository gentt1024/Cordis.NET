# Validation record

[中文](validation.zh.md)

## Profile installation and formatting, 2026-10-07

PR [#9](https://github.com/gentt1024/Cordis.NET/pull/9) merged as `05fc48731f54660b326eeca1316b100f0bbcfaaf`. Its Git tree matches tested head `04da6a1f02972969f710dd60df76b4ca66146a43`. [Workflow #41](https://github.com/gentt1024/Cordis.NET/actions/runs/37601516995) passed on Windows and Ubuntu 24.04, including canonical formatting, fixed-reference verification, runtime/package/JIT/AOT checks and authoring verification. Both platforms uploaded their validated packages and evidence.

Local evidence for that head includes 761 Windows tests, 30 focused Linux tests, 32 Python tests and 15 independent package-consumer cases. Isolated failures and their regressions cover stale profile writes, admitted-candidate association, equal-patch base configuration updates, legacy reconciliation customization and admission before physical package removal. The two consumers' Cordis DLLs match their inspected package bytes. This establishes the stated library contracts, not downstream application acceptance or full upstream production-caller fidelity.

The preparation for `0.2.0-alpha.4` changes version metadata, internal project dependency locks and release documentation. It does not change runtime source, SDK, third-party dependencies or the fixed upstream baseline. The preceding alpha.3-versioned verification packages must not be relabeled or published as alpha.4; the final versioned batch requires its own existing CI/release gates.

## Merged application infrastructure, 2026-10-05

PR [#5](https://github.com/gentt1024/Cordis.NET/pull/5) was squash-merged as `f8deed1b1a654314773ecfa8403ee7ca5d827be9`. Its source tree is identical to the reviewed head `4d57f65dad65611c4e8924b8f3764697b2f46ae5`. [Workflow #34](https://github.com/gentt1024/Cordis.NET/actions/runs/37328839303) passed on Windows and Linux at the actual PR checkout `3d4afbbf0ede45d18f34d9b9ff881455918aa3e7`, including fixed upstream comparisons, JIT/AOT, CLR deployment, packaging and independent consumers. Each platform's downloaded package batch separately passed payload/XML, commit metadata, DLL/PDB identity and checksum checks, and actual remote SourceLink retrieval.

Real browser checks covered shell-owned module identity and subpaths, default/custom bundle loading, missing-supplier startup rejection, plugin/draft retention across connection loss, handshake recovery and actual graph withdrawal. Targeted built-client and independent CLR/CLI consumers covered cancellation, obsolete callbacks, close ownership, startup activation auditing, opaque application arguments, readiness and bounded exit.

The earlier Windows unknown-installation lookup failure (expected exit 3, actual 1) remains recorded. The diagnostic-only follow-up retained that assertion; a later passing matrix does not establish its root cause. Normal CLI shutdown can still report an HMR `ObjectDisposedException`; the diagnostic is not suppressed. These observations remain separate from the passed checks.

At this checkpoint, `0.2.0-alpha.3` was a release candidate; it was subsequently published on 2026-10-06. The results in this section establish the implementation checkpoint, not evidence for a later release batch. Final artifact binding and publication remain version-specific; earlier validation packages must not be uploaded under a new version.

## Application management and delivery, 2026-10-05

Implementation checkpoint `ed2b63d657e5405a79f08308451b385f7cf0969c` passed local Windows x64 and Ubuntu 24.04 x64 verification with SDK 10.0.111. This extends the earlier slices with package execution, ordered configuration edits/reset, declaration exports, HTTP/SSE management, CLI commands and browser modules. Documentation-only updates are bound separately to the unchanged implementation. The fixed upstream lock and published alpha.1 remain unchanged.

| Evidence category | Actual result |
|---|---|
| Native tests | Windows: 732 passed. Linux: 729 passed; three exact Windows Job cases were not executed and are reported separately |
| Existing semantics | Established and upgrade traces match the fixed DSH implementation; JIT and static Native AOT agree |
| Original upstream tests | 178 selected configuration/Profile/HMR cases and 98 original Core/Loader cases passed on both platforms. Another bounded Linux run passed 265 unique official management/Settings cases |
| Consumers and deployment | Both platforms passed source and isolated-package authoring, actual TypeScript consumers, compiler rejection controls, designated mutations, static JIT/AOT, CLR isolation and ASP.NET folder/single-file deployment |
| Package contents and source | Nine local test packages and their symbol packages passed strict inspection; an isolated Core consumer resolved an actual source frame offline. A separate Windows no-Git source export passed the complete AOT/package gate using exact committed source bytes |
| Browser and application | The maintained application exercised two dependent modules, SlotCore withdrawal, watch success/failure, connection recovery, configuration editing, and one independent plugin author's install/remove/restart chain, including alpha.1 removal and alpha.2 installation with a fresh resolver |

The three Linux platform exceptions require exact test names, definitions and reasons. They cannot satisfy mapped assertion claims; any other skip or failure is rejected. Linux abrupt host death was separately checked with the final CLR DLL: a surviving process group blocks takeover until explicitly stopped. Windows Job cleanup remains a separate contract.

Original failures are retained: a running example locked Windows build outputs, mixed working-tree line endings broke package inspection, the old test gate rejected the Linux platform exceptions, and C-drive exhaustion interrupted independent consumers. After independent rule review, exports now read Git blobs directly and platform reporting preserves unexecuted cases. Moving completed private artifacts and using a separate temporary directory resolved the disk failure without changing source or assertions. The completed upstream steps from that same frozen Windows source remain separate evidence; the remaining local gate was rerun successfully.

The default broad upstream resolution run was stopped after 530 seconds and is not counted as passed. Final checks used the previously selected configuration/Profile cases plus the related HMR files. A first management run lacked the pinned pnpm executable; installing that exact tool made the failed file pass unchanged. These records do not claim an entire DSH audit or complete native assertion parity.

Use the commands in [development](development.md), selecting the five configuration/Profile files recorded below plus `--upstream-test packages/boot/hmr/tests/`. Also install dependencies with `npm ci --prefix clients/modules --ignore-scripts`. Raw commands, per-step results, source/package hashes and prior failures are retained outside the public tree.

Hosted Windows/Linux CI, remote SourceLink retrieval and publication were not executed. Local packages are validation artifacts; they must not replace the published version. A future release still needs a new authorized version and its own final commit, tag and package binding.

## Application infrastructure slices, 2026-10-04

Implementation checkpoint `06966e49bb287220c56d1a7e326d10f9800cbfb2` passed the existing full gate on Windows x64 and actual Ubuntu 24.04 x64 under WSL2, using SDK 10.0.111. A final consumer-diagnostic correction at `9e6af99bcf248c316f1c4db3f42bba99e11daa07` changed no production code, assertions or verifier conditions; independent read-only review and both-platform authoring reruns passed. Final delivery documentation is bound separately to these unchanged validated sources. The formal upstream lock and published alpha.1 are unchanged.

| Check | Actual result on both platforms |
|---|---|
| Release build / native tests | Zero warnings; 672 passed, zero failed/skipped: Core 148, Composition 395, Extensions 100, Platform 29 |
| Existing semantics | 35 established and 3 upgrade paired traces match fixed DSH under JIT and actual static Native AOT; JIT repeated three times |
| Relevant upstream execution | 109 cases in five named application/volatile files, plus 98 origin Core/Loader cases; reference execution, not native assertion closure |
| New consumer acceptance | Manual/composed typed configuration, field edits, and actual HTTP/unchanged DSH form-model/store consumption; source and isolated NuGet consumers under JIT/AOT |
| Designated rejection | Nine compiled production/client mutations rejected for their designated runtime reasons, without relaxing assertions |
| Deployment and artifacts | CLR private-dependency and collectible boundaries; framework-dependent and self-contained ASP.NET folder/single-file JIT deployments; eight-package inspection, symbols/checksums and offline consumer source frames; optional adapters and installed CLI |

```console
npm ci --prefix reference --ignore-scripts
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --upstream-test scripts/volatile-config.spec.ts --upstream-test scripts/loader-config-diff.spec.ts --upstream-test scripts/loader-volatile-update.spec.ts --upstream-test packages/boot/app-boot/tests/profile.spec.ts --upstream-test packages/boot/app-boot/tests/user-patches.spec.ts --aot --package --package-output artifacts/application-infrastructure-packages
python scripts/verify-authoring.py --aot --packages artifacts/application-infrastructure-packages
python -m unittest discover -s scripts/tests -p "test_*.py" -v
python scripts/check-docs.py
```

Both-platform verifier self-tests passed. Raw reports, source inventories, package hashes, independent review and actual commands are held in the delivery evidence. Linux's successful Git checkout had all 324 raw source hashes equal to the frozen ZIP; two initial checkout newline differences were replaced with the frozen bytes, with no semantic Git diff. No whole-repository S0 or unrelated DSH suites were added.

Failures remain visible: the old Linux SDK first refused restore; the no-Git export then passed runtime/AOT checks but failed symbol packaging because no SourceLink record was generated. The export symbol path is still open; source ZIP build/run and successful Git-checkout symbol checks do not close it. A truncated Node data-URL error failed the designated-rejection check, prompting the independently reviewed diagnostic correction and reruns. Hosted CI, remote SourceLink retrieval, browser UI rendering, other RIDs and Maker product execution were not run. Settings remains top-level primitive/live SET-only; full schema export, reset/multi-edit, module graphs, remote protocols and install tooling remain separate gaps.

The fixed upstream locale-file test calls `context.skip()` when its filesystem cannot store both `en.json` and `EN.json`. The verifier records that exact assertion and reason as skipped, not passed; it still rejects failures, unknown skips and inconsistent report totals. Linux executes the assertion on its case-sensitive filesystem. This reference-side capability outcome is separate from .NET test results and does not remove any upstream test.

## Public-review repair candidate, 2026-10-02

The following upgrade record describes the prior delivery. The current 0.2.0-alpha.1 candidate adds the bounded fixes in the upgrade matrix. Final-source verification, authoring, actual CLR deployments, portable PDB/source checksum checks and offline NuGet consumer frame results are recorded separately in its delivery evidence. Hosted CI and remote SourceLink retrieval are not claimed.

```console
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --upstream-test scripts/volatile-config.spec.ts --upstream-test scripts/loader-config-diff.spec.ts --upstream-test scripts/loader-volatile-update.spec.ts --aot --package --package-output artifacts/review-packages
python scripts/verify-authoring.py --aot --packages artifacts/review-packages
python scripts/verify-clr-deployment.py --dotnet dotnet --rid linux-x64 --output artifacts/clr-deployment
python scripts/package_inspection.py --directory artifacts/review-packages --version 0.2.0-alpha.1 --symbols --debug-consumer --source-root .
```

## DSH 0.2.0-rc.2 upgrade, 2026-10-02

The implementation checkpoint `fc41a420b0e4180da9656c1b5070e827f4050213` passed the required local checks on Windows x64 and actual Ubuntu 24.04 x64 under WSL2, using SDK 10.0.111 and Node 24.12.0. Final delivery changes only baseline/documentation metadata and documentation checking; evidence separately binds that final file inventory to the unchanged validated native source. This is local execution evidence; hosted CI and publication remain pending.

| Check | Actual result |
|---|---|
| Release build / .NET tests | Both platforms: zero warnings; 631 passed, zero failed/skipped (Core 145; Composition 362; Extensions 99; Platform 25) |
| Actual fixed-source paired traces | 35 established and 3 upgrade cases equal the fixed DSH output on both platforms, JIT and actual static Native AOT; JIT repeated three times |
| Native-only static configuration check | Captured typed validation/projections, immutable references, equal snapshot identity, typed simplify and shared/recursive graph metadata passed under JIT and actual AOT |
| Relevant original source suites | Linux: 5,521 current application/volatile/HMR cases across 24 files, plus 98 origin Core/Loader cases routed to the new vendored Core; reference execution only |
| Packages and isolated consumers | Both platforms: eight-package content inspection; independent JIT/AOT composition and new configuration API consumer; optional adapter packages and installed CLI tool |
| Authoring and CLR delivery | Both platforms: compiler positive/negative contracts, actual deployed CLR plugin, fresh resolver/cache boundaries, Probes JIT/AOT and independent consumers of the same eight-package batch |

The reference suite's first run passed 23 files/5,495 cases and failed to import the remaining volatile suite because its new HMR source route was missing. That original failure is retained; adding the source route made all 26 cases in the affected file pass. The 98 origin tests passed separately. The union above does not relabel the first failed run as green or turn source executions into native assertion closure.

The first Linux exported-source pack omitted the NuGet repository commit. The unchanged package inspector rejected it. The verifier now supplies the source manifest's checkpoint commit, and the complete flow passed on rerun. All failure logs and the successful source-bound reruns remain in the separate evidence ZIP. Native CLR unload tests also retain the actual failed counterexample before automatic observation stopped retaining exception instances.

Use exact clean reference checkouts at `639ed015397290b3745d163aafe02ffee4aa3f84` and `56b3d4f725681cf4556c1a8695a709cc3b6eed74`; do not follow a branch. Native AOT needs the usual MSVC or clang/zlib toolchain. A source ZIP supplies `SOURCE_SHA256.json`, including the native delivery commit for package repository metadata.

```console
npm ci --prefix reference --ignore-scripts
python scripts/check-docs.py
python -m unittest discover -s scripts/tests -p "test_*.py" -v
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --aot --package --package-output artifacts/upgrade-packages
python scripts/verify-authoring.py --aot --packages artifacts/upgrade-packages
```

The full native flow and authoring flow were executed separately from the fixed-source suites and paired comparisons; exact machine commands and SHA-256 inventories are in the evidence ZIP. [The upgrade matrix](upgrade-0.2.0-rc.2.md) records implementation scope and platform adaptations. The historical 631-instance upstream inventory and its 36 completed assertion reviews were not promoted into a new parity claim. The pre-existing reference-only `js-yaml` 4.2.0 high-severity audit findings remain open; no exemption or dependency upgrade was applied. The .NET runtime dependency graph was not changed by that finding.

The authoring changes have a separate [execution record](../verification/authoring-2026-09-22/evidence.json), covering new/raw API comparisons, deployment and negative cases. The record separates execution from upstream assertion review; it does not change the compatibility inventory. Reproduce it with `scripts/verify-authoring.py` and the existing full gate.

The earlier publication candidate completed the full local gate on Windows x64. The same checked-in gate is required on hosted Windows and Linux before publication. The following table describes that earlier candidate, not the authoring changes.

| Gate | Current Windows candidate | Historical two-platform run |
|---|---:|---:|
| Release build and analyzers | Passed, zero warnings | Passed, zero warnings on Windows and Linux |
| Discoverable .NET tests | 456 passed, 0 skipped | 455 passed, 0 skipped on each platform |
| TRX breakdown | Core 89; Composition 269; Extensions 82; Platform 16 | Core 89; Composition 268; Extensions 82; Platform 16 |
| Pinned original source tests | 98 Core/Loader and 311 application tests passed | Same on each platform |
| DSH/JIT differential scenarios | 34 matched; JIT repeated three times | Same on each platform |
| Static Native AOT scenarios | Published and all 34 traces matched | Same on each platform |
| NuGet packages | Eight inspected; isolated JIT and AOT consumers passed | Same on each platform |

The historical runs inspected the same 191 source paths. All 191 matched after newline normalization; 187 also had identical raw hashes. The current candidate uses the serviced .NET SDK 10.0.111 and Node 24.12.0.

This record demonstrates the named checks for the earlier candidate. It is not a proof of formal equivalence, a hosted CI result, or a promise for unreviewed inventory entries. Raw logs containing machine paths and user names are held in a private maintainer archive and are not part of the public source tree.

The 455 total above belongs to that historical run. Later validation derives its total and per-assembly counts from the produced TRX files; it does not carry this number forward.


### Reusing solution results within a verification job

After `verify.py` completes successfully, `verify-authoring.py --verification artifacts/verification/verification.json` reuses that checkout's solution TRX instead of restoring, building and testing the solution again. It rejects failed or incomplete reports, changed source, a different SDK/RID/checkout, and missing or altered TRX. The authoring gate still audits required suites and runs its compiler, mutation, example, deployment and package-consumer checks. Omit `--verification` to run authoring verification independently. CI and release use this same-job path; it does not reuse another workflow's packages or authorize publication.
