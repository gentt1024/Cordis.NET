# Cordis.NET.Core

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Core/README.zh.md)

Build an in-process plugin application with services, dependency-driven activation, events and cleanup owned by plugin instances. Core works without Node, a network server or Remote.

## Install

Use the .NET 10 SDK and matching Cordis.NET package versions.

```console
dotnet add package Cordis.NET.Core --version 0.2.1-alpha.1
```

## Use this package

Start with [your first plugin](https://github.com/gentt1024/Cordis.NET/blob/main/docs/getting-started.md). The complete console program registers a plugin, reads its service, prints `41` and disposes it.

Use `Context.RunAsync` when entering Cordis from application code. `Plugin<T>` defines a plugin; the returned `Fiber` represents its registered instance. [Core concepts](https://github.com/gentt1024/Cordis.NET/blob/main/docs/core-concepts.md) explains service views, dependencies and effects. Add Composition when configuration should select modules and instances.

## Lifetime and support

A Fiber owns its registrations and registered cleanup. Withdrawing a service does not dispose the supplied object. `WaitAsync` waits for current lifecycle work; a missing dependency can leave the Fiber pending. Static applications support Native AOT. Runtime DLL loading belongs to Clr and requires an ordinary runtime.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
