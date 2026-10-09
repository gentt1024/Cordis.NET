# Authoring plugins and applications

[中文](authoring.zh.md)

These optional .NET authoring helpers use the existing Cordis runtime. A package makes code available; module registration maps names to code; patch/profile entries select instances; `Inject` determines when a plugin can activate. Service identity remains its name and realm. None of these helpers adds a second registry or lifecycle.

## Start with the runnable example

The authoring helpers on this page require the authoring release listed in the [release notes](../CHANGELOG.md); older NuGet packages do not include them. Until that release is available on NuGet, use this source checkout. The commands below run the repository examples through project references with the SDK selected by `global.json`; installing packages alone does not provide the example projects.

The [shared contracts](../examples/Probes.Contracts/Probes.cs), [provider and configuration](../examples/Probes.Plugin/ProbeModule.cs), and [static host with two consumers](../examples/Probes/Program.cs) are the authoritative source examples. They show a plain formatting capability, a caller-bound probe registry, typed events, early revocation, provider disappearance and consumer reactivation.

```console
dotnet run --project examples/Probes/Probes.csproj
dotnet publish examples/Probes/Probes.csproj -c Release -r win-x64 -p:PublishAot=true
```

Use the runtime identifier for the machine that will build and run the executable. A publish command is not evidence that it ran successfully: completed results belong in [validation](validation.md). The [V1](../tests/fixtures/ProbeV1/ProbeV1.csproj) and [V2](../tests/fixtures/ProbeV2/ProbeV2.csproj) DLL fixtures compile the same provider source behind an [explicit CLR entry](../tests/fixtures/ProbeEntry.cs).

### Run the same provider through CLR DLL replacement

The [CLR console host](../examples/Probes.Clr/Program.cs) references only the shared contracts and `Cordis.Clr` (with Composition transitively available). It loads the same provider source from the two fixture bundles; it has no static reference to `Probes.Plugin` or either fixture. From the repository root:

```console
dotnet build tests/fixtures/ProbeV1/ProbeV1.csproj -c Release
dotnet build tests/fixtures/ProbeV2/ProbeV2.csproj -c Release
dotnet run --project examples/Probes.Clr/Probes.Clr.csproj -c Release -- tests/fixtures/ProbeV1/bin/Release/net10.0 tests/fixtures/ProbeV2/bin/Release/net10.0
```

The two arguments are bundle directories containing `ProbePlugin.dll` and its dependencies. The host passes its exact contract assembly to `ClrModuleResolver`, loads V1, activates two consumers, replaces V1 with V2, and verifies both consumers reacquire caller-bound views. Disposing one consumer removes only its contribution; disposing the other leaves an empty registry before the root stops. The explicit `ClrModuleDefinition` uses `Cordis.ProbeFixture.Entry`; the other fixture entry types exist for negative tests.

After lifecycle cleanup, the host requests unload and reports collection and shadow deletion separately. It never forces GC. Exit code zero means the contribution checks, lifecycle cleanup and unload requests succeeded; `collected=False` or `shadow deleted=False` may legitimately remain at exit. Pending temporary shadow directories are printed for later cleanup. The host performs no runtime package restore and needs no source checkout when deployed: publish it normally and pass two prepared bundle directories. This CLR route requires an ordinary runtime and does not support Native AOT. The [deployment test](../tests/Cordis.Platform.Tests/ProbeDeploymentTests.cs) separately verifies eventual collection with test-only forced GC.

## Choose the smallest useful authoring form

| Task | Existing form | Optional convenience | When the simpler form is enough |
|---|---|---|---|
| Read a declared capability | `(IProbeRegistry)ctx.Reflect.Read("probes")!` | `ctx.Reflect.Read<IProbeRegistry>("probes")`; the contract's C# extension property exposes `ctx.Probes` | One read may need only the cast. The generic overload centralizes the cast; a shared extension centralizes the name. |
| Publish or observe one payload | String name and `object?[]` arguments | `EventKey<ProbeChanged>` and typed `On`/dispatch overloads | Existing multi-argument protocols keep their raw layout. A key pays for itself across publishers and listeners. |
| Validate configuration | `Plugin<T>.Config = raw => ...` | `ConfigBinding.FromJsonTypeInfo(metadata, validate: rules)` | A scalar or non-data contract often needs only a short direct validator. Metadata is useful for nested data contracts. |
| Accept external notifications | Effect-owned subscription, registration flag, `RunAsync`, error observation | `ctx.SubscribeExternal(subscribe, callback, reportError)` | A source already adapted to Cordis may need no extra wrapper. The helper removes repeated ownership and stale-callback checks. |
| Start an application | Explicit `Context` + `Loader` + `MountAsync` | `ApplicationBoot.BootGenericAsync` | Keep explicit assembly when the application already owns these objects. `BootAsync` remains the DSH compatibility entry. |
| Read embedded patches | Open manifest stream, read text, parse entries | `PatchResources.Read(assembly, resourceName)` | `ReadText` keeps callers that need text simple; neither helper activates anything. |

Attributes and custom generators remain design options, not blanket exclusions. This implementation uses ordinary C# extension properties and handwritten `CreateView` methods because the remaining service-view code is small and its ownership is visible. A custom attribute/generator could reduce repeated declarations across a larger contract surface, but would add a generator package, diagnostics, generated-code debugging, versioning and compatibility checks. Adopt it when measured repetition or errors justify those costs. The existing System.Text.Json generator already supplies a useful, bounded metadata contract; it does not generate Cordis lifecycles or guarantee CLR unloadability.

## Consumer plugins

Declare service names in `Inject`, then obtain the capability during `Apply`. The example's `ctx.Probes` property delegates to `Reflect.Read<IProbeRegistry>` and preserves injection inheritance, interception, realm lookup and caller tracing. It does not add an injection declaration. `ctx.Get<T>(name)` remains the optional lookup route with its existing lookup rules; it is not a substitute for property-style dependency checking.

The generic read has the same cast behavior as the handwritten cast: incompatible objects fail, and a null reference can still be null despite nullable annotations. Read a view again on each activation. Retaining a view does not route it to a replacement provider and can retain a collectible plugin assembly.

An `EventKey<T>` describes exactly one payload slot. Keys with the same name share the raw listener table; raw publishers and typed listeners interoperate. There is no implicit tuple/DTO conversion for existing multi-argument events. Wrong raw argument counts or incompatible payload types fail explicitly. Nullable reference annotations cannot enforce non-null payloads at runtime.

`On` and `Once` preserve `EventOptions`, filtering, receiver, ordering and effect ownership. Dispatch accepts `receiver` separately from the payload. Action observers return `Undefined.Value`. Object-result listeners preserve `null`, `false`, `Undefined.Value`, zero and empty strings. Task overloads retain the existing raw dispatcher's result rules. `ParallelAsync` awaits tasks without returning their results; `SerialAsync` extracts the result of `Task<object?>`, but awaits other `Task<T>` values as observers and substitutes `Undefined.Value`. A method group returning `Task<int>`, `Task<string>` or `Task<bool>` compiles when passed directly to typed `On` or `Once`; its result is therefore discarded during serial dispatch, just as on the raw path. This is an inherited raw limitation, not a typed-only regression.

| Listener shape, where `Answer` returns `Task<int>` | `SerialAsync` behavior |
|---|---|
| `ctx.On(key, Answer)` (or `Once`) | Awaits the task, substitutes `Undefined.Value`, then invokes the next listener. |
| `ctx.On(key, (evt, value) => (object?)Answer(evt, value))` | Casting the task object does not adapt its result; behavior is the same as above. |
| `ctx.On(key, async (evt, value) => (object?)await Answer(evt, value))` | Produces `Task<object?>`; the awaited integer is preserved and stops serial dispatch, including when it is zero. |

Use the same explicit `async (evt, value) => (object?)await Answer(evt, value)` adapter with `Once` and with `Task<string>` or `Task<bool>`. For raw listeners, return a `Task<object?>` from an equivalent async helper. After adaptation, `null`, `false` and `Undefined.Value` let serial dispatch continue; zero, an empty string and `true` stop it. The dispatcher does not reflect over arbitrary `Task.Result` properties. A null task reference remains the raw null result.

Synchronous `Emit`, `Bail` and `Waterfall` do not await tasks. `Emit` invokes subsequent listeners immediately; `Bail` and a listener that returns without calling `next` in `Waterfall` return the original task object, without wrapping it or extracting its result. `Waterfall` still requires explicit `next`; an observer does not continue automatically. Use awaited dispatch to observe asynchronous errors.

## Service providers and configuration

The example's `IProbeFormatter` is a plain shared object supplied with `ctx.Provide`. It needs no service base class, proxy or state wrapper. `IProbeRegistry.Register` creates caller-owned resources, so its implementation uses the existing `Service<TState>` model: provider and views share `State`, each view carries its caller `Context`, and `CreateView` calls the view constructor rather than registering the provider again.

Automatic cleanup comes from `Context.Effect` inside `Register`, not from the interface return type. The returned disposer also permits early revocation. Disposing one consumer revokes its own contribution while another consumer and the provider remain usable. A shared ordinary object can instead take an explicit owner when that better expresses its API.

`ConfigBinding.FromJsonTypeInfo` returns a delegate for `Plugin<T>.Config`. Supply explicit `JsonTypeInfo<T>`, normally generated by System.Text.Json, and optional domain rules that return issue strings. Binding failures include a data path and a `binding` prefix; returned business-rule issues have a `validation` prefix. Exceptions thrown by business rules retain their original identity. Successful deserialization alone is not domain validation.

The supported input domain is data: strings, booleans, standard integers, decimal, finite floating-point values, string/object dictionaries, `IList` sequences and `JsonElement`, with nested nulls and a depth limit of 64. `JsonElement` numbers must fit a finite double. Arbitrary objects, delegates, cycles, undefined nested values and unevaluated expressions are rejected. Shared acyclic child data is allowed. Loader expression evaluation occurs before binding, and only an expression's supported data result can use this adapter. The adapter does not rewrite the raw entry or `Fiber.RawConfig`.

Missing root configuration (`Undefined.Value`) and explicit root null are rejected with different messages. A plugin wanting a default must explicitly wrap the binder or supply a direct `Config` delegate; the helper never silently substitutes `{}`. Metadata governs member defaults, required members, constructors, enum conversion, naming and unknown fields. In particular, generated metadata can replace an absent init-only initializer with the CLR default. The example uses a record constructor's optional parameter to express its missing-field default. Missing fields, null, zero, false and empty strings remain distinct inputs; domain rules decide which are allowed.

The returned binder retains its metadata and validator. For collectible plugins, keep that delegate with the plugin and release externally retained binders, converters and errors. There is no global metadata cache. Direct `Plugin<T>.Config` remains appropriate for non-data configuration.

For a complete typed data object with live fields, prefer `ConfigObject<T>.Create(validator).Field(...).Build()` from Composition. It combines explicit keys, descriptions and projections into the existing configuration contract; it does not infer POCO members or change validation/defaults. Supply `ConfigBinding.FromJsonTypeInfo` as that validator when generated metadata is suitable, and keep its naming/default rules aligned with the field declarations. Every ordinary and persisted field must be declared. See the [configuration example](configuration-description.md) and [actual manual/composed consumer](../examples/Probes/ConfigurationScenario.cs). Special conversions and nested live paths continue to use `ConfigSchema<T>` directly. This recommendation requires `0.2.0-alpha.3` or later; it is not in the published `0.2.0-alpha.1` batch.

## External callbacks and ownership

[`SubscribeExternal`](../src/Cordis.Extensions/ExternalCallbacks.cs) adapts a source that accepts `Action<T>` and returns `IDisposable`; call it inside a Cordis callback or `RunAsync`. It registers effect ownership before subscribing, so synchronous notification and reentrant disposal during subscription are covered. Each registration has its own validity flag. Teardown closes admission before unsubscribing, and execution checks the flag again after entering `RunAsync`. An old queued callback cannot become valid when the same Fiber activates again.

The callback returns `Task`; its eventual failure goes to the required `reportError` sink even after root shutdown. Cancellation is separate and reaches only the optional cancellation sink. Sinks can run on an external thread and must not throw. Already-started work may complete after unsubscription: this helper neither cancels nor drains it. A source that throws during subscription must release any registration it created. Keeping the supplied callback externally can retain plugin objects.

Execution-domain entry, stopping new calls, requesting cancellation and draining in-flight work are separate responsibilities. An application registry with execution leases can coexist with Cordis services: Cordis controls visibility and activation; the lease registry controls admission and completion of already-started calls. Avoid maintaining duplicate contribution facts without an explicit owner and ordering rule. Domain transactions, remote permissions, UI scope and lease/drain policy belong to the application SDK. Do not replace a lease borrow with a plain service lookup merely because both return the same interface.

## Host assembly, resources and diagnosis

`BootGenericAsync` prepares a context, mounts configuration, waits for current work, audits failures and cleans up failed startup. It supplies no DSH home service or default required set. Pending dependencies are legal unless the application explicitly marks the present entry as required. Required names do not install absent modules, demand absent entries or wait indefinitely for future providers. Application readiness remains explicit.

`BootAsync` retains its existing signature, `DshRequiredEntries` defaults and `dshHomePath` service. Both entries share the same mechanism. The caller owns the returned context and retains ownership of the resolver. Existing `ProfileSession`, HMR and [Generic Host integration](usage.md) remain available; borrowed container services are disposed by their original container.

Use an explicit assembly and manifest resource name with `PatchResources.Read`; fix that name with `EmbeddedResource LogicalName` in the project. It opens the deployed assembly resource on every call, closes the stream and parses through `ConfigurationFile`. It never searches a source checkout, package cache or assembly inventory, and caches neither assemblies nor parsed rows. Missing resources identify the assembly/name; malformed resources preserve the parser exception. Pass rows to `EntryPatches.Apply`, boot or the existing reconciliation path. Resource reading performs no module-name rebasing or implicit activation; patch application retains the existing replacement/merge rules.

`AuditAsync` reports module-resolution failures, missing dependencies and activation errors. `Fiber.FailurePhase` distinguishes configuration from Apply by the operation that actually failed, rather than guessing from exception type. Disabled-expression diagnostics retain their own phase. Original exceptions remain available for short-lived debugging. Convert each `EntryDiagnostic` with `ToSnapshot()` before retaining a report long term: the snapshot contains names, state, copied dependency names and error text. Do not retain the original `StartupException`, log argument objects or other plugin references merely because a snapshot also exists. Callback errors use the callback sink; CLR unload status uses `ClrUnloadObservation`.

## Application configuration and client consumption

For the complete plugin lifecycle, start with [ManagedPlugin](../examples/ManagedPlugin/README.md), then follow [ManagedApplication](../examples/ManagedApplication/README.md) to build a local feed, install the plugin, edit its configuration and remove it. The [CLI guide](../tools/Cordis.Cli/README.md) uses the same host operations. The Probes example below demonstrates a smaller client consumption boundary.

Use the session's existing `ConfigurationOperations`. Read a fresh configuration revision, then submit ordered SET/unset paths through `MutateConfigurationAsync` or the selected `MutateSettingsAsync`. The library validates the complete final candidate and saves once; object unset restores inheritance, while array unset removes an element. Ordinary fields keep normal restart behavior; `liveOnly` additionally requires a captured live boundary and compatible ordinary effective values. Handle `Saved`, `Applied` and recovery diagnostics separately. The original Config delegate and advanced `ConfigSchema<T>` authoring remain available.

Declare application metadata with `descriptor.WithMetadata(new ConfigurationMetadata { ... })`. Renderer roles, localized descriptions, badges, hidden/disabled/collapse, links/comments, extra plain data and explicit min/max/step/pattern/loose hints travel through immutable Core annotations. They describe the existing validator and do not add validation behavior. Nested fixed live paths use `ConfigSchema<T>.WithVolatile(path, projection)`; Settings omits ordinary siblings and redacts secret values into presence-only sidecars. Every edit checks its visible declared path; replacing an ancestor containing unreadable secret/hidden fields is refused. See the [complete declaration, settings and discovery examples](configuration-description.md).

The two exports are separate contracts: `ToSchemastery` produces the browser form's uid/refs envelope, while `ToJsonSchema` produces a JSON Schema 2020-12 declaration with explicit runtime limitations. `ReadSettingsSchemasAsync` selects live fields and removes hidden properties and defaults; the full configuration export requires separate host authorization. For discovery before activation, `ConfigurationSchemaDiscovery.DiscoverAsync` uses an explicit resolver and reads native group/include sources without starting a Context or writing fallback include files. Import/capture are trusted code; validators, Apply, raw expressions and lazy builders remain unexecuted.

The example's primitive live view requires explicit host selection and hidden fields. Do not expose a raw configuration read as a browser settings response. Its transport is loopback-only demonstration code; deployed hosts provide authentication, authorization, request limits and their policy. The retained DSH form model stages drafts and saves through the real host. SET-only does not support inherited reset or splitting multi-field saves into sequential writes. Build and run from the repository root:

```console
npm ci --prefix reference --ignore-scripts
node reference/node_modules/typescript/bin/tsc -p examples/Probes/client/tsconfig.json
node scripts/build-application-client.mjs artifacts/application-client
dotnet run --project examples/Probes/Probes.csproj -c Release -- --application-host http://127.0.0.1:17639/ artifacts/application-client
node scripts/application-client-consumer.mjs http://127.0.0.1:17639 artifacts/application-client
```

The final command runs in another terminal and mutates only the generated example artifact to verify a new content generation. Stop the demonstration host with POST `/stop`. The artifact layout uses the upstream web/client declaration, while immutable ESM delivery is a native adaptation. The example client remains a bounded consumption example; declaration export and discovery are separate library APIs described above. The existing authoring gate repeats this real client chain under source and independently consumed packages, including Native AOT when requested.

## Deployment and verification boundaries

The candidate admission and coordinated metadata-save APIs below require `0.2.0-alpha.4` or later. Alpha.4 is currently a release candidate; the published alpha.3 packages do not provide this contract.

For package and bundle-selection policy, set `session.ConfigurationOperations.AdmitProfileAsync`. The callback receives `ProfileCandidate.ManifestJson` (the exact proposed saved text), `ConfigurationJson` (the effective raw root rows, without evaluating expressions), and a detached `Composition` view with source layers and skipped selections. During installation, `Package` also exposes the prepared directory and planned publication destination so policy can inspect artifacts before Publish. It is null for other operations. Throw to refuse. Validate this library candidate instead of re-reading the profile and predicting another candidate in `IProfilePackageToolchain.PublishAsync`. Do not re-enter configuration operations from admission. Keep product-owned policy inputs stable until the operation settles; this callback does not freeze external SDK or application state. `ProfileSession` supplies candidate-aware application automatically.

If you customize `ReconcileAsync`, assign `ReconcileCandidateAsync` afterwards to opt into candidate application for that customization. Replacing only the legacy callback keeps legacy behavior without admission; it does not silently approve an unbound reconciliation when admission is enabled. Dependency-removal admission runs before physical deletion; declining it may leave the separately approved deselection saved.

Read metadata drafts with `ReadProfileAsync`, retain the returned `Revision`, and submit the edited JSON with `SaveProfileMetadataAsync(text, revision)`. A save waits behind installation and refuses a stale revision with `profile-conflict`; preserve the user's draft and let the product decide the next request. Dependencies and `dsh` remain owned by the existing package, selection and compatibility operations. These methods do not expose a new unauthenticated transport, and their waiting/conflict experience needs the product's acceptance.

Toolchains that relocate preparation output set `PreparedPackage.PublicationDirectory` before returning from Prepare. Null declares that the directory stays in place. Wrappers must preserve this value. Publish may move the same relative file contents to that directory and add exactly that package's mapping; an unrelated source or mapping change is a conflict. Retain `PackageChange`'s separate installed/selected/application/residual fields, especially if publication completed before rejection. The profile lock coordinates cooperating writers, not arbitrary editor saves; see [compatibility](compatibility.md) for the commit-window boundary.

| Path | Code and configuration | Boundary |
|---|---|---|
| Static registration, ordinary JIT | Shared contracts and explicitly registered modules; patch/profile changes remain dynamic | New implementation code requires changing the deployed application. |
| Static registration, Native AOT | The same applicable provider and host source, with explicit serialization metadata | New managed DLL code requires republishing; there is no runtime assembly scanning or Emit requirement. |
| CLR DLL loading | Explicit `ClrModuleDefinition` and shared contract assemblies; independent V1/V2 implementations | Ordinary runtime only; host references contracts, not the unloadable implementation. |

For CLR, pass the host's exact contract assemblies as `sharedContracts` to `ClrModuleResolver`. Strongly typed interfaces are still strong references. Release old views, callbacks, binders, asynchronous work and exceptions. Lifecycle end, new-version activation, unload request, GC collection and shadow deletion are separate observations; production does not force GC. Code replacement rollback and normal configuration-update failure retain their separate existing policies. `!!js` remains the optional Jint path, with no Native AOT claim.

Authoring tests are .NET adaptation evidence, not additional completed upstream assertion reviews. The focused coverage lives in [typed events](../tests/Cordis.Core.Tests/TypedEventTests.cs), [configuration binding](../tests/Cordis.Composition.Tests/ConfigBindingTests.cs) and [boot/resources](../tests/Cordis.Composition.Tests/AuthoringBootTests.cs), alongside existing lifecycle, hosting and CLR tests. The authoring verification command exercises the example, negative compilation and deployment paths; the full gate remains required for runtime/package changes:

```console
python scripts/verify-authoring.py
python scripts/verify.py
```

Consult [validation](validation.md) for executed environments and limitations, and [compatibility](compatibility.md) for the fixed DSH target and evidence vocabulary. A listed test or deployment command is not by itself a claim that its latest run completed.

## Module exports and generated Remote contracts, 2026-10-09

This source continuation belongs to the existing application infrastructure scope; see the [scope reconciliation](development.md#application-infrastructure-scope-reconciliation-2026-10-09). It does not announce a published package batch. The following contracts require a package batch built from this continuation.

### Multiple CLR entries in one author package

Keep the existing single-entry `assembly` and `entryType` fields in `cordis.plugin.json`. An optional `exports` object declares additional explicit subpaths in that assembly. For the [independent author fixture](../tests/fixtures/ClrMultiEntry/Plugin.cs), the declaration is:

```json
{
  "assembly": "IndependentMultiEntry.dll",
  "entryType": "IndependentMultiEntry.First",
  "exports": {
    "./second": "IndependentMultiEntry.Second"
  }
}
```

`DotnetPluginToolchain` registers the package root as `nuget:independentmultientry` and the additional entry as `nuget:independentmultientry/second`. Single-entry authors need no empty export table. Static hosts can continue to register exact requests through their existing resolvers. Module selection and Cordis service `Provide` remain separate operations; neither a Core `Exports` member nor an application `ApiCatalog` is required.

`ClrModuleResolver` loads exports with the same normalized bundle directory in one shadow copy and collectible assembly load context. Aliases of the same assembly/entry type reuse the plugin instance; different entry types retain separate plugins. Loader entries retain their own raw configuration, Fiber activation and effect cleanup. Removing one resolver lease preserves other exports; the final lease requests bundle unload. Stop the relevant Fibers before removing their resolver mappings. Unload request, collection and shadow deletion remain separate observations.

The resolver shares Core, Clr and Composition contract assemblies with the host by default. Pass other exact host contract assemblies through `sharedContracts`; private plugin dependencies stay in the bundle. This includes the identity required for generated `ITypertRemoteService` bindings and `IClrTypertModule` contributions.

Explicit resolver registrations can select different main assemblies under one bundle directory. Each main assembly contributes its own dependency resolver and directory before loading. Private managed and native resolution queries all registered roots. The same path or byte-identical copies reuse one dependency; different binaries for the same dependency name are refused, including when a new entry would otherwise reuse an already loaded assembly. This is a conservative native bundle rule, not an ABI or assembly-version compatibility algorithm. Native libraries not located by those resolvers retain CLR/OS lookup behavior. The [multi-assembly fixture](../tests/fixtures/ClrMultiAssembly/Consumer.cs) checks both entry orders and actual private native calls.

Before admitting a dependency root, the resolver reads managed references and P/Invoke declarations, recursively follows resolver-located managed images, and checks their candidate paths without executing a factory. This covers declared, locatable conflicts even when the original entry has not yet called its dependency. Already selected dependencies are checked too. Arbitrary dynamic loads and factory side effects do not become a rollback transaction.

Replace a bundle containing distinct exports with the dictionary overload of `ClrModuleResolver.ReplaceAsync`, supplying every registered request, including unloaded exports and aliases. Its callback can use `Loader.ReplacePluginsAsync` to transfer existing raw configurations and switch the affected Fibers together. The single-entry overload remains available for one export and its aliases. Candidate preparation and route publication preserve a single bundle generation; callback effects and product state are not a transaction. Activation failure uses the existing Loader recovery path, and a settled Pending Fiber remains legal.

### Declare and generate native Remote contracts

`Cordis.NET.Composition` carries its Roslyn analyzer in the NuGet analyzer directory. An author consuming the package can declare a public, top-level, non-generic partial class with `RemoteService` and explicit `RemoteMethod` attributes. Supply a source-generated `JsonSerializerContext` for boundary types. This reduced declaration follows the [independent Remote author](../tests/fixtures/TypertConsumer/Author.cs):

```csharp
using System.Text.Json.Serialization;
using Cordis.Composition;

public sealed record EchoRequest(string Text, int Count);
public sealed record EchoReply(string Text, int Count);

[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoReply))]
public partial class RemoteJson : JsonSerializerContext;

[RemoteService("sample:remote", typeof(RemoteJson), Namespace = "sample")]
public partial class EchoService
{
    [RemoteMethod]
    public Task<EchoReply> Echo(EchoRequest request) =>
        Task.FromResult(new EchoReply(request.Text, request.Count));
}
```

The generator emits `EchoServiceTypert.Contribution("IndependentRemote")`, descriptors and direct typed invocation bindings. The plugin still provides its actual `EchoService` under `sample:remote` using `Context.Provide`. Contract registration does not create or activate that service. JSON naming, member nullability and required constructor fields follow the supplied metadata; the two `Respect...` settings above are author choices, not implicit generator defaults. Declare metadata for every ordinary argument and result type. Unsupported declarations fail compilation; unsupported client schema shapes fail client generation.

Root nullable reference annotations, such as a `string?` argument or `Task<string?>` result, are supplied by Roslyn through `TypertCodec.CreateNullable`: runtime JSON type metadata erases these annotations. The codec adds a null branch and relocates local schema references, preserving non-null recursive children. Generated declarations therefore expose `string | null` for these boundaries. This does not claim complete nested generic nullability analysis or add result-schema validation to the Gateway.

`TypertCodec` prepares and checks its schema lazily when `Schema` or `Decode` is first used, including nullable input. Decode validates the supported native subset and then deserializes. Encode serializes with the supplied metadata without preparing or validating the result schema. Unsupported schema features fail explicitly. Client projection preserves `prefixItems` as readonly tuples, including bounded optional prefix elements, nested local references, and unbounded typed or unknown tails. A minimum beyond the prefix or a finite tail length limit is rejected. The [tuple fixture](../tests/fixtures/TypertConsumer/TupleContract.cs) supplies an explicit schema to a generated binding and distinguishes the real closed-tuple HTTP call from codec/projection-only variants. It does not demonstrate Roslyn inference of CLR tuple types or the complete source type graph.

Remote methods currently use required ordinary parameters and `Task<T>`, or explicit `RemoteMethod(Stream = true)` with `IAsyncEnumerable<T>`. A final `CancellationToken` parameter may be included for cooperative cancellation, without a default value. Explicit Context and object lookup declarations have host-owned registration APIs; the complete scoped/lookup example is in the same fixture.

### Register, call and withdraw contracts

Create a `TypertRegistry` and `TypertGateway` in the existing Cordis context. `TypertLoader.StartAsync` discovers contributions for live Loader entries through an explicit artifact resolver; it does not scan assemblies. A static author uses `StaticTypertArtifactResolver.Register` with its generated contribution factory. A dynamic entry can implement `IClrTypertModule.CreateTypertContribution()` alongside `IClrPluginModule.CreatePlugin()`; `ClrModuleResolver` then supplies the artifacts from the same loaded bundle and factory identity. See the [static consumer](../tests/fixtures/TypertConsumer/Consumer.cs) and [multi-entry consumer](../tests/fixtures/ClrMultiEntry/Consumer.cs).

The Typert loader's owner Fiber owns registrations and its activation-lifetime import cache. Multiple live entries of one exact module request share a contribution; removing the last matching entry withdraws it unless that request was explicitly configured. Registry registration validates the contribution before publishing it and rejects conflicts. Gateway calls resolve the live Cordis provider and check registration validity; withdrawing a definition invalidates retained invocations.

The native Gateway also checks provider generations after successful resolution and before encoding successful business results or stream items. Withdrawing a Service, lookup or Context provider does not actively abort work already running. A withdrawn provider's successful result is refused even if the generated definition remains active. The [independent lifetime cases](../tests/fixtures/TypertConsumer/LifetimeCases.cs) hold each provider at its actual asynchronous boundary, replace it, and verify both rejection of old success and admission through the current provider. These checks are a native validity adaptation, not product retirement or draining policy.

For dynamic bundle replacement, stop the Typert loader owner Fiber before switching provider Fibers. After the resolver commits the new bundle, start a fresh owner Fiber and `TypertLoader`; after failed replacement and old-provider recovery, do the same against the old bundle. This releases old CLR `JsonTypeInfo` and generated bindings instead of reusing them with new CLR types. The independent multi-entry consumer demonstrates this ordering and old invocation rejection. Retained contributions, clients, service objects or errors can still keep collectible code alive; release them when their ownership ends.

`MapCordisRemote` maps a host-authorized `TypertGateway` to native unary JSON and downlink NDJSON routes. The host supplies the authorization callback. Request abort and generated-client `AbortSignal` convey cancellation; `byte[]` results use JSON base64. Host unary dispatch passes the signal to the binding and normalizes a business failure while cancelled; it does not preempt successful business execution. Downlink reads race cancellation, then cleanup waits for a pending native read before disposing the enumerator in its invocation Context. A body or cleanup that never settles can prevent termination, as in the pinned stream cleanup boundary. This carrier does not promise the complete pinned Typert wire protocol.

Dispose or drain active Gateway iterators before closing their Cordis root. Iterator cleanup re-enters that execution domain; a closed root cannot run it.

Generate `.mjs` and `.d.mts` artifacts with `TypertArtifacts.GenerateClient(contribution)`. The result uses the same descriptors and schemas as the host. Generated clients expose typed calls and the Remote result/error envelope:

```typescript
import { createRemote, mountRemote } from "./remote.mjs";

const client = createRemote("/remote");
const echo = client["sample/Echo"];
const result = await echo({
  request: { Text: "hello", Count: 1 },
});
client.dispose();

const mounted = await mountRemote(ctx, "/remote");
await mounted.dispose();
```

Here `ctx` is the client Cordis owner. Await `mountRemote`: it registers owner cleanup and contributes methods to the shared root `remote.<namespace>` service. Contributions with disjoint methods can share that namespace; duplicate methods or an unrelated existing service are refused. Withdrawal removes only that contribution's methods, and final withdrawal removes the namespace service. Disposing either client stops admission and aborts its active fetches.

### Consumer evidence and remaining scope

The [multi-entry gate](../scripts/verify-clr-multi-entry.py) independently packs an author package, consumes it through `PackageReference`, installs its root/subpath entries and exercises shared identity, separate configurations, failed and successful replacement, withdrawal, generated TypeScript calls over real HTTP and invalid arguments. The separate [Remote gate](../scripts/verify-typert.py) covers the native Remote author chain. Use a fresh local package batch and the required Node/TypeScript dependencies. Platform acceptance remains governed by the completed results in [validation](validation.md); these examples alone do not establish Windows/Linux or Native AOT closure. Dynamic CLR loading requires the ordinary runtime.

The remaining source type graph, rich Context/owned-value graph, Peer/uplink/event remotes and binary attachment protocol remain open scope. Migration of existing PluginManager, Settings/configuration and client management consumers to generated Typert contracts remains open. Existing handwritten `MapCordisService` endpoints are still usable, but do not close those gaps. Product replacement admission, permissions and business retirement/draining policies remain product responsibilities.
