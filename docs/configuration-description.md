# Declare configuration and live fields

English | [中文](configuration-description.zh.md)

Define one validator, describe the fields it accepts, and explicitly select values that may update live. The descriptor adds forms and exports to that same configuration contract; it does not create another validator.

## Choose the declaration API

Use Core for Plugin<T>, ConfigSchema<T> and configuration references. Add Composition for ConfigObject<T>, Loader, Settings and export/discovery helpers. The configuration declaration APIs ship in `0.2.1-alpha.1`. The profile document export described below requires a newer build containing `DiscoverDocumentAsync`. Use matching packages and tools. This is ordinary plugin configuration, distinct from [Remote input defaults](authoring.md#static-data-defaults-on-remote-input).

Use ConfigObject<T> for complete data objects with a field list. Each Field ties one raw key, descriptor and pure value projection together; the helper supplies live bindings, ordinary comparisons and complete simplification including defaults. Declare every field. Use ConfigSchema<T> directly for nested live paths, opaque values or custom equality/conversion. A scalar can use Plugin<T>.Config directly.

Plugin<T>.Config remains the validation authority. If both Config and Configuration are supplied, they must reference the same validator delegate. Registration captures validator, descriptor and projections together. Custom IPlugin adapters can implement IConfigurationPlugin.CaptureConfiguration to supply that captured declaration.

## Run the live-field example

Create a console application with the .NET 10 SDK:

```console
dotnet new console -n ConfigExample -f net10.0
dotnet add ConfigExample package Cordis.NET.Composition --version 0.2.1-alpha.1
```

Replace ConfigExample/Program.cs with the following complete program. It validates limit/label, keeps limit live and treats label as an ordinary restart field. No Node or network Host is needed.

Program.cs:

```csharp
using Cordis;
using Cordis.Composition;
using System.Globalization;

static ConfigResult<Settings> Validate(object? raw)
{
    if (raw is not IReadOnlyDictionary<string, object?> map)
        return ConfigResult<Settings>.Failure("a configuration map is required");
    try
    {
        map.TryGetValue("limit", out var limitValue);
        map.TryGetValue("label", out var labelValue);
        int limit = Convert.ToInt32(limitValue ?? 1, CultureInfo.InvariantCulture);
        string label = labelValue is null ? "worker" : (string)labelValue;
        return limit > 0 ? ConfigResult<Settings>.Success(new(limit, label))
            : ConfigResult<Settings>.Failure("positive limit required");
    }
    catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
    {
        return ConfigResult<Settings>.Failure("invalid limit or label");
    }
}

var schema = ConfigObject<Settings>.Create(Validate)
    .Field("limit", ConfigDescriptor.Number().Default(1).Volatile(), value => value.Limit)
    .Field("label", ConfigDescriptor.String().Default("worker"), value => value.Label)
    .Build();

var references = new List<ConfigReference<int>>();
Plugin<Settings> CreatePlugin(string implementation) => new()
{
    Configuration = schema,
    Apply = (context, initial) =>
    {
        var limit = context.Fiber.GetConfigReference<int>("limit");
        references.Add(limit);
        Console.WriteLine($"{implementation}: {initial.Label}, limit={limit.Value}");
    }
};
static EntryOptions Raw(int limit, string label = "worker") => new()
{
    ["limit"] = limit, ["label"] = label
};

await using var root = new Context();
await root.RunAsync(async context =>
{
    var plugin = CreatePlugin("v1");
    var loader = new Loader(context, new StaticModuleResolver().Register("worker", plugin));
    await loader.Root.UpdateAsync([new() { Id = "worker", Name = "worker", Config = Raw(1) }]);
    await loader.WaitAsync();
    var entry = loader.Resolve("worker");
    var original = (Settings)entry.Fiber!.Config!;
    var old = references[^1];

    await entry.UpdateAsync(new() { Config = Raw(2) });
    Console.WriteLine($"live={old.Value}, initial={original.Limit}");

    await entry.UpdateAsync(new() { Config = Raw(3, "batch") });
    await loader.WaitAsync();
    Console.WriteLine($"old={old.Value}, restarted={references[^1].Value}");

    entry.Fiber!.Update(Raw(4, "batch"));
    await loader.WaitAsync();
    var savedRaw = entry.Options.Config;

    var beforeReplacement = references[^1];
    await loader.ReplacePluginAsync(plugin, CreatePlugin("v2"));
    Console.WriteLine($"retired={beforeReplacement.Value}, replacement={references[^1].Value}");
});

record Settings(int Limit, string Label);
```

Run `dotnet run --project ConfigExample`. The first live update prints `live=2, initial=1`: the reference changes while the activation's initial record does not. Changing label restarts and prints `old=2, restarted=3`. Replacing the implementation prints `retired=4, replacement=4`. Retired references retain their activation's final committed value.

Direct Fiber.Update uses Loader's save hook and WithSimplify; savedRaw includes both fields. A file-backed tree writes that data. Compatible live updates do not invoke the ordinary update/save hook. `noSave: true` skips persistence, not lifecycle. Static Native AOT code changes require republishing; runtime ReplacePluginAsync is not an AOT code loader.

## Keep validation and description consistent

Descriptor keys, shapes and defaults must match the validator. For a POCO, WithOrdinaryEquality compares every ordinary field and excludes live fields; WithSimplify returns complete raw data that can be validated again. It may omit a default only when the validator restores the same value. Structural maps can use descriptor comparison/simplification.

Each fixed live field needs exactly one projection. Missing, extra or blocked projections are refused; the library does not reflect POCO fields or prove that custom validation implements the described defaults. Live projections are detached immutable primitives, arrays or string-key maps. Equal snapshots reuse their value object; repeated acyclic branches are copied independently. Functions, opaque instances and cycles cannot become live snapshots. Ordinary configuration can still hold opaque values and recursive graphs.

Use object, IReadOnlyList<object?> or IReadOnlyDictionary<string, object?> for composite snapshots rather than expecting arbitrary typed collections to be reconstructed. The string GetConfigReference overload names one member; the object-key-list overload selects nested paths. Parameterless GetConfigReference and WithVolatileValue describe a root value with empty notification path. A reference typed with a collectible plugin's type can retain that assembly.

## Describe typed input and lazy branches

This fragment extends the preceding program's Settings, Validate, Raw and schema definitions:

```csharp
var typedInputSchema = new ConfigSchema<Settings>(
    raw => raw is Settings value ? Validate(Raw(value.Limit, value.Label)) : Validate(raw),
    schema.Descriptor)
    .WithVolatile("limit", value => value.Limit)
    .WithOrdinaryEquality((left, right) => left.Label == right.Label)
    .WithDescriptionData(value => new EntryOptions
    {
        ["limit"] = value.Limit, ["label"] = value.Label
    })
    .WithSimplify(value => new EntryOptions
    {
        ["limit"] = value.Limit, ["label"] = value.Label
    });

static ConfigDescriptor Tree() => ConfigDescriptor.Object(
    ("children", ConfigDescriptor.Array(ConfigDescriptor.Lazy(Tree))
        .Default(Array.Empty<object?>())));
var selectedUnion = ConfigDescriptor.Union(
    raw => raw is IReadOnlyDictionary<string, object?> ? 0 : 1,
    ConfigDescriptor.Lazy(Tree), ConfigDescriptor.String());
```

WithDescriptionData projects validated POCO input once into the complete plain shape used to resolve lazy descriptions; it is neither validation nor persistence, and WithSimplify does not replace it. Map input otherwise uses the actual configuration-hook result supplied to the validator.

A lazy union needs an explicit selector that agrees with the validator. Unused branches remain unexpanded. A lazy union without a selector is refused; a selector-bearing union cannot be serialized because its callback cannot be preserved. Retain the author declaration to reconstruct it.

Readonly references keep identity across compatible updates. Rejected raw input remains in Entry.Options.Config and Fiber.RawConfig, but effective configuration and references keep accepted values. A force update still processes partial disposal and patch Context; remounting depends on resulting changes. Core's RetainRawConfiguration, TryPrepareConfigurationUpdate and Commit are Loader integration APIs: they require the owning execution domain, allow one fresh commit and do not notify or save by themselves. They are not a concurrent property-update API.

## Explicit application metadata and independent exports

WithMetadata adds renderer and constraint hints next to the validator: role, plain/localized description, badges, extra plain data, hidden/disabled/collapse, links/comments, min/max/step, pattern source/flags and loose acceptance. Implement the actual rules and normalization in the validator:

```csharp
var limitDeclaration = ConfigDescriptor.Number().Default(2).Volatile()
    .WithMetadata(new ConfigurationMetadata
    {
        Min = 0, Max = 10, Step = 2, Role = "slider",
        Descriptions = new Dictionary<string, string> { ["en"] = "Even count", ["zh"] = "偶数数量" },
        Badges = [new ConfigurationBadge("preview", "warning")]
    });
var passwordDeclaration = ConfigDescriptor.String().Optional()
    .WithMetadata(new ConfigurationMetadata { Role = "secret" });
```

Core stores immutable acyclic annotations; caller containers are copied and opaque objects/delegates refused. Unknown annotations produce export diagnostics and cannot override required/default/live rules. A descriptor can retain an unresolved lazy callback, whose execution belongs to normal configuration resolution.

ToSchemastery exports the callable `{uid, refs}` form envelope; ToJsonSchema independently exports JSON Schema 2020-12 with `$defs` and x-cordis metadata. They preserve graph identities without calling validators or lazy builders. Empty tuple/union/intersection declarations have consumable forms; tuples keep the fixed source's non-strict open tail. Numeric bounds project directly, while step relative to a nonzero minimum, UTF-16 lengths and ECMAScript patterns have explicit JSON Schema limits. Pattern hints remain in x-cordis. Loose recovery widens acceptance. Complete reports whether runtime behavior remains; transforms, getters, branch decisions and native normalization are not replaced by a schema document.

## Nested settings and source edits

Use the session's ConfigurationOperations for ordered ConfigurationSet/ConfigurationUnset paths with a fresh source revision. It validates one complete final candidate and performs one source update/reconcile; intermediate states may be invalid. Object unset restores inheritance, array unset removes an element, and setting the current array length appends. Array indexes are canonical non-negative decimal strings. Saved, Applied and recovery failures are separate.

Mount SettingsForms over those operations. The following is a separate session fragment, not an extension of the limit/label-only program. It assumes an active worker declaration with explicitly projected live network.timeout and network.password fields:

```csharp
var forms = new SettingsForms(ctx, operations, _ => new SettingsPolicy(["network"]));
var view = (await forms.DescribeAsync()).Value.Namespaces.Single(row => row.Ns == "worker");
using var timeout = JsonDocument.Parse("30");
await forms.MutateAsync("worker",
    [new SettingsPathOp("set", ["network", "timeout"]) { Value = timeout.RootElement },
     new SettingsPathOp("unset", ["network", "password"])], view.Revision);
var formSchema = (await forms.DescribeAsync()).Value.Namespaces.Single(row => row.Ns == "worker").Schema;
```

SettingsPolicy filters the supported live form. Ordinary siblings and Host-hidden paths are omitted; ancestor replacement cannot bypass hidden paths. Secret values are write-only: replacing an ancestor may reset an omitted secret, so use path edits for incomplete views. Describe returns one coherent detached snapshot of base/user values, numeric revisions and secret-presence paths. Hidden/secret data is removed conservatively across union/intersection branches; secret array positions remain null placeholders. Non-live paths get fresh wrappers and live boundaries retain metadata. Full configuration Schema can include defaults and requires separate authorization.

Reading synchronizes through normal lifecycle and can execute plugin code. Recursive redaction follows present descendants; missing/null containers stop traversal. A lazy/union cycle without progress omits that value and reports a diagnostic rather than returning unredacted data. See [Settings](settings.md) for Remote result and revision handling.

## Discovery without plugin activation

PluginConfiguration.Descriptor exposes the captured declaration. Use an authorized resolver and explicit ordered layers; this fragment assumes configurationPath, plugin and layers already exist:

```csharp
var catalog = await ConfigurationSchemaDiscovery.DiscoverAsync(
    configurationPath, new StaticModuleResolver().Register("worker", plugin), layers);
foreach (var entry in catalog.Entries)
    Console.WriteLine($"{entry.Source}: {entry.EntryId}: {entry.Diagnostic ?? entry.JsonSchema?.Format}");
```

DiscoverAsync traverses native group/include entries, resolves relative include paths and uses literal initial data for a missing include without writing the file. It reports cycles and import failures while retaining surviving siblings. Disabled entries contribute declarations; source filename disambiguates positional IDs. Custom tree carriers produce an explicit unsupported traversal diagnostic. A native group/include containing `__jsExpr` remains opaque as a whole, including sibling path data.

Discovery creates no Context or Loader and invokes no Apply, ResolveConfig, expression evaluator or lazy builder. Module import and optional CaptureConfiguration are trusted author code and can execute. Discovery is not a sandbox or another validation engine. [Current support](development.md#current-support-and-release-status) and [generation support](development.md#generation-backend-support) cover release/platform and model-backend boundaries.

### Validate an entry list or a patch file

Pass the prepared profile launch, the entry-list filename and the same module resolver that the host uses. The launch supplies the bundle, profile, home and command-line layers in application order. The following CLI fragment writes a document even when one declaration fails. Handle profile preparation and root-file exceptions at the command boundary, before writing to standard output.

```csharp
var result = await ConfigurationSchemaDiscovery.DiscoverDocumentAsync(
    launch, configurationPath, resolver, cancellationToken);
Console.WriteLine(result.Document);
foreach (var diagnostic in result.Diagnostics)
    Console.Error.WriteLine(diagnostic);
return result.Complete ? 0 : 1;
```

Validate the parsed entry array against the document root. To validate an overlay, point the validator at `#/$defs/patchList` in the **same document** so its local references resolve. A patch that supplies `config` for a known root-tree ID replaces the whole value. An include has its own ID index. Plugin names without a collected declaration and IDs introduced by future patches stay open.

Inspect `x-cordis.entries` for the structural path and declaration status and `x-cordis.diagnostics` for collection or projection limitations. Distinct declarations for the same name across trees produce a union and a partial result. A CLI can return success for complete discovery even if a later runtime check would reject a configuration. Import and capture use the application's resolver. Applications with multiple module runtimes supply the corresponding declaration adapters.

### Supply a declaration from another module runtime

Implement `IConfigurationSchemaProjectionResolver` on the module resolver passed to discovery. Return null from `TryResolveSchemaAsync` for a specifier owned by the CLR resolver. For an owned module return `ResolvedConfigurationSchema` with its resolved export identity, a `ConfigurationSchemaExport` in `json-schema-2020-12` format, and whether the entry can omit its config. Use a null schema only after a successful import confirms that the declaration is absent. An unsupported declaration and an import exception produce positioned diagnostics.

Provide a self-contained draft 2020-12 input schema. Cordis preserves internal resource, anchor and pointer references when embedding it. Referenced resources must be present in that schema. Set `AcceptsMissing` to null for unknown omission behavior. Include any projection limitations in the export and set `Complete` to false. Cordis admits a Loader expression at the config root. Nested expression and validation semantics are properties of the supplied projection. The same module identity must identify the same declaration throughout discovery.

### Inspect the selected browser artifact

During a schema dump, prepare one `ProfileRefresh` and use its layers to select the client packages. Pass those selections and the deployment resolver to `ClientModuleCatalog.CaptureAsync`. For each graph entry, read the captured factory with `FindArtifact(entry.Id, entry.Revision).OpenRead()`. A missing match means the selected bytes are unavailable. For a shell module supplied through `staticModules`, inspect the export from the selected shell build.

Run `inspectClientModule` with that graph, its revisioned factory bytes and the shell externals used by client boot. For native Schemastery, pass the official app-boot exports `isNativeConfigSchema`, `createConfigProjector` and `LOADER_EXPRESSION_SCHEMA` in `nativeConfigProjection`. The authoring process owns this trusted import. A Host that only resolves CLR declarations can call discovery directly.

Bind the returned declaration to the resolved export and the captured graph, including the shell build when it supplies imports. Pass the adapter and the original refresh to `DiscoverDocumentAsync(refresh, profileName, filename, resolver)`. Discovery reuses the ordered layers and skipped-bundle diagnostics. It reads the root entry file and its includes at discovery time. If the selection, graph or contributing shell changes before a result is used as current, inspect the new selection. A local build and an installed package can expose different Config schemas under the same module name. [Client SDK inspection](../clients/modules/README.md)
