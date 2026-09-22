# Getting started

[中文](getting-started.zh.md)

## Prerequisites

Use the .NET SDK selected by `global.json`. The repository restores with locked dependency files.

## Run the example

```console
dotnet restore examples/Composition/Composition.csproj --locked-mode
dotnet run --project examples/Composition/Composition.csproj
```

The example uses `StaticModuleResolver`, so it runs in JIT and can be published with Native AOT. Its project references build the current source, including the unpublished `PatchResources` helper used by the Greeting plugin. Keep those project references when running this example. Start with [core concepts](core-concepts.md) before replacing its greeting plugin.

## Install from NuGet

```console
dotnet add package Cordis.NET.Composition --prerelease
dotnet add package Cordis.NET.Extensions --prerelease
dotnet tool install --global Cordis.NET.Tool --prerelease
```

Run the package commands in your application's project directory; they select the latest published runtime packages, including prereleases, and record the selected versions in your project. This is separate from running the source example above. The Greeting plugin is example source in this repository, not an additional public package to install.

The helpers described in the [authoring guide](authoring.md), including `EventKey<T>` and `BootGenericAsync`, are not yet published. Run the Probes examples from this source checkout; installing the published packages does not enable those APIs. Source builds and local package examples use the version from [Directory.Build.props](../Directory.Build.props), which is not a promise that the current source has been published.

Use `Cordis.NET.Clr`, `Cordis.NET.Hosting`, or `Cordis.NET.JavaScript` only for the corresponding optional adapter. Package IDs have the `Cordis.NET.*` prefix; the source namespaces remain `Cordis.*`.

## Validate configuration

```console
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- validate examples/Greeting.Plugin/cordis.patch.yml
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- preview examples/Greeting.Plugin/cordis.patch.yml
```

The installed tool exposes the same commands as the source invocation: `cordis validate <path>` and `cordis preview <path>`.
