# Core concepts

English | [中文](core-concepts.zh.md)

Cordis connects plugin activation to service availability and resource cleanup. A plugin describes work; a Fiber represents a registered instance of that plugin; its Context determines which services it sees and which Fiber owns the registrations it creates.

## Context and execution

Create a root `Context` for the application's Cordis lifetime. Enter it through `RunAsync` when registering plugins, reading services or manipulating Cordis state from application code. Plugin callbacks already execute in that domain.

The execution domain serializes callbacks and allows synchronous reentry. An `await` yields to other queued work, so an asynchronous operation is not a transaction. Do not synchronously block on lifecycle tasks or use `async void` callbacks. Code that leaves the execution domain, such as a continuation using `ConfigureAwait(false)`, must enter it again before manipulating Cordis.

Child contexts share the runtime but carry their own service view and owning Fiber. Closing the root Context drains its owned lifecycle work and permanently ends the .NET execution lifetime. Disposing or restarting the root Fiber is a separate operation; it does not replace root Context disposal at application shutdown.

## Plugins and fibers

`Plugin<T>` is a reusable definition: configuration validation, dependency declarations and one apply function. `T` is the validated configuration type. Calling `context.Plugin(plugin)` registers an instance and returns its `Fiber`; the same definition can have multiple instances.

```text
Root Context
└── registered Fiber ← Plugin<T> definition
    ├── plugin Context: service view and registration owner
    ├── effects: service registrations, listeners and registered cleanup
    └── child Fibers
```

The Fiber tracks activation, failure, updates and cleanup. Registering a plugin starts its lifecycle work; it does not synchronously finish asynchronous apply code. Await `fiber.WaitAsync()` to join the current work and observe its error. A required service that is absent can leave the Fiber pending after that wait completes. `WaitAsync` is not a promise that a future dependency will appear.

Dependencies declared through `Inject` govern activation. When a required service disappears, the dependent plugin's active work is cleaned up; when dependencies become available again, the registered plugin can activate again. A child plugin's lifetime also follows its parent. Use `DisposeAsync` to remove a registration when it is no longer needed.

## Services and resource ownership

`Provide` publishes a named service. `Get<T>` performs explicit lookup through the caller's Context; `T` checks the value's CLR type. Service identity is its name and isolation realm, so changing `T` does not create another service with the same name. Injection-aware access and typed service views have additional rules described in [architecture](architecture.md).

A service registration is an effect owned by the providing Fiber. Stopping that Fiber withdraws the registration. **Withdrawal does not dispose the registered object.** If a plugin creates a resource, it must attach the required cleanup to its lifecycle through an effect. Borrowing a service or passing an object to another component does not transfer resource ownership.

Effects run their setup when registered and retain cleanup for their owning Fiber. The lifetime therefore belongs to the registration site: use the plugin's Context for work that should stop with that plugin, rather than attaching it to the root by accident.

## Events

Event listeners also retain the Context in which they were registered. That Context supplies the listener's service view and effect ownership. `Undefined.Value` represents absence and remains distinct from explicit `null`. In `Bail` and `SerialAsync`, `Undefined.Value`, `null` and `false` continue dispatch; zero, an empty string or `true` stops dispatch and becomes the result. These rules depend on the event operation; they are not ordinary C# truthiness.

In-process events are part of Core. Exposing selected events to a remote caller adds declaration, connection and subscription lifetimes; see [Remote events](remote-events.md). It does not turn every application event into a network event.

## Composition

Core can register plugins directly, as in [your first plugin](getting-started.md). Add Composition when configuration should determine which modules run and how they are updated.

`Loader` turns profiles, groups, includes and patch layers into a live entry tree. A module resolver supplies the plugin implementation for an entry; the Loader applies it with the entry's configuration. Configuration editing and Settings reuse the same coordinated operations rather than maintaining another application state tree.

Use `StaticModuleResolver` when plugins are known at build time, including Native AOT applications. Use `ClrModuleResolver` for runtime DLL loading on an ordinary .NET runtime. The [Composition example](../examples/Composition/Program.cs) shows static modules and a live update; [CLR artifacts](clr-artifacts.md) covers independent plugin delivery.

Remote is an optional boundary over selected services. It reuses the application's Context and Registry, while the host owns authorization and connection lifetime. Continue with [authoring](authoring.md) when you need generated .NET or Web consumers.
