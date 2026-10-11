using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertGatewayPreparationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, "gateway/service-unavailable")]
    [InlineData(true, "gateway/binding-invalid")]
    public async Task Service_admission_precedes_parameter_decoding_and_lookup(bool invalidBinding, string code)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var calls = 0;
        await root.RunAsync(ctx =>
        {
            registry.Register(ctx, Contribution());
            registry.RegisterLookup(
                ctx,
                "left",
                Provider(
                    "left",
                    _ =>
                    {
                        calls++;
                        return ValueTask.FromResult<object?>(1);
                    }));
            if (invalidBinding)
                ctx.Provide("test:pair", new object());
            return Task.CompletedTask;
        });
        var gateway = new TypertGateway(root, registry);
        // Both valid and invalid input must lose to service admission.
        foreach (var value in new[] { "1", "\"invalid\"" })
        {
            var result = await gateway.InvokeAsync("pair/add", Json("{\"left\":" + value + ",\"right\":2}"));
            Assert.Equal(code, result.Error?.Code);
        }

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Independent_lookups_start_before_either_settles_and_preserve_argument_order()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var service = new PairService();
        var left = new LookupGate();
        var right = new LookupGate();
        await RegisterAsync(root, registry, service, left, right);
        var call = new TypertGateway(root, registry).InvokeAsync("pair/add", Json("{\"left\":1,\"right\":2}"));
        try
        {
            await left.Started.Task.WaitAsync(Limit);
            await right.Started.Task.WaitAsync(Limit);
            Assert.False(call.IsCompleted);
            right.Result.SetResult(20);
            left.Result.SetResult(10);
            var result = await call.WaitAsync(Limit);
            Assert.True(result.Ok);
            Assert.Equal(1020, result.Value!.Value.GetInt32());
            Assert.Equal(1, service.Calls);
        }
        finally
        {
            left.Result.TrySetResult(10);
            right.Result.TrySetResult(20);
            await call.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task First_observed_failure_returns_without_cancelling_or_waiting_for_the_other_lookup()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var service = new PairService();
        var left = new LookupGate();
        var right = new LookupGate();
        await RegisterAsync(root, registry, service, left, right);
        using var cancellation = new CancellationTokenSource();
        var call = new TypertGateway(root, registry).InvokeAsync(
            "pair/add",
            Json("{\"left\":1,\"right\":2}"),
            cancellation.Token);
        try
        {
            await left.Started.Task.WaitAsync(Limit);
            await right.Started.Task.WaitAsync(Limit);
            cancellation.Cancel();
            right.Result.SetException(new InvalidOperationException("right failed first"));
            var result = await call.WaitAsync(Limit);
            Assert.Equal("gateway/lookup-failed", result.Error?.Code);
            Assert.Equal("right", result.Error!.Details!.Value.GetProperty("field").GetString());
            Assert.False(left.Finished.Task.IsCompleted);
            Assert.Equal(0, service.Calls);
            // The already-started resolver remains cooperative; its later failure is observed.
            left.Result.SetException(new InvalidOperationException("late left failure"));
            await left.Finished.Task.WaitAsync(Limit);
            await root.RunAsync(_ => Task.CompletedTask);
            Assert.Equal(0, service.Calls);
        }
        finally
        {
            left.Result.TrySetResult(10);
            right.Result.TrySetResult(20);
            await call.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task An_immediate_decode_failure_still_starts_later_parameter_resolution()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var service = new PairService();
        var left = new LookupGate();
        var right = new LookupGate();
        await RegisterAsync(root, registry, service, left, right);
        var call = new TypertGateway(root, registry).InvokeAsync("pair/add", Json("{\"left\":\"bad\",\"right\":2}"));
        try
        {
            Assert.Equal("gateway/input-invalid", (await call.WaitAsync(Limit)).Error?.Code);
            await right.Started.Task.WaitAsync(Limit);
            Assert.False(left.Started.Task.IsCompleted);
            Assert.Equal(0, service.Calls);
        }
        finally
        {
            right.Result.TrySetResult(20);
            if (right.Started.Task.IsCompleted)
                await right.Finished.Task.WaitAsync(Limit);
        }
    }

    [Theory]
    [InlineData("definition", "gateway/definition-unavailable")]
    [InlineData("lookup", "gateway/lookup-unavailable")]
    [InlineData("service", "gateway/service-unavailable")]
    public async Task Withdrawal_during_parallel_resolution_rejects_dispatch(string withdrawn, string code)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var service = new PairService();
        var left = new LookupGate();
        var right = new LookupGate();
        var handles = await RegisterAsync(root, registry, service, left, right);
        var call = new TypertGateway(root, registry).InvokeAsync("pair/add", Json("{\"left\":1,\"right\":2}"));
        try
        {
            await left.Started.Task.WaitAsync(Limit);
            await right.Started.Task.WaitAsync(Limit);
            await root.RunAsync(async _ => await handles[withdrawn].DisposeAsync());
            left.Result.SetResult(10);
            right.Result.SetResult(20);
            Assert.Equal(code, (await call.WaitAsync(Limit)).Error?.Code);
            Assert.Equal(0, service.Calls);
        }
        finally
        {
            left.Result.TrySetResult(10);
            right.Result.TrySetResult(20);
            await call.WaitAsync(Limit);
        }
    }

    private static async Task<Dictionary<string, EffectHandle>> RegisterAsync(
        Context root,
        TypertRegistry registry,
        PairService service,
        LookupGate left,
        LookupGate right)
    {
        var handles = new Dictionary<string, EffectHandle>();
        await root.RunAsync(ctx =>
        {
            handles.Add("definition", registry.Register(ctx, Contribution()));
            handles.Add("lookup", registry.RegisterLookup(ctx, "left", Provider("left", left.ResolveAsync)));
            registry.RegisterLookup(ctx, "right", Provider("right", right.ResolveAsync));
            handles.Add("service", ctx.Provide("test:pair", service));
            return Task.CompletedTask;
        });
        return handles;
    }

    private static TypertContribution Contribution() => new(
        "pair",
        [
            new(
                "pair#add",
                "test:pair",
                "pair",
                "add",
                [
                    new("left", "left", Integer())
                    {
                        Lookup = "left"
                    },
                    new("right", "right", Integer())
                    {
                        Lookup = "right"
                    }
                ],
                Integer())
        ]);

    private static TypertLookupProvider Provider(string wire, Func<object?, ValueTask<object?>> resolve) =>
        new(wire, wire, "System.Int32", "System.Int32", resolve);

    private static TypertCodec Integer() => TypertCodec.Create(TypertRegistryJson.Default.Int32);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class LookupGate
    {
        public TaskCompletionSource Started
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<object?> Result
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<object?> ResolveAsync(object? identity)
        {
            Started.SetResult();
            try
            {
                return await Result.Task;
            }
            finally
            {
                Finished.SetResult();
            }
        }
    }

    private sealed class PairService : ITypertRemoteService
    {
        public int Calls
        {
            get;
            private set;
        }

        public TypertRemoteBinding TypertRemote => new(
            "test:pair",
            "pair",
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["add"] = (_, arguments, _) =>
                {
                    Calls++;
                    return Task.FromResult<object?>((int)arguments[0]! * 100 + (int)arguments[1]!);
                }
            },
            new Dictionary<string, TypertStreamInvoker>());
    }
}
