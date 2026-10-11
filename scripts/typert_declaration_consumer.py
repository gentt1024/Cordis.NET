"""Verify ordinary declaration artifacts through isolated package authors and a catalog consumer."""
import hashlib
import json
import shutil
from xml.sax.saxutils import escape
import zipfile


def verify_declaration_consumer(root, source, feed, output, version, dotnet, composition, run, project):
    fixtures = root / "tests/fixtures/DeclarationModel"
    external, external_project = project("DeclarationExternal",
        '<AssemblyName>IndependentDeclarations.External</AssemblyName>'
        '<PackageId>IndependentDeclarations.External</PackageId><Version>1.0.0-alpha</Version>', "")
    shutil.copyfile(fixtures / "External.cs", external / "External.cs")
    run("declaration-external-pack", [dotnet, "pack", external_project, "-c", "Release", "-o", feed])

    roots = (
        "IndependentDeclarations.Config",
        "IndependentDeclarations.PluginAuthor",
        "IndependentDeclarations.CatalogService",
        "IndependentDeclarations.IPage`1",
        "IndependentDeclarations.Page`1",
        "IndependentDeclarations.Page`1+Cursor",
        "IndependentDeclarations.PageExtensions")
    root_items = '<ItemGroup>' + ''.join('<CordisTypertModelRoot Include="' + name + '"/>' for name in roots) + '</ItemGroup>'
    author, author_project = project("DeclarationAuthor",
        '<AssemblyName>IndependentDeclarations.Provider</AssemblyName>'
        '<PackageId>IndependentDeclarations.Provider</PackageId><Version>1.0.0-alpha</Version>'
        '<LangVersion>14.0</LangVersion>', composition +
        '<PackageReference Include="IndependentDeclarations.External" Version="1.0.0-alpha"/>', root_items)
    shutil.copyfile(fixtures / "Author.cs", author / "Author.cs")
    run("declaration-author-pack", [dotnet, "pack", author_project, "-c", "Release", "-o", feed])
    response = author / "obj/Release/net10.0/cordis-typert"
    model = response / "IndependentDeclarations.Provider.cordis.model.json"
    owned = [model] + [response / name for name in ("model-roots.rsp", "model-sources.rsp", "model-references.rsp")]
    authored = json.loads(model.read_text(encoding="utf-8"))
    assert authored["formatVersion"] == 2
    assert not list(response.glob("*.cordis.typert.json")), "Ordinary author unexpectedly selected the remote pipeline"
    shutil.copyfile(model, output / "ordinary-declaration-model.json")
    with zipfile.ZipFile(feed / "IndependentDeclarations.Provider.1.0.0-alpha.nupkg") as archive:
        assert archive.read("cordis/typert/IndependentDeclarations.Provider.cordis.model.json") == model.read_bytes()
    hashes = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in owned}
    stamps = {path.name: path.stat().st_mtime_ns for path in owned}
    run("declaration-author-incremental", [dotnet, "build", author_project, "-c", "Release"])
    for path in owned:
        assert path.stat().st_mtime_ns == stamps[path.name], "Unchanged declaration output was rewritten: " + path.name
        assert hashlib.sha256(path.read_bytes()).hexdigest() == hashes[path.name]
    run("declaration-author-clean", [dotnet, "clean", author_project, "-c", "Release"])
    assert not any(path.exists() for path in owned), "Clean retained an owned declaration model or response file"
    run("declaration-author-cold-rebuild", [dotnet, "build", author_project, "-c", "Release"])
    for path in owned:
        assert hashlib.sha256(path.read_bytes()).hexdigest() == hashes[path.name], "Cold rebuild changed declaration facts: " + path.name
    assert json.loads(model.read_text(encoding="utf-8"))["contractIdentity"] == authored["contractIdentity"]

    contracts, contracts_project = project("DeclarationContracts",
        '<PackageId>IndependentDeclarations.Contracts</PackageId><Version>1.0.0-alpha</Version>'
        '<IncludeBuildOutput>false</IncludeBuildOutput><SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>', "",
        '<ItemGroup><None Include="IndependentDeclarations.Provider.cordis.model.json" Pack="true" PackagePath="cordis/typert/"/></ItemGroup>')
    shutil.copyfile(model, contracts / model.name)
    run("declaration-artifact-only-pack", [dotnet, "pack", contracts_project, "-c", "Release", "-o", feed])
    with zipfile.ZipFile(feed / "IndependentDeclarations.Contracts.1.0.0-alpha.nupkg") as archive:
        assert archive.read("cordis/typert/" + model.name) == model.read_bytes()
        assert not any(name.startswith("lib/") for name in archive.namelist()), "Artifact-only package contains an implementation assembly"

    sdk = run("declaration-selected-sdk", [dotnet, "--version"]).decode("utf-8").strip()
    compiler = source / "cache/cordis.net.composition" / version / "tools/net10.0/typert/Cordis.Typert.Compiler.dll"
    roslyn = dotnet.parent / "sdk" / sdk / "Roslyn/bincore"

    def extract_declarations(label, destination, selected_roots=None, expected_failure=False):
        compilation = authored["compilation"]
        output_kind = {"DynamicallyLinkedLibrary": "Library", "ConsoleApplication": "Exe", "WindowsApplication": "WinExe"}
        return run(label, [dotnet, compiler, "--roslyn-directory", roslyn, "extract-model",
            "--project-directory", author, "--sources", response / "model-sources.rsp",
            "--references", response / "model-references.rsp", "--assembly-name", compilation["assemblyName"],
            "--language-version", "14.0", "--nullable", compilation["nullable"],
            "--defines", ";".join(compilation["defines"]), "--allow-unsafe", str(compilation["allowUnsafe"]).lower(),
            "--check-overflow", str(compilation["checkOverflow"]).lower(), "--output-kind", output_kind[compilation["outputKind"]],
            "--platform", compilation["platform"], "--optimize", str(compilation["optimization"] == "Release").lower(),
            "--roots", selected_roots or response / "model-roots.rsp", "--output", destination], expected_failure)

    def member(document, metadata_name, name):
        declaration = next(value for value in document["declarations"] if value["metadataName"] == metadata_name)
        return next(value for value in declaration["members"] if value["name"] == name)

    baseline_model = author / "direct-baseline.cordis.model.json"
    extract_declarations("declaration-direct-baseline", baseline_model)
    assert json.loads(baseline_model.read_text(encoding="utf-8"))["contractIdentity"] == authored["contractIdentity"], \
        "Direct source controls must use the authored compilation options"
    original_source = (author / "Author.cs").read_bytes()
    changes = []
    for label, original, changed in (
            ("return-annotation", "[return: MaybeNull]", "[return: NotNull]"),
            ("extension-receiver", "this Page<Config> page", "Page<Config> page"),
            ("method-default", "int limit = 7", "int limit = 8")):
        changed_model = author / (label + ".cordis.model.json")
        changed_source = original_source.replace(original.encode(), changed.encode(), 1)
        assert changed_source != original_source, "Source mutation did not apply: " + label
        try:
            (author / "Author.cs").write_bytes(changed_source)
            extract_declarations("declaration-" + label + "-source-drift", changed_model)
        finally:
            (author / "Author.cs").write_bytes(original_source)
        changed_facts = json.loads(changed_model.read_text(encoding="utf-8"))
        assert changed_facts["contractIdentity"] != authored["contractIdentity"], "Changed ordinary facts retained identity: " + label
        if label == "return-annotation":
            returned = member(changed_facts, "IndependentDeclarations.Page`1", "ReadLabel")
            assert [value["name"] for value in returned["returnAttributes"]] == ["System.Diagnostics.CodeAnalysis.NotNullAttribute"]
            assert returned["type"]["nullability"] == "NotAnnotated"
        elif label == "extension-receiver":
            count = member(changed_facts, "IndependentDeclarations.PageExtensions", "Count")
            assert not count["parameters"][0]["this"] and count["parameters"][0]["scoped"] == "None"
            assert "extension" not in count["modifiers"]
            assert not count["parameters"][1]["this"] and count["parameters"][1]["scoped"] == "ScopedValue"
        else:
            page = next(value for value in changed_facts["declarations"] if value["metadataName"] == "IndependentDeclarations.Page`1")
            find = next(value for value in page["members"] if value["name"] == "Find" and len(value["parameters"]) == 2)
            assert find["parameters"][1]["optional"] and find["parameters"][1]["default"]["json"] == "8"
            assert member(changed_facts, "IndependentDeclarations.Page`1", "Limit")["initializer"]["json"] == "7"
        shutil.copyfile(changed_model, output / ("ordinary-" + label + "-drift.json"))
        changes.append({"changedFact": label, "contractIdentityChanged": True})

    for label, original, changed, diagnostic in (
            ("duplicate-signature", b"public T? Find(int index) => index >= 0 ? Item : null;",
             b"public T? Find(int index) => index >= 0 ? Item : null;\n    public T? Find(int index) => Item;", b"CS0111"),
            ("malformed-declaration", b"Find(int index)", b"Find(int index", b"CS1026")):
        refused_declaration = author / (label + ".cordis.model.json")
        refused_declaration.write_text("stale model", encoding="utf-8")
        changed_source = original_source.replace(original, changed, 1)
        assert changed_source != original_source
        try:
            (author / "Author.cs").write_bytes(changed_source)
            rejected = extract_declarations("declaration-" + label + "-rejected", refused_declaration, expected_failure=True)
        finally:
            (author / "Author.cs").write_bytes(original_source)
        assert diagnostic in rejected, rejected
        assert not refused_declaration.exists(), "Refused ordinary declaration retained a stale artifact: " + label

    invalid, invalid_project = project("DeclarationColdInvalidRoot",
        '<AssemblyName>IndependentDeclarations.InvalidRoot</AssemblyName><LangVersion>14.0</LangVersion>', composition,
        '<ItemGroup><CordisTypertModelRoot Include="IndependentDeclarations.Invalid.NotDeclared"/></ItemGroup>')
    shutil.copyfile(fixtures / "InvalidRoot.cs", invalid / "InvalidRoot.cs")
    assert not (invalid / "obj").exists(), "Invalid-root control must start before any successful build"
    rejected = run("declaration-first-invalid-root-build", [dotnet, "build", invalid_project, "-c", "Release"], True)
    assert b"IndependentDeclarations.Invalid.NotDeclared" in rejected and b"must identify one authored declaration" in rejected, rejected
    invalid_response = invalid / "obj/Release/net10.0/cordis-typert"
    invalid_owned = [invalid_response / name for name in ("model-roots.rsp", "model-sources.rsp", "model-references.rsp")]
    assert all(path.exists() for path in invalid_owned), "Invalid-root failure did not reach model extraction"
    invalid_owned.append(invalid_response / "IndependentDeclarations.InvalidRoot.cordis.model.json")
    assert not invalid_owned[-1].exists(), "Invalid-root build published an artifact"
    run("declaration-first-invalid-root-clean", [dotnet, "clean", invalid_project, "-c", "Release"])
    assert not any(path.exists() for path in invalid_owned), "Clean retained owned files after the first failed model extraction"

    generated, generated_project = project("DeclarationGeneratedNeighbor", '<LangVersion>14.0</LangVersion>', composition,
        '<ItemGroup><CordisTypertModelRoot Include="IndependentDeclarations.Generated.Root"/></ItemGroup>')
    shutil.copyfile(fixtures / "GeneratedNeighbor.cs", generated / "GeneratedNeighbor.cs")
    run("declaration-generated-neighbor-build", [dotnet, "build", generated_project, "-c", "Release"])
    generated_model = generated / "obj/Release/net10.0/cordis-typert/DeclarationGeneratedNeighbor.cordis.model.json"
    generated_facts = json.loads(generated_model.read_text(encoding="utf-8"))
    assert any(value["code"] == "CS0534" and value["category"] == "PreGeneratorDeclaration" for value in generated_facts["diagnostics"]), \
        "Pre-generator declaration errors must remain visible without rejecting a valid generated project"

    packaged_model = source / "cache/independentdeclarations.contracts/1.0.0-alpha/cordis/typert" / model.name
    command = [dotnet, compiler, "--roslyn-directory", roslyn, "inspect-model", "--model",
        "$(PkgIndependentDeclarations_Contracts)/cordis/typert/" + model.name,
        "--output", "$(IntermediateOutputPath)ordinary.catalog.json"]
    encoded_command = ' '.join('&quot;' + escape(str(value), {'"': '&quot;'}) + '&quot;' for value in command)
    catalog_target = '<Target Name="InspectDeclarationCatalog" BeforeTargets="CoreCompile" DependsOnTargets="ResolveReferences">' \
        '<Exec WorkingDirectory="$(MSBuildProjectDirectory)" Command="' + encoded_command + '"/>' \
        '<ItemGroup><FileWrites Include="$(IntermediateOutputPath)ordinary.catalog.json"/></ItemGroup></Target>'
    consumer, consumer_project = project("DeclarationCatalogConsumer", '<OutputType>Exe</OutputType>',
        '<PackageReference Include="IndependentDeclarations.Contracts" Version="1.0.0-alpha" GeneratePathProperty="true"/>',
        catalog_target)
    shutil.copyfile(fixtures / "CatalogConsumer.cs", consumer / "Program.cs")
    run("declaration-catalog-cold-build", [dotnet, "build", consumer_project, "-c", "Release"])
    catalog = consumer / "obj/Release/net10.0/ordinary.catalog.json"
    catalog_hash = hashlib.sha256(catalog.read_bytes()).hexdigest()
    catalog_stamp = catalog.stat().st_mtime_ns
    run("declaration-catalog-incremental", [dotnet, "build", consumer_project, "-c", "Release"])
    assert catalog.stat().st_mtime_ns == catalog_stamp, "Unchanged artifact catalog was rewritten"
    assert hashlib.sha256(catalog.read_bytes()).hexdigest() == catalog_hash
    run("declaration-catalog-clean", [dotnet, "clean", consumer_project, "-c", "Release"])
    assert not catalog.exists(), "Clean retained the catalog consumer's owned output"
    run("declaration-catalog-cold-rebuild", [dotnet, "build", consumer_project, "-c", "Release"])
    assert hashlib.sha256(catalog.read_bytes()).hexdigest() == catalog_hash, "Cold rebuild changed the artifact-only catalog"
    result = run("declaration-catalog-consumer", [dotnet, consumer / "bin/Release/net10.0/DeclarationCatalogConsumer.dll",
        packaged_model, catalog])
    assert b"ordinary declaration catalog consumer passed" in result
    assets = json.loads((consumer / "obj/project.assets.json").read_text(encoding="utf-8"))
    assert not any(name.startswith(("IndependentDeclarations.Provider/", "IndependentDeclarations.External/")) for name in assets["libraries"])
    assert not list((consumer / "bin").rglob("IndependentDeclarations.Provider.dll"))
    assert not list((consumer / "bin").rglob("IndependentDeclarations.External.dll"))
    shutil.copyfile(catalog, output / "ordinary-declaration-catalog.json")

    tampered = dict(authored, contractIdentity="sha256:" + "0" * 64)
    tampered_model = consumer / "tampered.cordis.model.json"
    tampered_model.write_text(json.dumps(tampered), encoding="utf-8")
    rejected_catalog = consumer / "tampered.catalog.json"
    rejected_catalog.write_text("stale catalog", encoding="utf-8")
    rejected = run("declaration-tampered-identity-rejected", [dotnet, compiler, "--roslyn-directory", roslyn,
        "inspect-model", "--model", tampered_model, "--output", rejected_catalog], True)
    assert b"identity does not match its authored facts" in rejected, rejected
    assert not rejected_catalog.exists(), "Refused model retained a stale catalog"

    refusal_roots = author / "refused-roots.rsp"
    refused_model = author / "refused.cordis.model.json"
    refusals = []
    for label, root_name, diagnostic in (
            ("unknown", "IndependentDeclarations.Unknown", "must identify one authored declaration"),
            ("nonpublic", "IndependentDeclarations.Hidden", "must be publicly accessible"),
            ("nonpublic-container", "IndependentDeclarations.HiddenContainer+Nested", "must be publicly accessible")):
        refusal_roots.write_text(root_name + "\n", encoding="utf-8")
        refused_model.write_text("stale model", encoding="utf-8")
        rejected = extract_declarations("declaration-" + label + "-root-rejected", refused_model, refusal_roots, True)
        assert root_name.encode() in rejected and diagnostic.encode() in rejected, rejected
        assert not refused_model.exists(), "Refused root retained a stale declaration model"
        refusals.append({"root": root_name, "staleOutputRemoved": True})

    refused_source = consumer / "OrdinaryRemoteClient.Generated.g.cs"
    refused_source.write_text("stale generated source", encoding="utf-8")
    rejected = run("declaration-remote-emission-rejected", [dotnet, compiler, "--roslyn-directory", roslyn,
        "emit-dotnet", "--model", packaged_model, "--namespace", "Caller.Ordinary", "--service", "ordinary",
        "--client", "OrdinaryRemoteClient", "--output", refused_source], True)
    assert b"roots" in rejected or b"Unsupported native model format 2" in rejected, rejected
    assert not refused_source.exists(), "Remote emission refusal retained a stale source"
    (output / "ordinary-declaration-evidence.json").write_text(json.dumps({
        "consumer": "ordinary-declaration-catalog",
        "contractIdentity": authored["contractIdentity"],
        "packageReferencesOnly": True,
        "artifactOnlyConsumerHasProviderImplementation": False,
        "externalContractFromSeparatePackage": True,
        "modelAndCatalogIncrementalStampsPreserved": True,
        "modelAndCatalogCleanOutputsRemoved": True,
        "coldRebuildPreservedIdentityAndCatalog": True,
        "tamperedIdentityRefusedAndStaleOutputRemoved": True,
        "sourceFactChanges": changes,
        "duplicateSignatureRefusedAndStaleOutputRemoved": True,
        "malformedDeclarationRefusedAndStaleOutputRemoved": True,
        "firstFailedBuildOwnedFilesRemovedByClean": True,
        "validGeneratedNeighborBuildPassedWithPreGeneratorDiagnostics": True,
        "refusedRoots": refusals,
        "ordinaryModelRefusedByRemoteEmitter": True
    }, indent=2) + "\n", encoding="utf-8")
