using Xunit;

namespace Cordis.Core.Tests;

public sealed class OriginalEffectsTests
{
    private sealed class Cleanup(Action callback) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            callback();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DisposeByPlugin()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0;
            var fiber = ctx.Plugin(new Plugin<object?> { Apply = (c, _) => c.Effect(() => (Action)(() => calls++), "test") });
            await fiber.WaitAsync();
            var meta = Assert.Single(fiber.GetEffects());
            Assert.Equal("test", meta.Label);
            Assert.Empty(meta.Children);
            Assert.Equal(0, calls);
            await fiber.DisposeAsync();
            Assert.Equal(1, calls);
            await fiber.DisposeAsync();
            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public async Task DisposeManually()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int calls = 0;
            var handle = ctx.Effect(() => (Action)(() => calls++));
            var meta = Assert.Single(ctx.Fiber.GetEffects());
            Assert.Equal("anonymous", meta.Label);
            Assert.Empty(meta.Children);
            Assert.Equal(0, calls);
            await handle.DisposeAsync();
            Assert.Equal(1, calls);
            await handle.DisposeAsync();
            Assert.Equal(1, calls);
        });
    }

    [Fact]
    public async Task YieldDisposeExactTreeAndOrder()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var order = new List<int>();
            var handle = ctx.Effect(() => new IAsyncDisposable[] { new Cleanup(() => order.Add(1)), ctx.On("custom-event", (_, _) => null), new Cleanup(() => order.Add(2)), ctx.Effect(() => new IAsyncDisposable[] { ctx.On("custom-event", (_, _) => null), new Cleanup(() => order.Add(3)) }) });
            ctx.On("custom-event", (_, _) => null);
            var effects = ctx.Fiber.GetEffects();
            Assert.Equal(2, effects.Count);
            Assert.Equal("anonymous", effects[0].Label);
            Assert.Equal(2, effects[0].Children.Count);
            Assert.Equal("ctx.on(\"custom-event\")", effects[0].Children[0].Label);
            Assert.Empty(effects[0].Children[0].Children);
            Assert.Equal("anonymous", effects[0].Children[1].Label);
            Assert.Equal("ctx.on(\"custom-event\")", Assert.Single(effects[0].Children[1].Children).Label);
            Assert.Empty(effects[0].Children[1].Children[0].Children);
            Assert.Equal("ctx.on(\"custom-event\")", effects[1].Label);
            Assert.Empty(effects[1].Children);
            Assert.Empty(order);
            await handle.DisposeAsync();
            Assert.Equal([3, 2, 1], order);
            await handle.DisposeAsync();
            Assert.Equal([3, 2, 1], order);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AsyncYieldExactSequence(int mode)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var one = new TaskCompletionSource();
            var two = new TaskCompletionSource();
            var three = new TaskCompletionSource();
            var enteredTwo = new TaskCompletionSource();
            var started = new TaskCompletionSource();
            var order = new List<int>();
            async IAsyncEnumerable<IAsyncDisposable> Generate()
            {
                started.SetResult();
                await one.Task;
                order.Add(1);
                yield return new Cleanup(() => order.Add(2));
                enteredTwo.SetResult();
                await two.Task;
                order.Add(3);
                yield return new Cleanup(() => order.Add(4));
                await three.Task;
                order.Add(5);
                yield return new Cleanup(() => order.Add(6));
            }

            var handle = ctx.Effect(Generate);
            Assert.Empty(order);
            await started.Task;
            Task? disposal = null;
            if (mode == 1)
                disposal = handle.DisposeAsync().AsTask();
            one.SetResult();
            if (mode != 1)
            {
                await enteredTwo.Task;
                Assert.Equal([1], order);
                if (mode == 2)
                    disposal = handle.DisposeAsync().AsTask();
            }

            two.SetResult();
            three.SetResult();
            await handle.Ready;
            if (disposal is null)
            {
                Assert.Equal([1, 3, 5], order);
                await handle.DisposeAsync();
            }
            else
                await disposal;
            Assert.Equal(mode switch
            {
                1 => new[] { 1, 2 },
                2 => [1, 3, 4, 2],
                _ => [1, 3, 5, 6, 4, 2]
            }, order);
        });
    }

    [Fact]
    public async Task ReturnWithError()
    {
        await using var root = new Context();
        await root.RunAsync(ctx =>
        {
            int calls = 0;
            Assert.Throws<InvalidOperationException>(() => ctx.Effect((Func<Action>)(() => throw new InvalidOperationException("test"))));
            Assert.Equal(0, calls);
            Assert.Empty(ctx.Fiber.GetEffects());
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ImmediateAsyncIteratorDisposalSkipsFirstMove()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int entered = 0;
            async IAsyncEnumerable<IAsyncDisposable> Generate()
            {
                entered++;
                yield return new Cleanup(() =>
                {
                });
                await Task.CompletedTask;
            }

            var handle = ctx.Effect(Generate);
            await handle.DisposeAsync();
            Assert.Equal(0, entered);
        });
    }
}
