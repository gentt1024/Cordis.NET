# Cordis.NET.Extensions

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Extensions/README.zh.md)

Run an application's configured profile with ProfileSession, coordinate refresh and HMR, and add timer and console services. This package builds on Core and Composition.

## Install

Use the .NET 10 SDK and matching Cordis.NET package versions.

```console
dotnet add package Cordis.NET.Extensions --version 0.2.1-alpha.1
```

## Use this package

Use `ProfileSession.StartAsync` to own the Context, Loader, configuration include and optional watchers. Use `session.ConfigurationOperations` for edits and package operations, so they share the session's queue. Follow [composition and deployment](https://github.com/gentt1024/Cordis.NET/blob/main/docs/usage.md) or the complete [managed application](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md).

Call `RefreshAsync` for manual refresh even with watching disabled. Inspect its `ProfileReloadResult`, including `RequiresRestart` and warnings. [Application authoring](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.md#start-and-configure-an-application) covers startup policy, diagnostics and configuration admission.

## Lifetime and support

Dispose the session to stop its owned runtime and watchers. HMR coordinates updates; it does not supply a business transaction or force arbitrary business work to stop. Static applications can use Native AOT. Dynamic CLR loading and optional `!!js` expressions require their separate ordinary-runtime adapters.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
