# Validation record

[中文](validation.zh.md)

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
