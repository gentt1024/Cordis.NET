# Build an independent managed plugin

English | [中文](README.zh.md)

This CLR plugin exposes live settings and an ordinary label. `Module.cs` implements the public entry declared in `cordis.plugin.json`; its validator defines defaults and accepted values, and its description uses the same contract.

## Prepare the build

Use the SDK pinned in the source checkout and run commands from the repository root. No npm manifest or Node is required for this C# plugin. First prepare the Cordis packages and locked YamlDotNet dependency with [ManagedApplication](../ManagedApplication/README.md#build-a-complete-local-plugin-feed). Skip that guide's browser steps when using a C#-only CLI host.

## Pack the plugin separately

To place the plugin in a separate feed:

```console
dotnet pack examples/ManagedPlugin/ManagedPlugin.csproj -c Release -p:IsPackable=true -o artifacts/example-feed
```

The package identity is `Example.ManagedPlugin`, version `1.0.0-alpha.1`. It references matching Core/Composition/Clr package versions through its build. Keep the complete declared dependency set available to preparation. This example is excluded from the product release whitelist.

## Install and exercise its settings

Pass both `artifacts/managed-feed` and `artifacts/example-feed` through repeated `--source` options of the CLI host, or use the complete local feed in ManagedApplication. Inspect and install the exact version with explicit build approval; see the [CLI walkthrough](../../tools/Cordis.Cli/README.md).

The profile row ID and Settings namespace are `example.managedplugin`; the full runtime address is `root:example.managedplugin`. `limit` accepts integers from 1 through 100 and defaults to 1. Its live reference updates without ordinary reactivation. `credential` is also live and secret. Use full Configuration to change ordinary `label`, default `worker`; it uses normal Loader replacement and is not offered as live Settings.

Remove through the running profile's operations. Files remain after logical removal; stop all consumers before [offline deletion](../../docs/clr-artifacts.md#delete-retained-files-offline). Dynamic CLR loading requires an ordinary runtime and runs with Host process privileges.
