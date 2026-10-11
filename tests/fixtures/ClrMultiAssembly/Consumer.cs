using System.Runtime.Loader;
using Cordis;
using Cordis.Clr;

var directory = Path.GetFullPath(args[0]);
var shadow = Path.Combine(directory, "shadows");
var baseUri = new Uri("file:///");

ClrModuleResolver Resolver(string bundle, string first, string second, string entry = "IndependentBundle.Entry")
{
    var resolver = new ClrModuleResolver(shadow);
    resolver.Register("A", new ClrModuleDefinition(Path.Combine(directory, bundle), first, entry));
    resolver.Register("B", new ClrModuleDefinition(Path.Combine(directory, bundle), second, entry));
    return resolver;
}

async Task CheckLoads(
    string bundle,
    string order,
    string first,
    string second,
    string[] names,
    string entry = "IndependentBundle.Entry")
{
    await using var resolver = Resolver(bundle, first, second, entry);
    AssemblyLoadContext? context = null;
    foreach (var key in order)
    {
        var plugin = await resolver.ResolveAsync(key.ToString(), baseUri);
        if (plugin.Name != names[key == 'A' ? 0 : 1])
            throw new InvalidOperationException($"Unexpected plugin from {bundle}/{key}: {plugin.Name}");
        var actual = AssemblyLoadContext.GetLoadContext(((Delegate)plugin.Identity).Method.Module.Assembly);
        if (actual is null || !actual.IsCollectible || context is not null && !ReferenceEquals(context, actual))
            throw new InvalidOperationException("Entries did not share one collectible bundle context.");
        context = actual;
    }

    Console.WriteLine($"PASS {bundle} {order}");
}

async Task CheckConflict<T>(
    string bundle,
    string order,
    string first,
    string second,
    string[] names,
    string message,
    string entry = "IndependentBundle.Entry") where T : Exception
{
    await using var resolver = Resolver(bundle, first, second, entry);
    var old = await resolver.ResolveAsync(order[0].ToString(), baseUri);
    if (old.Name != names[order[0] == 'A' ? 0 : 1])
        throw new InvalidOperationException($"Unexpected first plugin before the conflict: {old.Name}");
    try
    {
        await resolver.ResolveAsync(order[1].ToString(), baseUri);
        throw new InvalidOperationException("Different private binaries were silently substituted.");
    }
    catch (T error) when (error.ToString().Contains(message, StringComparison.Ordinal))
    {
        if (!ReferenceEquals(old, await resolver.ResolveAsync(order[0].ToString(), baseUri)))
            throw new InvalidOperationException("A rejected dependency root changed the existing plugin.");
    }

    Console.WriteLine($"PASS {bundle} rejects {order}");
}

async Task CheckContinuation<T>(
    string bundle,
    string first,
    string second,
    string message,
    string expected,
    string entry = "IndependentBundle.Entry") where T : Exception
{
    await using var resolver = Resolver(bundle, first, second, entry);
    var original = await resolver.ResolveAsync("A", baseUri);
    if (original.Name != "delayed")
        throw new InvalidOperationException("The original factory eagerly called its dependency.");
    try
    {
        await resolver.ResolveAsync("B", baseUri);
        throw new InvalidOperationException("Conflicting declared dependencies were accepted.");
    }
    catch (T error) when (error.ToString().Contains(message, StringComparison.Ordinal))
    {
    }

    Console.WriteLine($"PASS {bundle} rejected candidate; calling original entry for the first time");
    await using var context = new Context();
    string? observed = null;
    await original.ApplyAsync(context, (Action<string>)(value => observed = value));
    if (observed != expected)
        throw new InvalidOperationException($"The original entry returned {observed} after the rejected candidate.");
    Console.WriteLine($"PASS {bundle} original first dependency call after rejection: {observed}");
}

if (args.Length == 1 || args[1] == "late-managed")
    await CheckContinuation<FileLoadException>(
        "late-managed",
        "a/BundleA.dll",
        "b/BundleB.dll",
        "Conflicting managed dependency 'BundlePrivate'",
        "private-1");
if (args.Length == 1 || args[1] == "late-native")
    await CheckContinuation<DllNotFoundException>(
        "late-native",
        "a/NativeFirst.dll",
        "b/NativeSecond.dll",
        "Conflicting native dependency '",
        "native-a",
        "IndependentBundle.NativeEntry");
if (args.Length > 1)
    return;

foreach (var order in new[] { "AB", "BA" })
{
    await CheckLoads("order", order, "a/BundleA.dll", "b/BundleB.dll", ["entry-a", "private-1"]);
    await CheckLoads("same", order, "a/BundleA.dll", "b/BundleB.dll", ["private-1", "private-1"]);
    await CheckConflict<FileLoadException>(
        "conflict",
        order,
        "a/BundleA.dll",
        "b/BundleB.dll",
        ["private-1", "private-2"],
        "Conflicting managed dependency 'BundlePrivate'");
    await CheckLoads(
        "native-same",
        order,
        "a/NativeFirst.dll",
        "b/NativeFirst.dll",
        ["native-a", "native-a"],
        "IndependentBundle.NativeEntry");
    await CheckLoads(
        "native-conflict",
        order[..1],
        "a/NativeFirst.dll",
        "b/NativeSecond.dll",
        ["native-a", "native-b"],
        "IndependentBundle.NativeEntry");
    await CheckConflict<DllNotFoundException>(
        "native-conflict",
        order,
        "a/NativeFirst.dll",
        "b/NativeSecond.dll",
        ["native-a", "native-b"],
        "Conflicting native dependency '",
        "IndependentBundle.NativeEntry");
}

await using (var resolver = Resolver("order", "a/BundleA.dll", "b/BundleB.dll"))
{
    var first = await resolver.ResolveAsync("A", baseUri);
    resolver.Register(
        "wrong",
        new ClrModuleDefinition(
            Path.Combine(directory, "order"),
            "b/BundleB.dll",
            "IndependentBundle.WrongEntry"));
    try
    {
        await resolver.ResolveAsync("wrong", baseUri);
        throw new InvalidOperationException("A module without the shared entry contract was accepted.");
    }
    catch (InvalidOperationException error) when (error.Message.Contains(
                                                      "must implement the shared IClrPluginModule contract",
                                                      StringComparison.Ordinal))
    {
        if (!ReferenceEquals(first, await resolver.ResolveAsync("A", baseUri)))
            throw new InvalidOperationException("A wrong module changed the existing plugin.");
    }
}

Console.WriteLine("PASS wrong module rejection");
AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "same/a/BundlePrivate.dll"));
await using (var resolver = Resolver("missing", "a/BundleA.dll", "b/BundleB.dll"))
{
    await resolver.ResolveAsync("A", baseUri);
    try
    {
        await resolver.ResolveAsync("B", baseUri);
        throw new InvalidOperationException("An unrelated default-context assembly supplied a private dependency.");
    }
    catch (FileNotFoundException error) when (error
                                                  .ToString()
                                                  .Contains(
                                                      "Private dependency 'BundlePrivate",
                                                      StringComparison.Ordinal))
    {
    }
}

Console.WriteLine("PASS missing private dependency refuses default context");
