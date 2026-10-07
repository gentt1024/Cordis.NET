"""Format repository C# files, or check every stage in an isolated source copy."""
import argparse
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def source_files():
    names = subprocess.check_output(
        ["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=ROOT
    ).decode("utf-8").split("\0")
    files = []
    for name in sorted(set(names) - {""}):
        path = ROOT / name
        # Do not follow linked files or directories outside the source boundary.
        if not path.resolve().is_relative_to(ROOT.resolve()):
            raise ValueError(f"Source path escapes the repository: {name}")
        if path.is_file():
            files.append(name)
    return files


def format_files(root, files, check):
    def run(*command):
        subprocess.run(command, cwd=root, check=True)

    def snapshot():
        return {name: (root / name).read_bytes() for name in files}

    # All stages use the same frozen allowlist, including solution mode.
    # Batches stay below Windows' command-line limit.
    batches = [[]]
    length = 0
    for name in files:
        if length + len(name) + 3 > 24000:
            batches.append([])
            length = 0
        batches[-1].append(name)
        length += len(name) + 3

    def cleanup():
        # File mode is formatting-only and includes projects outside Cordis.slnx.
        for batch in batches:
            run("dotnet", "tool", "run", "jb", "--", "cleanupcode",
                "--profile=Built-in: Reformat Code", "--no-buildin-settings", "--no-updates",
                "--verbosity=WARN", *batch)

    def roslyn(workspace, *options):
        for batch in batches:
            run("dotnet", "format", "whitespace", workspace, *options, "--include", *batch)

    previous = snapshot()
    run("dotnet", "tool", "restore")
    run("dotnet", "restore", "Cordis.slnx", "--locked-mode")
    for label, action in (
        ("jb cleanupcode", cleanup),
        ("dotnet format whitespace", lambda: roslyn(".", "--folder")),
        ("dotnet format whitespace (solution symbols)", lambda: roslyn("Cordis.slnx", "--no-restore")),
        ("jb cleanupcode (repeat)", cleanup),
    ):
        print(f"RUN {label}", flush=True)
        action()
        current = snapshot()
        changed = [name for name in files if previous[name] != current[name]]
        if changed and (check or label != "jb cleanupcode"):
            print(f"FAIL {label} changed {len(changed)} file(s):", file=sys.stderr)
            print("\n".join(changed), file=sys.stderr)
            print("Run python scripts/format.py to format; if a later stage changes files, "
                  "resolve the formatter disagreement in .editorconfig.", file=sys.stderr)
            return 1
        print(f"PASS {label}: {len(changed)} changed file(s)", flush=True)
        previous = current
    print(f"PASS formatting: {len(files)} C# files; all formatter stages agree")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Check a temporary source copy without writing to the working tree")
    args = parser.parse_args()
    sources = source_files()
    files = [name for name in sources if name.endswith(".cs")]
    if not files:
        parser.error("No repository C# files found")
    if not args.check:
        return format_files(ROOT, files, check=False)

    # Copy current working-tree bytes, not HEAD or the index. Include new files
    # and configuration; leave ignored caches and .git behind. Never copy back.
    with tempfile.TemporaryDirectory(prefix="cordis-format-") as temporary:
        root = Path(temporary)
        for name in sources:
            destination = root / name
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(ROOT / name, destination)
        return format_files(root, files, check=True)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except subprocess.CalledProcessError as error:
        sys.exit(error.returncode)
