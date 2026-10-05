# Cordis.NET

[中文](README.zh.md)

Cordis.NET implements the DSH-maintained Cordis at a fixed revision. Its scope includes a domain-independent runtime and generic infrastructure for building, running, configuring, managing, and shipping modular applications. Core remains domain-independent; application libraries and platform adapters sit above it.

Available components include lifecycle and injection, profile/bundle/patch composition, running-session coordination, configuration references, plugin enablement and deployment reconciliation, and optional CLR, Generic Host and JavaScript adapters. Current source adds typed field composition, ordered configuration edits and resets, explicit declaration exports, a NuGet/SDK package toolchain, ASP.NET Core management endpoints, application CLI commands and reusable TypeScript client modules. Their combined delivery is still under verification; see the development and compatibility records for remaining work. Agent/LLM business logic, RSI goals and FSM domain logic belong to products. These new source capabilities are not in the published `0.2.0-alpha.1` batch.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft.

The behavior target is `deepseek-ai/deepseek-harness@639ed015397290b3745d163aafe02ffee4aa3f84`, whose vendored `@deepseek-ai/cordis` reports version `4.0.4`. This project adapts observable contracts to .NET; it does not claim complete compatibility with arbitrary TypeScript plugins.

## Try it from source

Install the .NET SDK selected by `global.json`, then run the working composition example:

```console
dotnet run --project examples/Composition/Composition.csproj
```

The example registers a static plugin, applies configuration, observes its service, and updates it.

The [managed application example](examples/ManagedApplication/README.md) combines real package installation, configuration and browser modules through the same profile session. Its optional frontend build requires Node; ordinary C# plugin development does not.

## Install the prerelease

```console
dotnet add package Cordis.NET.Composition --prerelease
dotnet tool install --global Cordis.NET.Tool --prerelease
```

These commands select the latest published packages, including prereleases. The Composition and Probes examples use the authoring helpers described in the [release notes](CHANGELOG.md); older packages do not include them. Until that release is available on NuGet, run the examples from source through their project references. See [getting started](docs/getting-started.md) and the [authoring guide](docs/authoring.md).

The package IDs use the `Cordis.NET.*` prefix. CLR namespaces and assembly names remain `Cordis.*`.

## Choose a deployment path

- **Static and Native AOT:** use `Cordis.NET.Core`, `Cordis.NET.Composition`, and optionally `Cordis.NET.Extensions` with statically registered modules.
- **CLR modules:** add `Cordis.NET.Clr` to load managed plugin assemblies in an ordinary .NET runtime. This path is not Native AOT compatible.
- **JavaScript expressions:** add `Cordis.NET.JavaScript` for `!!js` evaluation through Jint. Trusted configuration only; this is not a sandbox and no Native AOT support is claimed.

`Cordis.NET.Hosting` integrates the runtime with Generic Host. Current source adds optional `Cordis.NET.AspNetCore` for explicitly authorized management/service endpoints. The source CLI retains validation/preview and adds `run` plus remote management commands; those commands are not in the published tool yet.

## Documentation

- [Getting started](docs/getting-started.md)
- [Plugin and application authoring](docs/authoring.md)
- [Core concepts](docs/core-concepts.md)
- [Compatibility and evidence](docs/compatibility.md)
- [Upstream baseline and provenance](docs/upstream.md)
- [DSH 0.2.0-rc.2 upgrade matrix](docs/upgrade-0.2.0-rc.2.md)
- [Optional configuration descriptions](docs/configuration-description.md)
- [Development and verification](docs/development.md)
- [Security policy](SECURITY.md)
- [Contributing](CONTRIBUTING.md)

The project is an early preview. Public APIs and adaptation details may change before a stable release. See [CHANGELOG.md](CHANGELOG.md) for release notes and [NOTICE.md](NOTICE.md) for attribution.
