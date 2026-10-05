from pathlib import Path
import importlib.util
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("native_test_map", SCRIPTS / "test-map.py")
test_map = importlib.util.module_from_spec(spec)
spec.loader.exec_module(test_map)

CLASS = "Cordis.Platform.Tests.PackageProcessTests"
METHODS = (
    "Exited_sdk_cannot_report_prepared_output_while_its_child_still_writes",
    "Cancelled_preparation_stops_child_with_independent_output_pipes_before_returning",
    "Host_exit_terminates_owned_package_children_without_waiting_for_sdk_exit",
)
REASON = "Windows package process ownership regression."
NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
MAPPED = "Cordis.Platform.Tests.ConsumerTests.Preserved_assertion"


def row(name, outcome="Passed", *, class_name=None, assembly="Cordis.Platform.Tests.dll", reason=None):
    return {"name": name, "outcome": outcome, "class": class_name or name.rsplit(".", 1)[0],
            "assembly": assembly, "reason": reason}


def windows_rows(outcome="NotExecuted"):
    return [row(CLASS + "." + method, outcome, reason=REASON if outcome == "NotExecuted" else None) for method in METHODS]


def write_trx(directory, rows, name="platform.trx"):
    def tag(local): return "{" + NS + "}" + local
    run = ET.Element(tag("TestRun"))
    results = ET.SubElement(run, tag("Results"))
    definitions = ET.SubElement(run, tag("TestDefinitions"))
    for index, item in enumerate(rows):
        identity = str(index)
        result = ET.SubElement(results, tag("UnitTestResult"), {
            "testId": identity, "testName": item["name"], "outcome": item["outcome"]})
        if item["reason"] is not None:
            error = ET.SubElement(ET.SubElement(result, tag("Output")), tag("ErrorInfo"))
            ET.SubElement(error, tag("Message")).text = item["reason"]
        definition = ET.SubElement(definitions, tag("UnitTest"), {"id": identity, "name": item["name"]})
        ET.SubElement(definition, tag("TestMethod"), {"className": item["class"],
            "name": item["name"].rsplit(".", 1)[-1], "codeBase": "/build/" + item["assembly"]})
    # The actual xUnit TRX reports zero here even when three Results are NotExecuted.
    summary = ET.SubElement(run, tag("ResultSummary"))
    ET.SubElement(summary, tag("Counters"), {"notExecuted": "0"})
    ET.ElementTree(run).write(directory / name, encoding="utf-8", xml_declaration=True)


class NativePlatformResultsTests(unittest.TestCase):
    def verify(self, rows, *, platform="linux", mapped=MAPPED):
        mapping = {"nativeTheoryInstanceCounts": {}, "tests": [{"id": "preserved",
            "dotnet": [mapped], "dotnetOriginalAssertionSetClaim": True, "status": "adapted-assertions-verified"}]}
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            write_trx(directory, rows)
            with patch.object(sys, "platform", platform):
                return test_map.verify_results(mapping, directory)

    def test_linux_accepts_only_three_owned_windows_cases_and_counts_actual_results(self):
        result = self.verify([row(MAPPED), *windows_rows()])
        self.assertEqual(result["testCount"], 4)
        self.assertEqual(result["passedCount"], 1)
        self.assertEqual(result["skippedCount"], 3)
        self.assertEqual({case["name"] for case in result["platformExceptions"]}, {CLASS + "." + method for method in METHODS})
        self.assertEqual(result["tests"][0]["results"][0]["outcome"], "Passed")

    def test_windows_requires_three_actual_passes(self):
        result = self.verify([row(MAPPED), *windows_rows("Passed")], platform="win32")
        self.assertEqual(result["passedCount"], 4)
        self.assertEqual(result["skippedCount"], 0)

    def test_an_extra_skip_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.verify([row(MAPPED), *windows_rows(), row("Cordis.Platform.Tests.OtherTests.Some_case", "NotExecuted", reason=REASON)])

    def test_any_failed_result_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.verify([row(MAPPED, "Failed"), *windows_rows()])

    def test_wrong_skip_reason_is_rejected(self):
        for reason in (None, "Other skip.", REASON + " "):
            with self.subTest(reason=reason), self.assertRaises(AssertionError):
                cases = windows_rows()
                cases[0]["reason"] = reason
                self.verify([row(MAPPED), *cases])

    def test_the_exception_is_not_accepted_on_other_platforms(self):
        for platform in ("win32", "darwin", "freebsd"):
            with self.subTest(platform=platform), self.assertRaises(AssertionError):
                self.verify([row(MAPPED), *windows_rows()], platform=platform)

    def test_definition_class_cannot_impersonate_the_allowed_test_name(self):
        cases = windows_rows()
        cases[0]["class"] = "Cordis.Platform.Tests.OtherTests"
        with self.assertRaises(AssertionError): self.verify([row(MAPPED), *cases])

    def test_definition_assembly_cannot_impersonate_the_allowed_test_name(self):
        cases = windows_rows()
        for case in cases: case["assembly"] = "Cordis.Other.Tests.dll"
        with self.assertRaises(AssertionError):
            self.verify([row(MAPPED, assembly="Cordis.Other.Tests.dll"), *cases])

    def test_definition_method_must_match_the_case(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            write_trx(directory, [row(MAPPED), *windows_rows()])
            path = directory / "platform.trx"
            tree = ET.parse(path)
            tree.findall(".//{*}TestMethod")[1].set("name", "Other_method")
            tree.write(path, encoding="utf-8")
            with patch.object(sys, "platform", "linux"), self.assertRaises(AssertionError):
                test_map.verify_results({"nativeTheoryInstanceCounts": {}, "tests": []}, directory)

    def test_duplicate_or_missing_platform_cases_are_rejected_on_each_platform(self):
        for platform, outcome in (("linux", "NotExecuted"), ("win32", "Passed")):
            cases = windows_rows(outcome)
            for corrupted in (cases[:-1], [*cases, cases[0]]):
                with self.subTest(platform=platform, count=len(corrupted)), self.assertRaises(AssertionError):
                    self.verify([row(MAPPED), *corrupted], platform=platform)

    def test_an_allowed_platform_exception_cannot_satisfy_a_mapped_assertion(self):
        with self.assertRaises(AssertionError):
            self.verify([row(MAPPED), *windows_rows()], mapped=CLASS + "." + METHODS[0])

    def test_missing_original_assertion_match_is_rejected(self):
        with self.assertRaises(AssertionError): self.verify(windows_rows())


class AuthoringPlatformResultsTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location("native_authoring_gate", SCRIPTS / "verify-authoring.py")
        cls.authoring = importlib.util.module_from_spec(spec)
        with patch.object(sys, "path", [str(SCRIPTS), *sys.path]): spec.loader.exec_module(cls.authoring)

    def verify(self, *, missing=None, extra=None):
        class Gate:
            steps = []
            def record(self, name, **values): self.steps.append({"name": name, **values})
        gate = Gate()
        groups = {"Cordis.Platform.Tests.dll": windows_rows()}
        for suite in self.authoring.REQUIRED_SUITES:
            if suite == missing: continue
            assembly = ".".join(suite.split(".")[:3]) + ".dll"
            groups.setdefault(assembly, []).append(row(suite + ".Required_contract", assembly=assembly))
        if extra is not None: groups["Cordis.Platform.Tests.dll"].append(extra)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for assembly, rows in groups.items(): write_trx(directory, rows, assembly + ".trx")
            with patch.object(sys, "platform", "linux"): self.authoring.verify_test_suites(gate, directory)
        return gate.steps

    def test_authoring_reuses_the_full_audit_and_preserves_required_suites(self):
        steps = self.verify()
        audit = next(step for step in steps if step["name"] == "native-platform-results")
        self.assertEqual(audit["skippedCount"], 3)
        required = next(step for step in steps if step["name"] == "required-authoring-suites")
        self.assertEqual(set(required["suites"]), set(self.authoring.REQUIRED_SUITES))
        self.assertTrue(all(cases and all(case["outcome"] == "Passed" for case in cases) for cases in required["suites"].values()))

    def test_authoring_rejects_an_unrequired_suite_skip(self):
        with self.assertRaises(AssertionError):
            self.verify(extra=row("Cordis.Platform.Tests.OtherTests.Optional_case", "NotExecuted", reason=REASON))

    def test_authoring_still_rejects_a_missing_required_suite(self):
        with self.assertRaises(RuntimeError): self.verify(missing=self.authoring.REQUIRED_SUITES[0])


if __name__ == "__main__":
    unittest.main()
