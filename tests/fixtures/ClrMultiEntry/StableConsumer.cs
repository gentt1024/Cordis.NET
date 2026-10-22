using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Cordis;
using Cordis.Clr;

const string package = "IndependentMultiEntry";
const string module = "nuget:independentmultientry";
if (args[0] == "--refuse")
{
    await using var resolver = new ClrModuleResolver(Path.Combine(args[1], "forbidden-shadow"));
    try
    {
        using var toolchain = new DotnetPluginToolchain(args[1], resolver, [], args[2]);
    }
    catch (InvalidDataException)
    {
        Console.WriteLine("PASS receipt refusal before load");
        return;
    }

    throw new InvalidOperationException("Startup admitted the altered resource.");
}

if (args[0] is "--load" or "--worker")
{
    for (var index = 0;index < 2;index++)
    {
        await using var resolver = new ClrModuleResolver(Path.Combine(args[1], "forbidden-shadow"));
        using var toolchain = new DotnetPluginToolchain(args[1], resolver, [], args[2]);
        var bundle = toolchain.Bundles[package];
        var plugin = await resolver.ResolveAsync(module, new Uri("file:///"));
        await using (var context = new Context())
            await context.RunAsync(async ctx =>
            {
                ctx.Provide("trace", new List<string>());
                var owner = ctx.Plugin(plugin.Plugin, 7);
                await owner.WaitAsync();
                if (owner.State != FiberState.Active || (int)ctx.Get("first.value")! != 7 ||
                    Path.GetDirectoryName(ctx.Get<Assembly>("first.assembly")!.Location) != bundle)
                    throw new InvalidOperationException(
                        "The installed plugin did not execute from its published directory.");
            });
        if (args[0] == "--worker" && index == 0)
        {
            Console.WriteLine("READY retained worker");
            Console.Out.Flush();
            if (await Console.In.ReadLineAsync() != "continue")
                throw new InvalidOperationException("Worker did not receive its continuation.");
        }

        var native = await resolver.ResolveAsync(module + "/native", new Uri("file:///"));
        if (native.Plugin.Name != "native-a")
            throw new InvalidOperationException("A retained worker lost its private native library.");
        if (File.ReadAllText(Path.Combine(bundle, "retained-data.txt")) !=
            "published resource retained for worker" + Environment.NewLine)
            throw new InvalidOperationException("A retained worker lost the authored resource content.");
        await resolver.DisposeAsync();
        if (resolver.Unloads.Count != 1 || resolver.Unloads[0].LoadDirectory != bundle ||
            resolver.Unloads[0].ShadowDirectory is not null ||
            Directory.Exists(Path.Combine(args[1], "forbidden-shadow")))
            throw new InvalidOperationException("A stable load created or used a bundle copy.");
    }

    Console.WriteLine("PASS published load, fresh instance, native call and resource read");
    return;
}

var profile = Directory.CreateTempSubdirectory("cordis-stable-publication-").FullName;
await File.WriteAllTextAsync(
    Path.Combine(profile, "package.json"),
    "{\"name\":\"host\",\"version\":\"1.0.0\",\"dependencies\":{\"IndependentMultiEntry\":\"1.0.0-alpha\"}}");
await using var installed = new ClrModuleResolver(Path.Combine(profile, "forbidden-shadow"));
using var packages = new DotnetPluginToolchain(
    profile,
    installed,
    [args[0], args[1], "https://api.nuget.org/v3/index.json"],
    args[2]);
var inspection = await packages.InspectAsync(new(package, "1.0.0-alpha", args[0]));
var prepared = await packages.PrepareAsync(
    inspection,
    true,
    _ =>
    {
    });
await packages.PublishAsync(prepared);
var published = packages.Bundles[package];
var files = Snapshot(published);
var profilePaths =
    Directory.GetFiles(profile, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
for (var index = 0;index < 2;index++)
{
    var result = await RunAsync("--load");
    if (result.ExitCode != 0)
        throw new InvalidOperationException(result.Output);
}

using (var worker = Start("--worker"))
{
    var errors = worker.StandardError.ReadToEndAsync();
    try
    {
        if (await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) != "READY retained worker")
            throw new InvalidOperationException("The package Worker did not start.");
        await packages.RemoveAsync(package);
        await worker.StandardInput.WriteLineAsync("continue");
        worker.StandardInput.Close();
        var output = worker.StandardOutput.ReadToEndAsync();
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        if (worker.ExitCode != 0)
            throw new InvalidOperationException(await output + await errors);
    }
    finally
    {
        if (!worker.HasExited)
            worker.Kill(entireProcessTree: true);
        await worker.WaitForExitAsync();
    }
}

if (!files.SequenceEqual(Snapshot(published)) ||
    !profilePaths.SequenceEqual(
        Directory.GetFiles(profile, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)))
    throw new InvalidOperationException(
        "Repeated startup or logical removal changed the published tree or created copies.");
var resource = Path.Combine(published, "retained-data.txt");
var original = await File.ReadAllBytesAsync(resource);
try
{
    await File.AppendAllTextAsync(resource, "changed");
    var refused = await RunAsync("--refuse");
    if (refused.ExitCode != 0 || refused.Output.Trim() != "PASS receipt refusal before load")
        throw new InvalidOperationException("Startup did not reject an altered non-assembly file: " + refused.Output);
}
finally
{
    await File.WriteAllBytesAsync(resource, original);
}

Console.WriteLine(
    $"PASS one Prepare/Publish, repeated new processes/instances, stable LoadDirectory, unchanged {files.Length} files/{Directory.GetFiles(published, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)} bytes, retained Worker/native/resource, changed-file refusal");

Process Start(string mode)
{
    var start = new ProcessStartInfo(args[2])
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in new[] { Assembly.GetExecutingAssembly().Location, mode, profile, args[2] })
        start.ArgumentList.Add(argument);
    return Process.Start(start)!;
}

async Task<(int ExitCode, string Output)> RunAsync(string mode)
{
    using var child = Start(mode);
    var stdout = child.StandardOutput.ReadToEndAsync();
    var stderr = child.StandardError.ReadToEndAsync();
    try
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (child.ExitCode, await stdout + await stderr);
    }
    finally
    {
        if (!child.HasExited)
            child.Kill(entireProcessTree: true);
        await child.WaitForExitAsync();
    }
}

static KeyValuePair<string, string>[] Snapshot(string directory) =>
    Directory
        .GetFiles(directory, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(path => KeyValuePair.Create(
            Path.GetRelativePath(directory, path),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
        .ToArray();
