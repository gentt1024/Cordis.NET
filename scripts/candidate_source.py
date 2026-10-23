"""Rebuild the reviewed candidate adaptation without changing its upstream checkout."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = "687f282e8625c6b8d11ff58211a90524be2e4317"
COMMIT = "fba488001a6c2d37787d800beeab54a770c826a1"
RECORD = ROOT / "reference/candidate-adaptation.json"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def identity():
    value = json.loads(RECORD.read_text(encoding="utf8"))
    require(value["format"] == "cordis-candidate-adaptation/v1" and
            value["upstreamCommit"] == UPSTREAM and value["commit"] == COMMIT,
            "Unreviewed candidate adaptation")
    patch = (RECORD.parent / value["patch"]).read_bytes()
    require(hashlib.sha256(patch).hexdigest() == value["patchSha256"], "Candidate patch digest changed")
    commit = value["commitObject"].encode("utf8")
    require(hashlib.sha1(b"commit " + str(len(commit)).encode() + b"\0" + commit).hexdigest() == COMMIT,
            "Candidate commit object changed")
    require(value["commitObject"].startswith(f'tree {value["tree"]}\nparent {UPSTREAM}\n'),
            "Candidate ancestry changed")
    return value


def git(directory, *args, data=None):
    return subprocess.check_output(["git", "-c", "core.autocrlf=false", "-c", "core.eol=lf",
                                    "-C", str(directory), *args], input=data).decode().strip()


def prepare(output, source=None):
    value = identity()
    # A failed reconstruction stays available for inspection; never reset a caller's checkout.
    output.mkdir(parents=True, exist_ok=False)
    git(output, "init", "--object-format=sha1")
    git(output, "config", "core.autocrlf", "false")
    git(output, "config", "core.eol", "lf")
    git(output, "fetch", "--depth=1", str(source or value["upstreamRepository"]), UPSTREAM)
    require(git(output, "rev-parse", "FETCH_HEAD^{tree}") == value["upstreamTree"], "Upstream tree changed")
    git(output, "read-tree", UPSTREAM)
    for file, blobs in value["files"].items():
        require(git(output, "rev-parse", UPSTREAM + ":" + file) == blobs["beforeBlob"], "Original blob changed")
    git(output, "apply", "--cached", "-", data=(RECORD.parent / value["patch"]).read_bytes())
    tree = git(output, "write-tree")
    require(tree == value["tree"], "Patched candidate tree changed")
    changed = git(output, "diff-tree", "--no-commit-id", "--name-only", "-r", UPSTREAM, tree).splitlines()
    require(changed == sorted(value["files"]), "Candidate patch changed unreviewed files")
    for file, blobs in value["files"].items():
        require(git(output, "rev-parse", tree + ":" + file) == blobs["afterBlob"], "Patched blob changed")
    commit = git(output, "hash-object", "-t", "commit", "-w", "--stdin", data=value["commitObject"].encode("utf8"))
    require(commit == COMMIT, "Reconstructed candidate commit changed")
    git(output, "checkout-index", "--all")
    git(output, "checkout", "--detach", commit)
    require(not git(output, "status", "--porcelain"), "Reconstructed candidate is not clean")
    return value


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True, help="New checkout directory")
    parser.add_argument("--source", type=Path, help="Existing local upstream repository; fetched at the same fixed SHA")
    args = parser.parse_args()
    value = prepare(args.output.resolve(), args.source.resolve() if args.source else None)
    print(f'Prepared candidate {value["commit"]} from upstream {value["upstreamCommit"]}')
