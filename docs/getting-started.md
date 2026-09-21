# Getting started

[中文](getting-started.zh.md)

## Prerequisites

Use the .NET SDK selected by `global.json`. The repository restores with locked dependency files.

## Run the example

```console
dotnet restore examples/Composition/Composition.csproj --locked-mode
dotnet run --project examples/Composition/Composition.csproj
```

The example uses `StaticModuleResolver`, so it runs in JIT and can be published with Native AOT. Start with [core concepts](core-concepts.md) before replacing its greeting plugin.

## Validate configuration

```console
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- validate examples/Greeting.Plugin/cordis.patch.yml
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- preview examples/Greeting.Plugin/cordis.patch.yml
```

Packages are not on NuGet yet. Build from source until a release and package feed are linked from this repository.
