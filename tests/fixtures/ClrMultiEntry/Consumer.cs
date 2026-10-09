using System.Reflection;
using System.Diagnostics;
using System.Runtime.Loader;
using System.Text.Json;
using Cordis;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Extensions;
using Cordis.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var bundle = Path.GetFullPath(args[0]);
var shadow = Path.Combine(Path.GetTempPath(), "cordis-multi-entry-shadow-" + Guid.NewGuid().ToString("N"));
await using var resolver = new ClrModuleResolver(shadow);
resolver.Register("first-module", new(bundle, "IndependentMultiEntry.dll", "IndependentMultiEntry.First"));
resolver.Register("second-module", new(bundle, "IndependentMultiEntry.dll", "IndependentMultiEntry.Second"));
resolver.Register("first-alias", new(bundle, "IndependentMultiEntry.dll", "IndependentMultiEntry.First"));
var packages = DeploymentPackageResolver.Native((name, _) => name == "multi-entry" ? bundle : null);
var modules = new DeploymentModuleResolver(packages, resolver)
    .Register(bundle, ".", "first-module")
    .Register(bundle, "./second", "second-module");
var trace = new List<string>();
await using var root = new Context();
Loader? loader = null;
await root.RunAsync(async context =>
{
    context.Provide("trace", trace);
    loader = new Loader(context, modules, diagnostic: Console.Error.WriteLine);
    await loader.Root.UpdateAsync(
    [
        new EntryOptions
        {
            Id = "one",
            Name = "multi-entry",
            Config = 7
        },
        new EntryOptions
        {
            Id = "two",
            Name = "multi-entry/second",
            Config = 11
        },
    ]);
    await loader.WaitAsync();
    if (loader.Resolve("one").Fiber?.State != FiberState.Active ||
        loader.Resolve("two").Fiber?.State != FiberState.Active)
        throw new InvalidOperationException("Both initial CLR entries must activate before identity validation.");
    var first = context.Get<Assembly>("first.assembly")!;
    var second = context.Get<Assembly>("second.assembly")!;
    if (!ReferenceEquals(first, second) ||
        !ReferenceEquals(AssemblyLoadContext.GetLoadContext(first), AssemblyLoadContext.GetLoadContext(second)) ||
        !ReferenceEquals(context.Get("first.identity"), context.Get("second.identity")))
        throw new InvalidOperationException(
            "Two exports of the same CLR bundle lost their shared assembly/ALC identity.");
    if ((int)context.Get("first.value")! != 7 || (int)context.Get("second.value")! != 11)
        throw new InvalidOperationException("Entries did not retain their separate configurations.");
    var plugin = await resolver.ResolveAsync("first-module", new Uri("file:///"));
    var alias = await resolver.ResolveAsync("first-alias", new Uri("file:///"));
    if (!ReferenceEquals(plugin, alias))
        throw new InvalidOperationException("Aliases for the same CLR export did not preserve plugin identity.");
});

Dictionary<string, ClrModuleDefinition> Candidate(string directory) => new(StringComparer.Ordinal)
{
    ["first-module"] = new(directory, "IndependentMultiEntry.dll", "IndependentMultiEntry.First"),
    ["first-alias"] = new(directory, "IndependentMultiEntry.dll", "IndependentMultiEntry.First"),
    ["second-module"] = new(directory, "IndependentMultiEntry.dll", "IndependentMultiEntry.Second"),
};

await RejectAsync(
    () => resolver
        .ReplaceAsync(
            "first-module",
            Candidate(args[1])["first-module"],
            (_, _) => throw new InvalidOperationException("An incomplete bundle must not reach Fiber switching."))
        .AsTask(),
    "Replace every registered export");
object? beforeFailure = null;
await root.RunAsync(context =>
{
    beforeFailure = context.Get("first.identity");
    return Task.CompletedTask;
});
var invalid = Candidate(args[1]);
invalid["second-module"] = invalid["second-module"] with
{
    EntryType = "IndependentMultiEntry.Missing"
};
await RejectAsync(
    () => resolver
        .ReplaceAsync(invalid, _ => throw new InvalidOperationException("Invalid export reached switching."))
        .AsTask(),
    "IndependentMultiEntry.Missing");
await root.RunAsync(context =>
{
    if (!ReferenceEquals(beforeFailure, context.Get("first.identity")))
        throw new InvalidOperationException("Failed candidate preparation disturbed the active bundle.");
    return Task.CompletedTask;
});

await using var hmr = new HmrCoordinator();
var target = args[2];
hmr.RegisterModule(
    Path.Combine(bundle, "IndependentMultiEntry.dll"),
    () => resolver
        .ReplaceAsync(
            Candidate(target),
            async pairs =>
            {
                if (pairs.Count != 2)
                    throw new InvalidOperationException("Aliases did not collapse to two distinct plugin exports.");
                await loader!.ReplacePluginsAsync(pairs);
            })
        .AsTask());
await RejectAsync(
    () => hmr.NotifyChangedAsync(Path.Combine(bundle, "IndependentMultiEntry.dll")),
    "Candidate second entry refused activation");
await root.RunAsync(_ =>
{
    if (!ReferenceEquals(beforeFailure, root.Get("first.identity")) ||
        !ReferenceEquals(root.Get("first.identity"), root.Get("second.identity")) ||
        (int)root.Get("first.version")! != 1 || (int)root.Get("second.version")! != 1 ||
        loader!.Resolve("one").Fiber!.State != FiberState.Active ||
        loader.Resolve("two").Fiber!.State != FiberState.Active)
        throw new InvalidOperationException("Multi-entry activation failure did not restore both previous exports.");
    return Task.CompletedTask;
});
target = args[1];
var replacing = hmr.NotifyChangedAsync(Path.Combine(bundle, "IndependentMultiEntry.dll"));
await replacing.WaitAsync(TimeSpan.FromSeconds(10));
await root.RunAsync(async context =>
{
    if (loader!.Resolve("two").Fiber!.State != FiberState.Pending || (int?)context.Get("first.version") != 2 ||
        context.Get("second.remote") is not null)
        throw new InvalidOperationException("HMR did not preserve the sibling's settled Pending state.");
    context.Provide("late", true);
    await loader.WaitAsync();
});
await root.RunAsync(async context =>
{
    if (!ReferenceEquals(context.Get("first.identity"), context.Get("second.identity")) ||
        (int)context.Get("first.version")! != 2 || (int)context.Get("second.version")! != 2 ||
        (int)context.Get("first.value")! != 7 || (int)context.Get("second.value")! != 11)
        throw new InvalidOperationException("Bundle replacement lost shared identity or per-entry configuration.");
    await loader!.UpdateAsync(
        "one",
        new EntryOptions
        {
            Disabled = true
        });
    if (context.Get("first.identity") is not null || context.Get("second.identity") is null)
        throw new InvalidOperationException("Disabling one entry affected the other entry's lifecycle.");
    var unloads = resolver.Unloads.Count;
    await resolver.RemoveAsync("first-module");
    await resolver.RemoveAsync("first-alias");
    if (resolver.Unloads.Count != unloads || (int)context.Get("second.value")! != 11)
        throw new InvalidOperationException(
            "Removing one export requested unload while a sibling export remained live.");
    await loader.UpdateAsync(
        "two",
        new EntryOptions
        {
            Disabled = true
        });
    await resolver.RemoveAsync("second-module");
    if (resolver.Unloads.Count != unloads + 1 || !resolver.Unloads[^1].UnloadRequested)
        throw new InvalidOperationException("Removing the final export did not retire the shared bundle once.");
});
var profile = Directory.CreateTempSubdirectory("cordis-multi-entry-profile-").FullName;
File.WriteAllText(Path.Combine(profile, "package.json"), "{\"name\":\"host\",\"version\":\"1.0.0\"}");
await using var installedResolver = new ClrModuleResolver(Path.Combine(profile, "shadows"));
using var toolchain = new DotnetPluginToolchain(
    profile,
    installedResolver,
    [args[3], args[4], "https://api.nuget.org/v3/index.json"],
    args[5]);
var inspection = await toolchain.InspectAsync(new("IndependentMultiEntry", "1.0.0-alpha", args[3]));
var prepared = await toolchain.PrepareAsync(
    inspection,
    buildApproved: true,
    _ =>
    {
    });
await toolchain.PublishAsync(prepared);
await using var installedRoot = new Context();
await installedRoot.RunAsync(async context =>
{
    context.Provide("trace", new List<string>());
    context.Provide("late", true);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    context.Provide("remote-entered", entered);
    context.Provide("remote-release", release);
    var installedLoader = new Loader(context, installedResolver);
    await installedLoader.Root.UpdateAsync(
        EntryPatches.Apply(
            [],
            await ConfigurationFile.ReadEntriesAsync(
                Path.Combine(toolchain.Bundles["IndependentMultiEntry"], "cordis.patch.yml"))));
    await installedLoader.WaitAsync();
    var registry = new TypertRegistry(context);
    context.Provide("typert", registry);
    TypertLoader? artifacts = null;
    var contractPlugin = new Plugin<object?>
    {
        ApplyAsync = async (owner, _) =>
            artifacts = await TypertLoader.StartAsync(owner, installedLoader, registry, installedResolver)
    };
    var contractOwner = context.Plugin(contractPlugin);
    await contractOwner.WaitAsync();
    var gateway = new TypertGateway(context, registry);
    if (!ReferenceEquals(context.Get("first.identity"), context.Get("second.identity")) ||
        context.Get("first.identity") is null || (int)context.Get("second.value")! != 11 ||
        toolchain.LocateManifest("nuget:independentmultientry/second", new Uri("file:///")) is null)
        throw new InvalidOperationException(
            "The standard NuGet toolchain did not deliver the declared secondary export.");
    if (registry.ListLocal().Count != 3 || registry.GetSchema("multi-entry.first#int") is null)
        throw new InvalidOperationException(
            "The same dynamic bundle did not export its compiler-generated descriptors and schema.");
    using var arguments = JsonDocument.Parse("{\"request\":{\"delta\":2}}");
    var firstCall = await gateway.InvokeAsync("first/Echo", arguments.RootElement);
    var secondCall = await gateway.InvokeAsync("second/Echo", arguments.RootElement);
    if (!firstCall.Ok || firstCall.Value!.Value.GetInt32() != 109 || !secondCall.Ok ||
        secondCall.Value!.Value.GetInt32() != 113)
        throw new InvalidOperationException(
            "Typed Remote did not consume both entries of the installed author package.");
    var clientDirectory = Directory.CreateDirectory(Path.Combine(profile, "clients")).FullName;
    foreach (var name in new[] { "first", "second" })
    {
        var client = TypertArtifacts.GenerateClient(registry.GetPackage("multi-entry." + name)!);
        await File.WriteAllTextAsync(Path.Combine(clientDirectory, name + ".client.mjs"), client.Module);
        await File.WriteAllTextAsync(Path.Combine(clientDirectory, name + ".client.d.mts"), client.Declaration);
    }

    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseKestrel();
    await using var app = builder.Build();
    app.Urls.Add("http://127.0.0.1:0");
    app.MapCordisRemote("/remote", gateway, (_, _) => Task.FromResult(true));
    await app.StartAsync();
    try
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Single();
        var start = new ProcessStartInfo(args[6])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { args[7], address + "/remote", clientDirectory, args[8] })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException("Generated client consumption failed: " + await output + await errors);
        Console.WriteLine(await output);
    }
    finally
    {
        await app.StopAsync();
    }

    var staleCall = gateway.InvokeAsync("first/Wait", arguments.RootElement);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var typedTarget = args[2];
    await using var typedHmr = new HmrCoordinator();
    var installedAssembly = Path.Combine(toolchain.Bundles["IndependentMultiEntry"], "IndependentMultiEntry.dll");
    typedHmr.RegisterModule(
        installedAssembly,
        async () =>
        {
            var retired = false;
            try
            {
                await installedResolver.ReplaceAsync(
                    new Dictionary<string, ClrModuleDefinition>(StringComparer.Ordinal)
                    {
                        ["nuget:independentmultientry"] = new(
                            typedTarget,
                            "IndependentMultiEntry.dll",
                            "IndependentMultiEntry.First"),
                        ["nuget:independentmultientry/second"] = new(
                            typedTarget,
                            "IndependentMultiEntry.dll",
                            "IndependentMultiEntry.Second"),
                    },
                    async pairs =>
                    {
                        await contractOwner.DisposeAsync();
                        retired = true;
                        await installedLoader.ReplacePluginsAsync(pairs);
                    });
            }
            finally
            {
                if (retired)
                {
                    contractOwner = context.Plugin(contractPlugin);
                    await contractOwner.WaitAsync();
                }
            }
        });
    await RejectAsync(
        () => typedHmr.NotifyChangedAsync(installedAssembly),
        "Candidate second entry refused activation");
    release.TrySetResult();
    var staleResult = await staleCall.WaitAsync(TimeSpan.FromSeconds(10));
    if (staleResult.Ok || staleResult.Error?.Code != "gateway/definition-unavailable")
        throw new InvalidOperationException("An old invocation survived generated contract withdrawal and recovery.");
    if (!(await gateway.InvokeAsync("second/Echo", arguments.RootElement)).Ok)
        throw new InvalidOperationException("Typed Remote was not restored after failed bundle replacement.");
    typedTarget = args[1];
    await typedHmr.NotifyChangedAsync(installedAssembly);
    firstCall = await gateway.InvokeAsync("first/Echo", arguments.RootElement);
    secondCall = await gateway.InvokeAsync("second/Echo", arguments.RootElement);
    if (!firstCall.Ok || firstCall.Value!.Value.GetInt32() != 209 || !secondCall.Ok ||
        secondCall.Value!.Value.GetInt32() != 213)
        throw new InvalidOperationException(
            "Replacement reused old CLR DTO codecs or failed to reload generated contracts.");
    await installedLoader.UpdateAsync(
        "one",
        new EntryOptions
        {
            Disabled = true
        });
    await artifacts!.WaitForIdleAsync();
    if ((await gateway.InvokeAsync("first/Echo", arguments.RootElement)).Ok ||
        !(await gateway.InvokeAsync("second/Echo", arguments.RootElement)).Ok)
        throw new InvalidOperationException("Withdrawing one entry failed to withdraw only its Remote definition.");
    await installedLoader.Root.UpdateAsync([]);
    await artifacts.WaitForIdleAsync();
    if (registry.ListLocal().Count != 0 || (await gateway.InvokeAsync("second/Echo", arguments.RootElement)).Ok)
        throw new InvalidOperationException("The final entry withdrawal left a generated Remote definition live.");
    var unloads = installedResolver.Unloads.Count;
    await toolchain.RemoveAsync("IndependentMultiEntry");
    await RejectAsync(
        () => installedResolver.ResolveAsync("nuget:independentmultientry/second", new Uri("file:///")).AsTask(),
        "No CLR module mapping");
    if (installedResolver.Unloads.Count != unloads + 1 || Directory.Exists(prepared.PublicationDirectory))
        throw new InvalidOperationException(
            "Standard package removal did not withdraw every export and retire its bundle once.");
});
Console.WriteLine(
    "Independent multi-entry package consumer passed (shared bundle, subpaths, configurations, HMR pending/recovery, withdrawal, NuGet toolchain).");

static async Task RejectAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (Exception error) when (error.ToString().Contains(message, StringComparison.Ordinal))
    {
        return;
    }

    throw new InvalidOperationException("Expected rejection: " + message);
}
