using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ScopeTests
{
    [Fact]
    public async Task Routing_preserves_identity_ancestry_filters_and_global_listeners()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var outerKey = new object();
            var innerKey = new object();
            var otherKey = new object();
            await using var outer = Scope.Create(ctx, outerKey);
            await using var inner = Scope.Create(outer.Context, innerKey);
            await using var other = Scope.Create(ctx, otherKey);
            Assert.Null(Scope.Of(ctx));
            Assert.Same(innerKey, Scope.Of(inner.Context.Extend()));
            Assert.Null(Scope.ParentOf(innerKey));
            var binding = Scope.BindParent(innerKey, outerKey);
            Assert.Equal([innerKey, outerKey], Scope.ChainOf(innerKey));
            Assert.Throws<InvalidOperationException>(() => Scope.BindParent(innerKey, otherKey));
            Assert.Throws<InvalidOperationException>(() => Scope.BindParent(outerKey, innerKey));
            Assert.Throws<InvalidOperationException>(() => binding.Rebind(innerKey));
            var seen = new List<string>();
            Listen(ctx, "untagged");
            Listen(outer.Context, "outer");
            Listen(inner.Context, "inner");
            Listen(other.Context, "other");
            other.Context.On(
                "routed",
                (_, _) =>
                {
                    seen.Add("global");
                    return Undefined.Value;
                },
                new(Global: true));

            Dispatch(innerKey, ["untagged", "outer", "inner", "global"]);
            Dispatch(outerKey, ["untagged", "outer", "global"]);
            Dispatch(null, ["untagged", "global"]);
            binding.Rebind(otherKey);
            Dispatch(innerKey, ["untagged", "inner", "other", "global"]);

            var subject = ctx.Extend();
            subject.Filter = _ => false;
            var carrier = Scope.Target(subject, innerKey);
            subject.Filter = _ => true;
            seen.Clear();
            ctx.Events.EmitWith(carrier, "routed");
            Assert.Equal(["global"], seen);
            Assert.True(Scope.IsCarrier(carrier));
            Assert.Same(innerKey, Scope.CarrierKeyOf(carrier));
            Assert.False(Scope.IsCarrier(subject));
            var unkeyed = Scope.Target(subject, null);
            Assert.True(Scope.IsCarrier(unkeyed));
            Assert.Null(Scope.CarrierKeyOf(unkeyed));

            void Listen(Context owner, string name) => owner.On(
                "routed",
                (_, _) =>
                {
                    seen.Add(name);
                    return Undefined.Value;
                });

            void Dispatch(object? key, string[] expected)
            {
                seen.Clear();
                ctx.Events.EmitWith(Scope.Target(ctx, key), "routed");
                Assert.Equal(expected, seen);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Raw_and_repeat_disposal_join_the_same_async_cleanup(bool activated)
    {
        await using var root = new Context();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Scope scope = null!;
        Task raw = Task.CompletedTask;
        var finished = 0;
        await root.RunAsync(ctx =>
        {
            scope = Scope.Create(ctx, new object(), new object());
            scope.Context.Effect(() => new Cleanup(async () =>
            {
                started.SetResult();
                await release.Task;
                finished++;
            }));
            if (!activated)
                raw = scope.DisposeRawAsync().AsTask();
            return Task.CompletedTask;
        });
        if (activated)
        {
            await scope.Context.Fiber.WaitAsync();
            raw = scope.DisposeRawAsync().AsTask();
        }

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = scope.DisposeAsync().AsTask();
        var second = scope.DisposeAsync().AsTask();
        await root.RunAsync(_ => Task.CompletedTask);
        try
        {
            Assert.False(first.IsCompleted, first.Exception?.ToString());
            Assert.False(second.IsCompleted, second.Exception?.ToString());
            Assert.Equal(0, finished);
        }
        finally
        {
            release.SetResult();
        }

        await Task.WhenAll(raw, first, second).WaitAsync(TimeSpan.FromSeconds(10));
        await scope.DisposeAsync();
        Assert.Equal(1, finished);
    }

    private sealed class Cleanup(Func<Task> callback) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(callback());
    }
}
