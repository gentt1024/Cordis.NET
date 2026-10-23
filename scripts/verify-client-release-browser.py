"""Exercise packaged native management HTTP and WebSocket release recovery in Chromium."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import queue
import shutil
import subprocess
import tempfile
import threading
import time
import urllib.request
import xml.etree.ElementTree as ET

from package_inspection import inspect_restored_packages


def hashes(directory):
    return {path.relative_to(directory).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*")) if path.is_file()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--runtime", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--package-version")
    parser.add_argument("--dotnet", default="dotnet")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = options.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("Output must be empty; preserve previous evidence and select a new directory.")
    output.mkdir(parents=True, exist_ok=True)
    packages, runtime = options.packages.resolve(), options.runtime.resolve()
    version = options.package_version or ET.parse(root / "Directory.Build.props").findtext(".//Version")
    receipt = {"status": "failed", "platform": platform.system(), "packageVersion": version,
               "packageReferencesOnly": True, "nativeManagementHttp": True, "nativeWebSocket": True,
               "actualChromiumRequired": True, "aotRequested": False, "steps": []}
    source = Path(tempfile.mkdtemp(prefix="cordis-release-browser-"))
    host = browser = None
    reader = None
    host_log = browser_log = None
    try:
        supplied = {"packages": hashes(packages), "runtime": hashes(runtime)}
        if not supplied["packages"] or not (runtime / "client.mjs").is_file():
            raise RuntimeError("A package batch and prebuilt same-batch browser runtime are required.")
        receipt["inputSha256"] = supplied
        executable = next((path for path in [os.environ.get("CORDIS_TEST_BROWSER"),
            "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
            "C:/Program Files/Google/Chrome/Application/chrome.exe",
            "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"] if path and Path(path).is_file()), None)
        if executable is None:
            raise RuntimeError("Set CORDIS_TEST_BROWSER to an installed Chromium browser; this check cannot be skipped.")
        dotnet = Path(shutil.which(options.dotnet) or options.dotnet).resolve()
        env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), NUGET_PACKAGES=str(source / "cache"),
                   DOTNET_CLI_UI_LANGUAGE="en")
        shutil.copyfile(root / "global.json", source / "global.json")
        fixture = root / "tests/fixtures/ClientReleaseBrowser"
        shutil.copyfile(fixture / "Host.cs", source / "Program.cs")
        shutil.copyfile(fixture / "index.html", source / "index.html")
        project = ET.Element("Project", Sdk="Microsoft.NET.Sdk.Web")
        properties = ET.SubElement(project, "PropertyGroup")
        for name, value in {"TargetFramework": "net10.0", "ImplicitUsings": "enable", "Nullable": "enable",
                            "TreatWarningsAsErrors": "true", "IsPackable": "false"}.items():
            ET.SubElement(properties, name).text = value
        references = ET.SubElement(project, "ItemGroup")
        ET.SubElement(references, "PackageReference", Include="Cordis.NET.AspNetCore", Version=version)
        project_path = source / "ReleaseBrowserHost.csproj"
        ET.ElementTree(project).write(project_path, encoding="utf-8")
        config = ET.Element("configuration")
        feeds = ET.SubElement(config, "packageSources")
        ET.SubElement(feeds, "clear")
        ET.SubElement(feeds, "add", key="candidate", value=str(packages))
        ET.SubElement(feeds, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
        ET.SubElement(ET.SubElement(config, "fallbackPackageFolders"), "clear")
        mapping = ET.SubElement(config, "packageSourceMapping")
        for name, pattern in (("candidate", "Cordis.NET.*"), ("nuget.org", "*")):
            ET.SubElement(ET.SubElement(mapping, "packageSource", key=name), "package", pattern=pattern)
        ET.ElementTree(config).write(source / "NuGet.Config", encoding="utf-8")
        for name, command in (("restore", [dotnet, "restore", project_path]),
                              ("build", [dotnet, "build", project_path, "-c", "Release", "--no-restore"])):
            with (output / (name + ".log")).open("wb") as log:
                process = subprocess.run([str(part) for part in command], cwd=source, env=env,
                                         stdout=log, stderr=subprocess.STDOUT, timeout=300)
            receipt["steps"].append({"name": name, "exitCode": process.returncode})
            if process.returncode:
                raise RuntimeError(name + " failed; see " + str(output / (name + ".log")))
        assets = json.loads((source / "obj/project.assets.json").read_text(encoding="utf-8"))
        if {Path(path).resolve() for path in assets["packageFolders"]} != {(source / "cache").resolve()}:
            raise RuntimeError("Native Host resolved a shared or fallback package cache.")
        cordis = {name: library["type"] for name, library in assets["libraries"].items() if name.startswith("Cordis.NET.")}
        if not cordis or any(name.split("/", 1)[1] != version or kind != "package" for name, kind in cordis.items()):
            raise RuntimeError("Native Host did not resolve only the selected candidate packages.")
        receipt["resolvedPackages"] = sorted(cordis)
        receipt["restoredArchiveSha256"] = inspect_restored_packages(source / "cache", packages)
        ready = queue.Queue()
        host_log = (output / "native-host.log").open("w", encoding="utf-8")
        host = subprocess.Popen([str(dotnet), str(source / "bin/Release/net10.0/ReleaseBrowserHost.dll"),
                                 str(source / "workspace"), str(runtime), str(source / "index.html")],
                                cwd=source, env=env, stdout=subprocess.PIPE, stderr=host_log,
                                text=True, encoding="utf-8")

        def read_host():
            announced = False
            for line in host.stdout:
                host_log.write(line)
                host_log.flush()
                if not announced and line.startswith("READY "):
                    ready.put(line.strip()[6:])
                    announced = True
            if not announced:
                ready.put(None)

        reader = threading.Thread(target=read_host, daemon=True)
        reader.start()
        address = ready.get(timeout=60)
        if not address or not address.startswith("http://127.0.0.1:"):
            raise RuntimeError("Native Host did not announce a loopback address.")
        browser_log = (output / "chromium.log").open("wb")
        browser = subprocess.Popen([executable, "--headless=new", "--no-first-run", "--disable-default-apps",
            "--user-data-dir=" + str(source / "browser-profile"), address], stdout=browser_log,
            stderr=subprocess.STDOUT)
        result_path = source / "workspace/browser-result.json"
        deadline = time.monotonic() + 60
        while not result_path.is_file():
            if host.poll() is not None:
                raise RuntimeError("Native Host exited before the browser result.")
            if browser.poll() is not None:
                raise RuntimeError("Chromium exited before reporting its result.")
            if time.monotonic() > deadline:
                raise RuntimeError("Chromium native management recovery timed out.")
            time.sleep(0.1)
        result = json.loads(result_path.read_text(encoding="utf-8"))
        (output / "browser-result.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        if result["status"] != "passed":
            raise RuntimeError("Browser verification failed: " + str(result.get("error")))
        receipt["steps"].append({"name": "native-management-chromium", "exitCode": 0, "checks": len(result["checks"])})
        with urllib.request.urlopen(urllib.request.Request(address + "/test/stop", method="POST"), timeout=5) as response:
            response.read()
        if host.wait(timeout=15) != 0:
            raise RuntimeError("Native Host shutdown failed.")
        if supplied != {"packages": hashes(packages), "runtime": hashes(runtime)}:
            raise RuntimeError("The supplied package/runtime batch changed during verification.")
        receipt["inputUnchanged"] = True
        receipt["status"] = "passed"
        print("PASS packaged native management HTTP/WebSocket release recovery in actual Chromium", flush=True)
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        for process in (browser, host):
            if process is not None and process.poll() is None:
                process.kill()
                process.wait(timeout=15)
        if reader is not None:
            reader.join(timeout=15)
        for log in (host_log, browser_log):
            if log is not None:
                log.close()
        (output / "results.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
