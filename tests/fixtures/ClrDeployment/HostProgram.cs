using Cordis;
using Cordis.Clr;

try { return await RunAsync(args); }
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static async Task<int> RunAsync(string[] args)
{
    if (args.Length != 3) throw new ArgumentException("Expected bundle, json/http and folder/single.");
#pragma warning disable IL3000 // This fixture deliberately verifies the bundled CoreLib location contract.
    var coreLocation = typeof(object).Assembly.Location;
#pragma warning restore IL3000
    Console.WriteLine($"CoreLib.Location={coreLocation}");
    Console.WriteLine($"APP_CONTEXT_DEPS_FILES={AppContext.GetData("APP_CONTEXT_DEPS_FILES")}");
    using (var stream = System.Reflection.Assembly.GetEntryAssembly()!.GetManifestResourceStream("Cordis.Clr.HostFrameworks.txt"))
    {
        if (stream is null) throw new InvalidOperationException("The host framework manifest was not generated.");
        using var reader = new StreamReader(stream);
        var frameworks = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Console.WriteLine("Framework classification source=SDK host manifest; declared frameworks=" +
            string.Join(",", frameworks.Select(line => line.Split('|')[0]).Distinct()));
        if (args[1] == "http" && !frameworks.Any(line => line.Trim() == "Microsoft.AspNetCore.App|Microsoft.AspNetCore.Http"))
            throw new InvalidOperationException("The declared ASP.NET framework was not captured.");
    }
    if (args[2] == "single" && coreLocation.Length != 0)
        throw new InvalidOperationException("CoreLib was not actually bundled.");
    var trace = new List<string>();
    await using var resolver = new ClrModuleResolver(Path.Combine(Path.GetTempPath(), "cordis-deployment-" + Guid.NewGuid().ToString("N")));
    resolver.Register("external", new(Path.GetFullPath(args[0]), "DeploymentPlugin.dll", "DeploymentPlugin.Entry"));
    await using (var root = new Context())
    {
        await root.RunAsync(async ctx =>
        {
            ctx.Provide("trace", trace);
            var plugin = await resolver.ResolveAsync("external", new Uri("file:///"));
            var fiber = ctx.Plugin(plugin);
            await fiber.WaitAsync();
            if (fiber.State != FiberState.Active) throw new InvalidOperationException("External plugin did not activate.");
            var result = ctx.Get<Func<string>>("deployment-result")!();
            var expected = args[1] == "json" ? "17" : "202:/from-plugin";
            if (result != expected) throw new InvalidOperationException($"Expected {expected}, received {result}.");
            Console.WriteLine("External plugin result=" + result);
        });
    }
    if (!trace.SequenceEqual(["disposed"])) throw new InvalidOperationException("Plugin lifecycle cleanup did not complete.");
    await resolver.DisposeAsync();
    if (resolver.Unloads.Count != 1 || !resolver.Unloads[0].UnloadRequested)
        throw new InvalidOperationException("External module unload was not requested.");
    Console.WriteLine("CLR deployment scenario passed (activation, service call, cleanup, unload request).");
    return 0;
}
