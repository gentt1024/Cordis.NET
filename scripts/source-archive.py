"""Archive tracked sources, extract separately, verify exact paths and SHA-256."""
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile

root = Path(__file__).resolve().parents[1]
commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
if subprocess.check_output(["git", "status", "--porcelain"], cwd=root).strip():
    raise SystemExit("Commit all implementation files before making the delivery archive.")
files = subprocess.check_output(["git", "ls-files", "-z"], cwd=root).decode().split("\0")[:-1]
blocked = {"bin", "obj", "node_modules", ".git", ".tools", "artifacts", "__pycache__", "TestResults"}
for file in files:
    if blocked.intersection(Path(file).parts): raise SystemExit(f"Forbidden source artifact: {file}")
hashes = {file: hashlib.sha256((root / file).read_bytes()).hexdigest() for file in files}
# The export has no Git provider. Supply the SDK's standard SourceRoot contract,
# bound to the real checkpoint, so it emits the same commit-specific SourceLink.
archive_props = f'''<Project>
  <PropertyGroup>
    <RepositoryCommit>{commit}</RepositoryCommit>
    <SourceRevisionId>{commit}</SourceRevisionId>
    <DeterministicSourcePaths>true</DeterministicSourcePaths>
  </PropertyGroup>
  <ItemGroup>
    <SourceRoot Include="$(MSBuildThisFileDirectory)" SourceControl="git" RevisionId="{commit}"
                SourceLinkUrl="https://raw.githubusercontent.com/gentt1024/Cordis.NET/{commit}/*" />
  </ItemGroup>
</Project>
'''
generated = {"Cordis.SourceArchive.props": archive_props.encode("utf-8")}
out = root.parent / f"Cordis.NET-{commit[:12]}-source.zip"
with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_DEFLATED) as archive:
    for file in files: archive.write(root / file, f"Cordis.NET/{file}")
    for file, content in generated.items(): archive.writestr(f"Cordis.NET/{file}", content)
    archive.writestr("Cordis.NET/SOURCE_SHA256.json", json.dumps({"commit": commit, "files": hashes,
        "generatedFiles": {name: hashlib.sha256(content).hexdigest() for name, content in generated.items()}}, indent=2) + "\n")
with tempfile.TemporaryDirectory(prefix="cordis-source-verify-") as temporary:
    with zipfile.ZipFile(out) as archive: archive.extractall(temporary)
    extracted = Path(temporary) / "Cordis.NET"
    actual = {p.relative_to(extracted).as_posix() for p in extracted.rglob("*") if p.is_file()}
    assert actual == set(files) | set(generated) | {"SOURCE_SHA256.json"}, "Archive file set differs"
    for file, digest in hashes.items(): assert hashlib.sha256((extracted / file).read_bytes()).hexdigest() == digest, file
    for file, content in generated.items(): assert (extracted / file).read_bytes() == content, file
    projects = list(extracted.rglob("*.csproj"))
    assert projects and (extracted / "Cordis.slnx").exists() and (extracted / "build.ps1").exists()
    import xml.etree.ElementTree as ET
    for project in projects:
        for reference in ET.parse(project).iter("ProjectReference"):
            assert (project.parent / reference.attrib["Include"]).resolve().is_file(), (project, reference.attrib)
report = {"commit": commit, "archive": str(out), "files": len(files), "sha256": hashlib.sha256(out.read_bytes()).hexdigest(), "extractedHashesVerified": True}
out.with_suffix(".verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(json.dumps(report, indent=2))
