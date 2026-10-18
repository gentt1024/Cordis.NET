# Cordis.NET

[中文](README.zh.md)

Cordis.NET is a faithful, idiomatic .NET port of the fixed DSH-maintained Cordis runtime and generic application infrastructure. Build ordinary C# plugins and services, compose applications, or expose selected services to .NET and Web clients. Remote is optional; ordinary plugin development does not require Node.

Fixed upstream behavior and justified platform adaptations govern the implementation. Earlier experiments do not establish a separate framework specification or compatibility obligation. See [compatibility](docs/compatibility.md).

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft. The behavior baseline is deepseek-ai/deepseek-harness at 639ed015397290b3745d163aafe02ffee4aa3f84, whose vendored Cordis reports 4.0.4; arbitrary TypeScript plugins are not a supported deployment format.

## Start with your task

| Task | Packages and entry |
|---|---|
| Run ordinary .NET plugins and services | Core; add Composition for Loader, profiles and configuration. [First package-only application](docs/getting-started.md). |
| Expose a Host to .NET or Web consumers | Composition supplies declaration extraction, generated bindings, independent clients and shared Remote contracts; AspNetCore supplies the authorized HTTP/WebSocket host adapter. [Authoring](docs/authoring.md). |
| Install and run separate CLR plugins | Clr supplies the SDK toolchain and stable artifact loading over existing configuration operations. [Artifact workflow](docs/clr-artifacts.md), [managed plugin](examples/ManagedPlugin/README.md), [managed application](examples/ManagedApplication/README.md). |

Optional Hosting integrates Microsoft Generic Host. Extensions provides selected runtime extensions; JavaScript evaluates trusted configuration expressions through Jint, without a Node environment or sandbox. Static Core/Composition applications support Native AOT; dynamic CLR loading and Jint require an ordinary runtime. The CLI package is Cordis.NET.Tool. Package IDs use Cordis.NET.*, while namespaces and assemblies use Cordis.*.

## Select a package batch

This source targets `0.2.0-alpha.5.2`. Use the exact package version and distribution listed in the [matching release](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.0-alpha.5.2); a source version alone does not establish NuGet availability. The earlier alpha.5 archive is superseded and must not be published. Published alpha.4 does not contain these changes. [Current support and release status](docs/development.md#current-support-and-release-status) separates implemented capabilities, known limits and pending decisions; [validation](docs/validation.md#current-candidate-index) identifies the actual tested packages.

Follow [getting started](docs/getting-started.md) to install the selected batch and run without cloning the repository. To explore the same composition workflow from source with the SDK in global.json:

```console
dotnet run --project examples/Composition/Composition.csproj
```

The example mounts a static plugin, reads its service and updates configuration. The [managed application](examples/ManagedApplication/README.md) adds package installation and browser modules; only its optional frontend build needs Node.

## Documentation

- [Getting started](docs/getting-started.md): package selection, first call and shutdown.
- [Authoring](docs/authoring.md): declarations, generated consumers, errors and ownership.
- [Core concepts](docs/core-concepts.md) and [architecture](docs/architecture.md): runtime and subsystem responsibilities.
- [Development](docs/development.md): current support, maintenance navigation and verification.
- [Compatibility](docs/compatibility.md), [upstream provenance](docs/upstream.md) and [upgrade matrix](docs/upgrade-0.2.0-rc.2.md): fixed behavior and platform adaptations.
- [Publication readiness](docs/publication-readiness.md), [changelog](CHANGELOG.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md) and [notices](NOTICE.md).

This is an early preview. Public APIs and adaptations may change before a stable release; persisted data, Git history and published package identities are preserved.
