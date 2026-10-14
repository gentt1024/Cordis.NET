# Getting started

[中文](getting-started.zh.md)

## Choose the batch and SDK

Use .NET 10 and the SDK selected by the chosen batch's global.json. This source checkout uses 10.0.111. Pick an exact Cordis.NET package version and feed from its release or [validation receipt](validation.md#current-candidate-index). Before alpha5 is published, use the isolated candidate feed supplied with that receipt. Do not mix its packages with published alpha.4 merely because a local CI package carries the same version text.

Replace `<selected-version>` and `<feed>` below with that batch's exact version and NuGet source. A public release can use https://api.nuget.org/v3/index.json; an unpublished batch requires its supplied local feed. These are placeholders, not a request to publish or rename a package.

## Run an ordinary plugin

Create a console application. No repository checkout, Node, HTTP server or source generator configuration is required for Core.

```console
dotnet new console -n MyApp -f net10.0
dotnet add MyApp package Cordis.NET.Core --version <selected-version> --source <feed>
```

Replace MyApp/Program.cs with:

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
    Console.WriteLine(owner.Get<Func<int>>("answer")());
    await fiber.DisposeAsync();
});
```

```console
dotnet run --project MyApp
```

The program prints 41 and exits. The root Context owns the plugin Fiber; the Fiber owns its service registration. WaitAsync observes activation before the read, and DisposeAsync withdraws the plugin before the root closes. Calls execute inside RunAsync's Context execution domain. An activation failure throws from WaitAsync; it is not a RemoteResult. Application-owned external resources still need their own disposal.

For profiles, configuration and static modules, add Cordis.NET.Composition from the same batch. The existing [Composition example](../examples/Composition/Program.cs) uses StaticModuleResolver and Loader for a live configuration update. Ordinary plugin/service calls remain in process. See [core concepts](core-concepts.md) for dependencies, service views and effects.

## Connect an existing Host to generated consumers

Use Composition for author declarations and generated bindings, and AspNetCore for the HTTP unary and WebSocket mux adapter. Keep one existing Cordis Context and Registry; the host owns authorization and the Connection. Do not create another plugin runtime for RPC.

Follow [declare and generate](authoring.md#declare-and-generate-native-remote-contracts), then [register, call and withdraw](authoring.md#register-call-and-withdraw-contracts). Those sections supply the author declaration, JSON context, host binding and Web call. For a provider-independent .NET caller, use the [model and client project settings](authoring.md#native-authored-models-and-net-consumers-2026-10-10) before ordinary build. The generated source participates in the same compilation as STJ.

Use saved declaration facts plus explicit reference models when the independent backend supports the contract. Use TypertArtifacts.GenerateClient with an actual contribution when export needs the author's runtime metadata. Both use the shared emitter and Connection; offline generation does not execute arbitrary author code. [Supported backend shapes](development.md#alpha5-independent-backend-disposition) define the boundary.

Unary callers inspect RemoteResult.Ok before Value/Error. Preparation failures and stream errors have separate contracts. Dispose the client before its borrowed Connection, and the Connection before host resources; client disposal does not close a borrowed HttpClient. Start with the existing [Probes application](../examples/Probes/Program.cs) for application composition and [Remote events](remote-events.md) when adding listeners. Runtime metadata support does not establish static AOT support for arbitrary DTOs.

## Deliver independent CLR plugins

Use the existing [managed plugin](../examples/ManagedPlugin/README.md) and [managed application](../examples/ManagedApplication/README.md); they demonstrate the package metadata, selected feeds, build authorization and installation commands.

The standard path is Prepare → Publish → Validate → LoadDirectory through DotnetPluginToolchain and ProfileSession.ConfigurationOperations. Load the verified stable directory directly. Installed upgrades persist a new exact version and require restart; logical removal preserves files for retained code and other processes. Follow [CLR artifacts](clr-artifacts.md) for complete receipts, ownership and explicit offline deletion. Dynamic CLR loading is not Native AOT.

## Validate configuration and continue

The selected Cordis.NET.Tool batch provides cordis validate and cordis preview. Install the exact tool version from the matching feed using --version and --add-source, then run:

```console
cordis validate ./profile/cordis.patch.yml
cordis preview ./profile/cordis.patch.yml
```

Read [authoring](authoring.md) for Settings, management, optional adapters and lifecycle details. [Current support](development.md#current-support-and-release-status) separates known limits from [historical evidence](validation.md).
