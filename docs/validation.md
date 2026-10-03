# Validation record

[中文](validation.zh.md)

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
