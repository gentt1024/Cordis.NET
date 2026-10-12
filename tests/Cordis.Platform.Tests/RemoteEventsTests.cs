using System.Text.Json.Serialization;
using Cordis.AspNetCore;
using Cordis.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

[assembly: TypertContext("agent", typeof(string))]

namespace Cordis.Platform.Tests;

public sealed record EventAgent(Context Context, string Id);

public sealed record EventQuestion(string Question);

[RemoteEvent("approval/request", typeof(EventTestsJson))]
public delegate Task<RemoteOptional<string?>> ApprovalEvent(
    [RemoteLookup("agent", "agentId", typeof(string))]
    EventAgent agent,
    EventQuestion request,
    Func<Task<RemoteOptional<string?>>> next,
    CancellationToken signal);

[JsonSerializable(typeof(EventQuestion))]
[JsonSerializable(typeof(string))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class EventTestsJson : JsonSerializerContext;

public sealed class RemoteEventsTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false, false, "null")]
    [InlineData(true, false, false, "null")]
    [InlineData(true, true, false, "null")]
    [InlineData(true, true, true, "null")]
    [InlineData(true, false, false, "undefined")]
    [InlineData(true, false, false, "next")]
    public async Task Resolved_context_survives_handler_and_reply_settlement(
        bool owned,
        bool cancel,
        bool closeConnection,
        string result)
    {
        await using var host = new Context();
        await using var clientRoot = new Context();
        var subject = new EventAgent(host, "agent-1");
        TypertHostConnection connection = null!;
        await host.RunAsync(ctx =>
        {
            var registry = new TypertRegistry(ctx);
            var gateway = new TypertGateway(ctx, registry);
            gateway.RegisterRemoteEvents(
                ctx,
                TypertRemoteEvents.FromCordis(
                    ctx,
                    [ApprovalEventTypert.Forward(agent => new(agent.Context, agent.Id))]),
                new("test-home"));
            connection = new(ctx, gateway);
            return Task.CompletedTask;
        });
        var replyStarted = Signal();
        var replyReleased = Signal();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.Value!.EndsWith("/$events/result", StringComparison.Ordinal))
            {
                replyStarted.TrySetResult();
                await replyReleased.Task;
            }

            await next(context);
        });
        app.MapCordisRemote("/remote", connection, (_, _) => Task.FromResult(true));
        await app.StartAsync();
        using var http = new HttpClient();
        await using var carrier = new TypertConnection(http, new Uri(app.Urls.Single() + "/remote"));
        var registryClient = new TypertRegistry(clientRoot);
        await using var events = new TypertRemoteEventsClient(clientRoot, registryClient, carrier);
        var ready = Signal();
        var entered = Signal();
        var handlerReleased = Signal();
        var cancelled = Signal();
        var released = Signal();
        var releaseCount = 0;
        var resolveCount = 0;
        await clientRoot.RunAsync(ctx =>
        {
            registryClient.RegisterClientContext(
                ctx,
                "agent",
                new(
                    _ => "agent-1",
                    identity =>
                    {
                        Assert.Equal("agent-1", identity);
                        resolveCount++;
                        return owned
                            ? TypertResolvedContext.Owned(
                                ctx,
                                () =>
                                {
                                    releaseCount++;
                                    released.TrySetResult();
                                })
                            : ctx;
                    }));
            ApprovalEventTypert.On(events, ctx, Handle);
            return Task.CompletedTask;
        });
        using var generation = new CancellationTokenSource();
        var pump = events.RunAsync(
            info =>
            {
                Assert.Equal("test-home", info.Home);
                ready.TrySetResult();
            },
            generation.Token);
        using var request = new CancellationTokenSource();
        Task<RemoteOptional<string?>> outcome = null!;
        try
        {
            await ready.Task.WaitAsync(Limit);
            await host.RunAsync(ctx =>
            {
                outcome = ApprovalEventTypert.InvokeAsync(
                    ctx,
                    subject,
                    new("allow?"),
                    () => Task.FromResult(new RemoteOptional<string?>("fallback")),
                    request.Token);
                return Task.CompletedTask;
            });
            await entered.Task.WaitAsync(Limit);
            Assert.Equal(1, resolveCount);
            Assert.Equal(0, releaseCount);
            if (cancel)
            {
                await request.CancelAsync();
                await cancelled.Task.WaitAsync(Limit);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outcome.WaitAsync(Limit));
                var closing = closeConnection ? carrier.DisposeAsync().AsTask() : events.DisposeAsync().AsTask();
                Assert.False(closing.IsCompleted);
                Assert.Equal(0, releaseCount);
                handlerReleased.TrySetResult();
                await closing.WaitAsync(Limit);
                Assert.False(replyStarted.Task.IsCompleted);
            }
            else
            {
                handlerReleased.TrySetResult();
                await replyStarted.Task.WaitAsync(Limit);
                Assert.Equal(0, releaseCount);
                replyReleased.TrySetResult();
                var returned = await outcome.WaitAsync(Limit);
                Assert.Equal(result != "undefined", returned.HasValue);
                if (returned.HasValue)
                    Assert.Equal(result == "next" ? "fallback" : null, returned.Value);
                if (owned)
                    await released.Task.WaitAsync(Limit);
                await events.DisposeAsync();
            }

            await pump.WaitAsync(Limit);
            Assert.Equal(owned ? 1 : 0, releaseCount);
            await events.DisposeAsync();
            await clientRoot.RunAsync(ctx =>
            {
                ctx.Provide("still-borrowed", new object());
                return Task.CompletedTask;
            });
        }
        finally
        {
            handlerReleased.TrySetResult();
            replyReleased.TrySetResult();
            await generation.CancelAsync();
            await events.DisposeAsync();
            await pump.WaitAsync(Limit);
            await connection.DisposeAsync();
        }

        async Task<RemoteEventResult<RemoteOptional<string?>>> Handle(
            Context agent,
            EventQuestion value,
            Func<Task<RemoteEventResult<RemoteOptional<string?>>>> next,
            CancellationToken signal)
        {
            Assert.Same(clientRoot, agent);
            Assert.Equal("allow?", value.Question);
            using var registration = signal.Register(() => cancelled.TrySetResult());
            entered.TrySetResult();
            await handlerReleased.Task;
            Assert.Equal(0, releaseCount);
            if (result == "next")
                return await next();
            return result == "undefined" ? RemoteOptional<string?>.Undefined : new RemoteOptional<string?>(null);
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

}
