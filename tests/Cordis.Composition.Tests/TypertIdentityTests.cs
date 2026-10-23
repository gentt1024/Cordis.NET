using System.Runtime.CompilerServices;
using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertIdentityTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task Principal_is_detached_and_only_non_owner_calls_are_attributed()
    {
        TypertPrincipal principal;
        using (var document = JsonDocument.Parse("{\"kind\":\"guest\",\"subject\":\"g1\"}"))
            principal = new(document.RootElement);
        Assert.Equal("g1", principal.Identity.GetProperty("subject").GetString());
        Assert.Null(TypertPrincipal.PrincipalOf(null));
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            await using var owner = new TypertPeer(ctx, new(TypertPrincipal.Owner));
            await using var guest = new TypertPeer(ctx, new(principal));
            await using var lookalike = new TypertPeer(ctx, new(new(Json("{\"kind\":\"Owner\"}"))));
            Assert.Null(
                TypertPrincipal.PrincipalOf(
                    new(ctx, "test/read", default)
                    {
                        Peer = owner
                    }));
            Assert.Same(
                principal,
                TypertPrincipal.PrincipalOf(
                    new(ctx, "test/read", default)
                    {
                        Peer = guest
                    }));
            Assert.Same(
                lookalike.Principal,
                TypertPrincipal.PrincipalOf(
                    new(ctx, "test/read", default)
                    {
                        Peer = lookalike
                    }));
            Assert.Same(guest, Scope.Of(guest.Context));
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"kind\":1}")]
    [InlineData("{\"kind\":\"\"}")]
    [InlineData("{\"kind\":\"owner\",\"kind\":\"guest\"}")]
    public void Principal_requires_one_string_kind(string value) =>
        Assert.Throws<ArgumentException>(() => new TypertPrincipal(Json(value)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Peer_lifetime_and_registrations_end_once_with_explicit_disposal_or_owner(bool withdrawOwner)
    {
        await using var root = new Context();
        TypertPeer peer = null!;
        TypertPeer sibling = null!;
        Fiber owner = null!;
        var cleanup = 0;
        var cancelled = 0;
        await root.RunAsync(ctx =>
        {
            sibling = new(ctx, new(TypertPrincipal.Owner));
            owner = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (child, _) =>
                    {
                        peer = new(child, new(TypertPrincipal.Owner));
                        peer.Context.Effect(() => (Action)(() => cleanup++));
                    }
                });
            return Task.CompletedTask;
        });
        await owner.WaitAsync();
        using var registration = peer.CancellationToken.Register(() => cancelled++);
        if (withdrawOwner)
        {
            await root.RunAsync(async _ => await owner.DisposeAsync());
            await owner.WaitAsync();
        }
        else
            await Task.WhenAll(peer.DisposeAsync().AsTask(), peer.DisposeAsync().AsTask()).WaitAsync(Limit);

        Assert.True(peer.CancellationToken.IsCancellationRequested);
        Assert.False(sibling.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, cleanup);
        Assert.Equal(1, cancelled);
        await peer.DisposeAsync();
        Assert.Equal(1, cleanup);
        await sibling.DisposeAsync();
    }

    [Theory]
    [InlineData("identity/read", false)]
    [InlineData("missing/read", false)]
    [InlineData("$events/result", false)]
    [InlineData("identity/watch", true)]
    [InlineData("missing/watch", true)]
    [InlineData("$events", true)]
    public async Task Closed_peer_precedes_descriptors_arguments_and_event_preparation(string endpoint, bool stream)
    {
        await using var root = new Context();
        var (gateway, connection, service) = await Setup(root);
        await connection.DisposeAsync();
        TypertRemoteResult result;
        var uplink = new UnusedUplink();
        if (stream)
        {
            await using var reader = gateway
                .StreamAsync(endpoint, Json("null"), connection.Peer, uplink: uplink)
                .GetAsyncEnumerator();
            Assert.True(await reader.MoveNextAsync());
            result = reader.Current;
            Assert.False(await reader.MoveNextAsync());
            Assert.True(uplink.Disposed);
            Assert.False(uplink.Read);
        }
        else
            result = await gateway.InvokeAsync(endpoint, Json("null"), connection.Peer);

        Assert.Equal("gateway/peer-disposed", result.Error?.Code);
        Assert.Equal(endpoint, result.Error?.Details?.GetProperty("endpoint").GetString());
        Assert.Equal(0, service.Calls);
        Assert.Equal("gateway/peer-disposed", (await connection.InvokeAsync(endpoint, Json("null"))).Error?.Code);
    }

    [Theory]
    [InlineData("identity/read", false)]
    [InlineData("$events/result", false)]
    [InlineData("identity/watch", true)]
    [InlineData("$events", true)]
    public async Task Non_owner_is_refused_before_preparation_on_every_gateway_entry(string endpoint, bool stream)
    {
        await using var root = new Context();
        var (gateway, connection, service) = await Setup(
            root,
            new(new(Json("{\"kind\":\"guest\",\"subject\":\"g1\"}"))));
        await using var lifetime = connection;
        await root.RunAsync(ctx =>
        {
            ctx.Provide("connection", connection);
            return Task.CompletedTask;
        });
        for (var explicitPeer = 0;explicitPeer < 2;explicitPeer++)
        {
            TypertRemoteResult result;
            if (stream)
            {
                await using var reader = (explicitPeer == 1
                    ? gateway.StreamAsync(endpoint, Json("null"), connection.Peer)
                    : gateway.StreamAsync(endpoint, Json("null"))).GetAsyncEnumerator();
                Assert.True(await reader.MoveNextAsync());
                result = reader.Current;
                Assert.False(await reader.MoveNextAsync());
            }
            else
                result = explicitPeer == 1
                    ? await gateway.InvokeAsync(endpoint, Json("null"), connection.Peer)
                    : await gateway.InvokeAsync(endpoint, Json("null"));

            Assert.Equal("remote-access/denied", result.Error?.Code);
            Assert.Equal("non-owner-unavailable", result.Error?.Details?.GetProperty("reason").GetString());
        }

        Assert.Equal(0, service.Calls);
    }

    [Fact]
    public async Task Passive_owner_can_call_business_but_cannot_subscribe_or_answer_events()
    {
        await using var root = new Context();
        var (_, connection, service) = await Setup(root, new(TypertPrincipal.Owner, TypertPeerInteractions.None));
        await using var lifetime = connection;
        Assert.True((await connection.InvokeAsync("identity/read", Json("{}"))).Ok);
        Assert.Same(connection.Peer, service.Last);
        Assert.Equal(
            "gateway/interactions-forbidden",
            (await connection.InvokeAsync("$events/result", Json("null"))).Error?.Code);
        await using var stream = connection.StreamAsync("$events", Json("null")).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("gateway/interactions-forbidden", stream.Current.Error?.Code);
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task Peer_closure_cancels_pending_stream_but_running_unary_keeps_carrier_token()
    {
        await using var root = new Context();
        var (_, connection, service) = await Setup(root);
        using var carrier = new CancellationTokenSource();
        var unary = connection.InvokeAsync("identity/wait", Json("{}"), carrier.Token);
        await service.UnaryStarted.Task.WaitAsync(Limit);
        await using var stream = connection.StreamAsync("identity/watch", Json("{}")).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stream.Current.Ok);
        var pending = stream.MoveNextAsync().AsTask();
        await service.StreamWaiting.Task.WaitAsync(Limit);
        await connection.DisposeAsync();
        Assert.True(await pending.WaitAsync(Limit));
        Assert.Equal("gateway/cancelled", stream.Current.Error?.Code);
        Assert.Same(connection.Peer, service.Cleanup);
        Assert.False(service.UnaryToken.IsCancellationRequested);
        Assert.Equal(carrier.Token, service.UnaryToken);
        service.UnaryRelease.SetResult();
        Assert.True((await unary.WaitAsync(Limit)).Ok);
        Assert.False(await stream.MoveNextAsync());
    }

    [Fact]
    public async Task Peer_closure_ends_event_stream_and_explicit_sibling_remains_live()
    {
        await using var root = new Context();
        var (gateway, connection, _) = await Setup(root);
        await using var peerLifetime = connection;
        TypertHostConnection sibling = null!;
        await root.RunAsync(ctx =>
        {
            gateway.RegisterRemoteEvents(ctx, EmptyEvents, new("home"));
            sibling = new(ctx, gateway);
            return Task.CompletedTask;
        });
        await using var siblingLifetime = sibling;
        await using var stream = connection.StreamAsync("$events", Json("{}")).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("ready", stream.Current.Value?.GetProperty("type").GetString());
        var pending = stream.MoveNextAsync().AsTask();
        await connection.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Limit));
        Assert.True((await sibling.InvokeAsync("identity/read", Json("{}"))).Ok);
    }

    [Fact]
    public async Task Peer_closure_discards_buffered_event_frames()
    {
        await using var root = new Context();
        var (gateway, connection, _) = await Setup(root);
        await using var peerLifetime = connection;
        await root.RunAsync(ctx =>
        {
            var codec = TypertCodec.Create(TypertRegistryJson.Default.Int32);
            gateway.RegisterRemoteEvents(
                ctx,
                TypertRemoteEvents.FromCordis(ctx, [new("identity/changed", [codec])]),
                new("home"));
            return Task.CompletedTask;
        });
        await using var stream = connection.StreamAsync("$events", Json("{}")).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        await root.RunAsync(ctx =>
        {
            ctx.Emit("identity/changed", 1);
            ctx.Emit("identity/changed", 2);
            return Task.CompletedTask;
        });
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(1, stream.Current.Value?.GetProperty("args")[0].GetInt32());
        await connection.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.MoveNextAsync().AsTask().WaitAsync(Limit));
    }

    private static async IAsyncEnumerable<TypertRemoteEventDispatch> EmptyEvents(
        [EnumeratorCancellation] CancellationToken token)
    {
        await Task.Delay(Timeout.Infinite, token);
        yield break;
    }

    [Fact]
    public async Task Disposed_mounted_connection_does_not_fall_back_until_withdrawn()
    {
        await using var root = new Context();
        var (gateway, connection, service) = await Setup(root);
        EffectHandle mount = null!;
        await root.RunAsync(ctx =>
        {
            mount = ctx.Provide("connection", connection);
            return Task.CompletedTask;
        });
        await connection.DisposeAsync();
        Assert.Equal("gateway/peer-disposed", (await gateway.InvokeAsync("identity/read", Json("{}"))).Error?.Code);
        Assert.Equal(0, service.Calls);
        await root.RunAsync(async _ => await mount.DisposeAsync());
        Assert.True((await gateway.InvokeAsync("identity/read", Json("{}"))).Ok);
        Assert.NotSame(connection.Peer, service.Last);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_call_captures_peer_before_awaiting_context_resolution(bool stream)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var service = new Service();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolved = new TaskCompletionSource<Context?>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypertHostConnection first = null!;
        TypertHostConnection second = null!;
        EffectHandle mount = null!;
        await root.RunAsync(ctx =>
        {
            var codec = TypertCodec.Create(TypertRegistryJson.Default.Int32);
            registry.Register(
                ctx,
                new TypertContribution(
                    "identity",
                    [
                        new("identity#capture", "identity", "identity", stream ? "watch" : "read", [], codec)
                        {
                            IsStream = stream,
                            Invocation = new("room", "id", codec)
                        }
                    ]));
            registry.RegisterHostContext(
                ctx,
                "room",
                new(
                    "id",
                    "System.Int32",
                    _ =>
                    {
                        started.SetResult();
                        return new(resolved.Task);
                    }));
            ctx.Provide("identity", service);
            first = new(ctx, gateway);
            second = new(ctx, gateway);
            mount = ctx.Provide("connection", first);
            return Task.CompletedTask;
        });
        await using var firstLifetime = first;
        await using var secondLifetime = second;
        var endpoint = stream ? "identity/watch" : "identity/read";
        await using var reader = stream ? gateway.StreamAsync(endpoint, Json("{\"id\":1}")).GetAsyncEnumerator() : null;
        var moving = reader?.MoveNextAsync().AsTask();
        var unary = stream ? null : gateway.InvokeAsync(endpoint, Json("{\"id\":1}"));
        await started.Task.WaitAsync(Limit);
        await root.RunAsync(async ctx =>
        {
            await mount.DisposeAsync();
            ctx.Provide("connection", second);
        });
        resolved.SetResult(root);
        if (moving is not null)
        {
            Assert.True(await moving.WaitAsync(Limit));
            Assert.True(reader!.Current.Ok);
        }
        else
            Assert.True((await unary!.WaitAsync(Limit)).Ok);

        Assert.Same(first.Peer, service.Last);
    }

    private static async Task<(TypertGateway Gateway, TypertHostConnection Connection, Service Service)> Setup(
        Context root,
        TypertPeerOptions? options = null)
    {
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var service = new Service();
        TypertHostConnection connection = null!;
        await root.RunAsync(ctx =>
        {
            var codec = TypertCodec.Create(TypertRegistryJson.Default.Int32);
            registry.Register(
                ctx,
                new TypertContribution(
                    "identity",
                    new[] { "read", "wait", "watch" }
                        .Select(name => new TypertInvocationDescriptor(
                            "identity#" + name,
                            "identity",
                            "identity",
                            name,
                            [],
                            codec)
                        {
                            IsStream = name == "watch"
                        })
                        .ToArray()));
            ctx.Provide("identity", service);
            connection = new(ctx, gateway, options ?? new(TypertPrincipal.Owner));
            return Task.CompletedTask;
        });
        return (gateway, connection, service);
    }

    private sealed class Service : ITypertRemoteService
    {
        internal int Calls;
        internal TypertPeer? Last;
        internal TypertPeer? Cleanup;
        internal CancellationToken UnaryToken;
        internal readonly TaskCompletionSource UnaryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource UnaryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource StreamWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TypertRemoteBinding TypertRemote => new(
            "identity",
            "identity",
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["read"] = (_, _, _) =>
                {
                    Calls++;
                    Last = TypertInvocation.Current?.Peer;
                    return Task.FromResult<object?>(1);
                },
                ["wait"] = async (_, _, token) =>
                {
                    Calls++;
                    UnaryToken = token;
                    UnaryStarted.SetResult();
                    await UnaryRelease.Task;
                    return 1;
                },
            },
            new Dictionary<string, TypertStreamInvoker>
            {
                ["watch"] = (_, _, token) => Watch(token)
            });

        private async IAsyncEnumerable<object?> Watch([EnumeratorCancellation] CancellationToken token)
        {
            Calls++;
            try
            {
                Last = TypertInvocation.Current?.Peer;
                yield return 1;
                StreamWaiting.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            finally
            {
                Cleanup = TypertInvocation.Current?.Peer;
            }
        }
    }

    private sealed class UnusedUplink : IAsyncEnumerable<JsonElement>, IAsyncEnumerator<JsonElement>
    {
        internal bool Read;
        internal bool Disposed;
        public JsonElement Current => default;
        public IAsyncEnumerator<JsonElement> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync()
        {
            Read = true;
            return ValueTask.FromResult(false);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
