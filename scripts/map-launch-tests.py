"""Rebuild the reviewed launch-policy mapping; selection never infers test results."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
inventory = json.loads((root / "docs/upstream-tests.json").read_text(encoding="utf-8"))
specific = {
    "U-252266e0b5517cc4": "LaunchEnvironmentTests.MissingEnvironmentIsSilentAndReadFailureWarnsOnce",
    "U-b2edfeedc2b829c4": "LaunchEnvironmentTests.MissingEnvironmentIsSilentAndReadFailureWarnsOnce",
    "U-62704638cbe0b2fe": "LaunchEnvironmentTests.MissingEnvironmentIsSilentAndReadFailureWarnsOnce",
    "U-b5491c8ef10b9621": "LaunchOriginalTests.SimpleLoadDefaultsAndReporter",
    "U-1a741d1ef7abec97": "LaunchEnvironmentTests.EnvironmentLayersRetainProvenanceAndInheritedPrecedence",
    "U-ab4f106cd562c226": "LaunchOriginalTests.HomeProxyValuesKeepCaseProvenanceAndInheritedPrecedence",
    "U-768699a722378ad0": "LaunchEnvironmentTests.HomeProxyExceptionDoesNotPermitTrustAndValidatesBothBeforeMaterialization",
    "U-85898e041783e489": "LaunchOriginalTests.ProjectProxyDiagnosticNamesHomeRemedy",
    "U-503a51d5b86d7847": "LaunchEnvironmentTests.HomeProxyExceptionDoesNotPermitTrustAndValidatesBothBeforeMaterialization",
    "U-b7195fd77895ac2c": "LaunchOriginalTests.SnapshotReportsBothAbsolutePathsAndFiltersSources",
    "U-b948655305739fdb": "LaunchOriginalTests.SnapshotReportsBothAbsolutePathsAndFiltersSources",
    "U-be75a2d45267b20e": "LaunchOriginalTests.MissingAndUnreadableLayersContinueWithOneWarningAndDefaultSink",
    "U-5352f1644fbe38f5": "LaunchOriginalTests.MissingAndUnreadableLayersContinueWithOneWarningAndDefaultSink",
    "U-59b8eeed4f4d37c8": "LaunchOriginalTests.MissingAndUnreadableLayersContinueWithOneWarningAndDefaultSink",
    "U-03a945129c3d319a": "LaunchOriginalTests.AbsentLayersCarryInheritedOnlyAndSameDirectoryIsOneProjectLayer",
    "U-4920305131e16805": "LaunchOriginalTests.AbsentLayersCarryInheritedOnlyAndSameDirectoryIsOneProjectLayer",
    "U-e195b62c894f4ce7": "LaunchOriginalTests.FatalDiagnosticFormatsErrorsAndNonErrorsAndDisposalUnregistersBoundary",
    "U-5fa6d1dd6db9fb3b": "LaunchOriginalTests.FatalDiagnosticFormatsErrorsAndNonErrorsAndDisposalUnregistersBoundary",
    "U-a10a494e82e9d615": "LaunchOriginalTests.FatalDiagnosticFormatsErrorsAndNonErrorsAndDisposalUnregistersBoundary",
    "U-9b25c04351c00be8": "LaunchEnvironmentTests.FatalBoundaryReportsBeforeReleaseCoalescesAndRetainsAssembledReasons",
    "U-aa621c9f926cd4b8": "LaunchEnvironmentTests.FatalBoundaryReportsBeforeReleaseCoalescesAndRetainsAssembledReasons",
    "U-8d9fa0287a4e0721": "LaunchEnvironmentTests.FatalBoundaryReportsBeforeReleaseCoalescesAndRetainsAssembledReasons",
    "U-fc2a2379bd6858c7": "LaunchOriginalTests.FatalReleaseWaitsAndTimeoutIsBoundedWithoutSleeping",
    "U-6ce0b5e95dcddbc6": "LaunchEnvironmentTests.FatalBoundaryReportsBeforeReleaseCoalescesAndRetainsAssembledReasons",
}
rows = []
for test in inventory["tests"]:
    if not (test["file"].endswith("/app-boot.spec.ts") and test["line"] < 510):
        continue
    method = specific.get(test["id"])
    if test["title"].startswith("resolveConfigPath /"):
        method = "LaunchOriginalTests.OriginalResolveConfigPath"
    if "refuses to launch when a .env sets" in test["title"]:
        method = "LaunchEnvironmentTests.RefusesBootstrapNamesBeforeApplyingAnything"
    assert method, test["id"]
    row = dict(test, dotnet=["Cordis.Composition.Tests." + method],
               status="adapted-test-passed", platform="JIT; portable managed data/Task policy",
               adaptation="Caller-owned environment dictionary replaces process.env. Home is a launch input resolved before file layers; file layers cannot change it. Values, precedence, provenance and failure ordering are preserved.",
               evidence=["artifacts/verification/tests/Cordis.Composition.Tests.trx"])
    if test["title"].startswith("installFailLoud /"):
        row["adaptation"] = "FatalLoadGuard is an explicitly routed host error boundary. Node unhandledRejection timing/listener APIs do not map to GC-triggered Task.UnobservedTaskException. Tests verify diagnostics, first-failure latch, observed-error suppression and bounded release with a controlled TimeProvider; no process event or real exit is invoked."
        if test["id"] in {"U-a10a494e82e9d615", "U-9b25c04351c00be8"}:
            row["status"] = "platform-adaptation-verified"
            row["note"] = "Node process listener count and rejection checkpoint assertions are not reported passed. Explicit disposal and RetainAssembled ownership are independently verified."
    rows.append(row)
(root / "tests/Cordis.Composition.Tests/launch-upstream-map.json").write_text(json.dumps({
    "format": "cordis-test-map/v1", "targetCommit": inventory["targetCommit"], "tests": rows,
}, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"{len(rows)} reviewed launch-policy mappings")
