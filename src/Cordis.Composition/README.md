# Cordis.NET.Composition

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Composition/README.zh.md)

Compose configured applications with Loader, profiles and coordinated edits, or declare selected Remote services and generate their .NET and Web consumers. The package includes the Typert analyzer, compiler and MSBuild targets.

## Install

Use the .NET 10 SDK and matching Cordis.NET package versions.

```console
dotnet add package Cordis.NET.Composition --version 0.2.1-alpha.1
```

## Use this package

- Load statically registered modules and update their configuration: [Composition example](https://github.com/gentt1024/Cordis.NET/blob/main/examples/Composition/Program.cs).
- Expose live configuration forms through the existing profile operations: [Settings guide](https://github.com/gentt1024/Cordis.NET/blob/main/docs/settings.md).
- Declare a Remote Service, register its contribution and connect a caller: [Remote authoring](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.md#declare-and-generate-native-remote-contracts).
- Extract declarations or generate independent DTOs and clients: [model and client workflows](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.md#generate-models-and-independent-clients).

C# generation uses the selected .NET SDK and explicit STJ metadata; it does not require Node. Browser author compilation uses separate Node tools.

## Lifetime and support

Ordinary plugin calls stay in process. Remote clients, Registry, Gateway and Connection reuse the application's Context; the Host owns authorization and connection lifetime. Static composition supports Native AOT when its serializers and selected backend shapes support it. See [backend limits](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.md#generation-backend-support).

Published alpha.1 does not contain the later incremental-generation and referenced-assembly Host-default fixes on main. Check [release and source status](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.md#current-support-and-release-status) before choosing tools. Settings uses numeric form revisions; full-configuration hashes have separate source-consistency duties.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
