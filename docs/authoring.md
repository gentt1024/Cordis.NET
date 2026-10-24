# Authoring plugins and applications

[中文](authoring.zh.md)

These optional .NET authoring helpers use the existing Cordis runtime. A package makes code available; module registration maps names to code; patch/profile entries select instances; `Inject` determines when a plugin can activate. Service identity remains its name and realm. None of these helpers adds a second registry or lifecycle.

## Candidate browser release and assets, 2026-10-23

Pass `release: new ClientModuleRelease("application-version", "build-commit")` when capturing a `ClientModuleCatalog`. Use the same application identity for the initial graph and subsequent management responses. Omit it only when the application intentionally has no release identity. A graph revision change within one release permits normal plugin replacement; a release change requires a page reload before that release's graph can run.

Subscribe to `modules.releaseChange` and read its `getSnapshot()` to display a reload notice. `modules.dismissRelease()` dismisses the notice only. Keep the normal `management.close()` and `modules.dispose()` ownership sequence. The SDK does not reload the page automatically.

Author packages can import `.svg`, `.png` and `.webp` files as data URL strings. Image-only changes participate in `cordis-client-build --watch` and produce a new immutable generation. Declare inline slots with `inline: true`; application renderers consume that hint and may pass a member name to keyed standard sources. These additions do not install a React renderer.

## Candidate Remote metadata, 2026-10-23

Use the generated Host `Info(package)` factory or the independent .NET caller's static `Info` to inspect Remote methods without opening a connection. `CreateInfo(package)` on an independent caller selects an explicit deployment package identity; its default Info uses the saved model's assembly identity. `Methods` contains direct endpoints; `Descriptors` also contains Context receivers. For registered facts, use `TypertRegistry.GetLocalInfo(package)` or `GetRemoteInfo(package)`.

For a selected descriptor, call `Input()` or `Input(strict: true)` and parse named wire arguments with `Parse(JsonElement)`. Read `JsonSchema` only when a tool consumer needs a projection, and handle `JsonSchemaError` when it is unavailable. An unavailable projection does not imply an unusable parser. `ResultValue` exposes result metadata separately from the input view. Individual parameters also expose `Input(strict)`; `UplinkValue` exposes stream-item input metadata, including on Context-only descriptors. `Create()` returns a cached parser delegate independently of tool projection. Keep optional absence distinct from explicit null.

Pass parsed arguments to the descriptor's `Invoke(client, arguments, cancellationToken)`. Its `object` result is the existing generated method's task, including the task returning a duplex handle. The generated client implements `ITypertNamedRemote`; this metadata route preserves its connection and lifetime checks. Continue using ordinary typed methods when compile-time argument/result types are known. Browser generated modules expose `info`, named `methods`, `input({ strict: true })` and `invoke(remote, arguments, signal)`.


## Candidate event access and permission clients, 2026-10-23

Register the standard follow contribution alongside the Host service:

```csharp
var access = new TypertRemoteAccess(ctx);
ctx.Provide("remoteAccess", access);
access.RegisterRemote(ctx, registry);
```

Use roles containing `event:<selected-event-name>` to allow non-owner notifications. Add `ResourceReaders` to the selected event definition when the notification names resources; cast each positional reader argument to `JsonElement`. Event readers must return nonempty IDs; unlike ordinary call readers, null does not remove a kind. Owner subscriptions do not need grants. Bind a session's Peer lifetime to revocation; changing policies alone does not retract queued notifications.

Call `Invalidate()` after changing bindings, expiry or conditional answers, or pass the complete affected principal to wake only matching followers. `SnapshotFor(principal)` supplies an explicit initial baseline for that trusted identity. A snapshot is a UI hint without call arguments, not permission to bypass Gateway authorization. `remoteAccess/follow` needs no grant and always observes the calling Peer.

For .NET, construct `TypertRemoteAccessClient` with the owning Context and borrowed Connection. Read `State` permission sources and run `RunAsync` for each transport generation; the application owns reconnection. For browser modules, import `mountAccess` from `@cordis-net/client-modules/access`, then call `mountAccess({ context, connection })`. Subscribe through `source(kind, id, action)` or `global(action)`. Optional `initial` must be a Host-selected baseline. Dispose the mount before releasing its Context/Connection; a new mount without a baseline starts false. Browser observables can feed the application's existing UI framework.

## Candidate resource policies, 2026-10-23

Inside the Host execution domain, provide one `TypertRemoteAccess` as `remoteAccess` and register a policy in its owner's Fiber:

```csharp
var access = new TypertRemoteAccess(ctx);
ctx.Provide("remoteAccess", access);
var read = new TypertAccessRole("document reader", "document", ["documents/read"]);
access.RegisterPolicy(ctx, "guest", new(principal =>
    [new(read, TypertAccessResource.Create("document", "document-1"))]));
```

Read current business grants in the policy callback; do not cache decisions across calls. A condition receives the action, resource and decoded named arguments for a concrete call. Its arguments are null for `Allows`. Return `TypertAccessDecision.Allowed` or `new TypertAccessDecision.Deny("reason")`. `Explain` only describes an existing refusal. Call `access.Allows(itemResource)` inside the Remote method to filter each list/stream item. Each call observes current bindings. Policy registration is withdrawn with its owner; exceptions deny access.

Default resource fields use the S5 registry declarations and provider `Resource` facts. A Host service may implement `ITypertRemoteResourceSource` when a method needs custom extraction. `ResourceReaders` is keyed by exported method name, then resource kind. Each reader receives the same decoded CLR parameter objects as the method, with lookup identities still represented as `JsonElement`, omitted slots as `Undefined.Value`, and the caller cancellation token separately. Return `[id]`, several IDs, or null to remove that kind. Empty lists and invalid IDs refuse the call. Readers must not resolve Contexts or lookup objects themselves. Metadata is read from the root service even for a scoped Context invocation.

Declare an explicit reader for setter-only resource fields, opaque custom-codec objects or polymorphic values without a selected metadata contract. Do not reserialize decoded values to infer permissions: converter output and serialization-only getters can differ from accepted input. Ordinary JSON decode errors now precede Service/Context/lookup preparation. No client wire change is needed. S7 above adds the event and access-snapshot closure.

## Candidate Remote identities and resources, 2026-10-23

Create Peers inside the owning Context's execution domain. The ordinary `TypertHostConnection(owner, gateway)` still selects the local owner with event answering enabled. A passive owner connection is explicit:

```csharp
var connection = new TypertHostConnection(owner, gateway,
    new TypertPeerOptions(TypertPrincipal.Owner, TypertPeerInteractions.None));
```

For another Host-owned lifetime, construct `new TypertPeer(owner, options)` and pass it to `gateway.InvokeAsync(endpoint, arguments, peer, cancellationToken)` or `StreamAsync`. Dispose the Peer when its owner session ends; await active stream cleanup before closing the root. Disposing one Peer leaves siblings and already-running unary calls alone. A disposed mounted Connection does not fall back to a fresh owner. Withdraw its service registration explicitly if the trusted direct-call fallback should become available again.

Identity drivers construct `TypertPrincipal` from their own credential-free JSON object with a `kind` field. The value clones the JSON, so the originating `JsonDocument` may be disposed. Never build it from a request's principal field. Use `TypertPrincipal.PrincipalOf(TypertInvocation.Current)` for non-owner attribution; it returns null for owner and outside a Remote call. S6/S7 now authorize non-owner dispatch through resource and event policies.

Register a domain-owned default identity field with `registry.RegisterResource(owner, "document", new TypertResourceDeclaration("documentId"))`. Query it with `GetResource` or `ListResources`; observe changes through the existing `Subscribe`. The declaration disappears with its Fiber. Do not register the reserved `global` kind. Set `Resource = "document"` on the relevant `TypertLookupProvider` or `TypertHostContextAdapter`; configuring its resolver preserves the association. Resource registration alone grants no access and does not yet inspect method arguments.

Existing generated business clients need no wire changes for S5. Calls that previously passed a bare third-argument `default` to Gateway should name it `cancellationToken: default` to distinguish the new Peer overload. Host code can adopt the added APIs after rebuilding against this candidate. The [compatibility notes](compatibility.md) separate this foundation from the remaining authorization work.

## Start with the runnable example

For a first package-only application, start with [getting started](getting-started.md). This guide describes the current selected batch, including generated Remote and model consumers. Choose the exact feed/version from [validation](validation.md#released-version-evidence). The optional examples below use source project references and the SDK in global.json.

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

The host loads the two complete stable directories directly and retains them after lifecycle cleanup and unload requests. It never forces GC or deletes code directories. Exit code zero means the contribution checks, lifecycle cleanup and unload requests succeeded; collection can remain pending. Publish the host normally and supply the two prepared directories; no runtime package restore or source checkout is required. This CLR route requires an ordinary runtime and does not support Native AOT. The [artifact workflow](clr-artifacts.md) covers library-managed installation and offline deletion. The [deployment test](../tests/Cordis.Platform.Tests/ProbeDeploymentTests.cs) separately retains explicit shadow-copy coverage and test-only GC observations.

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

Use the session's existing `ConfigurationOperations`. Read a fresh configuration revision, then submit ordered SET/unset paths through `MutateConfigurationAsync`. The library validates the complete final candidate and saves once; object unset restores inheritance, while array unset removes an element. Ordinary fields keep normal restart behavior; `liveOnly` additionally requires a captured live boundary and compatible ordinary effective values. Handle `Saved`, `Applied` and recovery diagnostics separately. The original Config delegate and advanced `ConfigSchema<T>` authoring remain available.

Declare application metadata with `descriptor.WithMetadata(new ConfigurationMetadata { ... })`. Renderer roles, localized descriptions, badges, hidden/disabled/collapse, links/comments, extra plain data and explicit min/max/step/pattern/loose hints travel through immutable Core annotations. They describe the existing validator and do not add validation behavior. Nested fixed live paths use `ConfigSchema<T>.WithVolatile(path, projection)`; Settings omits ordinary siblings and redacts secret values into presence-only sidecars. Submitted edits check offered paths. Host-hidden paths cannot be replaced indirectly; secrets remain writable, and whole replacement may reset omitted secrets. See the [complete declaration, settings and discovery examples](configuration-description.md).

The two exports are separate contracts: `ToSchemastery` produces the browser form's uid/refs envelope, while `ToJsonSchema` produces a JSON Schema 2020-12 declaration with explicit runtime limitations. The standard Settings describe response includes the selected live schema with hidden properties omitted, safe defaults preserved, and secret defaults and required metadata removed; the full configuration export requires separate host authorization. For discovery before activation, `ConfigurationSchemaDiscovery.DiscoverAsync` uses an explicit resolver and reads native group/include sources without starting a Context or writing fallback include files. Import/capture are trusted code; validators, Apply, raw expressions and lazy builders remain unexecuted.

The example's primitive live view requires explicit host selection and hidden fields. Do not expose a raw configuration read as a browser settings response. Its loopback host uses the existing ASP.NET Core Remote adapter and shared Connection; deployed hosts provide authentication, authorization, request limits and their policy. The Settings generation command writes both the generated wrapper and its shared runtime/type declarations. The retained DSH form model stages drafts and saves through the real host. SET-only does not support inherited reset or splitting multi-field saves into sequential writes. Build and run from the repository root:

```console
npm ci --prefix reference --ignore-scripts
dotnet run --project examples/Probes/Probes.csproj -c Release -- --settings-client artifacts/settings-client
node reference/node_modules/typescript/bin/tsc -p examples/Probes/client/tsconfig.json
node scripts/build-application-client.mjs artifacts/application-client
dotnet run --project examples/Probes/Probes.csproj -c Release -- --application-host http://127.0.0.1:17639/ artifacts/application-client
node scripts/application-client-consumer.mjs http://127.0.0.1:17639 artifacts/application-client
```

The final command runs in another terminal and mutates only the generated example artifact to verify a new content generation. Stop the demonstration host with POST `/stop`. The artifact layout uses the upstream web/client declaration, while immutable ESM delivery is a native adaptation. The example client remains a bounded consumption example; declaration export and discovery are separate library APIs described above. The existing authoring gate repeats this real client chain under source and independently consumed packages, including Native AOT when requested.

## Deployment and verification boundaries

The APIs below are available in `0.2.0-alpha.5.2`. Use matching packages and tools from the [release](validation.md#released-version-evidence); see the [Settings guide](settings.md) for the standard configuration contract.

The unreleased S4 slice changes `await session.RefreshAsync()` to return `ProfileReloadResult`. Inspect `RequiresRestart`, then `RestartReason` or the first pending module's `Reason`; inactive-entry diagnostics are available in `Warnings`. Existing `RequiresRestart` and events remain available. Automatic watching is optional even when manual refresh and package operations share the session queue. Toolchain wrappers must forward `GetLoadedModulesAsync` as well as retained-directory inspection so package removal can detect Fibers outside its owned rows. The [compatibility record](compatibility.md) lists restart codes and immutable reinstall boundaries. This API change is not part of the published alpha.5.2 package.

For package and bundle-selection policy, set `session.ConfigurationOperations.AdmitProfileAsync`. The callback receives `ProfileCandidate.ManifestJson` (the exact proposed saved text), `ConfigurationJson` (the effective raw root rows, without evaluating expressions), and a detached `Composition` view with source layers and skipped selections. During installation, `Package` also exposes the prepared directory and planned publication destination so policy can inspect artifacts before Publish. It is null for other operations. Throw to refuse. Validate this library candidate instead of re-reading the profile and predicting another candidate in `IProfilePackageToolchain.PublishAsync`. Do not re-enter configuration operations from admission. Keep product-owned policy inputs stable until the operation settles; this callback does not freeze external SDK or application state. `ProfileSession` supplies candidate-aware application automatically.

If you customize `ReconcileAsync`, assign `ReconcileCandidateAsync` afterwards to opt into candidate application for that customization. Replacing only the legacy callback keeps legacy behavior without admission; it does not silently approve an unbound reconciliation when admission is enabled. Dependency-removal admission runs before the toolchain releases its runtime mapping; declining it may leave the separately approved deselection saved.

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

These workflows reuse the owners described in [architecture](architecture.md) and the published package batch above.

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

`ClrModuleResolver` loads exports with the same normalized bundle directory in one collectible assembly load context. StableDirectory loads the retained files in place; explicit ShadowCopy shares one development copy. All entries of a loaded bundle must use the same mode. Aliases of the same assembly/entry type reuse the plugin instance; different entry types retain separate plugins. Loader entries retain their own raw configuration, Fiber activation and effect cleanup. Removing one resolver lease preserves other exports; the final lease requests bundle unload. Stop the relevant Fibers before removing their resolver mappings. Unload request, collection and shadow deletion remain separate observations.

The resolver shares Core, Clr and Composition contract assemblies with the host by default. Pass other exact host contract assemblies through `sharedContracts`; private plugin dependencies stay in the bundle. This includes the identity required for generated `ITypertRemoteService` bindings and `IClrTypertModule` contributions.

Explicit resolver registrations can select different main assemblies under one bundle directory. Each main assembly contributes its own dependency resolver and directory before loading. Private managed and native resolution queries all registered roots. The same path or byte-identical copies reuse one dependency; different binaries for the same dependency name are refused, including when a new entry would otherwise reuse an already loaded assembly. This is a conservative native bundle rule, not an ABI or assembly-version compatibility algorithm. Native libraries not located by those resolvers retain CLR/OS lookup behavior. The [multi-assembly fixture](../tests/fixtures/ClrMultiAssembly/Consumer.cs) checks both entry orders and actual private native calls.

Before admitting a dependency root, the resolver reads managed references and P/Invoke declarations, recursively follows resolver-located managed images, and checks their candidate paths without executing a factory. This covers declared, locatable conflicts even when the original entry has not yet called its dependency. Already selected dependencies are checked too. Arbitrary dynamic loads and factory side effects do not become a rollback transaction.

Replace a bundle containing distinct exports with the dictionary overload of `ClrModuleResolver.ReplaceAsync`, supplying every registered request, including unloaded exports and aliases. Its callback can use `Loader.ReplacePluginsAsync` to transfer existing raw configurations and switch the affected Fibers together. The single-entry overload remains available for one export and its aliases. Candidate preparation and route publication preserve a single bundle generation; callback effects and product state are not a transaction. Activation failure uses the existing Loader recovery path, and a settled Pending Fiber remains legal.

Both CLR replacement overloads now pass `IReadOnlyList<ModuleReplacement>` to the callback. Each pair contains `ResolvedModule Previous` and `Replacement`; do not collapse this list into a dictionary keyed by `IPlugin`. Different imports may share the old plugin and split into distinct successors. `IModuleResolver.ResolveAsync` and the static, deployment and CLR implementations return `ResolvedModule`. Direct runtime callers use `module.Plugin`. Custom resolvers must retain one module reference per import in a generation; deployment adapters must forward it unchanged.

`Entry.Module` records the import actually backing that entry. Changing a name mapping does not rewrite it; disabling and enabling the entry resolves the new mapping. A replacement updates it to the prepared successor, and activation failure restores the previous identity after candidate cleanup. A direct Fiber has no entry identity: a shared old plugin with multiple distinct successors is refused before teardown. Multiple aliases of the same successor remain unambiguous. The generic `Loader.ReplacePluginAsync(IPlugin, IPlugin)` still performs a blanket one-to-one replacement. HMR tracking prefers `Entry.Module.Filename`, with the host locator as fallback. Holding a module retains its exported plugin, so release caller-owned references before expecting collectible unload.

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

An explicit Remote class may derive from `Service<TState>`. Its `CreateView` can create a fresh caller-bound view sharing the provider's State, as in the [independent author](../tests/fixtures/TypertConsumer/Author.cs). Generated bindings invoke that view. The Gateway checks the underlying registration and original provider value; view allocation does not retire a provider. Ordinary in-process Service consumption still uses the Core API directly.

Generated metadata codecs use `TypertCodec.CreateDeclared` to retain nullability from the common declaration model where CLR metadata erases it, including supported collection elements and dictionary values. The generated query identifies complete declaration uses at concrete Schema paths, so nested nullable and non-null uses remain distinct even when STJ reuses a reference. Always-ignored members do not override active JSON properties. No additional author annotation is required. STJ still owns naming, converters and serialization; this does not claim arbitrary generic type support or add result-schema validation to the Gateway.

`TypertCodec` prepares and checks its schema lazily when `Schema` or `Decode` is first used, including nullable input. Decode validates the supported native subset and then deserializes. Encode serializes with the supplied metadata without preparing or validating the result schema. Unsupported schema features fail explicitly. Client projection preserves `prefixItems` as readonly tuples, including bounded optional prefix elements, nested local references, and unbounded typed or unknown tails. A minimum beyond the prefix or a finite tail length limit is rejected. The [tuple fixture](../tests/fixtures/TypertConsumer/TupleContract.cs) supplies an explicit schema to a generated binding and distinguishes the real closed-tuple HTTP call from codec/projection-only variants. It does not demonstrate Roslyn inference of CLR tuple types or the complete source type graph.

Remote methods support required ordinary parameters and optional parameters with supported constant defaults, returning `Task`, `Task<T>` or, with explicit `RemoteMethod(Stream = true)`, `IAsyncEnumerable<T>`. Omitting an optional argument uses its authored default; explicit null remains a separate wire value subject to the parameter's null contract. A final `CancellationToken` parameter supplies cooperative cancellation and may declare `= default`; it is not a wire argument. See the [optional-parameter consumer](../tests/fixtures/TypertOptional/OptionalConsumer.cs) for omission, null and required-input cases. Explicit Context and object lookup declarations have host-owned registration APIs; the scoped/lookup example remains in the Typert fixture.

### Register, call and withdraw contracts

Create a `TypertRegistry` and `TypertGateway` in the existing Cordis context. `TypertLoader.StartAsync` discovers contributions for live Loader entries through an explicit artifact resolver; it does not scan assemblies. A static author uses `StaticTypertArtifactResolver.Register` with its generated contribution factory. A dynamic entry can implement `IClrTypertModule.CreateTypertContribution()` alongside `IClrPluginModule.CreatePlugin()`; `ClrModuleResolver` then supplies the artifacts from the same loaded bundle and factory identity. See the [static consumer](../tests/fixtures/TypertConsumer/Consumer.cs) and [multi-entry consumer](../tests/fixtures/ClrMultiEntry/Consumer.cs).

The Typert loader's owner Fiber owns registrations and its activation-lifetime import cache. Multiple live entries of one exact module request share a contribution; removing the last matching entry withdraws it unless that request was explicitly configured. Registry registration validates the contribution before publishing it and rejects conflicts. Gateway calls resolve the live Cordis provider and check registration validity; withdrawing a definition invalidates retained invocations.

The native Gateway also checks provider generations after successful resolution and before encoding successful business results or stream items. Withdrawing a Service, lookup or Context provider does not actively abort work already running. A withdrawn provider's successful result is refused even if the generated definition remains active. The [independent lifetime cases](../tests/fixtures/TypertConsumer/LifetimeCases.cs) hold each provider at its actual asynchronous boundary, replace it, and verify both rejection of old success and admission through the current provider. These checks are a native validity adaptation, not product retirement or draining policy.

For dynamic bundle replacement, keep the Typert loader owner and call `SuspendAsync` with the affected exact Loader request names after candidate preparation and before switching provider Fibers. These names can differ from CLR resolver keys when deployment routing supplies aliases. After the resolver returns and commits, call `ResumeAsync` for those requests. On failure, resume only when `PluginReplacementFailure.Recovery` confirms `Succeeded`; otherwise keep that scope suspended. Resume propagates registration errors and withdraws its selected batch on failure. Same-request lifecycle reentry from registration or withdrawal observers is refused. Suspension detaches unfinished imports without forcibly stopping resolver code; retained tasks can still retain their old code until they settle. The independent multi-entry consumer verifies unchanged contribution identity and calls for an unrelated package, refreshed CLR codecs after replacement, recovery ordering and old invocation rejection. Retained contributions, clients, service objects or errors can still keep collectible code alive; release them when their ownership ends.

`MapCordisRemote` maps a Host-owned `TypertHostConnection` to standard HTTP unary and text WebSocket mux routes. Create the Connection inside the existing Context execution domain, and register ASP.NET WebSocket middleware with `UseWebSockets()`. The Connection owns its operator Peer; requests and sockets borrow it. Authorization remains the Host's callback. Socket exit releases its Peer registration; it does not dispose the Peer.

Publish that Connection with `owner.Provide("connection", connection)` in the same execution domain. Direct Gateway calls then borrow its operator, including after a current service replacement. Without a mounted Connection, the Gateway lazily owns one operator under its Context. A stream keeps its initial operator through cleanup. The host must withdraw the service registration when disposing its Connection; a borrowed Peer does not transfer that responsibility to the Gateway.

Unary uses the fixed `client-request` / `server-response` envelope and correlated `rpcId`. Host authors return business values, `Task`, or their declared stream. The shared runtime owns framing and failures. A cancelled business failure is normalized by the Gateway; cancellation does not prove that a write never executed. Stream cleanup waits for pending native reads before disposing the iterator in its invocation Context. Drain active iterators before closing that Context.

Generate `.mjs` and `.d.mts` artifacts with `TypertArtifacts.GenerateClient(contribution)`. Ship `TypertClientRuntime.Module` as `cordis-remote.mjs` and `TypertClientRuntime.Declaration` as `cordis-remote.d.mts` beside them. `Cordis.ClientAssets` writes these shared assets once for its standard modules.

This entry reads actual Host descriptors and STJ Schema, then uses the shared client emitter. It is a supported export route, not a compatibility bridge for the saved-model backend. It requires the author implementation and serialization metadata and can run during application export; it does not regenerate clients for each call. The independent saved-model backend reads declaration facts and explicit references without executing author constructors, accessors or converters. Its refusal of a DTO does not automatically prohibit that DTO on the Host or descriptor export route. Descriptor export still depends on the supported Schema and emitter constructs; it does not promise arbitrary CLR types. Clients from both routes share the Connection and invocation contract.

For expected business rejection, authors can throw `RemoteError` at their existing service boundary with a public code, message and optional JSON details. Gateway preserves those fields. Ordinary unhandled exceptions currently become `gateway/internal` with their exception message retained, matching fixed DSH [rpcFailure](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts). This is not automatic redaction or a host-wide exception mapping service. Applications that must conceal internal information should map failures and record diagnostics at their owned business invocation boundary. Outer ASP.NET exception middleware cannot assume it will catch an exception already converted into a Gateway result. This does not change the separate cancellation, assembly-failure or stream contracts, or promise an existing global mapping callback.

`TypertCodec.Schema` describes input; the public read-only, non-virtual `ResultSchema` describes JSON output. Export the result codec from the actual generated contribution:

```csharp
var contribution = EchoServiceTypert.Contribution("Example.Author");
var resultCodec = contribution.Invocations.Single(method => method.Endpoint == "example/echo").Result;
File.WriteAllText("echo-result.schema.json", resultCodec.ResultSchema.GetRawText());
```

Use the endpoint declared by your service. The exported JSON can be stored and consumed without the provider or its `JsonTypeInfo`. It is the same output projection used by native `DecodeOutput` and the client emitter, not a second schema declaration. Input and output differ for conditional omission, read-only/computed members and accessor nullability. Supported local references resolve within the exported schema. Nullable and Optional codecs preserve their existing composition; top-level `Undefined` remains an absence contract (`AcceptsUndefined`), not a JSON null value.

Access preserves lazy projection and precise `NotSupportedException` diagnostics for unsupported schema constructs. Format annotations do not promise validation by every external validator. This describes serialized JSON, not multipart framing or restored JavaScript binary values. It exposes reading, not a new external override protocol or an HTTP endpoint. Generated Web type declarations alone do not validate arbitrary result DTOs. ResultSchema validates JSON presence and nullability before DecodeOutput uses native STJ metadata. Getter-only members are not marked as required native assignments: a constant getter retains its authored value, and a JsonConstructor-bound getter is restored by the constructor. This does not reconstruct arbitrary computed state or expand the static DTO backend.

Applications choose when to validate and how to handle failure, including recording business acceptance before validation. Read or export this schema for that purpose; do not substitute input Schema, reflect DTOs or duplicate the output rules. Reading the property adds no Gateway result validation and changes no acceptance, cancellation, reply or cleanup ordering.

For values already reconstructed by the TypeScript Connection, import the executable output contract from the generated module:

```typescript
import { resultCodecs } from "./remote.mjs";

const output = resultCodecs["sample/Echo"].parse(decodedValue);
```

Use your declared endpoint and an `unknown` value obtained by your application. `parse` returns the same value with the generated output type, or throws `TypeError`. It sends no request, clones no objects or byte views, and owns no resources. The map and its codec objects are frozen. Generate and deploy the module with its matching `cordis-remote.mjs` runtime.

Supported Schema combinations apply to that same decoded value. For example, default byte metadata with `allOf: [{ type: "string" }, { not: { type: "null" } }]` accepts the same non-null `Uint8Array` values as `type: "string"` alone. Nested byte positions retain their metadata, while additional JSON-only values cannot contain typed arrays.

The codec preserves output properties, requiredness, omission, null, optional top-level `undefined`, arrays, string-key dictionaries and local recursive references. An optional property may be absent; explicit `undefined` is not JSON null or an omitted field. Default binary metadata admits `Uint8Array`, including empty bytes, instead of its serialized string. Ordinary strings and custom-converter outputs retain their JSON representation. Supported literal, union, numeric and collection constraints are compiled from the same bounded schema used by the emitter. Unsupported keywords or patterns refuse generation. Format annotations retain the existing schema policy, rather than acquiring general format validation; recursive predicate evaluation is bounded to 256 levels. Saved-model binary metadata remains unsupported and explicitly refused.

Generated unary callers automatically use this codec for binary results, after attachment recovery and the existing cancellation/withdrawal checks. Invalid values become `RemoteResult` failures with code `gateway/internal` and the original local `TypeError` cause. Ordinary JSON results and stream items acquire no automatic business validation. Applications can use the exported codec independently at their chosen validation boundary. This does not expose acceptance before multipart reconstruction: if reconstruction or strict binary decoding fails, use the application's request identity and receipt recovery to resolve the unknown outcome.

```typescript
import { createRemote, mountRemote, TypertConnection } from "./remote.mjs";

const connection = new TypertConnection(new URL("/remote", location.href));
const client = createRemote(connection);
const result = await client["sample/Echo"]({ request: { Text: "hello", Count: 1 } });
if (!result.ok) console.error(result.error.code, result.error.message);
await client.dispose();
const mounted = await mountRemote(ctx, connection);
await mounted.dispose();
await connection.dispose();
```

Await `mountRemote`: it registers owner cleanup and contributes methods to the shared root `remote.<namespace>` service. Disjoint contributions can share a namespace; duplicate methods or unrelated services are refused. Withdrawal removes only that contribution, then removes the namespace when its last contribution leaves. Client disposal drains its work without closing the borrowed Connection. The application owns Connection disposal. Optional fetch and WebSocket factories configure the application's carrier identity, without changing generated methods or Host authorization.

The shared runtime supports unary results, unary multipart attachment recovery, downlink and typed uplink mux, with generated .NET/TS clients. The [validation record](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/validation.md#shared-remote-connection-package-gate-2026-10-12) separates the frozen package gate, fixed DSH interoperability and Native AOT evidence. Selected reverse Context/owned-value and fixed event forwarding are described in [Remote events](remote-events.md). The previous base64 wire and per-method NDJSON paths are removed.

### Consumer evidence and remaining scope

The [multi-entry gate](../scripts/verify-clr-multi-entry.py) independently packs an author package, consumes it through `PackageReference`, installs its root/subpath entries and exercises shared identity, separate configurations, failed and successful replacement, withdrawal, generated TypeScript calls over real HTTP and invalid arguments. The separate [Remote gate](../scripts/verify-typert.py) covers the native Remote author chain. Use a fresh local package batch and the required Node/TypeScript dependencies. Platform acceptance remains governed by the completed results in [validation](validation.md); these examples alone do not establish Windows/Linux or Native AOT closure. Dynamic CLR loading requires the ordinary runtime.

The selected fixed event workflow, reverse Context and owned-value handling use the common declarations described in [Remote events](remote-events.md). Browser outbound scoped calls, native Peer routing and Scope stores/layers have bounded consumer evidence; current support and refused shapes are in the [backend support table](development.md#alpha5-independent-backend-disposition), and outstanding named integrations are in [remaining support boundaries](development.md#remaining-support-boundaries). Recorded candidates are linked as [historical evidence](validation.md#historical-evidence). Operator Peer ownership, typed uplink and unary multipart are also implemented. These results do not establish arbitrary type graphs or every Peer workflow. Settings, PluginManager and selected host emit notifications use generated contracts over the existing profile owners; see [management migration](management-remote-migration.md). Native raw configuration, host state and artifact delivery retain their independent duties. Existing application-specific `MapCordisService` endpoints do not close the remaining wire gaps. Product replacement admission, permissions and business retirement/draining policies remain product responsibilities.

## Native authored models and .NET consumers, 2026-10-10

Ordinary and Remote declarations use one common model and purpose-specific projections. The [current support table](development.md#current-support-and-release-status) and backend disposition distinguish runtime metadata from independent generation.

Ordinary Plugin, Service and Config use remains in process. Remote is an optional boundary for consumers in another environment. A C# author keeps the existing Remote attributes and explicit STJ context; setting `CordisTypertService` opts that project into source-model extraction. Composition supplies the SDK compiler tool and build target. Node and TypeScript are not prerequisites for this path.

```xml
<PropertyGroup>
  <CordisTypertService>settingsController</CordisTypertService>
</PropertyGroup>
```

The build publishes `cordis/typert/$(AssemblyName).cordis.model.json` in the author package. Service selection and ordinary roots feed the same `extract-model` entry and declaration graph. For an author assembly named `Example`, the file is `Example.cordis.model.json`. The Remote generator compares selected service facts with the current compilation and refuses drift with `CORDISREMOTE002`; unrelated declarations are not a second service contract. Referenced declarations keep their CLR metadata provenance and adjacent XML documentation. Missing source initializers or implementations are not reconstructed from metadata. Referenced source definitions compose through the explicit reference-model inputs below.

A separate contract package can consume a copy of that artifact without referencing the provider implementation:

```xml
<PropertyGroup>
  <CordisTypertService>settingsController</CordisTypertService>
  <CordisTypertClientModel>Example.cordis.model.json</CordisTypertClientModel>
  <CordisTypertClientNamespace>IndependentSettings.Client</CordisTypertClientNamespace>
  <CordisTypertClientName>SettingsClient</CordisTypertClientName>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

For external DTO declarations, add explicit reference models to the client project:

```xml
<ItemGroup>
  <CordisTypertClientReferenceModel Include="Shared.cordis.model.json" />
</ItemGroup>
```

The CLI equivalent is `emit-dotnet --reference-models references.rsp`, with one model path per line. Remote selection stays in the primary model. Referenced source definitions resolve by complete assembly-qualified identity through the same composition used by ordinary data emission. Metadata signatures alone do not preserve initializers and cannot replace the source model. Missing or conflicting definitions reject generation and remove stale client output. References are build inputs; their response file and generated source belong to `Clean`. The independent caller consumes the generated contract package without loading provider assemblies.

The target writes ordinary DTO/client source before `CoreCompile`, so STJ sees it in the same compilation. Same-round source-generator output cannot serve as STJ input. The same properties also generate language-service inputs on the first design-time build after restore, without a preceding normal build. Design-time projections use the `design-time` subdirectory of `CordisTypertOutputDirectory`; ordinary build/pack owns the regular outputs. Both consume the same explicit model and reference inputs. `Clean` removes both sets of generated files; identical incremental emission preserves bytes and timestamps. Source-model extraction for packing remains a normal-build operation; the live source generator reads the current author compilation. MSBuild design-time input and actual compiler checks do not establish live IDE interaction or named UI-platform acceptance. The selected SDK must provide the matching Roslyn assemblies; the package does not redistribute them.

Nullable float and decimal constant initializers retain their authored types and defaults. An absent JSON member keeps its initializer; explicit null replaces it. Caller DTOs share the selected target namespace with generated helper types. A conflicting name, such as `DemoClient` when `CordisTypertClientName` is `DemoClient`, is rejected before compilation with both origins in the diagnostic. Choose a distinct DTO or client name; the emitter does not silently rename public types and removes its previous generated output when it refuses a projection.

```csharp
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri("http://localhost:5000/remote"));
await using var remote = new SettingsClient(connection);
var reply = await remote.DescribeAsync();
if (!reply.Ok) throw reply.Error;
var view = reply.Value;
```

The caller owns the supplied HttpClient and shared `TypertConnection`. Ordinary calls return `Task<RemoteResult<T>>`; inspect `Ok` before reading `Value` or `Error`. No-value authors return `Task`, projected as `Task<RemoteResult>`; absent success and explicit null remain distinct. Missing adapters or an absent Connection are local assembly exceptions. Remote failures preserve arbitrary owner codes and detached details; carrier failure and cancellation become unary outcomes. Downlink calls asynchronously prepare an eagerly opened `RemoteStreamHandle<T>`. Iterate raw business items once and await disposal. Stream Remote errors, carrier failures, local misuse and external cancellation remain distinct exceptions. Client disposal awaits its own calls and cleanup without disposing borrowed resources.

The production `SettingsController` resolves the optional ordinary `ISettingsProvider` named `settings` on every call. Mount `SettingsForms` over the session's existing `ConfigurationOperations`; it derives uniquely addressable active forms from the authors' live Config declarations. Its numeric revisions observe Fiber identity, complete schema and raw configuration. The same profile queue owns layered redacted reads, complete candidate validation and writes. Describe/update/replace/mutate use the one generated contract. See [Settings guide](settings.md) for public calls and removed hash-only Settings paths.

The [independent .NET Settings gate](../scripts/verify-typert-dotnet.py) packs an author and contract package, then builds a package-only caller outside this repository. The caller does not reference the provider implementation and uses static metadata with reflection fallback disabled. It covers typed describe, redaction, missing/failed provider, retry, held-view invalidation, definition withdrawal/re-registration, scoped suspension and borrowed-client ownership. Primitive roots, null/default arguments, nullable-state annotations and unsupported projections have separate cases. Run with `--aot` for the static caller; the dynamic CLR Host remains an ordinary-runtime boundary. Formal platform results belong to the exact completed checkpoint in [validation](validation.md).

This route covers supported unary/stream, Settings, PluginManager and event consumers. Consult the [backend disposition](development.md#alpha5-independent-backend-disposition) for refused shapes and [validation](validation.md#released-version-evidence) for named platform evidence.

## Typed Remote uplink

Declare the uplink on the author method, separately from ordinary wire arguments:

```csharp
[RemoteMethod("exchange", Stream = true)]
public async IAsyncEnumerable<string> Exchange(
    string prefix,
    [RemoteUplink] IAsyncEnumerable<string?> input,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    await foreach (var item in input.WithCancellation(cancellationToken))
        yield return prefix + (item ?? "null");
    yield return "complete";
}
```

Register the input value type in the author's STJ context, here `typeof(string)`. The ordinary parameter remains an argument; the input sequence is supplied by the Invocation. The common declaration model retains the input's root and supported collection element nullability. `typeof(T)` alone cannot express nullable reference inputs. For explicitly absent items use `IAsyncEnumerable<RemoteOptional<string?>>`: `HasValue` distinguishes an absent item from a present null. This value is not an extra wire object.

```csharp
await using var stream = await client.ExchangeAsync("prefix:", cancellationToken);
stream.Send(null);
stream.Send("first");
stream.End();
await foreach (var item in stream.WithCancellation(cancellationToken))
    Console.WriteLine(item);
```

`Send` validates and enqueues locally, including before the first read; it does not acknowledge Host receipt. `End` half-closes the input after queued sends while output remains readable. The generated return type is `Task<RemoteStreamHandle<string, string?>>`. Downlink-only methods retain `RemoteStreamHandle<T>` without send/end capabilities. Both have one output consumer. `End` is idempotent, including after termination. Sending after half-close or retirement fails locally. `DisposeAsync` cancels this logical call and awaits owned local work; it does not acknowledge remote business retirement. Host input codec failure terminates the logical stream even when the author catches the input exception. Sibling streams and the Connection remain active.

Generated TypeScript returns `RemoteDuplexHandle<Output, Input>` with `send`, `end`, and awaited `dispose`; optional items use `undefined`, independently of `null`. Its send boundary checks lossless JSON, and the Host validates the declared codec. .NET also validates its generated input codec before enqueueing. Text mux uses ordinary item values and omits `value` for absence; it adds no binary frame or wrapper. Primitive, nullable value, supported collection/dictionary and existing bounded DTO codecs retain their actual backend limits. Selected reverse Context/event delivery and owned-value settlement use the same Connection; see [Remote events](remote-events.md).

Composition's package supplies its own declaration model to the MSBuild client reference set. Other source models remain explicit `CordisTypertClientReferenceModel` inputs. A direct compiler invocation must supply the corresponding models with `--reference-models`; it cannot recover authored defaults from assembly metadata.

## Ordinary declaration models

Select ordinary Plugin, Service, Config and object declarations by exact CLR metadata name in a project referencing Composition:

```xml
<ItemGroup>
  <CordisTypertModelRoot Include="Example.Options" />
  <CordisTypertModelRoot Include="Example.Store`1" />
</ItemGroup>
```

Generic arity uses the CLR backtick form; nested names use `+`. Ordinary roots must be authored in the project and publicly accessible through their containers. An explicitly selected source type marked `RemoteService` may also be non-public, so analysis can retain its complete contract and diagnostics. Selection does not register a capability. Runtime service generation still requires a public top-level non-generic partial class. Ordinary analysis requires no Remote attribute, partial declaration or serializer context, and executes no constructor, initializer or method.

Normal build/pack writes the same `$(AssemblyName).cordis.model.json` under `cordis/typert/` for ordinary and Remote roots. `CordisTypertModelPath` controls the local output path. Definitions and applied references are distinct. Generic parameter owners/ordinals/constraints, direct bases/interfaces, member signatures, overloads, events/delegates, required/accessor/nullability/default facts, initializers and documentation are retained. Attributes include return, getter/setter and setter-value targets; parameter facts include extension `this` and `scoped`. Attribute types and `typeof` values retain complete declaration references, including array values, and enter the referenced graph. Source partial declarations, implicit/member provenance, instance constructors, explicitly marked non-public Remote methods and authored JSON metadata signatures remain facts rather than copied method bodies. External declarations keep complete assembly/metadata identity and their recorded signature provenance; absent source initializers and implementations are not reconstructed.

Declaration identity uses UTF-8 JSON with LF formatting, independently of the producer or consumer operating system. File formatting is not part of the identity; authored facts still are. A saved model can be generated on Linux and consumed on Windows. Models produced with the former Windows-dependent identity must be re-extracted, rather than edited or accepted through a fallback hash.

The Composition package carries the matching compiler and analyzer. Use its normal PackageReference and build targets; temporary compiler paths or analyzer replacement imports are diagnostic tools, not the authoring contract. Re-extract the actual reference-model closure with the matching toolset before packing author SDKs.

The packaged compiler validates and projects an artifact-only declaration catalog without loading or referencing the provider implementation:

```console
dotnet <compiler> --roslyn-directory <selected-sdk-Roslyn-directory> inspect-model --model Example.cordis.model.json --output catalog.json
```

The catalog retains analysis diagnostics and generates no binding, Remote registration, DTO serializer or executable Service. Syntax errors and duplicate declaration identities refuse extraction and remove owned stale output. Other pre-generator compilation errors are recorded explicitly because a valid build can depend on neighboring generator output. Source graph limits are 256 named definitions and 64 nested references; unresolved/function-pointer signatures are diagnosed as analysis gaps. This is not complete C# compiler or reflection coverage. The existing .NET Typert gate verifies package-only consumption, incremental timestamps, Clean including a failed first extraction, cold rebuild, reference identities and refused inputs.

`CordisTypertService=*` selects the authored services into the same declaration artifact; `CordisTypertModelRoot` adds explicit roots. The CLI entry is `extract-model`, with `--roots` for the root response file and `--service` for service selection; either or both may contribute roots. A selected service's reachable graph is retained. Source and saved inputs share service, method, DTO, constructor, naming and default-value interpretation. Conflicting JSON naming policies for a shared DTO, including nested and recursive references, fail with `TYPM113` in the affected projection. The old `extract` entry, `.cordis.typert.json` artifact and `CordisTypertDeclarationModelPath` are retired; they are not compatibility routes.

Selected ordinary classes can generate DTOs and a static STJ context from the saved artifact without a Remote declaration or provider assembly:

```xml
<PropertyGroup>
  <CordisTypertDataModel>Example.cordis.model.json</CordisTypertDataModel>
  <CordisTypertDataNamespace>Example.Consumer.Data</CordisTypertDataNamespace>
  <CordisTypertDataJsonContext>ExampleDataJson</CordisTypertDataJsonContext>
</PropertyGroup>
<ItemGroup>
  <CordisTypertDataRoot Include="Example.Options" />
  <CordisTypertDataReferenceModel Include="Shared.cordis.model.json" />
</ItemGroup>
```

The optional `CordisTypertDataReferenceModel` items supply saved models for external data definitions reached from the selected roots. Each input is checked with its model identity. Resolution requires an exact assembly-qualified declaration identity, including the assembly version and public-key token; names alone do not match. A referenced model supplies source facts for its own declarations. A metadata placeholder by itself remains insufficient. References do not add public roots: `CordisTypertDataRoot` still selects only roots published by the primary model.

The equivalent CLI accepts `emit-dotnet-data ... --reference-models reference-models.rsp`. The response file lists one model path per line; relative paths are resolved from the response file's directory. Omit this option when no reference models are needed. Every explicitly supplied reference identity is recorded in the generated source, in deterministic order. Conflicting source definitions for the same identity are refused; identical definitions may occur in several inputs, and different source locations alone do not conflict. Missing definitions, incompatible declaration shapes and generated type-name collisions are also refused. This validates declared identities and artifact contents, not correspondence to an arbitrary implementation DLL; publish each model with the source build that produced it.

The target invokes `emit-dotnet-data` before `CoreCompile`, using the same supported JSON projection as the Remote caller. Each build reads the primary and reference model contents; unchanged generated output retains its bytes and timestamp. Generation failure removes stale source, and `Clean` removes generated source and owned response files. Missing members retain supported constant defaults; explicit null remains distinct. These generated data carriers do not replay authored constructor or accessor behavior. Referenced DTOs use the same bounded public class/property subset as local DTOs; composition does not enable generic DTOs, inheritance, custom converters or other refused shapes. Extraction records `TYPM202` on a source type whose non-public instance data member has explicit `JsonInclude` but falls outside the public analysis surface. Projection rejects that type, including through a reference model, rather than silently dropping the JSON member; unrelated declarations remain usable.

A saved Remote model can also produce web artifacts without loading the Provider assembly:

```xml
<PropertyGroup>
  <CordisTypertWebModel>Example.cordis.model.json</CordisTypertWebModel>
  <CordisTypertService>example</CordisTypertService>
</PropertyGroup>
<ItemGroup>
  <CordisTypertWebReferenceModel Include="Shared.cordis.model.json" />
</ItemGroup>
```

Normal build emits `remote.schema.json`, `remote.mjs`, `remote.d.mts` and the existing `cordis-remote` runtime pair into `$(CordisTypertOutputDirectory)web/`. Pack includes these files under `cordis/typert/web/`; deliver that directory together. `emit-web --model Example.cordis.model.json --service example --reference-models references.rsp --output web` is the equivalent compiler command. It owns those five named files in its output directory. Failed generation removes them; Clean also removes the response file. The web target does not generate C# IDE inputs.

Import `createRemote` and `TypertConnection` from `remote.mjs`, construct a Connection for the Host's Remote address, then call the generated endpoint methods. The methods use the existing `RemoteResult`, streams, cancellation and borrowed Connection contract. The emitter is shared with runtime-descriptor generation; the saved-model route obtains its types directly from common declarations and explicit referenced source models. It does not load or execute author code, and it adds no wire protocol or model format.

The Schema file is a JSON Schema 2020-12 definition catalog. Select `#/$defs/<escaped namespace/method/arguments>` for a complete argument object, or the `parameters/<wire>`, `result` and optional `uplink` definitions. Escape `/` as `~1` and `~` as `~0` in a definition key. Local recursive references remain within this document. Artifact comments record the input identities; unchanged outputs retain their timestamps. Declaration initializers are not advertised as Host defaults: constructors and accessors are not executed by this backend.

For an event, set `CordisTypertService` to its declared event name (for example `alpha/changed`) or exact delegate metadata name. Use the same primary/reference model properties and the same five package assets. The module exports `createEvents`; it borrows the shared Connection:

```ts
import { createEvents } from './remote.mjs'
const events = createEvents(owner, connection, adapters)
events.on(owner, 'alpha/changed', (values, labels, required) => { /* consume typed arguments */ })
await events.run(signal, host => onReady(host.home))
```

The Host must still explicitly select the generated forwarder. A declaration in a model does not expose the event. Ordinary arguments and waterfall requests describe Host output; a handler reply describes Host input; `next()` describes output from the remaining chain. Conditional omission can therefore make the reply and continuation types different. `undefined`, explicit `null` and calling `next()` remain separate choices. The event Schema catalog uses `<event>/args/<index>`, `request`, `reply` and `next` keys. A JSON Schema describes present JSON values; optional `undefined` remains in the generated TypeScript contract.

The independent .NET Remote backend retains value-type structs/readonly record structs and generic definitions such as `Response<T>`, while expanding their actual closed arguments for JSON roots and nullability. It supports unconstrained data type parameters, not arbitrary open generic endpoints or constrained type execution. Getter-only members remain getter-only and receive wire values through generated JSON constructor parameters; no getter or author constructor body is copied. Unknown initializer expressions may be omitted only in output-only carriers, whose values come from the wire. Input and bidirectional carriers still refuse those expressions, including `new(false, [])`; they do not silently replace an authored default with null or `default`. Ordinary `emit-dotnet-data` keeps its separately documented narrower subset.

The .NET declaration projection also recognizes BCL `ImmutableArray<T>` and `DateTimeOffset`, and the default generic `JsonStringEnumConverter<TEnum>` applied to a source enum. Generated DTOs retain those native types and the known converter; no BCL source model or shadow DTO is required. Enum aliases, flags and explicit `JsonStringEnumMemberName` values keep STJ behavior. Source enum field order is retained in the common model so alias-order changes affect its identity. Alias-bearing models that do not retain declaration order must be re-extracted; there is no alternate model format or conversion path. Arbitrary converter implementations remain refused. Generated Remote result decoding uses the platform codec with common declaration nullability, including collection element annotations erased by STJ metadata. Regenerate clients and use the matching runtime when this generated call contract changes. Independent Web projection still refuses string-converted enums.

This backend currently accepts ordinary closed public DTO classes/records, supported scalar types, recursive references, arrays, framework collection interfaces and string-key dictionaries. It preserves wire names, required/nullable fields and supported serializer inclusion options. It rejects unsupported serializer policies, custom converters, polymorphism, data inheritance, applied generic DTOs, non-string dictionary keys, multidimensional arrays and binary attachment metadata. A refusal is not a completion claim; runtime bindings and the existing runtime artifact emitter retain their established support. The [backend disposition](development.md#alpha5-independent-backend-disposition) separates these limits by their actual facts and use.

Re-extract declaration models with the current compiler and rebuild maintained consumers. Inputs missing the required structured references or provenance, and obsolete model inputs, are rejected rather than converted. A saved artifact's identity validates its saved facts; it cannot recover omissions, prove every authored JSON configuration or establish correspondence to an arbitrary DLL. There is one current contract, with no historical format reader or versioned Remote compatibility route.

To generate a direct unary client from the same declaration artifact, select both the DTO and its marked service in the author project:

```xml
<ItemGroup>
  <CordisTypertModelRoot Include="Example.SharedData" />
  <CordisTypertModelRoot Include="Example.SharedRemote" />
</ItemGroup>
```

The service's `typeof` JSON context and its registered data enter the closure, including an internal source context. In the independent contract project, use that one model for the ordinary data properties above and the client:

```xml
<PropertyGroup>
  <CordisTypertService>sharedData</CordisTypertService>
  <CordisTypertClientModel>Example.cordis.model.json</CordisTypertClientModel>
  <CordisTypertClientNamespace>Example.Consumer.Remote</CordisTypertClientNamespace>
  <CordisTypertClientName>SharedClient</CordisTypertClientName>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

The equivalent command is `emit-dotnet --model Example.cordis.model.json --service sharedData --namespace Example.Consumer.Remote --client SharedClient --output SharedClient.g.cs`. It consumes the saved facts without the author's implementation assembly. The .NET caller backend covers supported unary methods, downlink streams, lookup wire parameters, scoped Context views, data/constants and trailing CancellationToken parameters. Independent callers must refuse serializer behavior they cannot reproduce, including custom metadata/converters or insufficient DTO facts; this does not remove analysis facts or automatically reject a Host using the author's actual implementation and explicit STJ metadata. The Host and caller share facts, not the intersection of their restrictions. An explicitly marked non-public method remains visible to analysis and is refused by Remote binding. Only a selected, valid annotation-only partial STJ context may defer its pre-generator abstract-member obligations; unrelated errors are not suppressed. Successful projection does not prove the author build or register a live service.

Declare Context keys and wire identity types with assembly attributes, the .NET equivalent of the fixed upstream `TypertContextMap`:

```csharp
[assembly: TypertContext("document", typeof(string))]
```

Declarations in the author assembly and its compiler references enter the same saved model. Missing receiver declarations and conflicting key/type declarations are refused. These declarations do not register live adapters. The Host continues to register `TypertHostContextAdapter` and `TypertLookupProvider`; the Client registers `TypertClientContextAdapter` in its existing `TypertRegistry`, owned by a Fiber effect.

```csharp
await callerContext.RunAsync(owner =>
{
    registry.RegisterClientContext(owner, "document", new(
        ctx => ctx.Metadata.TryGetValue("documentId", out var id) ? id : Undefined.Value,
        _ => null)); // Reverse lookup is unused by this outbound-only example.
    return Task.CompletedTask;
});
await using var connection = new TypertConnection(httpClient, remoteBase);
await using var client = new DocumentClient(connection);
await client.ReadAsync("doc-1");
var scoped = client.ForContext(callerContext, registry);
await scoped.ReadAsync();
```

Two scoped contracts remain distinct. A method with `RemoteMethod(Context = "...")` appears on the scoped view: the adapter identity selects the Host receiver. A direct method gains a scoped projection only when it has exactly one lookup parameter whose key has a Context declaration with the same wire type. Its ordinary client method still accepts the lookup identity; the scoped method omits that argument, while the Host receiver remains direct. Methods with multiple lookups retain their explicit wire arguments. A lookup's Host object is never reconstructed as a client DTO merely to pass its identity.

`ForContext` borrows the Context and registry, which must share a Cordis execution domain. It reads the current adapter and identity on each invocation inside `Context.RunAsync`. `Undefined.Value` means the Context does not supply an identity; explicit null remains a present wire value for reference-type identities and is subject to Host codec validation. Value-type identities require the declared CLR type. Missing adapters or identities refuse the call before transmission. Replacing an adapter affects existing views. The view owns neither its Context, adapters nor HttpClient; disposing the parent client cancels its requests and invalidates its views. Ending a scoped downlink uses the existing stream cleanup path. Explicit views provide C# signatures instead of upstream's dynamic direct/scoped selection; use the parent client for an explicit direct call. See the [shared package consumer](../tests/fixtures/DeclarationModel/SharedConsumer.cs) for both contracts and withdrawal.

For independent callers, a JSON context available only as assembly metadata lacks the source facts needed to reproduce its serializer and is refused with `TYPM115`. The Host may still bind that actual context. A non-finite optional numeric default is retained as an author fact and works for omitted Host arguments. If the selected JSON number handling cannot transmit it, standalone .NET caller generation refuses with `TYPM116`; it does not emit a method whose default call fails during serialization.

The independent consumer uses an authored DTO as both an ordinary root and a Remote argument/result through a real HTTP Host and package-only caller. Data consumers own DTO behavior; this route checks model and transport wiring. Backend limits and named IDE/UI evidence remain in the current support and validation records.

## Browser scoped Remote consumption

Mount the generated contribution once in its owning Cordis Fiber. Read the generated typed namespace through the consuming Context:

```typescript
import { mountRemote, getRemote } from './shared.mjs'

const mounted = await mountRemote(owner, connection, contextAdapters)
const remote = getRemote(agentContext, 'sharedData')
const result = await remote.scopedRead({ text: 'hello' })
const document = await remote.read({ suffix: '!' })
await mounted.dispose()
```

`getRemote` reads the existing `remote.sharedData` service; it creates no connection, registry or owner. Cordis tracing retains the consuming Context even when a method is saved as a callback. The borrowed adapter source supplies `getClient(key)` and an adapter's synchronous `identity(caller)`; both adapter and identity are read again on every invocation. It can be the same context-adapter owner used by reverse events when it provides both identity and resolve operations.

A receiver-scoped method supplies the declared identity field to select the Host Context. A lookup-scoped method supplies the declared lookup argument without changing that receiver. A defined identity, including null, selects the scoped route. Undefined or a missing adapter permits direct lookup only when the method also declares that direct route; receiver-only methods report an assembly failure. The direct lookup overload still requires its wire identity. Unary failures and stream failures retain their existing separate contracts. Generated argument objects retain omitted fields and explicit null rather than normalizing them.

The mounted contribution owns its methods and cancellation. Disposing it rejects retained callbacks and ends its work, while the borrowed Context, adapters and Connection keep their own owners. Namespace registrations continue to compose and retire through the existing mount queue. The `ctx` member is reserved for Cordis tracing. The explicit endpoint client remains available for direct wire calls; scoped application code uses the Context service above.

The ordinary DTO/STJ backend also accepts the framework `IEnumerable<T>`, `ICollection<T>`, `IReadOnlyCollection<T>` and `IList<T>` projections already recognized by the common wire model, alongside lists, arrays and string-key dictionaries. Referenced element declarations retain defaults and nullability. These are data carriers serialized by STJ, not execution of an author's enumerable implementation or arbitrary generic classes.

## Registration scopes and Peer event routing

Inside the owner's Cordis execution domain, create a registration scope and route an event to its opaque key:

```csharp
await using var scoped = Scope.Create(owner, subject);
scoped.Context.On("app/changed", (_, args) => {
    Observe(args[0]);
    return Undefined.Value;
});
owner.Events.EmitWith(Scope.Target(subject, subject), "app/changed", change);
```

The subject may implement `IEventFilter`; its filter is retained by the carrier. A carrier exposes no subject properties. Pass the actual subject in event arguments when the listener needs it. Generated Remote waterfall helpers do this automatically; their public invocation signature is unchanged.

`Scope.Of(context)` reads the nearest inherited tag. Supply `parent` to `Scope.Create` to bind routing ancestry, or keep the handle from `Scope.BindParent(key, parent)` when controlled reparenting is needed. Routing parents do not transfer ownership. `DisposeAsync` waits for owned cleanup, including a prior `DisposeRawAsync` call used by an ordered composite effect.

Remote methods use `TypertInvocation.Current!.Peer!` for the admitted Peer and register through `peer.Context`. A child scope with `parent: peer` routes upward to those Peer listeners. Do not dispose the borrowed Peer Context. A socket, logical stream or caller does not own the shared Connection's Peer, and no request argument is needed to identify it. The same mechanism serves the direct Gateway's owner-bound fallback.

### Guid in saved-model clients

Use `System.Guid` directly in ordinary data and Remote/event values with the default source-generated STJ context. For example, add this method to the normal `RemoteService` author and register `Item` with `JsonSerializable`:

```csharp
public sealed record Item(Guid Id);

[RemoteMethod("echo")]
public Task<Item> EchoAsync(Item value) => Task.FromResult(value);
```

Normal extract/build/pack preserves the Guid declaration identity. Independent C# data/callers use `global::System.Guid`; Web callers accept and return strings with string/uuid Schema. No framework source model, mapping annotation or Provider assembly is needed by the independent generator. Explicit references remain required for authored DTOs from another model.

`Guid?`, arrays, supported lists and string-key dictionary values compose with the same mapping. An omitted optional `Guid value = default` uses Guid.Empty; explicit null requires a nullable declaration. TypeScript string typing does not establish UUID validity: the Host's existing STJ codec admits or rejects actual values. A custom converter changes the contract and remains unsupported by the saved-model projection. Guid keys and application ID wrappers are not included.

### Numeric enums in saved-model clients

Keep numeric C# enums in Remote and ordinary data declarations and register the DTO in the author's JsonSerializerContext. Use the existing build/pack and referenced-model inputs. Independent .NET contracts generate their own enum declarations with the original underlying integer type, named constants, aliases, Flags and constant defaults. Web consumers use numbers: an unnamed `(MotionStatus)37` remains `37`. Nullable and array uses follow the same references. No provider DLL is needed by the independent consumer. Static projection supports public top-level enums with explicit source facts; it refuses string enum policies, custom enum converters and nested declarations. Actual Host metadata retains its own supported converter behavior. See [compatibility](compatibility.md#numeric-enum-projection-2026-10-16) for the upstream distinction and numeric precision boundary.

```csharp
public enum MotionStatus { Unavailable = -1, Succeeded = 4 }
public sealed class MotionResult
{
    public MotionStatus Status { get; set; } = MotionStatus.Unavailable;
    public MotionStatus? Previous { get; set; }
}
```

### Directional Web data contracts

Optional parameters and nullable values are independent. `[DisallowNull] double? expectedRevision = null!` accepts omission or a number, but rejects explicit JSON null. The generated TypeScript parameter is `expectedRevision?: number`.

A DTO may have different input and result shapes without a second declaration. `[JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Count { get; set; }` requires Count on input and allows Count to be absent on output. A nullable getter with DisallowNull still permits null in the result while rejecting it on input. Use the generated method's parameter and result types instead of reusing one directional type for both. Constructor defaults apply at the real Host; non-optional constructor parameters must be supplied even when RespectRequiredConstructorParameters is false or omitted.

`new TypertConnection(base)` and `new TypertConnection(base, globalThis.fetch)` both use the standard Connection. Bound functions and arrow overrides remain supported; consumers do not need to bind a native function to compensate for the library's call site.

Generated .NET methods use the same directional contracts automatically. Pass required zero/default values normally; input encoding includes them even when the author omits them when writing results. Returned DTOs accept the author's permitted nulls and omitted default fields. Do not override serializer options to compensate for these directions.

For optional JsonElement fields, Undefined can represent omission when the author declares conditional omission. Explicit JSON null is a present value. For example, a Settings unset operation leaves Value undefined; a set operation assigns a JSON value and still undergoes the existing form validation.

### Scope-aware contribution registries

Use the registration Context for both visibility and cleanup. A registry can keep several tables in one layer:

```csharp
sealed class MenuLayer : IScopeLayer
{
    public NamedEntries<string> Commands { get; } =
        new(name => new InvalidOperationException($"Duplicate command: {name}"));
    public AnonymousEntries<string> Hints { get; } = new();
    public bool IsEmpty => Commands.IsEmpty && Hints.IsEmpty;
}

var layers = new ScopedLayers<MenuLayer>(_ => new(), NotifyChanged);
var registration = layers.Effect(caller,
    layer => layer.Commands.Insert("refresh", "Refresh"), "refresh-command");
var visible = layers.Merge(Scope.Of(caller), layer => layer.Commands);
await registration.DisposeAsync();
```

Run registration in the owner's Cordis execution domain. The returned EffectHandle is also owned by caller; explicit disposal is optional and idempotent. Values remain borrowed. Use Peek for only one scope's own contributions, and ChainLayers for inherited overlays. Reads never create layers. Named and anonymous views remain live while their table generation is nonempty; a drained generation does not follow later registrations. A Remote method can use its current Peer Context without accepting an explicit identity argument.

### Open the Settings document

Mount SettingsForms over the session's ConfigurationOperations and SettingsController over the same Context. The default controller opens the provider's document on the host desktop. An embedded host can pass its own `Func<string, CancellationToken, Task>` as the controller's second constructor argument. That function receives the actual provider path, not a copy of redacted form data.

The generated .NET call is `await settingsClient.OpenSettingsDocumentAsync(cancellationToken: token)`. The standard TS call is:

```ts
const result = await settings['settings/openSettingsDocument']({})
```

Both return the ordinary RemoteResult with `{ opened: true }` on accepted handoff. Check `hasDocument` when presenting the action, and handle failure like other Settings operations. The managed application example exposes this host action; it does not download a document to the browser. Document opening does not bypass Gateway authorization or grant permission to edit arbitrary paths.

## Runtime numeric metadata and local causes

For a JIT author whose initializer semantics require reflection metadata, an explicit `JsonSerializerContext.GetTypeInfo` may return metadata from the application's configured `DefaultJsonTypeInfoResolver`. Keep declared naming/nullability facts aligned with that metadata. The generated binding still uses the same codec and Gateway; `TypertArtifacts.GenerateClient(contribution)` exports the runtime contract through the shared emitter. This route does not replay arbitrary reflection logic in saved-model clients or promise Native AOT support.

| Numeric policy | Input | Result and event output |
|---|---|---|
| Strict | JSON number, then native type/range admission | JSON number |
| AllowReadingFromString | JSON number or finite native numeric string | JSON number |
| WriteAsString / AllowNamedFloatingPointLiterals / other combinations | Affected metadata projection refused | Affected metadata projection refused |

Supported scalars are byte/sbyte, short/ushort, int/uint, long/ulong, float/double and decimal, in existing nullable and container shapes. Numeric string grammar is type-sensitive; native parsing still checks width and overflow. Unknown patterns are refused. Outside the supported static Remote defaults, omitted members keep the actual serializer's behavior; explicit zero and false remain present. Use declared wire names. Current schema admission is not fully case-insensitive even when the underlying Web serializer is. See [compatibility](compatibility.md#metadata-numeric-directions-2026-10-15) for that boundary and the exporter/native-parser difference.

Generated JS unary failures keep classification in `result.error.code`. Locally caught transport errors are also available unchanged in `result.error.cause`, including non-Error values. Cancellation preservation follows fixed DSH; preserving ordinary carrier causes is an explicit Cordis.NET extension. Remote business errors are rebuilt only from code/message/details. Cause is not wire data or an automatic logging policy, and no call is replayed automatically. Applications can inspect a reason they own without bypassing generated calls or Connection lifetimes; retain resource-bearing errors only as long as needed.

## Static data defaults on Remote input

Use the ordinary declaration and generated codec for a static data default:

```csharp
public sealed record Snapshot(int Id)
{
    public Policy Policy { get; init; } = new(false, []);
}
public sealed record Policy(bool Enabled, ImmutableArray<string> Overrides);
```

The extractor records the exact constructed type, parameter bindings and static arguments. The supported subset is constants, null, empty arrays and the finite BCL collection shapes already mapped by the backend, and nested non-generic primary record classes whose constructor only binds parameters to automatic data properties. Additional automatic properties without their own initializer or required wire obligation may keep their CLR defaults. Empty byte arrays project to the native base64 string, ordinary sequences to arrays, and supported string-key dictionaries to objects. Custom constructor/accessor bodies, behavioral member initializers, mutable static state, factories and arbitrary collection builders are not replayed. Unsupported standalone input initializers remain explicit refusals. Re-extract the owning source model when it previously retained only an expression; use the matching compiler and runtime and rebuild generated providers and clients. There is no new model generation or legacy expression interpreter.

Both generated Host bindings and independent C# client metadata use the same declaration facts. `CreateDeclared` accepts an optional `TypertInputDefault` query and exposes its values as input Schema `default` annotations. Its input decoder fills only absent, non-required fields before normal schema validation and STJ deserialization. Explicit null follows declared nullability, and explicit zero, false or object values remain present. Required fields remain required. The callback returns null for no default and a JSON null element for an actual null default.

For an independent client, read the method from `Client.Info.Descriptors`, then use `method.Parameters[index].Input().Parse(json)` or the strict variant. The decoded CLR value can be passed to the generated method normally. Host parameter codecs apply the same defaults before invoking the real Service. Plain `JsonSerializer.Deserialize` outside the codec is not this Remote input parser; generated JSON metadata alone does not repair STJ's parameterized-constructor/init-member omission behavior.

Input defaults do not fill missing outputs. `ResultSchema`, conditional output omission and required-result checks retain their own rules. The ordinary data-only backend and saved Web schema catalog retain their documented support limits; shared declaration facts do not imply that every backend executes every default.
