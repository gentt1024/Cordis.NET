"""Merge reviewed dispositions and verify their named tests against actual TRX results."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
COMPLETE = {"adapted-test-passed", "adapted-passed", "adapted-assertions-verified"}
UNRESOLVED = {"unmapped", "unimplemented", "adapted-scenario-needs-assertion-audit"}
WINDOWS_PROCESS_CLASS = "Cordis.Platform.Tests.PackageProcessTests"
WINDOWS_PROCESS_METHODS = (
    "Exited_sdk_cannot_report_prepared_output_while_its_child_still_writes",
    "Cancelled_preparation_stops_child_with_independent_output_pipes_before_returning",
    "Host_exit_terminates_owned_package_children_without_waiting_for_sdk_exit",
)
WINDOWS_PROCESS_NAMES = {WINDOWS_PROCESS_CLASS + "." + method for method in WINDOWS_PROCESS_METHODS}
WINDOWS_PROCESS_REASON = "Windows package process ownership regression."


def merged():
    inventory = json.loads((ROOT / "docs/upstream-tests.json").read_text(encoding="utf-8"))
    candidates = {}
    theory_counts = {}
    paths = sorted((ROOT / "tests").glob("**/*map.json"), key=lambda p: (p.name != "upstream-map.json", str(p)))
    for path in paths:
        component = json.loads(path.read_text(encoding="utf-8"))
        theory_counts.update(component.get("nativeTheoryInstanceCounts", {}))
        for item in component["tests"]:
            if item.get("status") in UNRESOLVED:
                continue
            candidates[item["id"]] = (item, path.relative_to(ROOT).as_posix())
    records = []
    for source in inventory["tests"]:
        item, path = candidates.get(source["id"], ({"status": "unimplemented"}, None))
        record = dict(source)
        record.update({k: v for k, v in item.items() if k not in ("keyAssertions", "file", "title", "parameter", "evidence")})
        record["mappingSource"] = path
        record["componentEvidence"] = item.get("evidence", [])
        record["evidence"] = [
            "verification/publication-readiness/windows/test-map-evidence.json",
            "verification/publication-readiness/linux/test-map-evidence.json",
        ] if record.get("dotnet") else []
        review = item.get("assertionReview")
        if item["status"] not in COMPLETE:
            claim = False
            review_status = "not-complete"
        elif item["status"] == "adapted-assertions-verified" or review == "reviewed-complete":
            claim = True
            review_status = "reviewed-complete"
        else:
            claim = None
            review_status = "not-recorded"
        record["assertionReviewStatus"] = review_status
        record["dotnetOriginalAssertionSetClaim"] = claim
        records.append(record)
    missing = [r["id"] for r in records if r["status"] in UNRESOLVED]
    assert not missing, f"Unreviewed inventory: {missing}"
    assert len({r["id"] for r in records}) == len(records)
    return {"format": "cordis-consolidated-test-map/v1", "targetCommit": inventory["targetCommit"],
            "nativeTheoryInstanceCounts": theory_counts,
            "countsByDisposition": dict(sorted(Counter(r["status"] for r in records).items())),
            "countsByAssertionReview": dict(sorted(Counter(r["assertionReviewStatus"] for r in records).items())),
            "meaning": "Scope dispositions and assertion correspondence are separate. A null dotnetOriginalAssertionSetClaim means the adaptation may pass but no complete source setup/helper/assertion review is recorded; it is not a negative equivalence finding.",
            "tests": records}


def method_name(location):
    if "::" in location:
        path, method = location.split("::", 1)
        file = ROOT / path
        assert file.is_file(), location
        source = file.read_text(encoding="utf-8")
        namespace = re.search(r"namespace\s+([\w.]+)", source).group(1)
        location = namespace + "." + file.stem + "." + method
    return location


def normalized_case(name):
    # Component maps may omit C# parameter labels or spell booleans as JS literals.
    name = re.sub(r"([,(])\s*\w+:\s*", r"\1", name)
    name = re.sub(r"\b(True|False)\b", lambda match: match[0].lower(), name)
    return re.sub(r",\s*", ",", name)


def audit_native_results(directory):
    """Require actual passes except the three defined Windows-only cases on Linux."""
    results = []
    counts_by_assembly = Counter()
    for file in sorted(directory.glob("*.trx")):
        tree = ET.parse(file)
        assemblies = {
            Path(method.attrib["codeBase"]).stem
            for method in tree.findall(".//{*}TestMethod")
            if method.attrib.get("codeBase")
        }
        assert len(assemblies) == 1, (file, assemblies)
        assembly = assemblies.pop()
        definitions = {}
        for definition in tree.findall(".//{*}TestDefinitions/{*}UnitTest"):
            identity = definition.attrib["id"]
            assert identity not in definitions, (file, identity, "duplicate test definition")
            definitions[identity] = definition
        for result in tree.findall(".//{*}UnitTestResult"):
            definition = definitions.get(result.attrib.get("testId"))
            method = definition.find("{*}TestMethod") if definition is not None else None
            code_base = method.attrib.get("codeBase", "") if method is not None else ""
            results.append({"name": result.attrib["testName"], "outcome": result.attrib["outcome"], "trx": file.name,
                            "class": method.attrib.get("className") if method is not None else None,
                            "method": method.attrib.get("name") if method is not None else None,
                            "assembly": code_base.replace("\\", "/").rsplit("/", 1)[-1],
                            "reason": result.findtext("{*}Output/{*}ErrorInfo/{*}Message")})
            counts_by_assembly[assembly] += 1
    assert results, "No native test results were discovered"
    platform_cases = [result for result in results if result["name"] in WINDOWS_PROCESS_NAMES]
    assert Counter(result["name"] for result in platform_cases) == Counter(WINDOWS_PROCESS_NAMES), \
        "Each Windows process ownership test must occur exactly once"
    for result in platform_cases:
        assert (result["class"] == WINDOWS_PROCESS_CLASS
                and result["method"] == result["name"].rsplit(".", 1)[-1]
                and result["assembly"] == "Cordis.Platform.Tests.dll"), (result, "incorrect platform test definition")
    exceptions = []
    for result in results:
        if result["outcome"] == "Passed":
            continue
        assert (sys.platform == "linux" and result["name"] in WINDOWS_PROCESS_NAMES
                and result["outcome"] == "NotExecuted" and result["reason"] == WINDOWS_PROCESS_REASON), \
            (result, "Native tests must pass except the exact Linux Windows-only cases")
        exceptions.append(result)
    # xUnit's TRX summary can report notExecuted=0 for these skips; count Results, never the counter.
    return {"testCount": len(results), "passedCount": sum(result["outcome"] == "Passed" for result in results),
            "skippedCount": len(exceptions), "platform": sys.platform, "platformExceptions": exceptions,
            "countsByAssembly": dict(sorted(counts_by_assembly.items())), "trxDirectory": directory.name,
            "results": results}


def verify_results(mapping, directory):
    audit = audit_native_results(directory)
    results = audit["results"]
    for method, expected in mapping["nativeTheoryInstanceCounts"].items():
        actual = sum(r["name"].startswith(method + "(") for r in results)
        assert actual == expected, (method, expected, actual)
    evidence = []
    for test in mapping["tests"]:
        matches = []
        for location in test.get("dotnet", []):
            name = method_name(location)
            selector = test.get("dotnetCaseSelectors", {}).get(location)
            if selector:
                actual = [r for r in results if r["name"] == selector]
            elif "(" in name:
                actual = [r for r in results if normalized_case(r["name"]) == normalized_case(name)]
            else:
                actual = [r for r in results if r["name"] == name or r["name"].startswith(name + "(")]
            assert actual, (test["id"], location, "not found in current run")
            assert all(result["outcome"] == "Passed" for result in actual), \
                (test["id"], location, "mapped assertions must actually pass")
            matches.extend(actual)
        if test["dotnetOriginalAssertionSetClaim"] is True:
            assert matches, (test["id"], "complete adaptation has no executed test")
        if matches:
            evidence.append({"id": test["id"], "disposition": test["status"], "results": matches})
    return {"format": "cordis-test-map-run/v1", **{key: value for key, value in audit.items() if key != "results"},
            "meaning": "All mapped method instances passed in this run. Exact Windows-only cases on Linux remain reported as unexecuted, never as assertion passes. Semantic assertion correspondence is reviewed in component maps.", "tests": evidence}



def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--write", action="store_true")
    parser.add_argument("--results", type=Path)
    parser.add_argument("--output", type=Path)
    options = parser.parse_args()
    mapping = merged()
    serialized = json.dumps(mapping, indent=2, ensure_ascii=False) + "\n"
    path = ROOT / "docs/test-map.json"
    if options.write:
        path.write_text(serialized, encoding="utf-8")
    else:
        assert path.read_text(encoding="utf-8") == serialized, "Run python scripts/test-map.py --write after reviewing map changes"
    if options.results:
        assert options.output
        options.output.write_text(json.dumps(verify_results(mapping, options.results), indent=2) + "\n", encoding="utf-8")
    print(json.dumps(mapping["countsByDisposition"], indent=2))


if __name__ == "__main__":
    main()
