# Cordis.NET.Clr

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Clr/README.zh.md)

Load independently deployed CLR plugins from complete artifact directories, replace their code and request cooperative unload. The package also provides the SDK/NuGet toolchain used by profile installation.

## Install

Use the .NET 10 SDK and matching Cordis.NET package versions.

```console
dotnet add package Cordis.NET.Clr --version 0.2.1-alpha.1
```

## Use this package

Follow [Prepare → Publish → Validate → LoadDirectory](https://github.com/gentt1024/Cordis.NET/blob/main/docs/clr-artifacts.md) for stable artifact delivery. Start with the [independent plugin](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedPlugin/README.md) and [application](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md) for a complete local-feed installation. Preparing plugin source requires the selected .NET SDK and explicit feeds; starting an already prepared artifact does not rebuild it.

Register a `ClrModuleDefinition` and pass the Host's exact additional contract assemblies as `sharedContracts`. Use `session.ConfigurationOperations` for managed installation. Hosts must approve build execution separately from installation. For code replacement, see [CLR authoring](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.md#deliver-clr-plugins).

## Lifetime and support

Dynamic CLR loading requires an ordinary .NET runtime and executes with the Host process privileges; it is not a sandbox or a Native AOT path. Default loading uses stable directories directly. Use explicit shadow copies only for mutable external development outputs.

Stop owning Contexts before disposing the resolver. Unload request, GC collection and file deletion are separate observations. Logical removal retains files; physical deletion is an explicit offline operation. Native libraries retain CLR/OS behavior, not per-ALC state isolation.

NuGet consumers receive the executable Host's framework-reference capture through `buildTransitive`. An external source `ProjectReference` consumer must import `src/Cordis.Clr/buildTransitive/Cordis.NET.Clr.targets` in its executable project. Declare every framework intended for sharing; private application dependencies are not automatically shared.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
