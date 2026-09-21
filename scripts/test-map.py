"""Merge reviewed dispositions and verify their named tests against actual TRX results."""
import argparse
from collections import Counter
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
COMPLETE = {"adapted-test-passed", "adapted-passed", "adapted-assertions-verified"}
UNRESOLVED = {"unmapped", "unimplemented", "adapted-scenario-needs-assertion-audit"}


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


def verify_results(mapping, directory):
    results = []
    counts_by_assembly = Counter()
    for file in directory.glob("*.trx"):
        tree = ET.parse(file)
        assemblies = {
            Path(method.attrib["codeBase"]).stem
            for method in tree.findall(".//{*}TestMethod")
            if method.attrib.get("codeBase")
        }
        assert len(assemblies) == 1, (file, assemblies)
        assembly = assemblies.pop()
        for result in tree.findall(".//{*}UnitTestResult"):
            results.append({"name": result.attrib["testName"], "outcome": result.attrib["outcome"], "trx": file.name})
            counts_by_assembly[assembly] += 1
    assert results and all(r["outcome"] == "Passed" for r in results), "All discovered tests must pass; no skipped cases"
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
            matches.extend(actual)
        if test["dotnetOriginalAssertionSetClaim"] is True:
            assert matches, (test["id"], "complete adaptation has no executed test")
        if matches:
            evidence.append({"id": test["id"], "disposition": test["status"], "results": matches})
    return {"format": "cordis-test-map-run/v1", "testCount": len(results),
            "countsByAssembly": dict(sorted(counts_by_assembly.items())), "trxDirectory": directory.name,
            "meaning": "All named method instances passed in this run. Semantic assertion correspondence is reviewed in component maps; platform-only assertions are not passes.", "tests": evidence}


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
