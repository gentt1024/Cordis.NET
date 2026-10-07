# Cordis.NET.Clr

```console
dotnet add package Cordis.NET.Clr --prerelease
```

Optional managed assembly module resolver for Cordis.NET. It loads, replaces, rolls back, and cooperatively unloads CLR plugin assemblies. This package requires an ordinary .NET runtime and is not Native AOT compatible.

Loaded assemblies execute with the host process privileges. Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft.

The NuGet `buildTransitive` target captures the executable host's SDK framework references. This supports declared additional frameworks in self-contained folder and single-file deployments while excluding private app dependencies. A `ProjectReference` consumer outside this repository must import `src/Cordis.Clr/buildTransitive/Cordis.NET.Clr.targets` in the executable project. Framework-dependent hosts also retain shared-framework deps discovery. The host must declare every framework it intends to share.

Current source also provides a NuGet package toolchain with explicit feeds and host-approved build execution. Application package management reuses `PluginConfigurationOperations`; it is an optional layer above Core. Follow the [independent managed plugin](../../examples/ManagedPlugin/README.md) and [application](../../examples/ManagedApplication/README.md) examples for building, installation, configuration and removal. These additions require `0.2.0-alpha.3` or later; they are not in the published `0.2.0-alpha.1` package.
