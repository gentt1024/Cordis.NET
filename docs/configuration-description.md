# Optional configuration descriptions

[中文](configuration-description.zh.md)

The existing description/reference APIs are available from `0.2.0-alpha.1`. The recommended `ConfigObject<T>` helper is a current source addition. See the [release notes](../CHANGELOG.md); a source version does not establish package availability.

`Plugin<T>.Config` remains the validation authority. A plugin can optionally supply `Configuration = new ConfigSchema<T>(validator, descriptor)`. If both properties are supplied, they must refer to the same delegate. Registration captures the validator, description, and explicit field projections together. Existing `IPlugin` implementations need no new members; adapters can opt into `IConfigurationPlugin.CaptureConfiguration()` and forward the captured bundle.

The author supplies the validation rules and keeps the descriptor's raw keys, shape and defaults consistent with them. Every marked fixed field needs one projection of its effective value. For a POCO, `WithOrdinaryEquality` must compare every ordinary field and exclude live fields; `WithSimplify` must return complete raw data the validator can read again. Structural maps can use the descriptor's ordinary comparison and simplification. Test typed adapters together, including ordinary changes and save round trips.

For complete data objects, prefer Composition's `ConfigObject<T>` below. A field combines its raw key, descriptor and pure plain-value projection. The helper supplies live bindings, compares every declared ordinary field with existing strict equality, and saves all declared fields including defaults. Authors must declare the complete field set and keep the validator's keys/defaults consistent. Share default constants when useful. Nested live bindings, opaque values, custom conversion or equality use `ConfigSchema<T>` directly. The old `Config` delegate remains supported. This helper is currently a source addition, not part of the published `0.2.0-alpha.1` packages.

The library captures these declarations together, rejects missing, extra or blocked projections, and publishes immutable snapshots atomically. It does not infer POCO fields, verify that a custom validator implements the declared defaults, or run another validation engine. A false ordinary comparison sends an update through the ordinary lifecycle.

The example accepts raw maps and produces a typed record. Apply reads ordinary settings from that record and live settings through a reference. All lifecycle operations run in the owning execution domain.

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

The first update prints `live=2, initial=1`: live reading changes while the effective record retains its identity and original value. Changing the ordinary label restarts and prints `old=2, restarted=3`. The direct `Fiber.Update` invokes Loader's save hook through `WithSimplify`; `savedRaw` contains both fields. A file-backed tree also writes that data. Compatible live updates do not invoke the update/save hook. `noSave: true` skips persistence but retains the ordinary lifecycle. An author may omit defaults in `WithSimplify` only when the validator reconstructs the same values.

Code replacement creates fresh references and prints `retired=4, replacement=4`. `ReplacePluginAsync` is for a prepared runtime implementation; CLR HMR also settles the candidate before replacing the old implementation. Static Native AOT applications must republish to change code. Retired references remain at their activation's final committed value after restart, replacement or recovery.

Rejected live input remains in `Entry.Options.Config` and `Fiber.RawConfig`, while effective configuration and running references retain their last accepted values. A `force` update still processes partial disposal and patch-context even when raw values compare equally; remounting depends on the resulting configuration changes.

Every marked fixed object field requires exactly one typed projection. A projection cannot name an ordinary or absent field. `WithVolatileValue()` and parameterless `GetConfigReference<T>()` describe a root whole-value reference; its notification path is the empty string. The object-key-list overload supports nested paths, displayed as escaped JSON pointers. The string overload names one exact object member. These explicit delegates work with static Native AOT authoring and do not inspect POCO properties through reflection.

Readonly references retain their identity during a compatible live update. Their values are detached immutable primitive, array, or string-keyed map snapshots; equal snapshots retain their previous value object and repeated acyclic branches are copied independently. Functions, opaque class instances, and cycles cannot become volatile snapshots. Ordinary configurations can still contain opaque CLR values and recursive graphs. Use `object`, `IReadOnlyList<object?>`, or `IReadOnlyDictionary<string, object?>` for graph snapshots; a mutable collection type is not a readonly snapshot type. Each activation owns its reference state: after restart or replacement, an old reference keeps its final value and the new activation receives new references.

`TryPrepareConfigurationUpdate` runs the existing configuration hook and captured validator once, then checks ordinary effective equality and all projected snapshots without publishing them. Typed records can supply `WithOrdinaryEquality`; structural maps use the captured object declaration. `ConfigurationUpdate.Commit()` consumes a candidate once and exchanges the complete reference state only when its activation and baseline are current. It preserves effective configuration identity, invokes no update hooks, and performs no save or restart. The Loader owns raw input retention and postcommit owner notifications. Direct `Fiber.Update` retains its existing update-hook, veto, save, and restart behavior.

Reference state holds only detached projected snapshots. The Fiber owns the complete effective config and validator delegates; a retired `ConfigReference<string>` does not retain that config's collectible POCO type. Deliberately retaining a reference whose generic argument belongs to a plugin can still retain its assembly. Snapshot publication exchanges all fields together; separate reads of multiple fields do not constitute a transaction.

Raw `__jsExpr` transports remain opaque to object descriptions. A child named `__jsExpr` cannot hide a changed parent expression source. Same-source transports compare equally without evaluation; changed source follows the ordinary lifecycle. An explicitly volatile whole node still supports compatible live updates.

Raw comparison descends only declared objects. Object omission or null uses that object's declared default; scalar defaults do not normalize a raw diff. Arrays, dictionaries, tuples, unions, intersections, getters, transforms, and lazy nodes compare strictly. Shared-node recursion falls back to strict comparison at the schema backedge. Strict equality keeps missing-key/`Undefined` equivalence, distinct null, NaN inequality, opaque identity, and cycle behavior. CLR numeric widths adapt the source Number domain; DateTime/DateTimeOffset use UTC milliseconds, Uri uses its canonical address, Regex uses pattern/options, and byte memory compares contents.

Descriptions preserve required/optional, default, volatile, child, and shared-node metadata; they do not implement a second validation engine. Lazy builders remain unexpanded during capture and raw comparison. Actual resolution walks used data paths; missing optional values and empty containers do not expand an infinite factory graph. Known unsafe volatile placements are rejected at capture; a used lazy path is checked before publication. `Serialize()`/`Deserialize()` preserve graph sharing, recursive edges, and metadata without serializing validators or callbacks. A graph that still contains unmaterialized lazy builders cannot be serialized even after its finite input resolves successfully.

`Fiber.SimplifyConfiguration` simplifies structural descriptions for persistence. POCOs and union branch selection require `WithSimplify`, an explicit complete typed conversion to raw data. No schema export or catalog substitutes for the captured runtime validator and references. Core uses the existing execution domain and has no dependency on Composition.

`RetainRawConfiguration`, `TryPrepareConfigurationUpdate`, and `ConfigurationUpdate.Commit` are Loader integration methods. Call them within the owning context's execution domain, for example through `Context.RunAsync`. A stale candidate cannot commit after another commit or activation change; it can be consumed only once. Commit itself sends no notification and saves nothing. Loader owns raw retention and emits owner notifications after commit. A schema-equal raw update with an ordinary-only descriptor uses a zero-reference shortcut without invoking the validator again. These methods are not a general concurrent property update API.

The CLR adaptation keeps unknown opaque objects identity-based. Composition's known raw expression nodes compare by source text without evaluation. Arbitrary typed collections are not automatically converted to immutable typed snapshots. A generic reference whose value type belongs to a collectible CLR bundle can retain that bundle until the host releases the reference.

When raw input itself is a POCO and the descriptor contains lazy nodes, supply `WithDescriptionData`. It projects the validated value once into the complete plain shape used to resolve the description. It is neither validation nor persistence. `WithSimplify` does not substitute for it. Map input is otherwise taken from the actual configuration-hook result passed to the validator, rather than inferred from the effective POCO.

A union containing lazy branches needs an explicit branch selector. The selector receives description data and returns the index of the captured child to resolve. It must agree with the validator's branch choice. Capture and raw comparison do not execute it; unused branches stay unexpanded. A lazy union without a selector is rejected. A union with a selector cannot be serialized because serialization cannot preserve that callback. Keep the authoring declaration to reconstruct it; ordinary unions without lazy branches remain supported.

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
