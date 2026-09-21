# Core concepts

[中文](core-concepts.zh.md)

## Context and execution

`Context` is the plugin environment and service view. `RunAsync` establishes the serialized execution domain while allowing synchronous reentry. Dispose the root context to close the host lifetime.

## Plugins and fibers

`Plugin<T>` combines configuration validation with a synchronous, asynchronous, or effect based apply function. Applying a plugin creates a `Fiber`, which owns effects and tracks readiness, failure, and disposal. Dependency declarations control activation and service visibility.

## Services, events, and effects

Services belong to their provider and are viewed through the caller's context. Events preserve listener context and distinguish `Undefined.Value`, `null`, `false`, and zero. Effects run setup immediately and clean up with their owning fiber.

## Composition

`Loader`, groups, includes, patch layers, and profiles turn configuration into a live entry tree. A module resolver supplies plugins. Use `StaticModuleResolver` for Native AOT, `ClrModuleResolver` for runtime DLL loading, or compose a resolver that matches the application's trust and deployment model.
