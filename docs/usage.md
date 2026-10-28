# Composition and deployment

For service, event, configuration and callback authoring, see the [authoring guide](authoring.md)
([中文](authoring.zh.md)), including the generic/DSH startup choice and deployed patch resources.

`BootGenericAsync` and the authoring helpers below are included in published
`0.2.1-alpha.1`. Use matching package versions. Check [release and source status](development.md#current-support-and-release-status)
for the incremental-generation and referenced-assembly default fixes that are only on main.

Start with `examples/Composition` and `examples/Greeting.Plugin`. They use an ordinary
ProjectReference for development; the verification gate consumes their packed NuGet artifact
from a separate directory and isolated cache. The patch is an embedded assembly resource,
so deployed applications need no source checkout or developer NuGet-cache path.

For your own host, register code explicitly and load the file inside the Cordis execution
domain. This integration fragment assumes `cordis.yml` selects the registered `greeting`
module with entry ID `root:greeting`, and `GreetingModule` is referenced from the example project:

```csharp
await using var root = new Cordis.Context();
await root.RunAsync(async ctx =>
{
    var modules = new StaticModuleResolver().Register("greeting", GreetingModule.Plugin);
    var loader = new Loader(ctx, modules);
    var include = await ApplicationBoot.MountAsync(loader, "cordis.yml");
    await loader.UpdateAsync("root:greeting", new EntryOptions { Disabled = true });
});
```

Use the actual IDs returned by `CreateAsync` or `Loader.Entries()` when working with nested
trees. `WaitAsync` waits for current work to converge; an unavailable injected service can
leave a Fiber pending. It does not wait indefinitely for future providers.

For a profile, load it with `Profiles.LoadAsync(profileDirectory, installationBundles,
localBundles)` and create a `ProfileLaunch(profile, home, overlays, installationBundles,
localBundles)`. `ProfileSession.StartAsync(configurationPath, launch, modules,
enableHmr: true)` owns the Context, Loader, Include and watchers. Dispose the session to stop
them. `RefreshAsync` also works with automatic watching disabled. Required entries, prepare
callbacks, expression evaluator and application readiness are explicit optional arguments.

Layers apply in bundle order, then profile patch, home patch and immutable launch overrides.
`ProfileComposition.PreviewAsync` labels source runs. `PluginConfigurationOperations` provides
inventory and persisted switches. Use `session.ConfigurationOperations` for a running
session; it already reconciles the session-owned Include inside the lifecycle queue, even
when automatic watching is disabled. Standalone, startup-only operations may omit
`RunExclusiveAsync`. Hosts composing an independent Include must supply their own exclusive
queue and reconciliation callback. See the [authoring guide](authoring.md) for the managed path.

Code HMR is separate from file/config changes. With the ordinary CLR, register a
`ClrModuleDefinition(bundleDirectory, relativeAssemblyPath, entryType)` on a
`ClrModuleResolver`. The entry implements `IClrPluginModule`; shared contracts must be the
host's exact assemblies. The default loads a complete stable directory directly. Publish new
code to another complete directory and retain old files while they have consumers. Replace with:

```csharp
await resolver.ReplaceAsync("my-plugin", nextDefinition,
    pairs => new ValueTask(loader.ReplacePluginsAsync(pairs)));
```

Stop the owning contexts before disposing the resolver. Inspect `ClrUnloadObservation` for
unload request, collection and the load directory independently. Production never forces GC.
For library-owned installation, verification, restart updates and explicit offline file removal,
use the [standard CLR artifact workflow](clr-artifacts.md). Explicit `ShadowCopy` remains available
for quiescent external development outputs that must later be rebuilt in place.
Use HMR's serialized mutation queue to coordinate code and configuration changes.
`hmr.TrackLoader(loader, resolver.LocateAsync)` connects entry-resolution diagnostics without
loading extra assemblies. It prefers the loaded module's filename and uses the locator only
when that identity has no filename. Register replacement callbacks and dependency edges explicitly.

Without the `Cordis.NET.JavaScript` package, evaluating a raw `!!js` fails explicitly. Pass
`new JintExpressionEvaluator()` to Loader, ApplicationBoot or ProfileSession for real JS.
Service/function access is through the live context. ApplicationBoot.BootAsync supplies the DSH
home path service; BootGenericAsync does not. A ProfileSession uses its own launch home.
This adapter requires ordinary JIT.

For Generic Host, call `services.AddCordis(options => options.Borrow<MyService>("service")
.Configure((ctx, services, cancellation) => ...))`. The container owns `MyService`; Cordis
unpublishes it without disposing it twice. No Host package is needed for standalone use.

The tool validates file structure without activating plugins, so schema, dependency and JS
errors remain activation diagnostics:

```sh
cordis validate cordis.yml
cordis preview cordis.yml --patch cordis.patch.yml --json
```
