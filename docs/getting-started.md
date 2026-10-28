# Your first plugin

English | [中文](getting-started.zh.md)

This tutorial creates a console application, registers a plugin, calls its service and stops it. It uses the published `0.2.1-alpha.1` package. You need the .NET 10 SDK and access to nuget.org; no repository checkout or Node installation is required.

## Create the application

Run these commands in the directory where you want to create the project:

```console
dotnet new console -n MyApp -f net10.0
dotnet add MyApp package Cordis.NET.Core --version 0.2.1-alpha.1 --source https://api.nuget.org/v3/index.json
```

`Cordis.NET.Core` supplies the plugin runtime. The other packages are optional for this program. When working from a Cordis.NET source checkout, use the SDK pinned in its `global.json`.

## Register a plugin and call its service

Replace `MyApp/Program.cs` with:

```csharp
using Cordis;

await using var context = new Context();
await context.RunAsync(async owner =>
{
    var plugin = new Plugin<object?>
    {
        Apply = (ctx, _) =>
        {
            ctx.Provide("answer", (Func<int>)(() => 41));
        }
    };
    var fiber = owner.Plugin(plugin);
    await fiber.WaitAsync();
    var answer = owner.Get<Func<int>>("answer")
        ?? throw new InvalidOperationException("The answer service is unavailable.");
    Console.WriteLine(answer());
    await fiber.DisposeAsync();
});
```

The program uses three objects:

- `Context` provides the service view and execution domain. The root context owns this application's plugin lifetime.
- `Plugin<object?>` defines what to do when activated. Here it registers a function under the service name `answer`.
- `Fiber` represents the registered plugin instance. It owns the registration created by `Apply`.

`RunAsync` enters the Context execution domain; plugin callbacks already run there. `WaitAsync` waits for the plugin's current lifecycle work and reports an activation error if one occurs. This plugin has no dependencies, so its apply function has completed before the service read. For a plugin waiting on an absent dependency, `WaitAsync` can finish while the Fiber is still pending; it does not wait for a future provider.

## Run and stop it

```console
dotnet run --project MyApp
```

The program prints `41` and exits. `fiber.DisposeAsync()` stops the plugin and withdraws its service registration. The outer `await using` then closes the root context. Withdrawing a registration does not dispose the registered value; if your plugin creates a resource that needs cleanup, register that cleanup with an effect. See [resource ownership](core-concepts.md#services-and-resource-ownership).

The call to `answer()` is an ordinary C# call. A network server or Remote contract is unnecessary for services used within the application.

## Continue with your application

| Next task | Guide |
|---|---|
| Understand dependencies, service visibility and plugin cleanup | [Core concepts](core-concepts.md). |
| Load static modules from configuration and update them | Add `Cordis.NET.Composition` at the same version; follow the [Composition example](../examples/Composition/Program.cs). |
| Expose selected services to .NET or Web clients | [Declare and generate Remote contracts](authoring.md#declare-and-generate-native-remote-contracts), then [register, call and withdraw](authoring.md#register-call-and-withdraw-contracts). |
| Generate a provider-independent .NET client | [Model and client project settings](authoring.md#generate-an-independent-net-client) and [backend support](development.md#generation-backend-support). |
| Install independently deployed CLR plugins | [Managed plugin](../examples/ManagedPlugin/README.md), [managed application](../examples/ManagedApplication/README.md) and [CLR artifacts](clr-artifacts.md). |
| Validate or preview configuration from the command line | Install the matching `Cordis.NET.Tool` version; see the [CLI guide](../tools/Cordis.Cli/README.md). |

Use matching versions of Cordis.NET packages and tools, and rebuild generated providers and clients when changing versions. Browser archives are on the [matching release page](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1). For capabilities added after that release, check [current source status](development.md#current-support-and-release-status) before following a guide from main.
