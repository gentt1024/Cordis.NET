"""Run isolated fixed-source shards and require their complete union in CI.

Only the expensive linked-resolution matrix is split within a file. Its cases
create and dispose independent fixtures; the remaining suites keep their normal
file and test ordering. No upstream source is rewritten.
"""
import argparse
from collections import Counter
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import sys
import time

from verify import ROOT, source_hashes

SHARDS = 4
MATRIX = "packages/boot/app-boot/tests/linked-resolution-matrix.spec.ts"
DSH = "639ed015397290b3745d163aafe02ffee4aa3f84"
ORIGIN = "56b3d4f725681cf4556c1a8695a709cc3b6eed74"
SKIPPABLE = ("composition", "packages/boot/app-boot/tests/package-meta.spec.ts",
             "plugin locale display metadata > rejects case-equivalent language files on case-sensitive filesystems", 0)
CONFIGS = {"composition": "reference/vitest-composition.config.mjs", "core": "reference/vitest.config.mjs"}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def write(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def relative_file(name, root):
    return Path(name).resolve().relative_to(root.resolve()).as_posix()


def fixed_counts():
    manifest = read(ROOT / "reference/upstream-suite-counts.json")
    require(manifest["dsh"] == DSH, "Upstream count inventory belongs to another baseline")
    counts = {("composition", file): count for file, count in manifest["composition"].items()}
    historical = read(ROOT / "docs/upstream-tests.json")["tests"]
    counts.update(Counter(("core", test["file"]) for test in historical
                          if test["file"].startswith(("packages/core/tests/", "packages/loader/tests/"))))
    return counts


def validate_inventory(inventory, counts):
    require(Counter(tuple(case[:2]) for case in inventory) == counts,
            "Discovered files/case counts differ from the fixed original suite inventory")


def plan(inventory):
    keys = [tuple(case) for case in inventory]
    require(len(set(keys)) == len(keys), "Duplicate discovered test identity")
    matrix = sorted(key for key in keys if key[:2] == ("composition", MATRIX))
    require(len(matrix) >= SHARDS, "Linked-resolution matrix missing from discovery")
    assignments = {key: index % SHARDS + 1 for index, key in enumerate(matrix)}
    return {key: assignments.get(key, 1) for key in keys}


def validate_report(report, suite, root, expected, selected):
    """Validate every emitted case, including cases excluded by the shard filter."""
    require(report.get("success") is True and report.get("numFailedTests") == 0,
            "Vitest did not report a successful run")
    require(not report.get("numRuntimeErrorTestSuites", 0), "Vitest suite/runtime failure")
    observed, outcomes, statuses, occurrences = set(), [], Counter(), Counter()
    files = set()
    for file in report["testResults"]:
        relative = relative_file(file["name"], root)
        require(relative not in files and file["status"] == "passed", "Duplicate or failed test file")
        files.add(relative)
        for test in file["assertionResults"]:
            name = " > ".join([*test["ancestorTitles"], test["title"]])
            prefix = (suite, relative, name)
            key = (*prefix, occurrences[prefix])
            occurrences[prefix] += 1
            require(key not in observed, "Duplicate reported test identity")
            observed.add(key)
            status = test["status"]
            statuses[status] += 1
            require(not test.get("failureMessages"), "Test has failure messages")
            if key not in selected:
                require(key in expected and status == "skipped", "Unassigned test executed or unknown test reported")
                continue
            require(status == "passed" or (key == SKIPPABLE and status == "skipped"),
                    f"Selected test failed or unexpectedly skipped: {key}")
            outcomes.append({"case": list(key), "status": status})
    require(observed == expected, "Report does not contain the complete expected file/case set")
    require(files == {key[1] for key in expected}, "Report file set differs from discovery")
    require({tuple(row["case"]) for row in outcomes} == selected, "Missing selected test outcome")
    require(report["numTotalTests"] == len(observed)
            and report["numPassedTests"] == statuses["passed"]
            and report["numPendingTests"] == statuses["skipped"], "Vitest totals do not match outcomes")
    return outcomes


def run_shard(dsh, origin, shard):
    output = ROOT / "artifacts" / f"upstream-shard-{shard}"
    require(not output.exists(), f"Output already exists: {output}; use a fresh checkout or output directory")
    output.mkdir(parents=True)
    initial = source_hashes()
    identity = {"commit": git("rev-parse", "HEAD"), "platform": platform.system(),
                "architecture": platform.machine(), "node": subprocess.check_output(["node", "--version"], text=True).strip(),
                "dsh": DSH, "origin": ORIGIN, "sourceSha256": initial}
    env = dict(os.environ, CI="true", CORDIS_DSH_REFERENCE=str(dsh), CORDIS_ORIGIN_REFERENCE=str(origin))
    env.pop("CORDIS_UPSTREAM_SELECTION", None)
    env.pop("CORDIS_UPSTREAM_REPORT", None)
    temp = output / "temp"
    temp.mkdir()
    env.update(TEMP=str(temp), TMP=str(temp), TMPDIR=str(temp))
    steps, inventory, outcomes = [], [], []
    result = {"status": "failed", "shard": shard, "shards": SHARDS, "identity": identity,
              "inventory": inventory, "outcomes": outcomes, "steps": steps}

    def execute(label, command, environment=env):
        started = time.monotonic()
        print(f"RUN {label}", flush=True)
        process = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, timeout=2400)
        (output / f"{label}.stdout.log").write_bytes(process.stdout)
        (output / f"{label}.stderr.log").write_bytes(process.stderr)
        steps.append({"name": label, "seconds": round(time.monotonic() - started, 3), "exitCode": process.returncode})
        print(f"{'PASS' if process.returncode == 0 else 'FAIL'} {label} ({steps[-1]['seconds']}s)", flush=True)
        if process.returncode:
            print((process.stdout + process.stderr).decode("utf-8", "replace")[-6000:])
            raise RuntimeError(f"{label} exited {process.returncode}")

    try:
        require(read(ROOT / "upstream.lock.json")["harness"]["commit"] == DSH,
                "Review the matrix partition contract before changing the fixed upstream")
        for directory, pin in ((dsh, DSH), (origin, ORIGIN)):
            require(git("-C", str(directory), "rev-parse", "HEAD") == pin, "Unpinned upstream checkout")
            require(not git("-C", str(directory), "status", "--porcelain", "--untracked-files=no"), "Modified upstream source")
        for suite, root in (("composition", dsh), ("core", origin)):
            discovery = output / f"{suite}-discovery.json"
            execute(f"collect-{suite}", ["node", "reference/node_modules/vitest/vitest.mjs", "list",
                                         "--config", CONFIGS[suite], "--json", str(discovery)])
            occurrences = Counter()
            for case in read(discovery):
                prefix = (suite, relative_file(case["file"], root), case["name"])
                # Several unchanged upstream it.each titles are identical after
                # Vitest interpolation. Preserve their multiplicity and order.
                inventory.append([*prefix, occurrences[prefix]])
                occurrences[prefix] += 1
        inventory.sort()
        validate_inventory(inventory, fixed_counts())
        assignments = plan(inventory)
        matrix = {key for key in assignments if key[:2] == ("composition", MATRIX)}
        selected_matrix = {key for key in matrix if assignments[key] == shard}
        # This pinned file has one named describe and no nested suites. The JSON
        # discovery names use " > "; Vitest's name filter uses spaces instead.
        require(all(key[2].count(" > ") == 1 and key[3] == 0 for key in matrix),
                "Matrix nesting or unique case names changed")
        selection = output / "matrix-selection.json"
        write(selection, {"include": [MATRIX], "testNamePattern": "(?:^| )(?:" + "|".join(
            re.escape(key[2].replace(" > ", " ")) for key in sorted(selected_matrix)) + ")$"})
        matrix_report = output / "matrix.json"
        execute("linked-resolution-matrix", ["node", "reference/node_modules/vitest/vitest.mjs", "run",
                                              "--config", CONFIGS["composition"]],
                dict(env, CORDIS_UPSTREAM_SELECTION=str(selection), CORDIS_UPSTREAM_REPORT=str(matrix_report)))
        outcomes.extend(validate_report(read(matrix_report), "composition", dsh, matrix, selected_matrix))
        if shard == 1:
            remaining = {key for key in assignments if key[0] == "composition" and key[1] != MATRIX}
            selection = output / "remaining-selection.json"
            write(selection, {"include": sorted({key[1] for key in remaining})})
            for suite, root, expected in (("composition", dsh, remaining),
                                          ("core", origin, {key for key in assignments if key[0] == "core"})):
                report = output / f"{suite}.json"
                environment = dict(env, CORDIS_UPSTREAM_REPORT=str(report))
                if suite == "composition":
                    environment["CORDIS_UPSTREAM_SELECTION"] = str(selection)
                execute(f"original-{suite}", ["node", "reference/node_modules/vitest/vitest.mjs", "run",
                                              "--config", CONFIGS[suite]], environment)
                outcomes.extend(validate_report(read(report), suite, root, expected, expected))
        for directory in (dsh, origin):
            require(not git("-C", str(directory), "status", "--porcelain", "--untracked-files=no"), "Upstream source changed")
        require(initial == source_hashes(), "Source tree changed during upstream verification")
        result["status"] = "passed"
    finally:
        result["sourceUnchanged"] = initial == source_hashes()
        write(output / "result.json", result)


def aggregate(directory, expected_commit, expected_platform):
    paths = sorted(Path(directory).glob("**/result.json"))
    require(len(paths) == SHARDS, f"Expected {SHARDS} shard results; found {len(paths)}")
    reports = [read(path) for path in paths]
    require(sorted(report["shard"] for report in reports) == list(range(1, SHARDS + 1)), "Missing or duplicate shard")
    first = reports[0]
    identity = first["identity"]
    require(identity["commit"] == expected_commit and identity["platform"] == expected_platform,
            "Results belong to another commit or platform")
    require(identity["dsh"] == DSH and identity["origin"] == ORIGIN, "Unpinned result")
    validate_inventory(first["inventory"], fixed_counts())
    assignments = plan(first["inventory"])
    observed, outcomes = set(), []
    for report in reports:
        require(report["status"] == "passed" and report["sourceUnchanged"] is True, "Failed or drifting shard")
        require(report["shards"] == SHARDS and report["identity"] == identity, "Shard input identity differs")
        require(report["inventory"] == first["inventory"], "Shard discovery sets differ")
        expected = {key for key, shard in assignments.items() if shard == report["shard"]}
        actual = [tuple(row["case"]) for row in report["outcomes"]]
        require(len(set(actual)) == len(actual) and set(actual) == expected, "Missing, duplicate or misplaced case")
        require(not observed.intersection(actual), "Case executed by multiple shards")
        for row in report["outcomes"]:
            require(row["status"] == "passed" or (tuple(row["case"]) == SKIPPABLE and row["status"] == "skipped"),
                    "Failed or unexpectedly skipped selected case")
        observed.update(actual)
        outcomes.extend(report["outcomes"])
    require(observed == set(assignments), "Incomplete upstream case union")
    return {"status": "passed", "identity": identity, "cases": len(outcomes),
            "passed": sum(row["status"] == "passed" for row in outcomes),
            "skipped": [row for row in outcomes if row["status"] == "skipped"],
            "meaning": "Complete fixed-source execution on this platform; not .NET assertion-review closure.",
            "shards": [{"shard": report["shard"], "steps": report["steps"]} for report in reports],
            "outcomes": sorted(outcomes, key=lambda row: row["case"])}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    run = commands.add_parser("run")
    run.add_argument("--dsh", type=Path, required=True)
    run.add_argument("--origin", type=Path, required=True)
    run.add_argument("--shard", type=int, choices=range(1, SHARDS + 1), required=True)
    merge = commands.add_parser("aggregate")
    merge.add_argument("--input", type=Path, required=True)
    merge.add_argument("--platform", choices=("Windows", "Linux"), required=True)
    args = parser.parse_args()
    if args.command == "run":
        run_shard(args.dsh.resolve(), args.origin.resolve(), args.shard)
    else:
        result = aggregate(args.input, git("rev-parse", "HEAD"), args.platform)
        write(args.input / "summary.json", result)
        print(f"PASS complete upstream union: {result['passed']} passed, {len(result['skipped'])} explicit skips")


if __name__ == "__main__":
    main()
