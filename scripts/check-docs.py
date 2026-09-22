"""Check public documentation links, bilingual structure, and package readmes."""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
PAIRS = [
    ("README.md", "README.zh.md"),
    ("CONTRIBUTING.md", "CONTRIBUTING.zh.md"),
    ("docs/getting-started.md", "docs/getting-started.zh.md"),
    ("docs/core-concepts.md", "docs/core-concepts.zh.md"),
    ("docs/authoring.md", "docs/authoring.zh.md"),
    ("docs/compatibility.md", "docs/compatibility.zh.md"),
    ("docs/upstream.md", "docs/upstream.zh.md"),
    ("docs/development.md", "docs/development.zh.md"),
    ("docs/validation.md", "docs/validation.zh.md"),
]
PACKAGES = [
    "src/Cordis.Core", "src/Cordis.Composition", "src/Cordis.Extensions",
    "src/Cordis.Clr", "src/Cordis.JavaScript", "src/Cordis.Hosting",
    "tools/Cordis.Cli", "examples/Greeting.Plugin",
]
LINK = re.compile(r"(?<!!)\[[^\]]*\]\(([^)]+)\)")
FENCE = re.compile(r"^\x60\x60\x60[^\n]*\n(.*?)^\x60\x60\x60\s*$", re.MULTILINE | re.DOTALL)


def fail(message):
    print(f"documentation check: {message}", file=sys.stderr)
    raise SystemExit(1)


def content(path):
    full = ROOT / path
    if not full.is_file():
        fail(f"missing {path}")
    return full.read_text(encoding="utf-8-sig")


for english, chinese in PAIRS:
    left, right = content(english), content(chinese)
    for path, text, peer in ((english, left, Path(chinese).name), (chinese, right, Path(english).name)):
        lines = [line for line in text.splitlines() if line.strip()]
        if not lines or not lines[0].startswith("# ") or peer not in lines[1]:
            fail(f"{path} must place its language switch directly after H1")
    if [len(m.group(1)) for m in re.finditer(r"^(#+) ", left, re.MULTILINE)] != [
        len(m.group(1)) for m in re.finditer(r"^(#+) ", right, re.MULTILINE)
    ]:
        fail(f"heading structure differs: {english} / {chinese}")
    if FENCE.findall(left) != FENCE.findall(right):
        fail(f"code blocks differ: {english} / {chinese}")

for directory in PACKAGES:
    text = content(f"{directory}/README.md")
    if "independently maintained" not in text:
        fail(f"{directory}/README.md lacks independence statement")

for path in ROOT.rglob("*.md"):
    relative = path.relative_to(ROOT).as_posix()
    if any(part in {"node_modules", "bin", "obj", "artifacts"} for part in path.parts):
        continue
    if relative == "docs/reference/dsh-vendor-readme.snapshot.md":
        continue
    text = path.read_text(encoding="utf-8-sig")
    for raw in LINK.findall(text):
        target = raw.strip().split(maxsplit=1)[0].strip("<>").split("#", 1)[0]
        if not target or re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I):
            continue
        resolved = (path.parent / target).resolve()
        try:
            resolved.relative_to(ROOT)
        except ValueError:
            fail(f"{relative}: link escapes repository: {raw}")
        if not resolved.exists():
            fail(f"{relative}: missing link target: {raw}")

print(f"documentation check passed: {len(PAIRS)} bilingual pairs, {len(PACKAGES)} package readmes")
