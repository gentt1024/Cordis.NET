"""Real generated management/notification clients over an isolated package-only Host."""
import json
import os
from pathlib import Path
import queue
import shutil
import subprocess
import threading
import zipfile


def verify_management_consumer(root, source, feed, output, version, dotnet, composition, run, project, aot=False):
    package = next(feed.glob("Cordis.NET.Composition.*.nupkg"), None)
    if package is None:
        raise RuntimeError("The validated Composition package was not supplied.")
    model_name = "Cordis.Composition.cordis.model.json"
    with zipfile.ZipFile(package) as archive:
        model_bytes = archive.read("cordis/typert/" + model_name)
        model = json.loads(model_bytes)
        declarations = {declaration["id"]: declaration for declaration in model["declarations"]}
        service_keys = [json.loads(attribute["arguments"][0]["json"])
            for root_id in model["roots"]
            for declaration in [declarations[root_id]]
            if declaration["accessibility"] == "Public"
            for attribute in declaration["attributes"]
            if attribute["name"] == "Cordis.Composition.RemoteServiceAttribute"]
        if any(service_keys.count(key) != 1 for key in ("pluginManager",)):
            raise RuntimeError("The package must contain the actual standard PluginManager model.")
    contracts = []
    contract_feed = source / "author-feed"
    event_author, event_project = project("EventAuthor",
        '<AssemblyName>IndependentEvents.Author</AssemblyName><PackageId>IndependentEvents.Author</PackageId>'
        '<Version>1.0.0-alpha</Version>', composition,
        '<ItemGroup><CordisTypertModelRoot Include="Cordis.Platform.Tests.EventAlpha.Changed"/>'
        '<CordisTypertModelRoot Include="Cordis.Platform.Tests.EventAlpha.Nested"/>'
        '<CordisTypertModelRoot Include="Cordis.Platform.Tests.EventBeta.Changed"/></ItemGroup>')
    shutil.copyfile(root / "tests/fixtures/EventDeclarations/Author.cs", event_author / "Author.cs")
    run("event-author-pack", [dotnet, "pack", event_project, "-c", "Release", "-o", contract_feed])
    run("event-author-incremental", [dotnet, "build", event_project, "-c", "Release"])
    event_model = (event_author / "obj/Release/net10.0/cordis-typert/IndependentEvents.Author.cordis.model.json").read_bytes()
    for service, name, group, source_model in (
        ("pluginManager", "PluginManagerClient", "Manager", model_bytes),
        ("settingsController", "SettingsClient", "Settings", model_bytes),
        ("plugin-manager/changed", "ChangedEvent", "Notifications", model_bytes),
        ("alpha/changed", "CollectionEvent", "Collections", event_model),
        ("alpha/nested", "NestedEvent", "Nested", event_model),
    ):
        namespace = "IndependentManagement." + group
        package_id = namespace + ".Contracts"
        folder, path = project(group + "Contracts",
            f'<PackageId>{package_id}</PackageId><Version>1.0.0-alpha</Version><GenerateDocumentationFile>true</GenerateDocumentationFile>'
            f'<CordisTypertService>{service}</CordisTypertService>'
            f'<CordisTypertClientModel>{model_name}</CordisTypertClientModel>'
            f'<CordisTypertClientNamespace>{namespace}</CordisTypertClientNamespace>'
            f'<CordisTypertClientName>{name}</CordisTypertClientName>', composition,
            f'<ItemGroup><None Include="{model_name}" Pack="true" PackagePath="cordis/typert/"/></ItemGroup>')
        (folder / model_name).write_bytes(source_model)
        run("standard-" + service.replace("/", "-") + "-client-pack", [dotnet, "pack", path, "-c", "Release", "-o", contract_feed])
        contracts.append(f'<PackageReference Include="{package_id}" Version="1.0.0-alpha"/>')
    host, host_project = project("ManagementHost", '<OutputType>Exe</OutputType>', composition +
        f'<PackageReference Include="Cordis.NET.AspNetCore" Version="{version}"/>'
        f'<PackageReference Include="Cordis.NET.Extensions" Version="{version}"/>'
        '<FrameworkReference Include="Microsoft.AspNetCore.App"/>'
        '<PackageReference Include="IndependentEvents.Author" Version="1.0.0-alpha"/>')
    shutil.copyfile(root / "tests/fixtures/ManagementDotnet/Host.cs", host / "Program.cs")
    run("standard-management-host-build", [dotnet, "build", host_project, "-c", "Release"])
    references = composition + ''.join(contracts)
    caller, caller_project = project("ManagementCaller", '<OutputType>Exe</OutputType>', references)
    shutil.copyfile(root / "tests/fixtures/ManagementDotnet/Client.cs", caller / "Program.cs")
    run("standard-management-caller-build", [dotnet, "build", caller_project, "-c", "Release"])
    assert all("ProjectReference" not in path.read_text(encoding="utf-8") for path in source.glob("*/*.csproj"))
    native = source / "management-native"
    if aot:
        run("standard-management-caller-aot-publish", [dotnet, "publish", caller_project, "-c", "Release", "-p:PublishAot=true", "-o", native])
    node = shutil.which("node")
    if node is None:
        raise RuntimeError("The independent TypeScript management caller requires the repository's Node tool runtime.")
    node_commands = []
    node_env = dict(os.environ, NUGET_PACKAGES=str(source / "cache"), DOTNET_ROOT=str(Path(dotnet).parent))

    def node_run(name, command):
        result = subprocess.run([str(item) for item in command], cwd=source, env=node_env, capture_output=True, timeout=150)
        (output / (name + ".log")).write_bytes(result.stdout + result.stderr)
        node_commands.append({"name": name, "exitCode": result.returncode})
        print(name, "exit", result.returncode, flush=True)
        if result.returncode:
            print((result.stdout + result.stderr).decode("utf-8", "replace")[-10000:])
            raise RuntimeError(name + " failed")

    status = "failed"
    failures = []
    try:
        for mode in (["jit", "aot", "typescript"] if aot else ["jit", "typescript"]):
            ready = queue.Queue()
            lines = []
            with (output / ("standard-management-host-" + mode + ".log")).open("w", encoding="utf-8") as log:
                process = subprocess.Popen([str(dotnet), str(host / "bin/Release/net10.0/ManagementHost.dll"),
                    str(source / ("management-profile-" + mode))], cwd=source, env=node_env,
                    stdout=subprocess.PIPE, stderr=log, text=True, encoding="utf-8")

                def wait_ready():
                    announced = False
                    for line in process.stdout:
                        lines.append(line)
                        if not announced and line.startswith("READY "):
                            ready.put(line.strip()[6:])
                            announced = True
                    if not announced:
                        ready.put(None)

                reader = threading.Thread(target=wait_ready, daemon=True)
                reader.start()
                try:
                    address = ready.get(timeout=60)
                    assert address and address.startswith("http://127.0.0.1:"), address
                    if mode == "typescript":
                        node_run("standard-management-typescript", [node, root / "scripts/typert-management-client-consumer.mjs", address,
                            source / "ManagementTypescript"])
                    else:
                        command = [dotnet, caller / "bin/Release/net10.0/ManagementCaller.dll", address] if mode == "jit" else [
                            native / ("ManagementCaller.exe" if os.name == "nt" else "ManagementCaller"), address]
                        run("standard-management-" + mode, command)
                except Exception as error:
                    failures.append({"mode": mode, "error": str(error)})
                finally:
                    process.kill()
                    process.wait(timeout=30)
                    reader.join(timeout=30)
                    assert not reader.is_alive(), "Management Host reader did not retire."
                    log.write("".join(lines))
        if failures:
            raise RuntimeError("Standard management consumer failures: " + ', '.join(item["mode"] for item in failures))
        status = "requested-checks-passed"
    finally:
        (output / "standard-management-results.json").write_text(json.dumps({"status": status,
            "packageReferencesOnly": True, "ordinaryContractsReferencedOnly": True, "nodeCommands": node_commands,
            "platformAdapter": "explicit fixture toolchain with actual prepared files; SDK execution is covered separately",
            "aotRequested": aot, "failures": failures}, indent=2) + "\n", encoding="utf-8")
    return node_commands
