"""Build and verify the immutable artifact consumed by the NuGet publish job."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import shutil


PUBLISH_PACKAGES = (
    "Cordis.NET.Core",
    "Cordis.NET.Composition",
    "Cordis.NET.Extensions",
    "Cordis.NET.Clr",
    "Cordis.NET.Hosting",
    "Cordis.NET.JavaScript",
    "Cordis.NET.Tool",
)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def prepare(source: Path, output: Path, version: str, tag: str, commit: str) -> None:
    output.mkdir(parents=True, exist_ok=True)
    assert not any(output.iterdir()), f"Release artifact output must be empty: {output}"
    expected_validation = {f"{name}.{version}.nupkg" for name in (*PUBLISH_PACKAGES, "Cordis.Example.Greeting")}
    actual_validation = {path.name for path in source.glob("*.nupkg")}
    assert actual_validation == expected_validation, (actual_validation, expected_validation)

    copied: list[Path] = []
    for package_id in PUBLISH_PACKAGES:
        for suffix in ("nupkg", "snupkg"):
            candidate = source / f"{package_id}.{version}.{suffix}"
            assert candidate.is_file(), candidate
            destination = output / candidate.name
            shutil.copyfile(candidate, destination)
            copied.append(destination)

    manifest = {
        "schemaVersion": 1,
        "tag": tag,
        "version": version,
        "commit": commit,
        "packages": [{"name": path.name, "sha256": sha256(path)} for path in sorted(copied)],
    }
    (output / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def verify(directory: Path, tag: str, commit: str) -> None:
    manifest = json.loads((directory / "release-manifest.json").read_text(encoding="utf-8"))
    assert manifest["tag"] == tag, (manifest["tag"], tag)
    assert manifest["commit"] == commit, (manifest["commit"], commit)
    expected_names = {item["name"] for item in manifest["packages"]} | {"release-manifest.json"}
    actual_names = {path.name for path in directory.iterdir() if path.is_file()}
    assert actual_names == expected_names, (actual_names, expected_names)
    for item in manifest["packages"]:
        path = directory / item["name"]
        assert sha256(path) == item["sha256"], path


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    prepare_parser = subparsers.add_parser("prepare")
    prepare_parser.add_argument("--source", type=Path, required=True)
    prepare_parser.add_argument("--output", type=Path, required=True)
    prepare_parser.add_argument("--version", required=True)
    prepare_parser.add_argument("--tag", required=True)
    prepare_parser.add_argument("--commit", required=True)
    verify_parser = subparsers.add_parser("verify")
    verify_parser.add_argument("--directory", type=Path, required=True)
    verify_parser.add_argument("--tag", required=True)
    verify_parser.add_argument("--commit", required=True)
    options = parser.parse_args()
    if options.command == "prepare":
        prepare(options.source, options.output, options.version, options.tag, options.commit)
    else:
        verify(options.directory, options.tag, options.commit)


if __name__ == "__main__":
    main()
