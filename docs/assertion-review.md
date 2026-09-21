# Assertion correspondence review

Assertion correspondence is tracked separately from implementation disposition and test
execution. `adapted-passed` and `adapted-test-passed` mean the named .NET target exists and
passes; they no longer automatically set `dotnetOriginalAssertionSetClaim`. A null claim means
that a complete source setup/helper/assertion review has not been recorded. It is neither a
failure nor an equivalence claim. `adapted-assertions-verified` and an explicit
`assertionReview: reviewed-complete` record produce a true claim.

## Loader basic support

The four `Loader: basic support` cases were reread in the pinned
`packages/loader/tests/index.spec.ts`, including `beforeAll`, the three distinct mock plugins,
the loader helper observations, and the full data assertions. Their shared .NET theory now:

- registers separate `foo`, `bar`, and `qux` plugins, records apply/dispose per plugin, and restores each plugin's `internal/update` veto from the source `beforeAll`;
- checks `foo=1`, `bar=1`, `qux=0` and the registry enabled/enabled/disabled state after the initial load;
- checks unchanged `foo`, stopped `bar`, newly started `qux`, and the registry enabled/disabled/enabled state after replacement; and
- compares the complete two-entry configuration after self-update and self-dispose.

The reviewed IDs are `U-2fc67988058f2005`, `U-ac3c1c8b33dd7c76`,
`U-e107e426b4038796`, and `U-5c29f3f586121ef9`. These four component records explicitly carry
`assertionReview: reviewed-complete`.

## Risk sample beyond the reported case

The following merged targets were sampled because they cross control-flow, hook, presence,
parser, or recovery boundaries:

| Target | Review result |
|---|---|
| `OriginalEventDispatchFilteringAndFailure` | Receiver filtering and per-mode exception propagation remain distinct; the parallel all-settled assertion is retained in the paired `ParallelWaitsForAllFailures` target. No new defect found. |
| `DisabledExpressionsRemainRawAndReevaluateOnUpdates` | Raw expression retention and all mount/unmount transitions are observed. The real Jint undefined case is now an additional platform regression. No new defect found. |
| `MalformedPatchFilesFailLoud` | Failure categories are exercised, but the merged test does not preserve every original diagnostic-text assertion. Its implementation disposition remains useful, while its assertion claim is now null until that textual adaptation is reviewed explicitly. |
| `RequiredFailureDisposesStartup` | Seven failure kinds, cleanup, required diagnostics, missing-cause behavior, and relevant messages are observed. This review did not promote its merged records to a complete assertion claim because several source parameter cases share other diagnostic targets. |
| `RequiredIdHotReloadFailureKeepsSiblingThenRecovers` | Import, sync, async, and dependency failures preserve the sibling/root and recover the failed entry; the sibling fiber identity is checked. No new defect found. |

This is a bounded risk review, not a claim that every mapped original assertion was reread.
The consolidated map reports the unreviewed remainder explicitly instead of deriving a blanket
claim from passing methods.
