# Cordis.NET

[中文](README.zh.md)

Cordis.NET is a native .NET implementation of the plugin runtime and composition behavior pinned by DeepSeek Harness. It provides a context based plugin model, dependency injection, lifecycle effects, configuration composition, and optional adapters for CLR modules, Generic Host, and JavaScript expressions.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft.

The behavior target is `deepseek-ai/deepseek-harness@ddefc45fbc7f8e46dd73185e68295696d1297887`, whose vendored `@deepseek-ai/cordis` reports version `4.0.2`. This project adapts observable contracts to .NET; it does not claim complete compatibility with arbitrary TypeScript plugins.

## Try it from source

Install the .NET SDK selected by `global.json`, then run the working composition example:

```console
dotnet run --project examples/Composition/Composition.csproj
```

The example registers a static plugin, applies configuration, observes its service, and updates it. Packages have not been published, so this repository does not present a `dotnet add package` command yet.

## Choose a deployment path

- **Static and Native AOT:** use `Cordis.Core`, `Cordis.Composition`, and optionally `Cordis.Extensions` with statically registered modules.
- **CLR modules:** add `Cordis.Clr` to load managed plugin assemblies in an ordinary .NET runtime. This path is not Native AOT compatible.
- **JavaScript expressions:** add `Cordis.JavaScript` for `!!js` evaluation through Jint. Trusted configuration only; this is not a sandbox and no Native AOT support is claimed.

`Cordis.Hosting` integrates the runtime with Generic Host. `Cordis.Tool` provides configuration validation and preview commands.

## Documentation

- [Getting started](docs/getting-started.md)
- [Core concepts](docs/core-concepts.md)
- [Compatibility and evidence](docs/compatibility.md)
- [Upstream baseline and provenance](docs/upstream.md)
- [Development and verification](docs/development.md)
- [Security policy](SECURITY.md)
- [Contributing](CONTRIBUTING.md)

The project is an early preview. Public APIs and adaptation details may change before a stable release. See [CHANGELOG.md](CHANGELOG.md) for release notes and [NOTICE.md](NOTICE.md) for attribution.
