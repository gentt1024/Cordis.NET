# Run the managed application example

English | [中文](README.zh.md)

Build a local plugin feed, start an ASP.NET Core Host, then use its browser page to install a CLR plugin and edit its configuration. This is a loopback development application with native profile, package, Settings and browser-module owners.

## Prepare the environment

Use a source checkout, its `global.json` SDK, the matching ASP.NET Core runtime, and access to the configured NuGet and npm sources. Building the browser assets needs Node 22 or newer. Run all commands below from the repository root.

The application requires a runtime directory and at least one client package directory. The browser assets are not optional for this example; using prebuilt matching directories avoids their local Node build. For a C#-only host, follow the [CLI guide](../../tools/Cordis.Cli/README.md) instead.

Use a fresh local feed and profile for this walkthrough. The paths below are examples; if they already hold another source batch, choose new paths consistently. Main can contain unpublished fixes despite sharing the published version property; see [source status](../../docs/development.md#current-support-and-release-status).

## Build a complete local plugin feed

Run this block in PowerShell. It packs the source Cordis dependencies, the plugin and the exact locked YamlDotNet dependency:

```powershell
New-Item -ItemType Directory -Path artifacts/managed-feed -Force | Out-Null
dotnet restore examples/ManagedPlugin/ManagedPlugin.csproj
foreach ($managedExampleProject in @('Core','Composition','Clr')) {
  dotnet pack "src/Cordis.$managedExampleProject/Cordis.$managedExampleProject.csproj" -c Release -o artifacts/managed-feed -p:IsPackable=true
}
dotnet pack examples/ManagedPlugin/ManagedPlugin.csproj -c Release -o artifacts/managed-feed -p:IsPackable=true
$managedExampleCache = (dotnet nuget locals global-packages --list) -replace '^[^:]+:\s*', ''
$managedExampleLock = Get-Content examples/ManagedPlugin/packages.lock.json -Raw | ConvertFrom-Json
$managedExampleYaml = $managedExampleLock.dependencies.'net10.0'.YamlDotNet.resolved
Copy-Item -LiteralPath (Join-Path $managedExampleCache "yamldotnet/$managedExampleYaml/yamldotnet.$managedExampleYaml.nupkg") -Destination artifacts/managed-feed
```

The feed must contain `Example.ManagedPlugin` version `1.0.0-alpha.1`, the matching Core/Composition/Clr packages, and the YamlDotNet version from the lock. This example accepts a complete local directory, not a NuGet v3 URL. The package library and CLI have their own explicit source configuration. An incomplete feed fails installation; correct it, inspect again and install with a new request ID.

## Prepare the browser directories

To build the runtime and the independent provider/panel examples locally:

```console
npm ci --prefix reference --ignore-scripts
npm ci --prefix clients/modules --ignore-scripts
npm ci --prefix clients/tools
node scripts/build-client-modules.mjs artifacts/client-runtime
node clients/tools/build.mjs clients/modules/examples/provider artifacts/client-provider
node clients/tools/build.mjs clients/modules/examples/panel artifacts/client-panel
```

Keep the complete output directories, including manifests and licenses. Provider and panel entries are `client.ts`; their packages declare client-only bundles with `dsh.bundle.patch: []`. These outputs are local builds, not a new public release.

## Start the Host and install the plugin

Start the application:

```console
dotnet run --project examples/ManagedApplication -- artifacts/managed-profile artifacts/managed-feed artifacts/client-runtime artifacts/client-provider artifacts/client-panel
```

Open `http://127.0.0.1:5187`. For a fresh profile, the supplied client packages become selected bundles. Existing profile selection is retained on later starts. Inspect and install `Example.ManagedPlugin` at exact version `1.0.0-alpha.1`; installation uses the inspection's `contentHash` and a unique request ID. Wait/cancel observe that same task. A lost response is queried through the same generation's active wait operation; an unknown result is not success.

The example allows loopback requests with its local Host/Origin checks. It supplies no deployed account policy. Applications must define their own identity, authorization and build approval.

## Edit configuration and observe lifecycle changes

Use Settings for the filtered live form with numeric revisions. The Settings namespace is the profile row ID, `example.managedplugin`. Use Configuration for separately authorized raw configuration and schemas; its runtime address is `root:example.managedplugin`, with a separate source hash. Both use one ordered configuration writer.

Change `limit` from 1 to 7: the plugin accepts integer values from 1 through 100 and updates its live reference without ordinary reactivation. Use Configuration to change the ordinary `label`; it is not a live Settings field and follows Loader replacement. `credential` is a live secret: reads expose presence, not its value. Describe again after a write.

Disable the browser provider to withdraw its dependent panel; re-enable it to restore the selected graph. Temporary transport loss preserves plugins and drafts. Recovery subscribes before reading state and graph and never automatically replays writes. [Settings](../../docs/settings.md) and [management](../../docs/management-remote-migration.md) describe the public contracts.

## Run an author watcher when needed

To develop the provider, append the approved worker options to the complete launch command:

```console
dotnet run --project examples/ManagedApplication -- artifacts/managed-profile artifacts/managed-feed artifacts/client-runtime artifacts/client-provider artifacts/client-panel --watch=clients/modules/examples/provider --client-build-script=clients/tools/build.mjs
```

`--node=` can select the Node executable. Successful frames supply complete immutable generations; the Host checks identity/hash, captures the catalog through the profile queue and emits `client-modules/changed`. Compiler errors retain the last catalog. A failed callback, invalid/oversized frame or unconfirmed process cleanup is a failure.

Use Stop compiler in the page to cancel a worker. It waits for child termination and output settlement. Restart watch after changing package identity or external requests. Stopping compilation retains the last artifact; profile selection controls module withdrawal.

## Stop and retain the example state

Stop the Host with Ctrl+C and wait for it to exit before reusing the profile or deleting retained files. Shutdown closes the same worker owners and waits for catalog work. Prior build directories are retained. Logical package removal does not delete installed CLR bytes; follow [offline deletion](../../docs/clr-artifacts.md#delete-retained-files-offline).

A deployed application can serve precompiled browser bytes without author sources or Node. This source walkthrough does not establish new packaged, cross-platform, AOT or browser acceptance.
