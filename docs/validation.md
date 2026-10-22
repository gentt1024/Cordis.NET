# Validation

[中文](validation.zh.md)

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
