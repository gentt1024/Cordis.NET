"""Verify ordinary declaration artifacts through isolated package authors and a catalog consumer."""
import hashlib
import json
import shutil
from xml.sax.saxutils import escape
import zipfile


def verify_declaration_consumer(root, source, feed, output, version, dotnet, composition, run, project, hosted):
    fixtures = root / "tests/fixtures/DeclarationModel"
    external_setup = pack_external_data(fixtures, feed, dotnet, composition, run, project)

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
    shutil.copyfile(fixtures / "ArrayLabels.cs", author / "ArrayLabels.cs")
    run("declaration-author-pack", [dotnet, "pack", author_project, "-c", "Release", "-o", feed])
    response = author / "obj/Release/net10.0/cordis-typert"
    model = response / "IndependentDeclarations.Provider.cordis.model.json"
    owned = [model] + [response / name for name in ("model-roots.rsp", "model-sources.rsp", "model-references.rsp")]
    authored = json.loads(model.read_text(encoding="utf-8"))
    assert authored["declarations"], "Author produced no declaration facts"
    config_facts = next(value for value in authored["declarations"] if value["metadataName"] == "IndependentDeclarations.Config")
    arrays = {json.loads(value["arguments"][0]["json"]): value
        for value in config_facts["attributes"] if value["name"] == "IndependentDeclarations.ArrayLabelsAttribute"}
    assert set(arrays) == {"null", "empty", "values"}
    for label, attribute in arrays.items():
        positional = attribute["arguments"][1]
        named = next(value["value"] for value in attribute["namedArguments"] if value["name"] == "Types")
        assert positional["kind"] == named["kind"] == "array"
        assert positional["type"] == "string[]" and named["type"] == "System.Type[]"
        for value in (positional, named):
            if label in ("null", "empty"):
                assert value["json"] == ("null" if label == "null" else "[]")
                assert value["elements"] == []
            else:
                assert len(json.loads(value["json"])) == len(value["elements"]) == 1
        if label == "values":
            assert positional["elements"][0]["json"] == '"label"'
            type_reference = named["elements"][0]["reference"]
            assert type_reference["kind"] == "primitive" and type_reference["name"] == "System_String"
    shutil.copyfile(model, output / "ordinary-declaration-model.json")
    with zipfile.ZipFile(feed / "IndependentDeclarations.Provider.1.0.0-alpha.nupkg") as archive:
        assert archive.read("cordis/typert/IndependentDeclarations.Provider.cordis.model.json") == model.read_bytes()
        assert [name for name in archive.namelist() if name.startswith("cordis/typert/") and name.endswith(".json")] == \
            ["cordis/typert/" + model.name], "The author package exposes multiple declaration inputs"
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
            ("method-default", "int limit = 7", "int limit = 8"),
            ("null-array", '[ArrayLabels("null", null, Types = null)]',
                '[ArrayLabels("null", new string[] { }, Types = null)]'),
            ("null-array-named", '[ArrayLabels("null", null, Types = null)]',
                '[ArrayLabels("null", null, Types = new Type[] { })]')):
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
        elif label == "method-default":
            page = next(value for value in changed_facts["declarations"] if value["metadataName"] == "IndependentDeclarations.Page`1")
            find = next(value for value in page["members"] if value["name"] == "Find" and len(value["parameters"]) == 2)
            assert find["parameters"][1]["optional"] and find["parameters"][1]["default"]["json"] == "8"
            assert member(changed_facts, "IndependentDeclarations.Page`1", "Limit")["initializer"]["json"] == "7"
        else:
            config = next(value for value in changed_facts["declarations"] if value["metadataName"] == "IndependentDeclarations.Config")
            changed_array = next(value for value in config["attributes"]
                if value["name"] == "IndependentDeclarations.ArrayLabelsAttribute" and value["arguments"][0]["json"] == '"null"')
            assert changed_array["arguments"][1]["json"] == ("[]" if label == "null-array" else "null")
            assert changed_array["namedArguments"][0]["value"]["json"] == ("null" if label == "null-array" else "[]")
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
    assert b"Exactly one explicitly selected authored Remote service must match" in rejected, rejected
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
        "attributeArrays": {"cases": ["null", "empty", "values"], "constructorAndNamedArguments": True,
            "nullAndEmptyIdentitiesDiffer": True, "savedModelReadByCatalogCompiler": True},
        "tamperedIdentityRefusedAndStaleOutputRemoved": True,
        "sourceFactChanges": changes,
        "duplicateSignatureRefusedAndStaleOutputRemoved": True,
        "malformedDeclarationRefusedAndStaleOutputRemoved": True,
        "firstFailedBuildOwnedFilesRemovedByClean": True,
        "validGeneratedNeighborBuildPassedWithPreGeneratorDiagnostics": True,
        "refusedRoots": refusals,
        "ordinaryModelRefusedByRemoteEmitter": True
    }, indent=2) + "\n", encoding="utf-8")
    verify_data_consumer(fixtures, source, feed, output, dotnet, composition, compiler, roslyn, run, project, hosted)
    verify_referenced_data_consumer(root, source, feed, output, version, dotnet, composition, run, project, external_setup)


def pack_external_data(fixtures, feed, dotnet, composition, run, project):
    roots = ''.join('<CordisTypertModelRoot Include="IndependentDeclarations.External.' + name + '"/>'
        for name in ("ImportedData", "UnrelatedData", "ImportedHiddenData", "Envelope`1"))
    external, external_project = project("DeclarationExternal",
        '<AssemblyName>IndependentDeclarations.External</AssemblyName>'
        '<PackageId>IndependentDeclarations.External</PackageId><Version>1.0.0-alpha</Version>', composition,
        '<ItemGroup>' + roots + '</ItemGroup>')
    shutil.copyfile(fixtures / "External.cs", external / "External.cs")
    run("declaration-external-pack", [dotnet, "pack", external_project, "-c", "Release", "-o", feed])
    model = external / "obj/Release/net10.0/cordis-typert/IndependentDeclarations.External.cordis.model.json"
    return external, external_project, model


def verify_referenced_data_consumer(root, source, feed, output, version, dotnet, composition, run, project, external_setup=None):
    fixtures = root / "tests/fixtures/DeclarationModel"
    external, external_project, external_model = external_setup or pack_external_data(fixtures, feed, dotnet, composition, run, project)
    sdk = run("referenced-data-selected-sdk", [dotnet, "--version"]).decode("utf-8").strip()
    compiler = source / "cache/cordis.net.composition" / version / "tools/net10.0/typert/Cordis.Typert.Compiler.dll"
    roslyn = dotnet.parent / "sdk" / sdk / "Roslyn/bincore"
    author, author_project = project("ReferencedDataAuthor",
        '<AssemblyName>IndependentReferenced.Provider</AssemblyName>'
        '<PackageId>IndependentReferenced.Provider</PackageId><Version>1.0.0-alpha</Version>',
        composition + '<PackageReference Include="IndependentDeclarations.External" Version="1.0.0-alpha"/>',
        '<ItemGroup><CordisTypertModelRoot Include="IndependentDeclarations.ReferencedData"/>'
        '<CordisTypertModelRoot Include="IndependentDeclarations.ReferencedHiddenData"/>'
        '<CordisTypertModelRoot Include="IndependentDeclarations.DataExternal"/></ItemGroup>')
    shutil.copyfile(fixtures / "DataAuthor.cs", author / "DataAuthor.cs")
    run("referenced-data-author-pack", [dotnet, "pack", author_project, "-c", "Release", "-o", feed])
    model = author / "obj/Release/net10.0/cordis-typert/IndependentReferenced.Provider.cordis.model.json"
    for package_id, artifact in (("IndependentDeclarations.External", external_model), ("IndependentReferenced.Provider", model)):
        with zipfile.ZipFile(feed / (package_id + ".1.0.0-alpha.nupkg")) as archive:
            assert archive.read("cordis/typert/" + artifact.name) == artifact.read_bytes()

    contracts, contracts_project = project("ReferencedDataContracts",
        '<PackageId>IndependentReferenced.Contracts</PackageId><Version>1.0.0-alpha</Version>'
        '<CordisTypertDataModel>' + model.name + '</CordisTypertDataModel>'
        '<CordisTypertDataNamespace>IndependentDeclarations.Referenced</CordisTypertDataNamespace>'
        '<CordisTypertDataJsonContext>ReferencedDataJson</CordisTypertDataJsonContext>', composition,
        '<ItemGroup><CordisTypertDataRoot Include="IndependentDeclarations.ReferencedData"/>'
        '<CordisTypertDataReferenceModel Include="' + external_model.name + '"/>'
        '<None Include="*.cordis.model.json" Pack="true" PackagePath="cordis/typert/"/></ItemGroup>')
    primary_input = contracts / model.name
    reference_input = contracts / external_model.name
    shutil.copyfile(model, primary_input)
    shutil.copyfile(external_model, reference_input)
    main_identity = json.loads(primary_input.read_text(encoding="utf-8"))["contractIdentity"]
    reference_identity = json.loads(reference_input.read_text(encoding="utf-8"))["contractIdentity"]
    original_reference = reference_input.read_bytes()
    run("referenced-data-contracts-pack", [dotnet, "pack", contracts_project, "-c", "Release", "-o", feed])
    generated = contracts / "obj/Release/net10.0/cordis-typert/ReferencedDataJson.Data.g.cs"
    owned = [generated] + [generated.parent / name for name in ("data-roots.rsp", "data-reference-models.rsp")]
    first_source = generated.read_bytes()
    assert main_identity.encode() in first_source and reference_identity.encode() in first_source, "Generated data lost an explicit model input identity"
    stamp = generated.stat().st_mtime_ns
    run("referenced-data-contracts-incremental", [dotnet, "build", contracts_project, "-c", "Release"])
    assert generated.stat().st_mtime_ns == stamp and generated.read_bytes() == first_source
    caller, caller_project = project("ReferencedDataConsumer", '<OutputType>Exe</OutputType>',
        '<PackageReference Include="IndependentReferenced.Contracts" Version="1.0.0-alpha"/>')
    shutil.copyfile(fixtures / "ReferencedConsumer.cs", caller / "Program.cs")

    def call(label, default):
        run(label + "-build", [dotnet, "build", caller_project, "-c", "Release"])
        result = run(label, [dotnet, caller / "bin/Release/net10.0/ReferencedDataConsumer.dll", str(default)])
        assert ("referenced ordinary data carrier consumer passed: " + str(default)).encode() in result
        assets = json.loads((caller / "obj/project.assets.json").read_text(encoding="utf-8"))
        assert not any(name.startswith(("IndependentDeclarations.External/", "IndependentReferenced.Provider/"))
            for name in assets["libraries"]), "Caller acquired a referenced author's implementation package"
        for assembly in ("IndependentDeclarations.External.dll", "IndependentReferenced.Provider.dll"):
            assert not list((caller / "bin").rglob(assembly)), "Caller acquired an author's implementation assembly"
    call("referenced-data-caller", 11)

    external_source = external / "External.cs"
    original_source = external_source.read_bytes()
    try:
        changed_source = original_source.replace(b"= 11;", b"= 12;", 1)
        assert changed_source != original_source
        external_source.write_bytes(changed_source)
        run("referenced-data-source-change-build", [dotnet, "build", external_project, "-c", "Release"])
        changed_reference = external_model.read_bytes()
    finally:
        external_source.write_bytes(original_source)
    changed_identity = json.loads(changed_reference)["contractIdentity"]
    assert changed_identity != reference_identity
    reference_input.write_bytes(changed_reference)
    assert json.loads(primary_input.read_text(encoding="utf-8"))["contractIdentity"] == main_identity
    run("referenced-data-reference-change-build", [dotnet, "build", contracts_project, "-c", "Release"])
    changed_projection = generated.read_bytes()
    assert changed_projection != first_source and changed_identity.encode() in changed_projection
    assert reference_identity.encode() not in changed_projection
    contracts_project.write_bytes(contracts_project.read_bytes().replace(b'<Version>1.0.0-alpha</Version>', b'<Version>1.0.1-alpha</Version>'))
    run("referenced-data-changed-contracts-pack", [dotnet, "pack", contracts_project, "-c", "Release", "-o", feed])
    caller_project.write_bytes(caller_project.read_bytes().replace(b'Version="1.0.0-alpha"', b'Version="1.0.1-alpha"'))
    call("referenced-data-changed-caller", 12)

    original_input = contracts / "original-reference.cordis.model.json"
    original_input.write_bytes(original_reference)
    corrupt_input = contracts / "invalid-identity-reference.cordis.model.json"
    corrupt_input.write_text(json.dumps(dict(json.loads(changed_reference), contractIdentity="sha256:" + "0" * 64)), encoding="utf-8")
    roots = contracts / "refused-reference-roots.rsp"
    references = contracts / "refused-reference-models.rsp"
    refused = contracts / "Refused.Reference.Data.g.cs"
    refusals = []
    for label, root_name, selected_references, diagnostic in (
        ("missing", "IndependentDeclarations.ReferencedData", [], "External data declarations require referenced-model composition"),
        ("conflicting-source", "IndependentDeclarations.ReferencedData", [reference_input.name, original_input.name], "Conflicting source definitions"),
        ("reference-identity", "IndependentDeclarations.ReferencedData", [reference_input.name, corrupt_input.name], "identity does not match its authored facts"),
        ("reference-root", "IndependentDeclarations.External.UnrelatedData", [reference_input.name], "explicitly selected declaration root"),
        ("reference-generic", "IndependentDeclarations.DataExternal", [reference_input.name], "Generic data declarations require a further projection"),
        ("private-json-include", "IndependentDeclarations.ReferencedHiddenData", [reference_input.name], "TYPM202"),
    ):
        roots.write_text(root_name + "\n", encoding="utf-8")
        references.write_text(''.join(name + "\n" for name in selected_references), encoding="utf-8")
        refused.write_text("stale generated data", encoding="utf-8")
        rejected = run("referenced-data-refuse-" + label, [dotnet, compiler, "--roslyn-directory", roslyn,
            "emit-dotnet-data", "--model", primary_input, "--roots", roots, "--reference-models", references,
            "--namespace", "IndependentDeclarations.Referenced", "--context", "ReferencedDataJson", "--output", refused], True)
        assert diagnostic.encode() in rejected, rejected
        assert not refused.exists(), "Reference refusal retained stale data source: " + label
        refusals.append({"case": label, "staleOutputRemoved": True})

    project_bytes = contracts_project.read_bytes()
    try:
        contracts_project.write_bytes(project_bytes.replace(
            ('<CordisTypertDataReferenceModel Include="' + external_model.name + '"/>').encode(), b''))
        rejected = run("referenced-data-msbuild-missing-model", [dotnet, "build", contracts_project, "-c", "Release"], True)
        assert b"External data declarations require referenced-model composition" in rejected and not generated.exists()
    finally:
        contracts_project.write_bytes(project_bytes)
    run("referenced-data-failed-build-clean", [dotnet, "clean", contracts_project, "-c", "Release"])
    assert not any(path.exists() for path in owned), "Clean retained a generated data source or reference response file"
    run("referenced-data-cold-rebuild", [dotnet, "build", contracts_project, "-c", "Release"])
    assert generated.read_bytes() == changed_projection, "Cold rebuild changed a reference-composed carrier"
    shutil.copyfile(primary_input, output / "referenced-data-main-model.json")
    shutil.copyfile(reference_input, output / "referenced-data-library-model.json")
    (output / "referenced-data-evidence.json").write_text(json.dumps({
        "consumer": "reference-composed-ordinary-data-carrier-stj", "mainContractIdentity": main_identity,
        "referenceContractIdentity": reference_identity, "changedReferenceContractIdentity": changed_identity,
        "packageReferencesOnly": True, "authorImplementationsReferenced": False,
        "namesRequiredNullDefaultsAndRecursion": True, "referenceRootsDoNotExpandMainRoots": True,
        "sourceAuthoredConflict": True, "referenceChangeRegeneratedAndConsumed": True,
        "cleanAndFailedBuildRemovedOwnedOutputs": True, "refusals": refusals
    }, indent=2) + "\n", encoding="utf-8")


def verify_saved_remote_refusals(author, model, composition, compiler, roslyn, dotnet, run, project, output):
    baseline = json.loads(model.read_text(encoding="utf-8"))
    compilation = baseline["compilation"]
    response = model.parent
    source_file = author / "SharedAuthor.cs"
    original = source_file.read_text(encoding="utf-8")
    model_input = author / "refused-remote.cordis.model.json"
    refused_source = author / "RefusedClient.Generated.g.cs"
    output_kind = {"DynamicallyLinkedLibrary": "Library", "ConsoleApplication": "Exe", "WindowsApplication": "WinExe"}

    def extract(label, destination, reference_response=None):
        return run("saved-remote-extract-" + label,
            [dotnet, compiler, "--roslyn-directory", roslyn, "extract-model",
             "--project-directory", author, "--sources", response / "model-sources.rsp",
             "--references", reference_response or response / "model-references.rsp", "--assembly-name", compilation["assemblyName"],
             "--language-version", "14.0", "--nullable", compilation["nullable"],
             "--defines", ";".join(compilation["defines"]), "--allow-unsafe", str(compilation["allowUnsafe"]).lower(),
             "--check-overflow", str(compilation["checkOverflow"]).lower(), "--output-kind", output_kind[compilation["outputKind"]],
             "--platform", compilation["platform"], "--optimize", str(compilation["optimization"] == "Release").lower(),
             "--roots", response / "model-roots.rsp", "--output", destination])

    def emit_refusal(label, diagnostic):
        refused_source.write_text("stale client source", encoding="utf-8")
        rejected = run("saved-remote-refuse-" + label,
            [dotnet, compiler, "--roslyn-directory", roslyn, "emit-dotnet", "--model", model_input,
             "--namespace", "IndependentDeclarations.Refused", "--service", "sharedData", "--client", "RefusedClient",
             "--output", refused_source], True)
        assert diagnostic.encode() in rejected, rejected
        assert not refused_source.exists(), "Refused Remote projection retained a stale client: " + label

    def add_method(method):
        anchor = "\n}\n\n[JsonSourceGenerationOptions"
        assert anchor in original
        return original.replace(anchor, "\n" + method + anchor, 1)

    cases = (
        ("context-declaration-missing", original.replace(
            '[assembly: TypertContext("shared:receiver", typeof(int))]', ''), "TYPM009"),
        ("context-lookup-type-mismatch", original.replace(
            '[assembly: TypertContext("shared:document", typeof(string))]',
            '[assembly: TypertContext("shared:document", typeof(int))]'), "TYPM010"),
        ("context-wire-collision", original.replace('Wire = "receiverId"', 'Wire = "text"'), "TYPM011"),
        ("private-marked-method", add_method(
            '    [RemoteMethod("hidden")]\n    private Task<SharedData> HiddenAsync(SharedData value) => Task.FromResult(value);'), "TYPM003"),
        ("abstract-marked-method", add_method(
            '    [RemoteMethod("missing")]\n    public abstract Task<SharedData> MissingAsync(SharedData value);').replace(
                "public partial class SharedRemote", "public abstract partial class SharedRemote"), "TYPM007"),
        ("extern-marked-method", add_method(
            '    [RemoteMethod("missing")]\n    public extern Task<SharedData> MissingAsync(SharedData value);'), "TYPM007"),
        ("bodyless-marked-method", add_method(
            '    [RemoteMethod("missing")]\n    public partial Task<SharedData> MissingAsync(SharedData value);'), "TYPM007"),
        ("empty-json-context", original.replace("typeof(SharedRemoteJson)", "typeof(EmptyJson)", 1) +
            "\ninternal partial class EmptyJson : JsonSerializerContext;\n", "TYPM115"),
        ("authored-json-metadata", original.replace(
            "internal partial class SharedRemoteJson : JsonSerializerContext;",
            "internal partial class SharedRemoteJson : JsonSerializerContext\n{\n"
            "    public static SharedRemoteJson Default => throw new NotSupportedException();\n}\n"), "TYPM103"))
    refusals = []
    private_model = author / "private-marked-method.cordis.model.json"
    for label, changed, diagnostic in cases:
        assert changed != original, "Source control did not change the author: " + label
        try:
            source_file.write_text(changed, encoding="utf-8")
            extract(label, model_input)
        finally:
            source_file.write_text(original, encoding="utf-8")
        emit_refusal(label, diagnostic)
        if label == "private-marked-method":
            shutil.copyfile(model_input, private_model)
        refusals.append({"case": label, "diagnostic": diagnostic, "staleOutputRemoved": True})

    metadata, metadata_project = project("ExternalMetadataContext", "",
        '<PackageReference Include="IndependentData.Provider" Version="1.0.0-alpha"/>')
    metadata_source = metadata / "MetadataContext.cs"
    metadata_source.write_text(
        'using IndependentDeclarations; using System.Text.Json.Serialization; namespace IndependentExternalContext;\n'
        '[JsonSourceGenerationOptions(PropertyNamingPolicy=JsonKnownNamingPolicy.CamelCase, RespectNullableAnnotations=true)]\n'
        '[JsonSerializable(typeof(SharedData))] [JsonSerializable(typeof(int?))]\n'
        'public partial class MetadataOnlyJson : JsonSerializerContext;\n', encoding="utf-8")
    run("saved-remote-metadata-context-build", [dotnet, "build", metadata_project, "-c", "Release"])
    metadata_references = response / "external-metadata-references.rsp"
    metadata_references.write_text((response / "model-references.rsp").read_text(encoding="utf-8-sig") +
        "\n" + str(metadata / "bin/Release/net10.0/ExternalMetadataContext.dll") + "\n", encoding="utf-8")
    try:
        source_file.write_text(original.replace("typeof(SharedRemoteJson)",
            "typeof(IndependentExternalContext.MetadataOnlyJson)", 1), encoding="utf-8")
        extract("external-metadata-context", model_input, metadata_references)
    finally:
        source_file.write_text(original, encoding="utf-8")
    metadata_facts = json.loads(model_input.read_text(encoding="utf-8"))
    metadata_context = next(value for value in metadata_facts["declarations"]
        if value["metadataName"] == "IndependentExternalContext.MetadataOnlyJson")
    assert metadata_context["provenance"] == "metadata-signature", "The context control did not reach actual compiled metadata"
    emit_refusal("external-metadata-context", "TYPM115")
    refusals.append({"case": "external-metadata-context", "diagnostic": "TYPM115", "compiledLibraryInput": True,
        "staleOutputRemoved": True})

    try:
        source_file.write_text(original.replace("public partial class SharedRemote", "internal partial class SharedRemote"), encoding="utf-8")
        internal_model = author / "internal-service.cordis.model.json"
        extract("internal-service", internal_model)
    finally:
        source_file.write_text(original, encoding="utf-8")
    internal_facts = json.loads(internal_model.read_text(encoding="utf-8"))
    internal_service = next(value for value in internal_facts["declarations"]
        if value["metadataName"] == "IndependentDeclarations.SharedRemote")
    assert internal_service["accessibility"] == "Internal" and internal_service["id"] in internal_facts["roots"], \
        "A non-public marked service was excluded from declaration analysis"

    invalid_facts = []
    missing_body_fact = json.loads(model.read_text(encoding="utf-8"))
    service = next(value for value in missing_body_fact["declarations"]
        if value["metadataName"] == "IndependentDeclarations.SharedRemote")
    next(value for value in service["members"] if value["name"] == "EchoAsync").pop("implementation")
    invalid_facts.append(("missing-implementation", missing_body_fact))
    unknown = dict(baseline, unrecognizedDeclarationRepresentation=True)
    invalid_facts.append(("unknown-property", unknown))
    for label, invalid in invalid_facts:
        model_input.write_text(json.dumps(invalid), encoding="utf-8")
        emit_refusal(label, "Declaration model lacks valid required facts. Re-extract with the current tool.")
        refusals.append({"case": label, "invalidInputShape": True, "staleOutputRemoved": True})

    cold, cold_project = project("SavedRemoteColdRefusal",
        '<CordisTypertClientModel>private-marked-method.cordis.model.json</CordisTypertClientModel>'
        '<CordisTypertService>sharedData</CordisTypertService>'
        '<CordisTypertClientNamespace>IndependentDeclarations.Refused</CordisTypertClientNamespace>'
        '<CordisTypertClientName>RefusedClient</CordisTypertClientName>', composition)
    shutil.copyfile(private_model, cold / private_model.name)
    cold_source = cold / "obj/Release/net10.0/cordis-typert/RefusedClient.Generated.g.cs"
    cold_source.parent.mkdir(parents=True)
    cold_source.write_text("stale client source", encoding="utf-8")
    rejected = run("saved-remote-first-refused-build", [dotnet, "build", cold_project, "-c", "Release"], True)
    assert b"TYPM003" in rejected, rejected
    assert not cold_source.exists(), "First refused build retained a generated client"
    cold_source.write_text("owned residual after failed generation", encoding="utf-8")
    run("saved-remote-first-refused-clean", [dotnet, "clean", cold_project, "-c", "Release"])
    assert not cold_source.exists(), "Clean did not own client output after the first refused build"
    (output / "saved-remote-evidence.json").write_text(json.dumps({
        "consumer": "saved-declaration-remote-http", "contractIdentity": baseline["contractIdentity"],
        "sameModelForDataAndRemote": True, "providerImplementationReferenced": False,
        "reflectionFallbackEnabled": False, "realHttpInvocation": True,
        "scopedReceiverAndLookup": True, "scopedDownlink": True,
        "contextIdentityRereadAndAdapterWithdrawal": True, "undefinedAndNullIdentityDistinct": True,
        "serializerNamesAndRecursion": True, "optionalDefaultsAndExplicitNullBoundary": True,
        "terminalCancellationToken": True, "internalServiceAnalysisAccepted": True,
        "incrementalAndColdRebuild": True, "firstRefusedBuildAndCleanRemovedClient": True,
        "refusals": refusals
    }, indent=2) + "\n", encoding="utf-8")


def verify_data_consumer(fixtures, source, feed, output, dotnet, composition, compiler, roslyn, run, project, hosted, saved_remote_only=False):
    roots = ("DataOptions", "DataExpression", "DataExternal", "DataReadOnly", "DataWireCollision",
             "PluginAuthor", "CatalogService", "SharedData", "SharedRemote")
    if saved_remote_only:
        roots = ("SharedData", "SharedRemote")
    root_items = '<ItemGroup>' + ''.join('<CordisTypertModelRoot Include="IndependentDeclarations.' + name + '"/>'
        for name in roots) + '</ItemGroup>'
    author, author_project = project("OrdinaryDataAuthor",
        '<AssemblyName>IndependentData.Provider</AssemblyName>'
        '<PackageId>IndependentData.Provider</PackageId><Version>1.0.0-alpha</Version><LangVersion>14.0</LangVersion>',
        composition + ('' if saved_remote_only else '<PackageReference Include="IndependentDeclarations.External" Version="1.0.0-alpha"/>'), root_items)
    if not saved_remote_only:
        shutil.copyfile(fixtures / "Author.cs", author / "Author.cs")
        shutil.copyfile(fixtures / "DataAuthor.cs", author / "DataAuthor.cs")
    shutil.copyfile(fixtures / "SharedAuthor.cs", author / "SharedAuthor.cs")
    shutil.copyfile(fixtures / "ArrayLabels.cs", author / "ArrayLabels.cs")
    run("ordinary-data-author-pack", [dotnet, "pack", author_project, "-c", "Release", "-o", feed])
    model = author / "obj/Release/net10.0/cordis-typert/IndependentData.Provider.cordis.model.json"
    with zipfile.ZipFile(feed / "IndependentData.Provider.1.0.0-alpha.nupkg") as archive:
        assert archive.read("cordis/typert/" + model.name) == model.read_bytes()
        assert [name for name in archive.namelist() if name.startswith("cordis/typert/") and name.endswith(".json")] == \
            ["cordis/typert/" + model.name], "The shared author package exposes multiple declaration inputs"
    shutil.copyfile(model, output / "ordinary-data-model.json")
    declaration = json.loads(model.read_text(encoding="utf-8"))
    assert declaration["declarations"], "Author produced no declaration facts"
    shared = next(value for value in declaration["declarations"] if value["metadataName"] == "IndependentDeclarations.SharedData")
    facts = {value["name"]: value for value in shared["members"] if value["kind"] == "property"}
    assert facts["Count"]["name"] == "Count", "Serializer naming policy leaked into declaration facts"

    contract_items = '<ItemGroup>' + ('' if saved_remote_only else '<CordisTypertDataRoot Include="IndependentDeclarations.DataOptions"/>') + \
        '<CordisTypertDataRoot Include="IndependentDeclarations.SharedData"/>' \
        '<None Include="' + model.name + '" Pack="true" PackagePath="cordis/typert/"/></ItemGroup>'
    contracts, contracts_project = project("OrdinaryDataContracts",
        '<PackageId>IndependentData.Contracts</PackageId><Version>1.0.0-alpha</Version>'
        '<CordisTypertDataModel>' + model.name + '</CordisTypertDataModel>'
        '<CordisTypertDataNamespace>IndependentDeclarations.Data</CordisTypertDataNamespace>'
        '<CordisTypertDataJsonContext>OrdinaryDataJson</CordisTypertDataJsonContext>'
        '<CordisTypertClientModel>' + model.name + '</CordisTypertClientModel>'
        '<CordisTypertService>sharedData</CordisTypertService>'
        '<CordisTypertClientNamespace>IndependentDeclarations.Wire</CordisTypertClientNamespace>'
        '<CordisTypertClientName>SharedClient</CordisTypertClientName>', composition, contract_items)
    shutil.copyfile(model, contracts / model.name)
    run("ordinary-data-contracts-pack", [dotnet, "pack", contracts_project, "-c", "Release", "-o", feed])
    generated = contracts / "obj/Release/net10.0/cordis-typert/OrdinaryDataJson.Data.g.cs"
    response = generated.parent / "data-roots.rsp"
    client_source = generated.parent / "SharedClient.Generated.g.cs"
    owned_sources = [generated, client_source]
    hashes = {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in owned_sources}
    stamps = {path.name: path.stat().st_mtime_ns for path in owned_sources}
    run("ordinary-data-contracts-incremental", [dotnet, "build", contracts_project, "-c", "Release"])
    for path in owned_sources:
        assert path.stat().st_mtime_ns == stamps[path.name]
        assert hashlib.sha256(path.read_bytes()).hexdigest() == hashes[path.name]
    run("ordinary-data-contracts-clean", [dotnet, "clean", contracts_project, "-c", "Release"])
    assert not any(path.exists() for path in owned_sources + [response]), "Clean retained generated data/client input/output"
    run("ordinary-data-contracts-cold-rebuild", [dotnet, "build", contracts_project, "-c", "Release"])
    for path in owned_sources:
        assert hashlib.sha256(path.read_bytes()).hexdigest() == hashes[path.name]

    if not saved_remote_only:
        caller, caller_project = project("OrdinaryDataConsumer", '<OutputType>Exe</OutputType>',
            '<PackageReference Include="IndependentData.Contracts" Version="1.0.0-alpha"/>')
        shutil.copyfile(fixtures / "DataConsumer.cs", caller / "Program.cs")
        run("ordinary-data-caller-build", [dotnet, "build", caller_project, "-c", "Release"])
        result = run("ordinary-data-caller", [dotnet, caller / "bin/Release/net10.0/OrdinaryDataConsumer.dll"])
        assert b"ordinary data carrier consumer passed" in result
        assets = json.loads((caller / "obj/project.assets.json").read_text(encoding="utf-8"))
        assert not any(name.startswith(("IndependentData.Provider/", "IndependentDeclarations.External/"))
            for name in assets["libraries"]), "Data caller acquired the author or external implementation package"
        assert not list((caller / "bin").rglob("IndependentData.Provider.dll"))
        assert not list((caller / "bin").rglob("IndependentDeclarations.External.dll"))
    shared_caller, shared_project = project("SharedDeclarationConsumer", '<OutputType>Exe</OutputType>',
        '<PackageReference Include="IndependentData.Contracts" Version="1.0.0-alpha"/>')
    shutil.copyfile(fixtures / "SharedConsumer.cs", shared_caller / "Program.cs")
    run("shared-declaration-caller-build", [dotnet, "build", shared_project, "-c", "Release"])
    host, host_project = project("SharedRemoteHost", '<OutputType>Exe</OutputType>',
        composition.replace('Cordis.NET.Composition', 'Cordis.NET.AspNetCore') +
        '<PackageReference Include="IndependentData.Provider" Version="1.0.0-alpha"/>'
        '<FrameworkReference Include="Microsoft.AspNetCore.App"/>')
    shutil.copyfile(fixtures / "SharedHost.cs", host / "Program.cs")
    run("shared-declaration-host-build", [dotnet, "build", host_project, "-c", "Release"])
    with hosted("shared-declaration-host", [dotnet, host / "bin/Release/net10.0/SharedRemoteHost.dll"], expect_stop=True) as address:
        result = run("shared-declaration-caller", [dotnet, shared_caller / "bin/Release/net10.0/SharedDeclarationConsumer.dll", address])
        assert b"saved Remote declaration HTTP consumer passed" in result
    shared_assets = json.loads((shared_caller / "obj/project.assets.json").read_text(encoding="utf-8"))
    assert not any(name.startswith(("IndependentData.Provider/", "IndependentDeclarations.External/"))
        for name in shared_assets["libraries"]), "Shared consumer acquired the author's implementation"
    assert not list((shared_caller / "bin").rglob("IndependentData.Provider.dll"))
    assert not list((shared_caller / "bin").rglob("IndependentDeclarations.External.dll"))
    verify_saved_remote_refusals(author, model, composition, compiler, roslyn, dotnet, run, project, output)
    if saved_remote_only:
        return

    refused_roots = contracts / "refused-data-roots.rsp"
    refused_source = contracts / "Refused.Data.g.cs"
    refusals = []
    for name, context, diagnostic in (
            ("DataExpression", "RefusedJson", "Only constant data initializers"),
            ("DataExternal", "RefusedJson", "External data declarations require referenced-model composition"),
            ("DataReadOnly", "RefusedJson", "Only public readable properties with public set/init"),
            ("DataWireCollision", "RefusedJson", "JSON property names collide"),
            ("DataOptions", "DataOptions", "Caller type names collide"),
            ("DataChild", "RefusedJson", "explicitly selected declaration root"),
            ("CatalogService", "RefusedJson", "without inheritance or interfaces")):
        refused_roots.write_text("IndependentDeclarations." + name + "\n", encoding="utf-8")
        refused_source.write_text("stale data source", encoding="utf-8")
        rejected = run("ordinary-data-refuse-" + name.lower() + "-" + context.lower(),
            [dotnet, compiler, "--roslyn-directory", roslyn, "emit-dotnet-data", "--model", model,
             "--roots", refused_roots, "--namespace", "IndependentDeclarations.Data", "--context", context,
             "--output", refused_source], True)
        assert diagnostic.encode() in rejected, rejected
        assert not refused_source.exists(), "Refused data projection retained a stale source"
        refusals.append({"root": name, "context": context, "staleOutputRemoved": True})

    original_project = contracts_project.read_bytes()
    try:
        contracts_project.write_bytes(original_project.replace(
            b'CordisTypertDataRoot Include="IndependentDeclarations.DataOptions"',
            b'CordisTypertDataRoot Include="IndependentDeclarations.DataExpression"'))
        rejected = run("ordinary-data-msbuild-refusal", [dotnet, "build", contracts_project, "-c", "Release"], True)
        assert b"Only constant data initializers" in rejected
        assert not generated.exists(), "MSBuild refusal retained the previously generated data source"
        run("ordinary-data-failed-build-clean", [dotnet, "clean", contracts_project, "-c", "Release"])
        assert not generated.exists() and not response.exists()
    finally:
        contracts_project.write_bytes(original_project)
    (output / "ordinary-data-evidence.json").write_text(json.dumps({
        "consumer": "ordinary-data-carrier-stj",
        "contractIdentity": json.loads(model.read_text(encoding="utf-8"))["contractIdentity"],
        "providerImplementationReferenced": False,
        "reflectionFallbackEnabled": False,
        "requiredNullOmissionDefaultsAndRoundtrip": True,
        "authoredConstructorAndAccessorNotReplayed": True,
        "sameAuthoredDtoOrdinaryRootAndRemoteBoundary": True,
        "serializerNamesAppliedOnlyByProjection": True,
        "incrementalTimestampAndColdRebuild": True,
        "cleanAndRefusalRemovedOwnedSource": True,
        "refusals": refusals
    }, indent=2) + "\n", encoding="utf-8")
