# Validation

[中文](validation.zh.md)

## Candidate Remote metadata, 2026-10-23

S8 keeps the formal lock at `639ed015397290b3745d163aafe02ffee4aa3f84` and uses a scoped exception for candidate `687f282e8625c6b8d11ff58211a90524be2e4317`. Unmodified candidate originals passed 56 tests with no failures or skips: RemoteInfo 8, lazy loader 1, generator metadata 29 and the complete loader suite 18. The external source-mode harness uses Vitest 4.1.11, TypeScript 6.0.3 and Vite 8.2.2; this does not establish a full upstream workspace build or typecheck.

Native tests cover lazy index/parser/projection boundaries, independent normal/strict views, snapshot identity and descriptor copies, missing versus null, named invocation and optional tool-schema failure. The saved-model independent consumer exercises generated metadata without the author assembly, parses a recursive DTO despite projection refusal, and uses named invocation over real HTTP and duplex transport. Generated TypeScript checks cover metadata identity, input policies, optional projections and return-handle preservation. Existing declaration, output-codec and authoring failure checks remain in place.

Run the full fixed-baseline gate and verify one freshly built package batch:

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-typert-dotnet.py --packages <package-batch> --output <empty-saved-output> --saved-remote-only
python3 scripts/verify-typert-dotnet.py --packages <package-batch> --output <empty-web-output> --web-contracts-only
python3 scripts/verify-typert.py --packages <package-batch> --output <empty-author-output>
python3 scripts/verify-remote-metadata.py --packages <package-batch> --output <empty-metadata-output>
```

The local candidate checks do not establish Windows/Linux or Native AOT release approval. Those remain part of the same-batch platform matrix before a formal baseline promotion. Raw logs and machine-specific paths remain outside the repository.


## Candidate events and access clients, 2026-10-23

S7 uses the scoped candidate exception at `687f282e8625c6b8d11ff58211a90524be2e4317`; the formal lock remains `639ed015397290b3745d163aafe02ffee4aa3f84`. Unmodified candidate originals passed 67 tests with zero failures/skips: Gateway access 30, RemoteAccess Host 21, ClientAccess 10 and OperatorConnect 6. The external source-mode harness uses Vitest 4.1.11, TypeScript 6.0.3 and Vite 8.2.2. An initial run passed 61 tests but could not collect OperatorConnect without `compression`; after resolving candidate-lock dependencies outside both checkouts, the complete retry passed 67. Counts are not added across runs. This is not a complete upstream workspace build or a native test result.

| S7 contract | Native evidence |
|---|---|
| Whole-notification resource authorization, reader failures, owner bypass, passive refusal | `RemoteEventsGatewayTests` |
| Same-Peer answer ownership, non-owner observation, pending waterfalls across generations | `RemoteEventsGatewayTests` |
| Peer/source closure discards queued frames, sibling isolation | `TypertIdentityTests`, `RemoteEventsGatewayTests` |
| Snapshot ordering, coalescing, targeted full-principal invalidation, policy/service withdrawal | `TypertAccessSnapshotTests` |
| Native client carrier generations and terminal failure, package-only consumer | `scripts/verify-access-client.py` |
| Installed TypeScript declarations/runtime, stable observables, malformed frames, reconnect, old-generation fencing and fresh remount | `scripts/verify-client-access.mjs`, now a required `verify.py` step |

Run the full gate against the formal checkout, then exercise independent NuGet consumers from the resulting package batch:

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-access-client.py --packages <package-batch> --output <empty-access-output>
python3 scripts/verify-remote-access.py --packages <package-batch> --output <empty-policy-output>
```

The browser check uses the built package outside the checkout and real generated namespace/Connection APIs. The .NET consumer uses a fresh mapped package cache and actual loopback transport. Remount checks are native acceptance evidence; the original candidate ClientAccess suite does not itself test remount. These local checks do not establish Windows/Linux, Native AOT, full Node SDK interoperability or S8 projection compatibility. Raw logs, local paths and harness setup remain outside the repository.

`ExecutionContextTests.Concurrent_entries_keep_waking_the_context_and_remain_serial` exercises repeated public `Context.RunAsync` entry from one or four producers, verifies serialized callbacks, and bounds completion and disposal. It reproduced the native lost wake before the memory-ordering fix; the captured CLR-suite hang independently showed a queued disposal callback with no scheduled drain.


## Candidate resource decisions and preparation, 2026-10-23

S6 uses the [scoped candidate exception](compatibility.md#candidate-resource-decisions-and-preparation-2026-10-23) at `687f282e8625c6b8d11ff58211a90524be2e4317`; formal behavior lock and historical inventory remain `639ed015397290b3745d163aafe02ffee4aa3f84`. The local environment is macOS arm64, .NET SDK 10.0.111/runtime 10.0.11 and Node 24.13.0.

The unmodified candidate originals passed 106 cases, zero failures/skips: protocol remote-access 8, gateway-access 30, gateway host 47 and RemoteAccess host 21. The external minimal harness uses candidate-selected Vitest 4.1.11, TypeScript 6.0.3 and Vite 8.2.2. This is not a complete candidate workspace build/typecheck. Some original cases cover S7 events/snapshots; their source pass is reference evidence, not native implementation of those deferred contracts.

| S6 contract | Native checks |
|---|---|
| Binding alternatives, resource intersection, condition/explanation priority, failed policy and owner | `TypertRemoteAccessTests`; native malformed-output and diagnostic suppression/reset cases |
| No Context/lookup side effects before authorization; ordinary JSON error priority | `TypertGatewayAccessTests` and existing lookup-only `TypertGatewayPreparationTests` |
| Reader/provider/default precedence, first-object fallback, duplicate IDs, invalid/absent resources | `TypertGatewayAccessTests`, independent NuGet consumer |
| Decoded view reused by business; defaults, setter-only and opaque/polymorphic types, optional/null and cancellation | `TypertGatewayAccessTests`; serialization is forbidden by a counting codec |
| Dynamic list filtering and continued S7 event refusal | Real Gateway unary/stream tests and independent NuGet consumer |
| Identity cannot be chosen by wire; grants and withdrawal precede provider calls | `RemoteCarrierTests.Carrier_identity_and_access_are_host_selected_before_lookup`, real HTTP and WebSocket mux |

The required full gate uses the formal checkout, with prior evidence archived before choosing an empty output directory. The independent S6 consumer is a separate command, not an implicit `verify.py` step:

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-remote-access.py --packages <package-batch> --output <empty-output>
```

The consumer uses a generated console outside the checkout, a fresh cache and explicit local package-source mapping, with only `Cordis.NET.Composition` as a direct package dependency and serializer reflection disabled. Local evidence does not establish Windows/Linux, Native AOT, full DSH Node SDK interoperability or the remaining S7 closure. Machine paths, raw logs and private audit setup remain outside the repository.

Executed result: all 33 requested full-gate steps passed with `sourceUnchanged=true`. Native tests passed 1,153 cases with zero failures and the single existing Windows Job-object host-exit exclusion on macOS. Formal profile/module originals passed 54 cases. API, generated clients, CLI, differential traces and mapped test instances passed; original Core/Loader source suites were not requested. Focused composition tests passed 117 cases and real carrier/event tests passed 16, without failures or skips.

Nine packages passed inspection; seven packaged library DLLs matched the final tested Release outputs byte for byte. The independent S6 consumer passed all 37 checks; the existing saved-model Remote consumer passed all 37 steps with their expected outcomes. Eleven changed C# files passed all four canonical formatting stages. Review corrected implicit extraction for opaque enumerable and polymorphic objects and optional direct IDs; regression tests prove both refusal and explicit-reader recovery.

The S5 consumer's first run exposed an obsolete error-priority assertion: an undeclared `principal` argument now fails exact-field validation before access. Its successor explicitly checks that refusal and separately checks access denial for valid guest arguments, retaining zero-business-invocation assertions on both paths. All 16 checks then passed from the integrated root script. After the full gate, only that independently verified consumer script and these bilingual result paragraphs changed; documentation, whitespace and source hashes were rechecked. Runtime, tests and public API bytes remained unchanged. No failed test was skipped or removed.

## Candidate Remote identity and resources, 2026-10-23

S5 follows the [scoped candidate exception](compatibility.md#candidate-remote-identity-and-resource-slice-2026-10-23). Candidate source remains `687f282e8625c6b8d11ff58211a90524be2e4317`; the formal lock and historical inventory remain `639ed015397290b3745d163aafe02ffee4aa3f84`. This run uses macOS arm64, .NET SDK 10.0.111/runtime 10.0.11 and Node 24.13.0.

The unmodified candidate originals passed 65 cases with no failures or skips: `packages/typert/protocol/tests/remote-access.spec.ts` 8, `packages/typert/registry/tests/typert.spec.ts` 27 and `packages/api/gateway/tests/gateway-access.host.spec.ts` 30. The external minimal harness uses candidate-selected Vitest 4.1.11, TypeScript 6.0.3 and Vite 8.2.2; this is not a complete candidate pnpm workspace build or TypeScript typecheck. The suites include S6/S7 reference behavior that S5 does not implement. Passing the source suites is reference evidence, not a claim of complete native authorization equivalence.

| S5 source contract | Native checks |
|---|---|
| `principalOf`: guest identity, owner/outside omission and case-sensitive kind | `TypertIdentityTests.Principal_is_detached_and_only_non_owner_calls_are_attributed`; malformed JSON identity boundary cases |
| Resource kind/field ownership, reserved global and resolver overrides | All `TypertResourceRegistryTests`: 17 cases, including observer containment, subscription withdrawal, detached ordered snapshots and exact-registration cleanup |
| Closed Peer before preparation; stream lifetime versus running unary | `TypertIdentityTests`: direct/explicit/mounted dispatch, both event endpoints, owner withdrawal, sibling independence, unused-uplink release and buffered event cancellation |
| Host identity cannot be selected by wire fields | `RemoteCarrierTests.Carrier_identity_is_host_selected_and_non_owner_never_reaches_lookup`; real HTTP and WebSocket mux |
| Native carrier and trusted direct-call adaptations | Closed-Peer HTTP payload priority, capture before asynchronous Context resolution, no fallback from a disposed mounted Connection; existing Peer/Remote event suites |

The focused native composition run passed 88 cases; the Remote carrier/event run passed 15, with zero failures or skips. The first implementation incorrectly treated a newly created fallback scope in `Loading` state as closed. The corrected check follows the candidate cancellation signal; the full focused run was repeated. Review also added checks before event subscription/readiness and every buffered frame to prevent post-closure delivery. No failed assertion was bypassed.

Required whole-repository verification uses the formal checkout, then packages the same Release build for an independent S5 consumer:

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
dotnet pack Cordis.slnx -c Release --no-build --no-restore -o <package-batch>
python3 scripts/verify-remote-identity.py --packages <package-batch> --output <empty-output>
```

The independent gate uses only `Cordis.NET.Composition` and its package dependencies, a fresh cache, explicit local package-source mapping and a generated console outside this checkout. It checks identity snapshots, owner/guest attribution, provider resource facts, owner dispatch, non-owner refusal and Peer-owned withdrawal. It runs with serializer reflection disabled. These checks do not establish Windows/Linux or Native AOT execution, the remaining S6/S7 authorization closure, or complete DSH Node SDK interoperability.

Executed result: the final full gate passed all 33 requested steps with `sourceUnchanged=true`: 1,093 native tests passed, zero failed, and the single pre-existing Windows Job-object host-exit exclusion remained on macOS. Formal profile/module source tests passed 54 cases. Generated clients, API snapshot, CLI, differential traces and mapped test instances passed. The original Core/Loader test-source suite was not requested or run. An earlier attempt stopped at the nonempty client-output precondition; its evidence was archived, then the complete gate ran again with an empty output directory. No test was skipped to resolve that precondition.

The same Release batch passed inspection for nine packages, all 15 independent S5 consumer checks, and all 37 steps of the existing saved-model Remote package consumer with their expected outcomes. Seven packaged library DLLs matched the final tested Release outputs byte for byte. All four canonical formatting stages agreed for the 12 changed C# files. After the final gate, only these bilingual result paragraphs changed; documentation and whitespace were checked again, and source hashes confirmed no other changes. Machine paths, raw logs and private audit setup remain outside the repository.

## Candidate Profile and package reinstall, 2026-10-22

S4 follows the [scoped candidate exception](compatibility.md#candidate-profile-and-package-reinstall-slice-2026-10-22), with candidate `687f282e8625c6b8d11ff58211a90524be2e4317` and unchanged formal lock `639ed015397290b3745d163aafe02ffee4aa3f84`. Execution uses macOS arm64, .NET SDK 10.0.111/runtime 10.0.11 and Node 24.13.0.

Original candidate source evidence is separate from the formal inventory: HMR's ten files passed 236 tests; profile-resolution passed 233; patch, package-reload and profile-resolution-service passed 52; manager.spec passed 108. These 629 cases passed without source or assertion changes, failures or skips. The external harness uses the fixed checkout and candidate pnpm 11.28.5.

Native evidence covers successor removal and cache publication, stable-directory metadata changes, retained-directory/host rejection, pre-publication retirement, restoration of group order, cleanup failure, historical warnings and repaired-import retry. Real NuGet tests cover new-version and identical same-version reinstall, disabled installation, failed activation followed by removal/retry, direct and foreign Fiber refusal before persistence, and changed private dependency assemblies while a retained delegate still calls the old dependency. Receipt tests compare the entire prepared file tree and reject scaffold, dependency and resource changes. A persistence conflict after toolchain withdrawal can retry without losing unrelated manifest edits; lowercase requests preserve the original NuGet spelling.

The required full gate uses the formal checkout:

```sh
python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts \
  --upstream-test packages/boot/hmr/tests/modules.spec.ts
```

The same Release package batch is also checked with the standalone CLR deployment consumers and the existing repository-external macOS multi-entry adapter. That adapter supplies separately compiled Mach-O native libraries and the macOS RID; it retains the original C# consumer assertions. It is macOS-adapted evidence, not execution of the unmodified Windows/Linux script. No Windows/Linux runtime or Native AOT acceptance is claimed by this local slice. Machine paths, raw logs and private audit setup remain outside the repository.

Executed result: the final full gate passed all 33 requested steps with `sourceUnchanged=true`: 1,047 native tests passed, zero failed, and the single pre-existing Windows Job-object host-exit exclusion remained on macOS. Formal profile/module source tests passed 54 cases. Generated browser/.NET clients, API snapshot, CLI, differential traces and all mapped test instances passed. The actual management declaration contains nullable `restartReason`, and its HTTP result explicitly returns null when no restart is required. The original Core/Loader test-source suite was not requested or run.

The same package batch passed all 12 deployment steps and 25 macOS-adapted multi-entry steps. Seven packaged library DLLs matched the tested Release outputs byte for byte. All four canonical formatting stages agreed for the 17 changed C# files. The first full run exposed one stale incomplete-directory diagnostic assertion; its replacement requires the receipt failure while retaining all old-code, saved-version and byte-preservation checks. The next run passed every native test but found the old local-removal test name in the mapping. That record now names the executed successor tests and explicitly withdraws complete correspondence to the superseded historical assertion. No test was skipped to resolve either issue. After the final gate, only these bilingual results and the mapping's platform-evidence wording changed; documentation, regenerated mapping evidence and whitespace were checked again. Runtime, test code and public API bytes remained unchanged.

## Candidate module identity, 2026-10-22

S3 uses the [scoped candidate exception](compatibility.md#candidate-module-identity-slice-2026-10-22) at `687f282e8625c6b8d11ff58211a90524be2e4317`; the formal lock and historical source inventory remain unchanged.

- The original candidate `packages/boot/hmr/tests/package-manifest-dispatch.spec.ts` passed all 32 tests without changing the upstream source. Candidate evidence is separate from the formal baseline.
- The original implementation rejected the real CLR split fixture with “Aliases of one CLR export must retain the same replacement export.” The new `ClrIdentityTests` cover three loaded imports sharing an old callback, aliases splitting into distinct exports, repeated replacements, disabled entries, direct Fiber ambiguity, import failure, activation failure, complete identity restoration and retry.
- Native module-identity tests also exercise distinct plugin wrappers sharing a callback, deployment remapping while an entry remains live, disable/enable resolution and HMR's loaded-filename preference. Existing CLR tests continue to cover collectible unload, retained references, stable files, raw configuration, direct Fibers and cleanup/recovery failures.
- The final `python3 scripts/verify.py --dsh <fixed-0.2.0-checkout> --upstream-test packages/boot/hmr/tests/modules.spec.ts` gate covers the Release build, native tests, API snapshot, generated clients, CLI/client contracts, conformance traces, fixed-baseline source tests and whitespace. The existing Windows Job Object host-exit test is platform-excluded on macOS; this slice adds no skip or exclusion.
- Repository examples and standalone CLR consumer sources are migrated to the new resolver result and callback list. This local macOS run does not claim the Windows/Linux NuGet multi-entry native-library consumer, AOT or original Core/Loader test-source gates.

Executed results: the final gate passed all 33 requested steps with 1,007 native tests passing and the one existing platform exclusion. The fixed formal HMR module source suite passed 20 tests, separately from the candidate dispatch suite's 32. No runtime, test, example or API snapshot bytes changed after that gate; this results paragraph was added afterward.

The same Release package batch passed all 12 `verify-clr-deployment.py` steps on `osx-arm64`, including framework-dependent directories, self-contained single files and ASP.NET hosts. The three migrated standalone consumer sources also compiled with zero warnings and errors. A repository-external macOS adaptation of `verify-clr-multi-entry.py` passed all 25 steps, retaining all consumer assertions: shared/nested assemblies, native dependency conflicts, candidate recovery, actual HTTP calls through generated Remote clients, contract withdrawal, NuGet installation and the retained worker/resource publication check. The adaptation only supplies two separately compiled Mach-O native probe libraries and the macOS RID in place of the script's Windows/Linux system libraries; the C# fixtures, client program and library source are unchanged. This is macOS-adapted evidence, not execution of the unmodified Windows/Linux script.

Machine paths, raw logs and audit setup remain local. The public API migration is documented in both authoring guides and the reviewed snapshot.

## Candidate management persistence, 2026-10-22

S2 follows the [documented candidate exception](compatibility.md#candidate-management-persistence-slice-2026-10-22) at `687f282e8625c6b8d11ff58211a90524be2e4317`. The formal lock and historical source inventory remain unchanged. Execution used macOS arm64, .NET SDK 10.0.111/runtime 10.0.11 and Node 24.13.0.

Before implementation, regressions demonstrated a redundant `disabled: false` after restoring the bundle default and refusal to remove a selected local bundle without a dependency. Another counterexample demonstrated lost management protection after a bundle declaration disappeared. All now pass. The focused `PluginManagerOriginalTests` run passed **76 cases**, including **32 new cases** in `PluginManagerPersistenceTests.cs`. Coverage includes changing defaults, missing patches, comments and flow syntax, name assertions, settings, entry/key/value anchors, aliases, profile insertions, inherited expressions, home/launch overlays, base-owned groups, admission rejection, conflicting edits and activation failure after pruning. Removal checks verify no uninstall, reselection, missing bundles, retained contributions and management protection.

The complete command returned **`requested-checks-passed`**, with **33 steps passed** and **`sourceUnchanged=true`**:

```sh
python3 scripts/verify.py --dsh <locked-source-checkout> \
  --upstream-test packages/boot/app-boot/tests/user-patches.spec.ts \
  --upstream-test packages/boot/app-boot/tests/profile.spec.ts
```

- Native tests: **999 passed, zero failures**; Composition 591, Core 147, Extensions 118 and Platform 143. The single existing Windows Job-object host-exit case remains platform-excluded on macOS. No exclusion or skip was added.
- Formal-baseline originals: **55 passed** across user-patches and profile. JIT repeatability, fixed DSH differential traces, client build/type/lifecycle/replacement/shared-module/watch/package checks, CLI, API snapshot, optional Typert consumption and whitespace all passed.
- Candidate originals: patch **15**, package-reload **27**, complete manager **108**; **150 passed, zero failed or skipped**. Original source/tests were unmodified. The external Vitest 4.1.11 harness required candidate-pinned pnpm 11.28.5: four initial missing-pnpm failures disappeared after installation and a full manager-suite rerun. This is focused source evidence, not a full upstream workspace build.

The first full run passed native tests but refused to overwrite earlier client-replacement evidence. That output was preserved and the complete command rerun against an empty evidence directory. The final bilingual update passed `scripts/check-docs.py` and `git diff --check`. Machine-specific logs and dependency installations are not committed. This run does not provide Linux/Windows, AOT, full package-consumer or original Core/Loader source-suite evidence, and does not promote the behavior lock.

## Candidate engines.dsh admission, 2026-10-22

The first candidate slice is described in [compatibility](compatibility.md#candidate-dsh-021-admission-slice-2026-10-22). It uses candidate `687f282e8625c6b8d11ff58211a90524be2e4317`; `upstream.lock.json` and the historical source-test inventory remain at `639ed015397290b3745d163aafe02ffee4aa3f84`. Execution used macOS arm64, .NET SDK 10.0.111/runtime 10.0.11, Node 24.13.0 and locked npm development dependencies. Temporary directories used canonical filesystem paths to avoid macOS symlink aliases. Machine-specific logs are not committed.

The complete required command finished with **`requested-checks-passed`**:

```sh
python3 scripts/verify.py --dsh <locked-source-checkout> \
  --upstream-test packages/boot/app-boot/tests/plugin-compatibility.spec.ts \
  --upstream-test packages/boot/app-boot/tests/compatibility-preflight.spec.ts \
  --upstream-test packages/boot/app-boot/tests/profile-compatibility.spec.ts
```

- Native tests: **967 passed, zero failures**, with one existing Windows Job-object host-exit test unexecuted on macOS (Core 147, Composition 559, Extensions 118, Platform 143 passed). The test-map gate verifies that exact exclusion and requires the portable process tests to pass. The previously Windows-only SDK-exit case now also executes on macOS/Linux; no failing case was changed to a skip.
- All 65 `DshProfilePolicyTests` passed, including engine priority, fallback, malformed declarations, exact identity, bundle/entry/reconciliation admission and grant-lock concurrency/cancellation. Before implementation, 12 of the first 13 new compatibility cases failed. The 13 focused real NuGet cases also passed, including disabled admission before SDK execution, HTTP/CLI grants, metadata retention, restart/revocation and compatible engines overriding malformed peers.
- Candidate `plugin-compatibility.spec.ts`: 87 original cases passed separately. The three selected formal-baseline source files above passed 117 original cases. These are reference executions, not a claim of complete candidate compatibility.
- Restore/Release build, test-map, repeated JIT traces, example, both formal-baseline differential traces, browser module build and generated client type consumption, lifecycle/replacement/shared modules, author watch/package, CLI validation/preview/invocation/error exits, API snapshot, optional Typert consumption and whitespace checks all passed in the required run. The source inventory was unchanged during that run. The final bilingual evidence update passed `scripts/check-docs.py` afterward.

The first full run exposed two macOS failures, now repaired:

1. `PackageProcessTests.Cancelled_preparation_stops_child_with_independent_output_pipes_before_returning` had no macOS fixture: setup returned without creating the feed. The same failure reproduced on unchanged baseline `18b611c4cdc6972480e396702106484701118e81`. The fixture now builds a real package and a writer with independent output pipes on macOS.
2. `PackageManagementTests.Cancelling_running_sdk_preparation_drains_before_wait_completes_and_does_not_select` exposed incomplete process ownership. The old macOS path killed a discovered tree but waited only for its leader; .NET explicitly does not make [leader exit wait for descendants](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0). A stronger, repeatable counterexample then showed `Exited_sdk_cannot_report_prepared_output_while_its_child_still_writes` returning successful preparation while its writer remained alive. That test failed before the process-owner repair and passed afterward. Native group creation, group liveness checks, bounded termination waiting and direct-child reaping now prevent both premature outcomes.

The process-focused run passed nine cases; the original cancellation regression additionally passed five consecutive runs under concurrent full-suite load. The final suite also passed a new active-owner/recovery-record test and normal/nonzero exit/output checks. Incomplete termination still retains a recovery record and cannot report settled cancellation. The macOS host-crash boundary and deliberate process-group escape remain explicitly documented in [compatibility](compatibility.md).

This run does not provide Linux/Windows, AOT, full package-consumer or original Core/Loader source-suite evidence. It does not promote the candidate behavior lock.

## Choose the check

Run `python scripts/check-docs.py` for documentation and owning tests for code changes. Runtime, package, compatibility and release changes require `python scripts/verify.py`. See the [maintenance guide](development.md) for commands. Keep required CI checks.

## Released version evidence

The [alpha.5.2 release](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.0-alpha.5.2) supplies the formal manifest, evidence and SHA256SUMS. Its tag identifies `1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4`; all seven jobs in the [formal run](https://github.com/gentt1024/Cordis.NET/actions/runs/39145687915) passed. Eight product packages and matching symbols came from the Linux validation batch; Example was excluded. Public NuGet payloads matched sealed bytes apart from the added NuGet signature. Public release attachments were downloaded and their digests checked.

## Accepted risks and evidence boundaries

Cold Jint construction and native initialization can delay HMR refresh, queued work and draining shutdown. Historical timeouts remain failures. Sampling did not explain the entire delay or establish a latency bound. The ten-second regression remains unchanged; new failures still fail the gate. Missing progress, error publication or cleanup, or unacceptable real cold-start latency, requires concrete follow-up.

Windows/Ubuntu have full package and AOT validation. macOS has same-batch authoring/browser consumption only, not complete runtime, application or AOT acceptance. Rider acceptance does not cover other IDEs. Windows document opening proves system handoff, not editor contents or other desktop platforms. See the [maintenance guide](development.md) for backend restrictions and outstanding named UI integration evidence. Author-directory protection does not promise a hostile-filesystem sandbox.

## Historical evidence

The [original validation record](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/validation.md) retains each candidate identity, failure and then-pending work. [verification](../verification) retains tracked receipts and machine-consumed inputs. Git does not preserve untracked packages, TRX files, screenshots or expired CI artifacts; a hash does not prove that the original bytes remain available.

Record PR merge checkouts separately from branch heads. Local supplements do not turn a failed complete gate into a passing run; different candidates cannot be combined into one acceptance result.
