# Composition and deployment

For service, event, configuration and callback authoring, see the [authoring guide](authoring.md)
([中文](authoring.zh.md)), including the generic/DSH startup choice and deployed patch resources.

`BootGenericAsync` and the other authoring helpers require the authoring release listed in
the [release notes](../CHANGELOG.md). Until it is available on NuGet, use the project references
and source commands in that guide; older packages do not include these APIs.

Start with `examples/Composition` and `examples/Greeting.Plugin`. They use an ordinary
ProjectReference for development; the verification gate consumes their packed NuGet artifact
from a separate directory and isolated cache. The patch is an embedded assembly resource,
so deployed applications need no source checkout or developer NuGet-cache path.

Register available code explicitly, then load the file in a Cordis execution domain:

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
inventory and persisted switches. Set its `RunExclusiveAsync` callback to
`session.Hmr.RunExclusiveAsync` when integrating a management surface; the operations then
reconcile their owned Include inside that queue. Leave it absent for startup-only changes.
See `PluginManagerOriginalTests` for complete wiring and failure behavior.

Code HMR is separate from file/config changes. With the ordinary CLR, register a
`ClrModuleDefinition(bundleDirectory, relativeAssemblyPath, entryType)` on a
`ClrModuleResolver`. The entry implements `IClrPluginModule`; shared contracts must be the
host's exact assemblies. Keep the shadow directory outside the bundle. Replace with:

```csharp
await resolver.ReplaceAsync("my-plugin", nextDefinition,
    (oldPlugin, newPlugin) => new ValueTask(loader.ReplacePluginAsync(oldPlugin, newPlugin)));
```

Stop the owning contexts before disposing the resolver. Inspect `ClrUnloadObservation` for
unload request, collection and shadow deletion independently. Production never forces GC.
Use HMR's serialized mutation queue to coordinate code and configuration changes.
`hmr.TrackLoader(loader, resolver.LocateAsync)` connects entry-resolution diagnostics without
loading extra assemblies; register replacement callbacks and dependency edges explicitly.

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
