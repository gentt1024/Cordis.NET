"""Exercise generated optional Remote parameters with the repository's compiled tools and runtime."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--expect-refusal", action="store_true",
                        help="Confirm that the unmodified generator refuses the optional declarations.")
    options = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    output = (options.output or root / "artifacts/typert-optional").resolve()
    output.mkdir(parents=True, exist_ok=True)
    source = Path(tempfile.mkdtemp(prefix="cordis-typert-optional-"))
    shutil.copyfile(root / "global.json", source / "global.json")
    dotnet = Path(shutil.which(options.dotnet) or options.dotnet).resolve()
    env = dict(os.environ, DOTNET_ROOT=str(dotnet.parent), DOTNET_CLI_UI_LANGUAGE="en")
    env["PATH"] = str(dotnet.parent) + os.pathsep + env.get("PATH", "")
    generator = root / "tools/Cordis.Typert.Generator/bin/Release/net10.0/Cordis.Typert.Generator.dll"
    compiler = root / "tools/Cordis.Typert.Compiler/bin/Release/net10.0/Cordis.Typert.Compiler.dll"
    core = root / "src/Cordis.Core/bin/Release/net10.0/Cordis.Core.dll"
    composition = root / "src/Cordis.Composition/bin/Release/net10.0/Cordis.Composition.dll"
    for prerequisite in (generator, compiler, core, composition):
        if not prerequisite.is_file():
            raise RuntimeError("Build the Release tools and Composition first: " + str(prerequisite))
    steps = []
    status = "failed"

    def run(name, command, expected_failure=False):
        process = subprocess.run([str(value) for value in command], cwd=source, env=env, capture_output=True)
        data = process.stdout + process.stderr
        (output / (name + ".log")).write_bytes(data)
        steps.append({"name": name, "exitCode": process.returncode, "expectedFailure": expected_failure})
        print(name, "exit", process.returncode, flush=True)
        if (process.returncode != 0) != expected_failure:
            print(data.decode("utf-8", "replace")[-10000:])
            raise RuntimeError(name + " failed")
        return data

    def project(name, client_model=None):
        folder = source / name
        folder.mkdir()
        element = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        properties = ET.SubElement(element, "PropertyGroup")
        values = {
            "TargetFramework": "net10.0", "LangVersion": "14.0", "Nullable": "enable",
            "ImplicitUsings": "enable", "TreatWarningsAsErrors": "true",
            "JsonSerializerIsReflectionEnabledByDefault": "false",
            "CordisTypertService": "optional", "CordisTypertCompiler": str(compiler),
        }
        if client_model is not None:
            values.update(OutputType="Exe", CordisTypertClientModel=str(client_model),
                          CordisTypertClientNamespace="OptionalCaller", CordisTypertClientName="OptionalClient")
        for key, value in values.items():
            ET.SubElement(properties, key).text = value
        items = ET.SubElement(element, "ItemGroup")
        for path in (core, composition):
            reference = ET.SubElement(items, "Reference", Include=path.stem)
            ET.SubElement(reference, "HintPath").text = str(path)
        if client_model is None:
            ET.SubElement(items, "Analyzer", Include=str(generator))
        else:
            reference = ET.SubElement(items, "Reference", Include="OptionalAuthor")
            ET.SubElement(reference, "HintPath").text = str(source / "OptionalAuthor/bin/Release/net10.0/OptionalAuthor.dll")
        ET.SubElement(element, "Import", Project=str(root / "src/Cordis.Composition/buildTransitive/Cordis.NET.Composition.targets"))
        path = folder / (name + ".csproj")
        ET.ElementTree(element).write(path, encoding="utf-8")
        return folder, path

    try:
        author, author_project = project("OptionalAuthor")
        shutil.copyfile(root / "tests/fixtures/TypertOptional/OptionalAuthor.cs", author / "Author.cs")
        built = run("author-build", [dotnet, "build", author_project, "-c", "Release"], options.expect_refusal)
        if options.expect_refusal:
            if b"CORDISREMOTE001" not in built:
                raise RuntimeError("Baseline failed for a reason other than optional Remote refusal.")
            status = "baseline-refusal-observed"
            return
        model = author / "obj/Release/net10.0/cordis-typert/optional.cordis.typert.json"
        shutil.copyfile(model, output / "authored-model.json")
        consumer, consumer_project = project("OptionalConsumer", model)
        shutil.copyfile(root / "tests/fixtures/TypertOptional/OptionalConsumer.cs", consumer / "Program.cs")
        run("consumer-build", [dotnet, "build", consumer_project, "-c", "Release"])
        shutil.copyfile(consumer / "obj/Release/net10.0/cordis-typert/OptionalClient.Generated.g.cs",
                        output / "generated-client.g.cs")
        run("consumer-run", [dotnet, consumer / "bin/Release/net10.0/OptionalConsumer.dll"])
        status = "requested-checks-passed"
    finally:
        (output / "report.json").write_text(json.dumps({"status": status, "source": str(source), "steps": steps}, indent=2),
                                           encoding="utf-8")


if __name__ == "__main__":
    main()
