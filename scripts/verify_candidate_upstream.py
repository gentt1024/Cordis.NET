"""Discover and execute the separately pinned S10 candidate original-suite union.

The reviewed candidate scope and per-file counts are independent of the formal
behavior lock. Discovery is not a pass and never changes historical mappings.
"""
import argparse
from collections import Counter
import fnmatch
import hashlib
import json
import os
from pathlib import Path
import platform
import subprocess
import time

from verify import ROOT, source_hashes
from verify_upstream import (SHARDS, MATRIX, aggregate_reports, matrix_selection, plan, read, require, run_identity,
                             validate_inventory, validate_report, write)

MANIFEST = ROOT / "reference/candidate-manifest.json"
COUNTS = ROOT / "reference/candidate-suite-counts.json"
CONFIG = str(ROOT / "reference/vitest-candidate.config.mjs")
VITEST = str(ROOT / "reference/candidate-tools/node_modules/vitest/vitest.mjs")
DSH = "687f282e8625c6b8d11ff58211a90524be2e4317"
ORIGIN = "56b3d4f725681cf4556c1a8695a709cc3b6eed74"


def digest(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def git(root, *args):
    return subprocess.check_output(["git", "-C", str(root), *args], text=True).strip()


def manifest():
    value = read(MANIFEST)
    require(value["dsh"] == DSH and value["origin"] == ORIGIN, "Unreviewed candidate identity")
    require(value["matrix"] == MATRIX and value["matrixInstances"] == 4800,
            "Review the candidate matrix partition contract before changing it")
    return value


def fixed_sources(dsh, origin):
    value = manifest()
    subprocess.run(["node", "reference/candidate-bind-tools.mjs", str(dsh)], cwd=ROOT, check=True)
    for root, pin in ((dsh, DSH), (origin, ORIGIN)):
        require(git(root, "rev-parse", "HEAD") == pin, "Unpinned candidate checkout")
        require(not git(root, "status", "--porcelain", "--untracked-files=no"), "Modified candidate source")
    for excluded in value["excludedSourceTests"]:
        root, pin = (origin, ORIGIN) if excluded.get("repository") == "origin" else (dsh, DSH)
        require(git(root, "rev-parse", pin + ":" + excluded["file"]) == excluded["sourceBlob"], "Excluded original source changed")
    for rule in value.get("platformSkips", []):
        require(git(dsh, "rev-parse", DSH + ":" + rule["case"][1]) == rule["sourceBlob"], "Platform-skip original source changed")
    require(git(dsh, "rev-parse", DSH + ":" + MATRIX) == value["matrixBlob"], "Candidate matrix source changed")
    return {"dsh": git(dsh, "rev-parse", "HEAD^{tree}"), "origin": git(origin, "rev-parse", "HEAD^{tree}")}


def tool_identity():
    return {"node": subprocess.check_output(["node", "--version"], text=True).strip(),
            "lockSha256": digest(ROOT / "reference/candidate-tools/package-lock.json"),
            "manifestSha256": digest(MANIFEST)}



def expected_files(dsh, origin):
    files = {}
    for suite, spec in manifest()["suites"].items():
        root = origin if spec["repository"] == "origin" else dsh
        tracked = git(root, "ls-files").splitlines()
        excluded = {item["file"] for item in manifest()["excludedSourceTests"]
                    if item.get("repository", "dsh") == spec["repository"]}
        selected = sorted(path for path in tracked if path not in excluded and
                          any(fnmatch.fnmatchcase(path, glob) for glob in spec["include"]))
        require(selected, "Empty candidate source scope: " + suite)
        for pattern in spec["include"]:
            require(any(fnmatch.fnmatchcase(path, pattern) for path in selected), "Missing candidate scope: " + pattern)
        files[suite] = {path: git(root, "rev-parse", "HEAD:" + path) for path in selected}
    return files


def environment(dsh, origin, output, suite):
    env = dict(os.environ, CI="true", CORDIS_DSH_REFERENCE=str(dsh), CORDIS_ORIGIN_REFERENCE=str(origin),
               CORDIS_CANDIDATE_SUITE=suite, CORDIS_CANDIDATE_REFERENCE=str(ROOT / "reference"), CORDIS_CANDIDATE_CACHE=str(output / "cache"))
    for key in ("CORDIS_UPSTREAM_SELECTION", "CORDIS_UPSTREAM_REPORT"):
        env.pop(key, None)
    temp = output / "temp"
    temp.mkdir(exist_ok=True)
    env.update(TEMP=str(temp), TMP=str(temp), TMPDIR=str(temp))
    return env


def execute(output, steps, label, command, env, cwd=ROOT):
    print("RUN " + label, flush=True)
    started = time.monotonic()
    result = subprocess.run(command, cwd=cwd, env=env, capture_output=True, timeout=2400)
    (output / (label + ".stdout.log")).write_bytes(result.stdout)
    (output / (label + ".stderr.log")).write_bytes(result.stderr)
    steps.append({"name": label, "exitCode": result.returncode, "seconds": round(time.monotonic() - started, 3)})
    if result.returncode:
        print((result.stdout + result.stderr).decode("utf8", "replace")[-6000:])
    require(result.returncode == 0, label + " failed")
    print("PASS " + label, flush=True)


def collect(dsh, origin, output, steps):
    inventory = []
    sources = expected_files(dsh, origin)
    for suite, spec in manifest()["suites"].items():
        root = origin if spec["repository"] == "origin" else dsh
        discovery = output / (suite + "-discovery.json")
        execute(output, steps, "collect-" + suite,
                ["node", VITEST, "list", "--config", CONFIG, "--json", str(discovery)],
                environment(dsh, origin, output, suite), cwd=root)
        occurrences = Counter()
        observed = set()
        for case in read(discovery):
            file = Path(case["file"]).resolve().relative_to(root.resolve()).as_posix()
            prefix = (suite, file, case["name"])
            inventory.append([*prefix, occurrences[prefix]])
            occurrences[prefix] += 1
            observed.add(file)
        require(observed == set(sources[suite]), "Discovery omitted or added candidate files: " + suite + " " +
                repr(sorted(set(sources[suite]) ^ observed)))
    inventory.sort()
    # Also verifies unique identities and matrix presence, before recording discovery.
    plan(inventory)
    return inventory, sources


def approved_counts():
    value = read(COUNTS)
    require(value["dsh"] == DSH and value["origin"] == ORIGIN, "Candidate counts belong to other source")
    require(value["manifestSha256"] == digest(MANIFEST), "Candidate counts belong to another scope")
    return {(suite, file): count for suite, files in value["suites"].items() for file, count in files.items()}


def validate_frozen(value):
    require(value["format"] == "cordis-candidate-discovery/v1" and value["dsh"] == DSH and value["origin"] == ORIGIN,
            "Invalid candidate discovery identity")
    require(value["tools"]["lockSha256"] == tool_identity()["lockSha256"] and
            value["tools"]["manifestSha256"] == digest(MANIFEST) and value["tools"]["node"] == tool_identity()["node"], "Discovery tools or scope changed")
    validate_inventory(value["inventory"], approved_counts())
    require(sum(case[:2] == ["composition", MATRIX] for case in value["inventory"]) == 4800,
            "Incomplete candidate linked-resolution matrix")
    plan(value["inventory"])


def discover(dsh, origin, output, batch):
    require(not output.exists(), "Use a fresh candidate discovery output directory")
    output.mkdir(parents=True)
    before = source_hashes()
    trees = fixed_sources(dsh, origin)
    steps = []
    inventory, files = collect(dsh, origin, output, steps)
    require(trees == fixed_sources(dsh, origin) and before == source_hashes(), "Source changed during discovery")
    value = {"format": "cordis-candidate-discovery/v1", "dsh": DSH, "origin": ORIGIN, "runId": batch,
             "tools": tool_identity(), "sourceTrees": trees, "sourceFiles": files,
             "inventory": inventory, "steps": steps,
             "meaning": "Discovered original instances only. No runtime pass or native assertion correspondence inferred."}
    # Save raw discovery even before the independently reviewed count file exists.
    write(output / "inventory.json", value)
    counts = Counter(tuple(case[:2]) for case in inventory)
    write(output / "discovered-counts.json", {"dsh": DSH, "origin": ORIGIN, "manifestSha256": digest(MANIFEST),
          "suites": {suite: {file: count for (owner, file), count in sorted(counts.items()) if owner == suite}
                     for suite in manifest()["suites"]}})
    validate_frozen(value)
    print(f"PASS candidate discovery: {len(inventory)} original instances", flush=True)



def skip_rules(target_platform):
    value = manifest()
    capability = value["knownCapabilitySkip"]
    rules = {(capability["suite"], capability["file"], capability["name"], capability["ordinal"]): capability["reason"]}
    for rule in value.get("platformSkips", []):
        if rule["platform"] == target_platform:
            key = tuple(rule["case"])
            require(key not in rules and bool(rule["reason"]), "Duplicate or unexplained candidate skip")
            rules[key] = rule["reason"]
    return rules


def candidate_report(report, suite, root, expected, selected):
    rules = skip_rules(platform.system())
    outcomes = validate_report(report, suite, root, expected, selected, skippable=rules)
    for row in outcomes:
        if row["status"] == "skipped":
            row["skipReason"] = rules[tuple(row["case"])]
    return outcomes


def run_shard(dsh, origin, frozen_path, output, shard, batch):
    frozen = read(frozen_path)
    validate_frozen(frozen)
    require(frozen["runId"] == batch, "Discovery belongs to another batch")
    require(not output.exists(), "Use a fresh candidate shard output directory")
    output.mkdir(parents=True)
    before = source_hashes()
    trees = fixed_sources(dsh, origin)
    require(trees == frozen["sourceTrees"], "Candidate source trees differ from discovery")
    require(expected_files(dsh, origin) == frozen["sourceFiles"], "Candidate test source differs from discovery")
    identity = {"commit": git(ROOT, "rev-parse", "HEAD"), "platform": platform.system(),
                "architecture": platform.machine(), "dsh": DSH, "origin": ORIGIN,
                "sourceSha256": before, "tools": tool_identity(), "runId": batch,
                "inventorySha256": digest(frozen_path), "sourceTrees": trees}
    steps, outcomes = [], []
    result = {"status": "failed", "shard": shard, "shards": SHARDS, "identity": identity,
              "inventory": frozen["inventory"], "outcomes": outcomes, "steps": steps}
    try:
        # Rediscovery on every worker prevents a platform-dependent subset from passing.
        inventory, files = collect(dsh, origin, output, steps)
        require(inventory == frozen["inventory"] and files == frozen["sourceFiles"], "Worker discovery differs from frozen candidate")
        assignments = plan(inventory)
        matrix, selected, matrix_filter = matrix_selection(assignments, shard)
        selection = output / "matrix-selection.json"
        write(selection, matrix_filter)
        report = output / "matrix.json"
        env = environment(dsh, origin, output, "composition")
        execute(output, steps, "linked-resolution-matrix", ["node", VITEST, "run", "--config", CONFIG],
                dict(env, CORDIS_UPSTREAM_SELECTION=str(selection), CORDIS_UPSTREAM_REPORT=str(report)), cwd=dsh)
        outcomes.extend(candidate_report(read(report), "composition", dsh, matrix, selected))
        if shard == 1:
            for suite, spec in manifest()["suites"].items():
                expected = {key for key in assignments if key[0] == suite and key[1] != MATRIX}
                if not expected:
                    continue
                root = origin if spec["repository"] == "origin" else dsh
                selection = output / (suite + "-selection.json")
                write(selection, {"include": sorted({key[1] for key in expected})})
                report = output / (suite + ".json")
                execute(output, steps, "original-" + suite, ["node", VITEST, "run", "--config", CONFIG],
                        dict(environment(dsh, origin, output, suite), CORDIS_UPSTREAM_SELECTION=str(selection),
                             CORDIS_UPSTREAM_REPORT=str(report)), cwd=root)
                outcomes.extend(candidate_report(read(report), suite, root, expected, expected))
        require(trees == fixed_sources(dsh, origin) and before == source_hashes(), "Source changed during candidate run")
        result["status"] = "passed"
    finally:
        result["sourceUnchanged"] = before == source_hashes()
        write(output / "result.json", result)


def aggregate(directory, frozen_path, expected_commit, expected_platform, batch):
    frozen = read(frozen_path)
    validate_frozen(frozen)
    require(frozen["runId"] == batch, "Discovery belongs to another batch")
    paths = sorted(Path(directory).glob("**/result.json"))
    reports = [read(path) for path in paths]
    require(len(reports) == SHARDS, "Expected exactly four candidate shard receipts")
    for report in reports:
        require(report["inventory"] == frozen["inventory"], "Shard discovery differs from frozen candidate")
        identity = report["identity"]
        require(identity["runId"] == batch and identity["inventorySha256"] == digest(frozen_path), "Foreign candidate run or discovery")
        require(identity["tools"] == frozen["tools"], "Candidate tools or scope changed")
        require(identity["sourceTrees"] == frozen["sourceTrees"], "Candidate source identity differs")
        require(identity["sourceSha256"] == source_hashes(), "Shard source differs from current frozen checkpoint")
    rules = skip_rules(expected_platform)
    for report in reports:
        for row in report["outcomes"]:
            if row["status"] == "skipped":
                require(row.get("skipReason") == rules.get(tuple(row["case"])), "Candidate skip reason or platform differs")
    result = aggregate_reports(reports, expected_commit, expected_platform, dsh=DSH, origin=ORIGIN,
                               counts=approved_counts(), skippable=rules)
    result["meaning"] = "Complete frozen candidate original-suite union for this batch and platform. Does not promote the formal baseline or prove native assertion equivalence."
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    for name in ("discover", "run"):
        sub = commands.add_parser(name)
        sub.add_argument("--dsh", type=Path, required=True)
        sub.add_argument("--origin", type=Path, required=True)
        sub.add_argument("--output", type=Path, required=True)
        sub.add_argument("--run-id")
        if name == "run":
            sub.add_argument("--inventory", type=Path, required=True)
            sub.add_argument("--shard", type=int, choices=range(1, SHARDS + 1), required=True)
    sub = commands.add_parser("aggregate")
    sub.add_argument("--input", type=Path, required=True)
    sub.add_argument("--inventory", type=Path, required=True)
    sub.add_argument("--platform", choices=("Windows", "Linux", "Darwin"), required=True)
    sub.add_argument("--output", type=Path, required=True)
    sub.add_argument("--run-id")
    args = parser.parse_args()
    batch = run_identity(args.run_id)
    if args.command == "discover":
        discover(args.dsh.resolve(), args.origin.resolve(), args.output.resolve(), batch)
    elif args.command == "run":
        run_shard(args.dsh.resolve(), args.origin.resolve(), args.inventory.resolve(), args.output.resolve(), args.shard, batch)
    else:
        value = aggregate(args.input, args.inventory, git(ROOT, "rev-parse", "HEAD"), args.platform, batch)
        require(not args.output.exists(), "Do not overwrite candidate union evidence")
        write(args.output, value)
        print(f"PASS complete candidate union: {value['passed']} passed, {len(value['skipped'])} explicit skips")


if __name__ == "__main__":
    main()
