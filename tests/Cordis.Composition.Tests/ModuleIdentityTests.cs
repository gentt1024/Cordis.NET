using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ModuleIdentityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_callback_wrappers_replace_each_runtime_fiber_once_and_restore_the_actual_import(bool fail)
    {
        await using var context = new Context();
        var trace = new List<string>();
        Action<Context, string> apply = (ctx, id) =>
        {
            trace.Add("old:" + id);
            ctx.Effect(() => (Action)(() => trace.Add("stop:" + id)));
        };
        var resolver = new StaticModuleResolver()
            .Register(
                "one",
                new Plugin<string>
                {
                    Apply = apply
                })
            .Register(
                "two",
                new Plugin<string>
                {
                    Apply = apply
                });
        Loader? loader = null;
        await context.RunAsync(async ctx =>
        {
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
                    Id = "two",
                    Name = "two",
                    Config = "two"
                }
            ]);
            await loader.WaitAsync();
        });
        var one = loader!.Resolve("one").Module!;
        var two = loader.Resolve("two").Module!;
        Assert.NotSame(one.Plugin, two.Plugin);
        Assert.Same(one.Plugin.Identity, two.Plugin.Identity);
        var first = new ResolvedModule(
            new Plugin<string>
            {
                Apply = (_, id) => trace.Add("first:" + id)
            });
        var second = new ResolvedModule(
            new Plugin<string>
            {
                Apply = (_, id) =>
                {
                    trace.Add("second:" + id);
                    if (fail)
                        throw new InvalidOperationException("second refused");
                }
            });
        trace.Clear();
        var replacing = loader.ReplacePluginsAsync([new(one, first), new(two, second)]);
        if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => replacing);
            Assert.Same(one, loader.Resolve("one").Module);
            Assert.Same(two, loader.Resolve("two").Module);
            Assert.Equal(2, trace.Count(item => item.StartsWith("old:", StringComparison.Ordinal)));
        }
        else
        {
            await replacing;
            Assert.Same(first, loader.Resolve("one").Module);
            Assert.Same(second, loader.Resolve("two").Module);
        }

        Assert.Equal(1, trace.Count(item => item == "stop:one"));
        Assert.Equal(1, trace.Count(item => item == "stop:two"));
        Assert.Equal(1, trace.Count(item => item == "first:one"));
        Assert.Equal(1, trace.Count(item => item == "second:two"));
        Assert.All(loader.Entries(), entry => Assert.Equal(FiberState.Active, entry.Fiber!.State));
    }
}
