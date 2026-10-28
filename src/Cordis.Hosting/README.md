# Cordis.NET.Hosting

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Hosting/README.zh.md)

Integrate one Cordis root with Microsoft Generic Host startup and shutdown. Publish selected dependency-injection services into Cordis while keeping their ownership in the DI container.

## Install

Use the .NET 10 SDK and matching Cordis.NET package versions.

```console
dotnet add package Cordis.NET.Hosting --version 0.2.1-alpha.1
```

## Use this package

In an application that already uses Generic Host, call `services.AddCordis(...)`. Add `options.Borrow<MyService>("service")` for an existing registered service and use `options.Configure((ctx, services, cancellation) => ...)` for asynchronous startup. The callback runs inside the Cordis execution domain. See the [Generic Host setup](https://github.com/gentt1024/Cordis.NET/blob/main/docs/usage.md) and [public options and implementation](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Hosting/CordisHostExtensions.cs).

For a standalone application, Core's root Context is sufficient; the Hosting package is optional.

## Lifetime and support

Generic Host coordinates the root's startup and shutdown. A failed startup disposes that Context. Borrowed services are unpublished on shutdown and disposed by their original container. This static adapter supports Native AOT; dynamically loaded plugins and serializer choices have their own limits.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
