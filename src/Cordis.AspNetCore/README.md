# Cordis.NET.AspNetCore

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.AspNetCore/README.zh.md)

Expose selected Cordis services through authorized HTTP unary and WebSocket mux routes. Mount management over a running ProfileSession without creating another configuration or lifecycle authority.

## Install

Use the .NET 10 SDK and matching Cordis.NET package versions.

```console
dotnet add package Cordis.NET.AspNetCore --version 0.2.1-alpha.1
```

## Use this package

Use a .NET 10 ASP.NET Core Host.

1. Declare the Service and provide its actual instance in the existing Cordis Context.
2. Register its generated contribution in `TypertRegistry` and create a Host-owned `TypertHostConnection` in that execution domain.
3. Add `UseWebSockets()` before mapping `MapCordisRemote`, and supply the Host's authorization callback.
4. Connect the generated client to that mapped Remote address.

Follow [Host and client wiring](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.md#register-call-and-withdraw-contracts) for the integration code. For profile management, `CordisManagement.Map` exposes host state, raw configuration and client artifacts; `MountPluginManagerAsync` mounts generated management over the same operations. [ManagedApplication](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md) provides a complete application.

Use the [Settings guide](https://github.com/gentt1024/Cordis.NET/blob/main/docs/settings.md) for redacted live forms. Use `MapCordisService` for a selected in-process service with explicit `JsonTypeInfo` contracts.

## Lifetime and support

The Host supplies authentication, authorization and request policy; this adapter supplies no account system. Build permission is separate from installation permission. Full configuration uses `configuration-read` and `configuration-write`, because raw values and defaults can contain secrets.

Management mutations require the generation returned by `/state`; reads may also send `If-Cordis-Generation`. Reconnect subscribes before reading current state and graph; notifications have no durable replay guarantee. Close clients and transports before disposing their borrowed Context and services. A selected AOT path requires explicit serializer metadata; dynamic CLR loading still requires an ordinary runtime.

Build authorization permits one preparation, including dependency targets resolved from the selected sources. The inspected root archive hash binds that root package to preparation; it does not approve every transitive dependency hash. The separate `build` permission callback can inspect the source and root hash.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
