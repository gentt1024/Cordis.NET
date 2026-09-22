using Xunit;

namespace Cordis.Core.Tests;

public sealed class TypedEventTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Async_start_and_resume_order_matches_raw_dispatch(bool parallel)
    {
        async Task<string[]> Run(bool typed)
        {
            var trace = new List<string>();
            await using var root = new Context();
            await root.RunAsync(async ctx =>
            {
                var key = new EventKey<int>("async-order");
                async Task<object?> First(EventContext evt, int value)
                { trace.Add("first:start"); await Task.Yield(); trace.Add("first:end"); return Undefined.Value; }
                async Task<object?> Second(EventContext evt, int value)
                { trace.Add("second:start"); await Task.Yield(); trace.Add("second:end"); return 0; }
                if (typed) { ctx.On(key, First); ctx.On(key, Second); }
                else { ctx.On(key.Name, (evt, args) => First(evt, (int)args[0]!)); ctx.On(key.Name, (evt, args) => Second(evt, (int)args[0]!)); }
                Task pending = parallel
                    ? typed ? ctx.ParallelAsync(key, 1) : ctx.ParallelAsync(key.Name, 1)
                    : typed ? ctx.SerialAsync(key, 1) : ctx.SerialAsync(key.Name, 1);
                trace.Add("returned");
                await pending;
                if (pending is Task<object?> result) Assert.Equal(0, result.Result);
            });
            Assert.Equal(parallel
                ? ["first:start", "second:start", "returned", "first:end", "second:end"]
                : new[] { "first:start", "returned", "first:end", "second:start", "second:end" }, trace);
            return trace.ToArray();
        }
        Assert.Equal(await Run(false), await Run(true));
    }

    [Fact]
    public async Task Array_payload_remains_one_slot_and_task_is_not_wrapped_in_sync_dispatch()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var array = new EventKey<object?[]>("array");
            object?[] values = [1, "two"];
            ctx.On("array", (_, args) => { Assert.Single(args); Assert.Same(values, args[0]); return Undefined.Value; });
            ctx.Emit(array, values);
            var task = Task.FromResult<object?>(false);
            var key = new EventKey<int>("task");
            ctx.On(key, (_, _) => task);
            Assert.Same(task, ctx.Bail(key, 1));
            Assert.Same(task, ctx.Waterfall(key, () => "unused", 1));
            Assert.Same(Undefined.Value, await ctx.SerialAsync(key, 1));
        });
    }

    [Theory]
    [InlineData("emit")]
    [InlineData("parallel")]
    [InlineData("serial")]
    [InlineData("bail")]
    [InlineData("waterfall")]
    public async Task Typed_and_raw_paths_have_identical_order_receiver_results_and_disposal(string mode)
        => Assert.Equal(await Trace(mode, false), await Trace(mode, true));

    private static async Task<string[]> Trace(string mode, bool typed)
    {
        var trace = new List<string>();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var key = new EventKey<int>("probe");
            var receiver = new object();
            object? Listen(string tag, EventContext evt, int value)
            {
                Assert.Same(receiver, evt.Receiver);
                trace.Add($"{tag}:{value}");
                return mode == "waterfall" ? evt.Next() : Undefined.Value;
            }
            var first = typed ? ctx.On(key, (evt, value) => Listen("first", evt, value))
                : ctx.On("probe", (evt, args) => Listen("first", evt, (int)args[0]!));
            if (typed) ctx.Once(new EventKey<int>("probe"), (evt, value) => Listen("once", evt, value), new(Prepend: true));
            else ctx.Once("probe", (evt, args) => Listen("once", evt, (int)args[0]!), new(Prepend: true));
            // Each run mixes listener and dispatch APIs in opposite directions.
            async Task Dispatch()
            {
                object? result = Undefined.Value;
                if (typed)
                {
                    switch (mode)
                    {
                        case "emit": ctx.Events.EmitWith(receiver, "probe", 7); break;
                        case "parallel": await ctx.Events.ParallelWithAsync(receiver, "probe", 7); break;
                        case "serial": result = await ctx.Events.SerialWithAsync(receiver, "probe", 7); break;
                        case "bail": result = ctx.Events.BailWith(receiver, "probe", 7); break;
                        case "waterfall": result = ctx.Events.WaterfallWith(receiver, "probe", () => "end", 7); break;
                    }
                }
                else
                {
                    switch (mode)
                    {
                        case "emit": ctx.Emit(key, 7, receiver); break;
                        case "parallel": await ctx.ParallelAsync(key, 7, receiver); break;
                        case "serial": result = await ctx.SerialAsync(key, 7, receiver); break;
                        case "bail": result = ctx.Bail(key, 7, receiver); break;
                        case "waterfall": result = ctx.Waterfall(key, () => "end", 7, receiver); break;
                    }
                }
                trace.Add("result:" + (result?.ToString() ?? "null"));
            }
            await Dispatch();
            await Dispatch();
            await first.DisposeAsync();
            await Dispatch();
        });
        Assert.Contains("first:7", trace);
        Assert.Single(trace, item => item == "once:7");
        return trace.ToArray();
    }

    [Fact]
    public async Task Overloads_preserve_async_results_null_false_zero_empty_and_observer_veto()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var key = new EventKey<int>("overloads");
            foreach (object? result in new object?[] { null, false, Undefined.Value, 0, "" })
            {
                var handle = ctx.On(key, (_, _) => result);
                var expected = EventsService.IsBailed(result) ? result : Undefined.Value;
                Assert.Same(expected, ctx.Bail(key, 1));
                Assert.Same(expected, await ctx.SerialAsync(key, 1));
                await handle.DisposeAsync();
            }
            var nullLiteral = ctx.On(key, (_, _) => null);
            Assert.Same(Undefined.Value, ctx.Bail(key, 1));
            await nullLiteral.DisposeAsync();
            var trace = new List<string>();
            var observer = ctx.On(key, async (_, _) => { trace.Add("start"); await Task.Yield(); trace.Add("end"); });
            await ctx.ParallelAsync(key, 1);
            Assert.Equal(new[] { "start", "end" }, trace);
            await observer.DisposeAsync();
            var asyncResult = ctx.Once(key, async (_, _) => { await Task.Yield(); return (object?)0; });
            Assert.Equal(0, await ctx.SerialAsync(key, 1));
            Assert.Same(Undefined.Value, await ctx.SerialAsync(key, 1));
            await asyncResult.DisposeAsync();
            var veto = ctx.On(key, (_, _) => { trace.Add("observer"); });
            var next = false;
            Assert.Same(Undefined.Value, ctx.Waterfall(key, () => { next = true; return "next"; }, 1));
            Assert.False(next);
            await veto.DisposeAsync();
            ctx.On(key, (evt, _) => evt.Next());
            Assert.Equal("next", ctx.Waterfall(key, () => "next", 1));
        });
    }

    [Fact]
    public async Task Raw_bad_payload_fails_and_receiver_filter_global_and_async_errors_survive()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var key = new EventKey<int>("filter");
            var trace = new List<string>();
            ctx.On(key, (_, value) => { trace.Add("local:" + value); });
            ctx.On(key, (_, value) => { trace.Add("global:" + value); }, new(Global: true));
            var receiver = ctx.Extend();
            receiver.Filter = _ => false;
            ctx.Emit(key, 1, receiver);
            Assert.Equal(new[] { "global:1" }, trace);
            Assert.Throws<ArgumentException>(() => ctx.Emit("filter", "bad"));
            Assert.Throws<ArgumentException>(() => ctx.Emit("filter", 1, 2));
            Assert.Throws<ArgumentException>(() => ctx.Emit("filter"));
            Assert.Throws<ArgumentException>(() => ctx.Emit("filter", new object?[] { null }));
            var errors = new EventKey<string>("errors");
            ctx.On(errors, async (_, _) => { await Task.Yield(); throw new InvalidOperationException("async failure"); });
            var error = await Assert.ThrowsAsync<AggregateException>(() => ctx.ParallelAsync(errors, "x"));
            Assert.Equal("async failure", Assert.Single(error.InnerExceptions).Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.SerialAsync(errors, "x"));
            var stopped = ctx.Inject(Array.Empty<string>(), c => { c.On(key, (_, _) => { trace.Add("owned"); }); });
            await stopped.WaitAsync();
            await stopped.DisposeAsync();
            trace.Clear(); ctx.Emit(key, 2);
            Assert.Equal(new[] { "local:2", "global:2" }, trace);
        });
    }
}
