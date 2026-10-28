# Cordis.NET

English | [中文](README.zh.md)

Cordis.NET lets you build C# applications from plugins that provide services, declare dependencies and release their registrations when they stop. Add configuration, live updates and separately deployed CLR plugins as your application needs them. You can also expose selected services to .NET and Web clients through Remote.

The project is a faithful, idiomatic .NET port of the DSH-maintained Cordis runtime and its application infrastructure. Ordinary plugins run in process and do not require Node or Remote.

## Get started

Install the published preview, `0.2.1-alpha.1`, in a .NET 10 application:

```console
dotnet new console -n MyApp -f net10.0
dotnet add MyApp package Cordis.NET.Core --version 0.2.1-alpha.1
```

Follow [your first plugin](docs/getting-started.md) for the complete program, expected output and shutdown. No repository checkout is required.

The [release page](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1) provides browser runtime and author-tool archives, checksums and validation evidence. Use matching versions of the Cordis.NET packages and generated tools. **Current main includes unpublished fixes** for incremental generation and Host input defaults from referenced assemblies; see [release and source status](docs/development.md#current-support-and-release-status).

## Choose what to add

| If you need to… | Use |
|---|---|
| Register plugins, provide services and manage their lifetime | `Cordis.NET.Core`; start with [core concepts](docs/core-concepts.md). |
| Compose an application from configuration and update it while running | `Cordis.NET.Composition`; see the [Composition example](examples/Composition/Program.cs) and [Settings](docs/settings.md). |
| Expose selected services to generated .NET or Web clients | `Cordis.NET.Composition` for declarations, bindings and clients; `Cordis.NET.AspNetCore` for the HTTP/WebSocket adapter. See [Remote authoring](docs/authoring.md#declare-and-generate-native-remote-contracts). |
| Install and load independent CLR plugins | `Cordis.NET.Clr`; follow [CLR artifacts](docs/clr-artifacts.md), the [managed plugin](examples/ManagedPlugin/README.md) and [managed application](examples/ManagedApplication/README.md). |
| Integrate with Microsoft Generic Host | `Cordis.NET.Hosting`; see its [package guide](src/Cordis.Hosting/README.md). |

`Cordis.NET.Tool` supplies the `cordis` CLI. `Cordis.NET.Extensions` provides runtime extensions, and `Cordis.NET.JavaScript` evaluates trusted configuration expressions through Jint. Jint is not a sandbox. Static Core/Composition applications support Native AOT; dynamic CLR loading and Jint require an ordinary runtime. Package IDs use `Cordis.NET.*`; namespaces and assemblies use `Cordis.*`.

## Find the documentation

- **Learn:** [first plugin](docs/getting-started.md), [core concepts](docs/core-concepts.md).
- **Build an application:** [authoring](docs/authoring.md), [Settings](docs/settings.md), [Remote events](docs/remote-events.md), [CLR artifacts](docs/clr-artifacts.md).
- **Check support and design:** [supported backends](docs/development.md#generation-backend-support), [architecture](docs/architecture.md), [compatibility](docs/compatibility.md), [fixed upstream sources](docs/upstream.md).
- **Contribute:** [development and verification](docs/development.md), [contributing](CONTRIBUTING.md), [release procedure](docs/publication-readiness.md).
- **Look up a release:** [changelog](CHANGELOG.md), [validation evidence](docs/validation.md#released-version-evidence), [security](SECURITY.md), [notices](NOTICE.md).

To explore from source, use the SDK pinned in `global.json` and run:

```console
dotnet run --project examples/Composition/Composition.csproj
```

## Project scope

Behavior follows the fixed revision in [upstream.lock.json](upstream.lock.json), currently DSH `0.2.1-rc.2` with vendored Cordis `4.0.4`. [Compatibility](docs/compatibility.md) records the .NET adaptations and supported interoperability. Arbitrary TypeScript plugins are not a supported .NET deployment format.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft. It is an early preview; public APIs may change before a stable release. Published package and tag identities remain fixed.
