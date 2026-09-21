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

## Install from NuGet

```console
dotnet add package Cordis.NET.Composition --version 0.1.0-alpha.2
dotnet add package Cordis.NET.Extensions --version 0.1.0-alpha.2
dotnet tool install --global Cordis.NET.Tool --version 0.1.0-alpha.2
```

Use `Cordis.NET.Clr`, `Cordis.NET.Hosting`, or `Cordis.NET.JavaScript` only for the corresponding optional adapter. Package IDs have the `Cordis.NET.*` prefix; the source namespaces remain `Cordis.*`.

## Validate configuration

```console
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- validate examples/Greeting.Plugin/cordis.patch.yml
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- preview examples/Greeting.Plugin/cordis.patch.yml
```

The installed tool exposes the same commands as the source invocation: `cordis validate <path>` and `cordis preview <path>`.
