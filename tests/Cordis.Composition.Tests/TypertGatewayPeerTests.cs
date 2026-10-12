using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertGatewayPeerTests
{
    [Fact]
    public async Task Direct_calls_read_current_connection_and_reuse_the_owned_fallback()
    {
        await using var root = new Context();
        var service = new Observer();
        var registry = new TypertRegistry(root);
        Context owner = null!;
        Fiber scope = null!;
        await root.RunAsync(ctx =>
        {
            scope = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (child, _) => owner = child
                });
            Register(ctx, registry, service);
            return Task.CompletedTask;
        });
        await scope.WaitAsync();
        var gateway = new TypertGateway(owner, registry);
        var fallback = await Read(gateway, service);
        Assert.Same(fallback, await Read(gateway, service));
        var releases = 0;
        await fallback.Context.RunAsync(ctx =>
        {
            ctx.Effect(() => () => releases++);
            return Task.CompletedTask;
        });
        TypertHostConnection connection = null!;
        EffectHandle mount = null!;
        await root.RunAsync(ctx =>
        {
            connection = new(ctx, gateway);
            mount = ctx.Provide("connection", connection);
            return Task.CompletedTask;
        });
        Assert.Same(connection.Peer, await Read(gateway, service));
        await root.RunAsync(async _ => await mount.DisposeAsync());
        Assert.Same(fallback, await Read(gateway, service));
        var explicitCall = await connection.InvokeAsync("peer/read", Empty);
        Assert.True(explicitCall.Ok);
        Assert.Same(connection.Peer, service.Last);
        var routed = new List<string>();
        await root.RunAsync(ctx =>
        {
            fallback.Context.On(
                "peer/routed",
                (_, _) =>
                {
                    routed.Add("fallback");
                    return Undefined.Value;
                });
            connection.Peer.Context.On(
                "peer/routed",
                (_, _) =>
                {
                    routed.Add("connection");
                    return Undefined.Value;
                });
            ctx.Events.EmitWith(Scope.Target(ctx, connection.Peer), "peer/routed");
            Assert.Equal(["connection"], routed);
            ctx.Events.EmitWith(Scope.Target(ctx, fallback), "peer/routed");
            Assert.Equal(["connection", "fallback"], routed);
            return Task.CompletedTask;
        });
        await connection.DisposeAsync();
        await root.RunAsync(ctx =>
        {
            ctx.Events.EmitWith(Scope.Target(ctx, connection.Peer), "peer/routed");
            Assert.Equal(["connection", "fallback"], routed);
            return Task.CompletedTask;
        });
        Assert.Equal(0, releases);
        await root.RunAsync(async _ => await scope.DisposeAsync());
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stream_keeps_its_initial_peer_through_reads_and_cleanup(bool mounted)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var service = new Observer();
        TypertHostConnection connection = null!;
        EffectHandle? mount = null;
        await root.RunAsync(ctx =>
        {
            Register(ctx, registry, service);
            connection = new(ctx, gateway);
            if (mounted)
                mount = ctx.Provide("connection", connection);
            return Task.CompletedTask;
        });
        await using var stream = gateway.StreamAsync("peer/watch", Empty).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stream.Current.Ok);
        var initial = Assert.IsType<TypertPeer>(service.Last);
        if (mounted)
            Assert.Same(connection.Peer, initial);
        await root.RunAsync(async ctx =>
        {
            if (mount is not null)
                await mount.DisposeAsync();
            else
                mount = ctx.Provide("connection", connection);
        });
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stream.Current.Ok);
        Assert.Same(initial, service.Last);
        await stream.DisposeAsync();
        Assert.Same(initial, service.Cleanup);
        await root.RunAsync(async _ => await mount!.DisposeAsync());
        await connection.DisposeAsync();
    }

    private static JsonElement Empty
    {
        get
        {
            using var document = JsonDocument.Parse("{}");
            return document.RootElement.Clone();
        }
    }

    private static async Task<TypertPeer> Read(TypertGateway gateway, Observer service)
    {
        var result = await gateway.InvokeAsync("peer/read", Empty);
        Assert.True(result.Ok, result.Error?.Message);
        return Assert.IsType<TypertPeer>(service.Last);
    }

    private static void Register(Context owner, TypertRegistry registry, Observer service)
    {
        var codec = TypertCodec.Create(TypertRegistryJson.Default.Int32);
        registry.Register(
            owner,
            new TypertContribution(
                "peer",
                new[] { false, true }
                    .Select(stream =>
                        new TypertInvocationDescriptor(
                            "peer#" + (stream ? "watch" : "read"),
                            "peer",
                            "peer",
                            stream ? "watch" : "read",
                            [],
                            codec)
                        {
                            IsStream = stream
                        })
                    .ToArray()));
        owner.Provide("peer", service);
    }

    private sealed class Observer : ITypertRemoteService
    {
        internal TypertPeer? Last;
        internal TypertPeer? Cleanup;

        public TypertRemoteBinding TypertRemote => new(
            "peer",
            "peer",
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["read"] = (_, _, _) =>
                {
                    Last = TypertInvocation.Current?.Peer;
                    return Task.FromResult<object?>(1);
                }
            },
            new Dictionary<string, TypertStreamInvoker>
            {
                ["watch"] = (_, _, _) => Watch()
            });

        private async IAsyncEnumerable<object?> Watch()
        {
            try
            {
                for (var i = 0;i < 2;i++)
                {
                    Last = TypertInvocation.Current?.Peer;
                    yield return i;
                    await Task.CompletedTask;
                }
            }
            finally
            {
                Cleanup = TypertInvocation.Current?.Peer;
            }
        }
    }
}
