"""Compile an independent CLR application and consume the actual public CLI run command."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
dotnet = os.environ.get("DOTNET_HOST_PATH", "dotnet")
directory = Path(tempfile.mkdtemp(prefix="cordis-cli-invocation-"))
project = directory / "InvocationConsumer.csproj"
references = "".join(f'<Reference Include="Cordis.{name}"><HintPath>{escape(str(ROOT / "src" / ("Cordis." + name) / "bin/Release/net10.0" / ("Cordis." + name + ".dll")))}</HintPath></Reference>' for name in ("Core", "Composition", "Clr"))
project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{references}</ItemGroup></Project>')
(directory / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>')
(directory / "Directory.Build.props").write_text('<Project />')
(directory / "Directory.Build.targets").write_text('<Project />')
shutil.copyfile(ROOT / "tests/fixtures/InvocationConsumer/Module.cs", directory / "Module.cs")
subprocess.run([dotnet, "build", str(project), "-c", "Release", "--configfile", str(directory / "NuGet.Config")], cwd=directory, check=True)
profile = directory / "profile"
bundle = profile / ".cordis/packages/invocation/1.0.0"
shutil.copytree(directory / "bin/Release/net10.0", bundle)
(bundle / "cordis.plugin.json").write_text(json.dumps({"assembly": "InvocationConsumer.dll", "entryType": "Module"}))
(profile / "package.json").write_text(json.dumps({"name": "invocation-profile", "dependencies": {"Invocation": "1.0.0"}}))
(profile / "cordis.yml").write_text('- id: app\n  name: nuget:invocation\n')
cli = ROOT / "tools/Cordis.Cli/bin/Release/net10.0/Cordis.Cli.dll"
for arguments, forwarded, code, marker in (
    (["--resume", "abc", "--source", "app-feed", "--help"], ["--resume", "abc", "--source", "app-feed", "--help"], 0, "APP_READY"),
    (["--", "--source", "app-feed"], ["--source", "app-feed"], 0, "APP_READY"),
    (["--help"], ["--help"], 0, "APP_HELP"),
    (["--invalid"], ["--invalid"], 7, "APP_ERROR"),
    (["--fail-cleanup"], ["--fail-cleanup"], 0, "APP_READY"),
    (["--hang-cleanup"], ["--hang-cleanup"], 0, "five-second grace"),
    (["--fail-ready"], ["--fail-ready"], 1, "Application readiness failed"),
):
    result = subprocess.run([dotnet, str(cli), "run", str(profile), *arguments], cwd=directory, capture_output=True, text=True, timeout=30)
    print(result.stdout, end="")
    print(result.stderr, end="")
    assert result.returncode == code, (arguments, code, result.returncode)
    lines = result.stdout.splitlines()
    actual = json.loads(next(line.removeprefix("APP_ARGS:") for line in lines if line.startswith("APP_ARGS:")))
    assert actual == forwarded, actual
    assert marker in result.stdout + result.stderr
    if "--fail-cleanup" in arguments:
        assert "Application cleanup failed" in result.stderr
    if marker == "five-second grace":
        assert "APP_CLEANUP_PENDING" in lines and "APP_DISPOSED" not in lines
        continue
    assert lines.count("APP_DISPOSED") == 1, "CLI must await application cleanup before returning"
    if marker in ("APP_HELP", "APP_ERROR"):
        assert "APP_READY" not in lines, "Early application exits must not commit readiness"
    else:
        assert lines.index("APP_READY") < lines.index("APP_LATE_READY") < lines.index("APP_DISPOSED")
print("PASS independent CLI application: verbatim arguments, help/error, readiness and orderly exit without a feed or web listener")
