# Authoring plugins and applications

[中文](authoring.zh.md)

These optional .NET authoring helpers use the existing Cordis runtime. A package makes code available; module registration maps names to code; patch/profile entries select instances; `Inject` determines when a plugin can activate. Service identity remains its name and realm. None of these helpers adds a second registry or lifecycle.

## Start with the runnable example

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

## External callbacks and ownership

[`SubscribeExternal`](../src/Cordis.Extensions/ExternalCallbacks.cs) adapts a source that accepts `Action<T>` and returns `IDisposable`; call it inside a Cordis callback or `RunAsync`. It registers effect ownership before subscribing, so synchronous notification and reentrant disposal during subscription are covered. Each registration has its own validity flag. Teardown closes admission before unsubscribing, and execution checks the flag again after entering `RunAsync`. An old queued callback cannot become valid when the same Fiber activates again.

The callback returns `Task`; its eventual failure goes to the required `reportError` sink even after root shutdown. Cancellation is separate and reaches only the optional cancellation sink. Sinks can run on an external thread and must not throw. Already-started work may complete after unsubscription: this helper neither cancels nor drains it. A source that throws during subscription must release any registration it created. Keeping the supplied callback externally can retain plugin objects.

Execution-domain entry, stopping new calls, requesting cancellation and draining in-flight work are separate responsibilities. An application registry with execution leases can coexist with Cordis services: Cordis controls visibility and activation; the lease registry controls admission and completion of already-started calls. Avoid maintaining duplicate contribution facts without an explicit owner and ordering rule. Domain transactions, remote permissions, UI scope and lease/drain policy belong to the application SDK. Do not replace a lease borrow with a plain service lookup merely because both return the same interface.

## Host assembly, resources and diagnosis

`BootGenericAsync` prepares a context, mounts configuration, waits for current work, audits failures and cleans up failed startup. It supplies no DSH home service or default required set. Pending dependencies are legal unless the application explicitly marks the present entry as required. Required names do not install absent modules, demand absent entries or wait indefinitely for future providers. Application readiness remains explicit.

`BootAsync` retains its existing signature, `DshRequiredEntries` defaults and `dshHomePath` service. Both entries share the same mechanism. The caller owns the returned context and retains ownership of the resolver. Existing `ProfileSession`, HMR and [Generic Host integration](usage.md) remain available; borrowed container services are disposed by their original container.

Use an explicit assembly and manifest resource name with `PatchResources.Read`; fix that name with `EmbeddedResource LogicalName` in the project. It opens the deployed assembly resource on every call, closes the stream and parses through `ConfigurationFile`. It never searches a source checkout, package cache or assembly inventory, and caches neither assemblies nor parsed rows. Missing resources identify the assembly/name; malformed resources preserve the parser exception. Pass rows to `EntryPatches.Apply`, boot or the existing reconciliation path. Resource reading performs no module-name rebasing or implicit activation; patch application retains the existing replacement/merge rules.

`AuditAsync` reports module-resolution failures, missing dependencies and activation errors. `Fiber.FailurePhase` distinguishes configuration from Apply by the operation that actually failed, rather than guessing from exception type. Disabled-expression diagnostics retain their own phase. Original exceptions remain available for short-lived debugging. Convert each `EntryDiagnostic` with `ToSnapshot()` before retaining a report long term: the snapshot contains names, state, copied dependency names and error text. Do not retain the original `StartupException`, log argument objects or other plugin references merely because a snapshot also exists. Callback errors use the callback sink; CLR unload status uses `ClrUnloadObservation`.

## Deployment and verification boundaries

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
