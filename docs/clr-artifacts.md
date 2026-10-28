# Install and replace CLR plugin artifacts

English | [中文](clr-artifacts.zh.md)

Load complete stable artifacts directly, preserve their shared contracts, and retire them through their owners. Starting another process or collectible AssemblyLoadContext does not require another bundle copy. Dynamic CLR loading requires an ordinary .NET runtime.

## Prepare the package and Host

<a id="standard-package-workflow"></a>

Build a normal plugin NuGet package with `cordis.plugin.json`, private dependencies, resources and any Worker assets. Keep application data outside the code directory. Use the selected .NET SDK for preparation and explicit feeds containing the complete dependency set. [ManagedPlugin](../examples/ManagedPlugin/README.md) and [ManagedApplication](../examples/ManagedApplication/README.md) provide the package and Host.

Create `ClrModuleResolver(sharedContracts: [...])`, passing the Host's exact additional contract assemblies, then `DotnetPluginToolchain(profileDirectory, resolver, sources)`. Supply `toolchain.Bundles` as `ProfileLaunch.LocalBundles` and use `session.ConfigurationOperations`. The toolchain owns acquisition, preparation, file receipts, publication and retained directories; the application owns source selection, build approval and business readiness.

## Install, publish and start

1. Call InstallPackageAsync with an exact version and explicit build approval. Preparation performs the SDK deployment publish once into independent `.cordis/work` output. It writes the plugin's deployment dependency manifest under its own DLL basename for AssemblyDependencyResolver. A product wrapper may finish its metadata before returning the prepared package.
2. Toolchain publication moves that complete output into `.cordis/packages/<name>/<version>`, records the file set and SHA256 hashes in adjacent `.<version>.files.json`, then verifies and registers it. The receipt is outside the artifact tree; existing version directories and receipts are not overwritten. Admission and complete prepared/published tree comparison remain part of installation.
3. Startup verifies the receipt and identity before code registration, then loads the stable directory directly and reuses its module lease. Repeated processes verify, but do not recopy it. Missing, incomplete or modified deployments fail without rewriting the receipt.

Receipts detect changes at verification points. They are not publisher authentication or a hostile-filesystem sandbox. For external directories managed outside the toolchain, the deployment owner must supply complete stable files through the low-level resolver API; installed-package startup still requires its own receipt.

## Upgrade, remove and reinstall

An installed profile-owned upgrade prepares and saves the new exact version, then reports `restart-required`. The current process retains its old code route and normal configuration edits still target that implementation. Watched selection can enable or disable it before restart. Restart reads the saved version; a failed startup does not automatically restore the previous version. Same-version repeat installation and replacement of installation-owned packages remain refused.

RemovePackageAsync deselects the package, settles owned Fibers, and removes its profile dependency and resolver reference. Removal queries GetLoadedModulesAsync before changing selection. Direct or foreign Fibers produce `restart-required` with `unmanaged-plugin`. Toolchain wrappers must forward this query and GetRetainedDirectories. If persistence fails after withdrawal, removal can be retried to finish the dependency edit.

Reinstallation after removal can load a new dependency closure in the same session. Same-version reuse requires a valid receipt and identical complete prepared content; another valid tree is `package-moved`, and corruption remains a failure. Retained files alone do not permit re-enable. Do not alter an installed tree to force equality.

Logical removal preserves bytes for retained references, delayed loads, Workers and other processes. PackageChange.Residuals reports retained versions, including an old live version and a version waiting for restart. It does not report physical deletion.

## Delete retained files offline

Stop every Host, Worker and other consumer first. Then call `DotnetPluginToolchain.DeleteRetainedArtifactAsync(profileDirectory, name, version)` or use the CLI:

```console
cordis delete-retained <profile> <name> <version>
```

A profile referencing the exact version refuses deletion; the CLI also refuses a running standard CLI host. The API holds the existing `package.json.cordis-lock` writer lock while checking references and deleting, and refuses a conflicting coordinated writer. Invalid or unreadable manifests leave the artifact unchanged. The deployment owner must exclude external consumers and writers that ignore this protocol. A failure reports the remaining path; there is no automatic scan or GC-triggered deletion.

## Replace implementation code during development

<a id="development-and-implementation-hmr"></a>

Publish a complete directory for each code revision and keep prior output unchanged while consumers use it. Register a ClrModuleDefinition, then ReplaceAsync with the Loader replacement callback. [CLR Probes](../examples/Probes.Clr/Program.cs) uses distinct prepared V1 and V2 directories.

LoadMode defaults to StableDirectory. Select ShadowCopy only for an external development directory that must later be rebuilt in place. Finish and quiesce the entire build before preparation; copying cannot snapshot an active build. Source and copy must not overlap, and the bundle includes private/native dependencies, Workers and resources.

ClrUnloadObservation.LoadDirectory identifies the retiring instance. ShadowDirectory is null for stable loading, and TryDeleteShadow never deletes the stable source. Delete an explicit copy only after all consumers stop; managed collection does not prove that precondition. Native locks can delay deletion. Business retirement, activation, unload request, collection and deletion are separate; production does not force GC. Separate ALCs do not guarantee independent native static state.

## Inspect the prepared replacement

Use admitCandidate to inspect the actual contract before withdrawal. The optional callback receives registered names including aliases and inactive exports. GetContribution is null without IClrTypertModule; aliases share one module, plugin and cached contribution. Ordinary resolver reads still see the published generation.

This integration fragment shows the successful switch path. The resolver, Loader, Typert loader (`artifacts`), prepared replacement definitions and affected exact Loader request names must already exist:

```csharp
await resolver.ReplaceAsync(
    replacements,
    switchFibers: async pairs =>
    {
        await artifacts.SuspendAsync(affectedRequests);
        await loader.ReplacePluginsAsync(pairs);
    },
    admitCandidate: candidate =>
    {
        foreach (var name in candidate.Specifiers)
            if (candidate.GetContribution(name) is { } contribution)
                CheckApplicationContract(contribution);
        return ValueTask.CompletedTask;
    },
    cancellationToken: cancellationToken);
await artifacts.ResumeAsync(affectedRequests);
```

CheckApplicationContract is application policy. Read an invocation's result codec ResultSchema for output, with its normal lazy projection/diagnostic behavior. Copy JSON Schema and strings for long-term retention instead of keeping plugins, codecs, delegates or Types.

Admission failure or pre-switch cancellation retires the candidate and keeps old mappings. A switching failure requires checking the existing recovery result: resume affected contracts only after confirmed commit or successful recovery, and retain the original failure. The snippet does not implement that application failure branch. The view closes when admission ends; already captured CLR objects remain valid objects. Factories and schema preparation execute trusted author code and their side effects are outside rollback. Installed-package upgrades still use restart, not this development replacement path.
