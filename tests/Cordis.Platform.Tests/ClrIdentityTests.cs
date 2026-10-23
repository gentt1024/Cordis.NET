using Cordis.Clr;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class ClrIdentityTests
{
    private static readonly Uri Basis = new("file:///");

    private static ClrModuleDefinition Definition(string version, string entry) =>
        new(
            Path.Combine(AppContext.BaseDirectory, "fixtures", version),
            "VersionedPlugin.dll",
            "VersionedPlugin.Identity" + entry);

    private static Dictionary<string, ClrModuleDefinition> Definitions(string version) => new()
    {
        ["one"] = Definition(version, "One"),
        ["alias"] = Definition(version, "One"),
        ["two"] = Definition(version, "Two")
    };

    private static async Task<Loader> Mount(Context context, ClrModuleResolver resolver, List<string> trace)
    {
        foreach (var pair in Definitions("v1"))
            resolver.Register(pair.Key, pair.Value);
        Loader? loader = null;
        await context.RunAsync(async ctx =>
        {
            ctx.Provide("trace", trace);
            loader = new Loader(ctx, resolver);
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "one",
                    Name = "one",
                    Config = "one"
                },
                new()
                {
                    Id = "alias",
                    Name = "alias",
                    Config = "alias"
                },
                new()
                {
                    Id = "two",
                    Name = "two",
                    Config = "two"
                },
                new()
                {
                    Id = "cold",
                    Name = "one",
                    Disabled = true
                }
            ]);
            await loader.WaitAsync();
        });
        return loader!;
    }

    [Fact]
    public async Task Shared_export_splits_by_loaded_request_and_repeated_replacements_do_not_multiply_fibers()
    {
        await using var resolver = new ClrModuleResolver();
        await using var context = new Context();
        var trace = new List<string>();
        var loader = await Mount(context, resolver, trace);
        var one = loader.Resolve("one").Module!;
        var alias = loader.Resolve("alias").Module!;
        var two = loader.Resolve("two").Module!;
        Assert.NotSame(one, alias);
        Assert.NotSame(one, two);
        Assert.Same(one.Plugin, two.Plugin);
        Assert.Same(one.Plugin, alias.Plugin);
        Assert.Equal(Path.Combine(Definition("v1", "One").BundleDirectory, "VersionedPlugin.dll"), one.Filename);
        foreach (var version in new[] { "v2", "v1", "v2" })
        {
            trace.Clear();
            await resolver.ReplaceAsync(
                Definitions(version),
                async pairs =>
                {
                    Assert.Equal(3, pairs.Count);
                    await loader.ReplacePluginsAsync(pairs);
                });
            Assert.Equal(3, trace.Count(value => value.StartsWith("stop:", StringComparison.Ordinal)));
            var expected = version == "v1"
                ? new[] { "start:v1:shared:one", "start:v1:shared:alias", "start:v1:shared:two" }
                : new[] { "start:v2:one:one", "start:v2:one:alias", "start:v2:two:two" };
            Assert.Equal(
                expected.Order(),
                trace.Where(value => value.StartsWith("start:", StringComparison.Ordinal)).Order());
            await context.RunAsync(async ctx =>
            {
                foreach (var name in new[] { "one", "alias", "two" })
                {
                    var entry = loader.Resolve(name);
                    Assert.Same(await resolver.ResolveAsync(name, Basis), entry.Module);
                    Assert.Equal(FiberState.Active, entry.Fiber!.State);
                }

                var current = loader.Resolve("one").Module!;
                Assert.Equal(version == "v1" ? 3 : 2, ctx.Registry.Get(current.Plugin)!.Fibers.Count());
                Assert.Null(loader.Resolve("cold").Fiber);
                Assert.Null(loader.Resolve("cold").Module);
            });
        }

        await context.DisposeAsync();
        Assert.All(loader.Entries(), entry => Assert.Null(entry.Module));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_split_restores_every_loaded_identity_and_can_retry(bool importFailure)
    {
        await using var resolver = new ClrModuleResolver();
        await using var context = new Context();
        var trace = new List<string>();
        var loader = await Mount(context, resolver, trace);
        var previous = new[] { "one", "alias", "two" }.ToDictionary(name => name, name => loader.Resolve(name).Module!);
        var definitions = Definitions(importFailure ? "v2" : "bad");
        if (importFailure)
            definitions["two"] = definitions["two"] with
            {
                EntryType = "VersionedPlugin.InvalidEntry"
            };
        trace.Clear();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            resolver.ReplaceAsync(definitions, async pairs => await loader.ReplacePluginsAsync(pairs)).AsTask());
        if (importFailure)
            Assert.Empty(trace);
        else
        {
            Assert.Equal(3, trace.Count(value => value.StartsWith("stop:bad:", StringComparison.Ordinal)));
            Assert.Equal(3, trace.Count(value => value.StartsWith("start:v1:", StringComparison.Ordinal)));
        }

        foreach (var (name, module) in previous)
        {
            Assert.Same(module, loader.Resolve(name).Module);
            Assert.Same(module, await resolver.ResolveAsync(name, Basis));
            Assert.Equal(FiberState.Active, loader.Resolve(name).Fiber!.State);
        }

        await resolver.ReplaceAsync(Definitions("v2"), async pairs => await loader.ReplacePluginsAsync(pairs));
        Assert.NotSame(previous["two"].Plugin, loader.Resolve("two").Module!.Plugin);
    }

    [Fact]
    public async Task Ambiguous_direct_fiber_refuses_split_before_teardown_and_preserves_all_entries()
    {
        await using var resolver = new ClrModuleResolver();
        await using var context = new Context();
        var trace = new List<string>();
        var loader = await Mount(context, resolver, trace);
        Fiber? direct = null;
        await context.RunAsync(async ctx =>
        {
            direct = ctx.Plugin(loader.Resolve("one").Module!.Plugin, "direct");
            await direct.WaitAsync();
        });
        var original = loader.Resolve("one").Module;
        trace.Clear();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ReplaceAsync(Definitions("v2"), async pairs => await loader.ReplacePluginsAsync(pairs)).AsTask());
        Assert.Contains("ambiguous", failure.Message);
        Assert.Empty(trace);
        Assert.Equal(FiberState.Active, direct!.State);
        Assert.Same(original, loader.Resolve("one").Module);
        Assert.All(
            new[] { "one", "alias", "two" },
            name => Assert.Equal(FiberState.Active, loader.Resolve(name).Fiber!.State));
        await direct.DisposeAsync();
        await resolver.ReplaceAsync(Definitions("v2"), async pairs => await loader.ReplacePluginsAsync(pairs));
    }
}
