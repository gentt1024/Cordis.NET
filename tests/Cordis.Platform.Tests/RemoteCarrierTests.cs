using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis.AspNetCore;
using Cordis.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed class RemoteCarrierTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Event_stream_returns_unused_uplink_without_stopping_observers_or_siblings(bool overflow)
    {
        await using var root = new Context();
        await using var connection = await Connect(root, new CarrierService(), events: true);
        await using var app = Server(connection);
        await app.StartAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new UriBuilder(app.Urls.Single() + "/remote/remote.mux")
            {
                Scheme = "ws"
            }.Uri,
            CancellationToken.None);
        await Send("{\"type\":\"open\",\"streamId\":\"events\",\"endpoint\":\"$events\",\"payload\":{\"args\":{}}}");
        using var ready = await Read(socket).WaitAsync(Limit);
        Assert.Equal("ready", ready.RootElement.GetProperty("value").GetProperty("type").GetString());
        if (!overflow)
            await Send("{\"type\":\"end\",\"streamId\":\"events\"}");
        await Send(
            "{\"type\":\"item\",\"streamId\":\"events\",\"value\":\"" + new string('x', overflow ? 262144 : 1) + "\"}");
        // The sibling reply fences the preceding input frames through the same physical receive loop.
        await Send(
            "{\"type\":\"open\",\"streamId\":\"sibling\",\"endpoint\":\"carrier/watch\",\"payload\":{\"args\":{\"id\":1}}}");
        using var sibling = await Read(socket).WaitAsync(Limit);
        Assert.Equal("sibling", sibling.RootElement.GetProperty("streamId").GetString());
        Assert.Equal("item", sibling.RootElement.GetProperty("type").GetString());
        using var end = await Read(socket).WaitAsync(Limit);
        Assert.Equal("end", end.RootElement.GetProperty("type").GetString());
        await root.RunAsync(ctx =>
        {
            ctx.Emit("carrier/changed", 7);
            return Task.CompletedTask;
        });
        using var notification = await Read(socket).WaitAsync(Limit);
        Assert.Equal("events", notification.RootElement.GetProperty("streamId").GetString());
        Assert.Equal("carrier/changed", notification.RootElement.GetProperty("value").GetProperty("event").GetString());
        Assert.Equal(7, notification.RootElement.GetProperty("value").GetProperty("args")[0].GetInt32());
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

        Task Send(string frame) => socket.SendAsync(
            Encoding.UTF8.GetBytes(frame),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }

    [Fact]
    public async Task Both_carriers_reject_invalid_payload_before_lookup_and_business()
    {
        await using var root = new Context();
        var service = new CarrierService();
        var connection = await Connect(root, service);
        await using var app = Server(connection);
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new UriBuilder(new Uri(http.BaseAddress, "/remote/remote.mux"))
            {
                Scheme = "ws"
            }.Uri,
            CancellationToken.None);
        foreach (var payload in new[]
                 {
                     "{\"args\":{\"id\":1}}",
                     "{\"args\":{\"id\":1},\"extra\":true}",
                     "{}",
                     "{\"args\":null}",
                     "{\"args\":[]}",
                     "null",
                     "[]",
                     "{\"args\":{\"id\":1}}"
                 })
        {
            var valid = payload == "{\"args\":{\"id\":1}}";
            var before = (service.Lookups, service.Calls);
            // Fixed HTTP outer envelopes allow additional fields; nested payloads do not.
            using var response = await http.PostAsync(
                "/remote/carrier/read",
                new StringContent(
                    "{\"type\":\"client-request\",\"rpcId\":\"probe\",\"method\":\"carrier/read\",\"extra\":true,\"payload\":" +
                    payload + "}",
                    Encoding.UTF8,
                    "application/json"));
            response.EnsureSuccessStatusCode();
            using var unary = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = unary.RootElement.GetProperty("result");
            Assert.Equal(valid, result.GetProperty("ok").GetBoolean());
            if (!valid)
                Assert.Equal("gateway/internal", result.GetProperty("error").GetProperty("code").GetString());
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(
                    "{\"type\":\"open\",\"streamId\":\"probe\",\"endpoint\":\"carrier/watch\",\"payload\":" + payload +
                    "}"),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
            using var frame = await Read(socket).WaitAsync(Limit);
            Assert.Equal(valid ? "item" : "error", frame.RootElement.GetProperty("type").GetString());
            if (valid)
            {
                using var end = await Read(socket).WaitAsync(Limit);
                Assert.Equal("end", end.RootElement.GetProperty("type").GetString());
                Assert.Equal((before.Lookups + 2, before.Calls + 2), (service.Lookups, service.Calls));
            }
            else
            {
                Assert.Equal(
                    "gateway/internal",
                    frame.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.Equal(before, (service.Lookups, service.Calls));
            }
        }

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Peer_close_unblocks_owned_send_and_settles_the_request()
    {
        await using var root = new Context();
        var service = new CarrierService();
        var connection = await Connect(root, service);
        using var socket = new BlockedSocket();
        await using var app = Server(connection, socket);
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        var request = http.GetAsync("/remote/remote.mux");
        Task? closing = null;
        try
        {
            await socket.Sending.Task.WaitAsync(Limit);
            closing = connection.DisposeAsync().AsTask();
            await socket.SendReleased.Task.WaitAsync(Limit);
            await closing.WaitAsync(Limit);
            using var response = await request.WaitAsync(Limit);
            await connection.DisposeAsync();
            Assert.Equal(1, service.Calls);
        }
        finally
        {
            socket.Abort();
            if (closing is not null)
                await closing.WaitAsync(Limit);
            using var response = await request.WaitAsync(Limit);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Carrier_identity_is_host_selected_and_non_owner_never_reaches_lookup(bool guest)
    {
        await using var root = new Context();
        var service = new CarrierService();
        using var identity = JsonDocument.Parse("{\"kind\":\"guest\",\"subject\":\"g1\"}");
        await using var connection = await Connect(
            root,
            service,
            options: new(guest ? new(identity.RootElement) : TypertPrincipal.Owner));
        await using var app = Server(connection);
        await app.StartAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(
            new UriBuilder(new Uri(http.BaseAddress, "/remote/remote.mux"))
            {
                Scheme = "ws"
            }.Uri,
            CancellationToken.None);
        // HTTP permits extra envelope fields, but none may change the Host-selected Peer.
        using var response = await http.PostAsync(
            "/remote/carrier/read",
            new StringContent(
                "{\"type\":\"client-request\",\"rpcId\":\"identity\",\"method\":\"carrier/read\",\"principal\":{\"kind\":\"owner\"},\"peer\":{\"principal\":{\"kind\":\"owner\"}},\"payload\":{\"args\":{\"id\":1}}}",
                Encoding.UTF8,
                "application/json"));
        response.EnsureSuccessStatusCode();
        using var unary = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = unary.RootElement.GetProperty("result");
        Assert.Equal(!guest, result.GetProperty("ok").GetBoolean());
        if (guest)
            Assert.Equal("remote-access/denied", result.GetProperty("error").GetProperty("code").GetString());
        else
            Assert.Same(connection.Peer, service.LastPeer);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(
                "{\"type\":\"open\",\"streamId\":\"identity\",\"endpoint\":\"carrier/watch\",\"payload\":{\"args\":{\"id\":1}}}"),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
        using var frame = await Read(socket).WaitAsync(Limit);
        Assert.Equal(guest ? "error" : "item", frame.RootElement.GetProperty("type").GetString());
        if (guest)
            Assert.Equal(
                "remote-access/denied",
                frame.RootElement.GetProperty("error").GetProperty("code").GetString());
        else
        {
            using var end = await Read(socket).WaitAsync(Limit);
            Assert.Equal("end", end.RootElement.GetProperty("type").GetString());
            Assert.Same(connection.Peer, service.LastPeer);
        }

        Assert.Equal(guest ? 0 : 2, service.Lookups);
        Assert.Equal(guest ? 0 : 2, service.Calls);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Closed_peer_HTTP_error_precedes_nested_payload_validation()
    {
        await using var root = new Context();
        var service = new CarrierService();
        await using var connection = await Connect(root, service);
        await using var app = Server(connection);
        await app.StartAsync();
        await connection.DisposeAsync();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        foreach (var endpoint in new[] { "carrier/read", "missing/read", "$events/result" })
        {
            using var response = await http.PostAsync(
                "/remote/" + endpoint,
                new StringContent(
                    "{\"type\":\"client-request\",\"rpcId\":\"closed\",\"method\":\"" + endpoint +
                    "\",\"payload\":null}",
                    Encoding.UTF8,
                    "application/json"));
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = document.RootElement.GetProperty("result").GetProperty("error");
            Assert.Equal("gateway/peer-disposed", error.GetProperty("code").GetString());
            Assert.Equal(endpoint, error.GetProperty("details").GetProperty("endpoint").GetString());
        }

        Assert.Equal(0, service.Lookups);
        Assert.Equal(0, service.Calls);
    }

    private static WebApplication Server(TypertHostConnection connection, WebSocket? socket = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        if (socket is null)
            app.UseWebSockets();
        else
            app.Use((context, next) =>
            {
                context.Features.Set<IHttpWebSocketFeature>(new SocketFeature(socket));
                return next(context);
            });
        app.MapCordisRemote("/remote", connection, (_, _) => Task.FromResult(true));
        return app;
    }

    private static async Task<TypertHostConnection> Connect(
        Context root,
        CarrierService service,
        bool events = false,
        TypertPeerOptions? options = null)
    {
        TypertHostConnection result = null!;
        await root.RunAsync(owner =>
        {
            var registry = new TypertRegistry(owner);
            var codec = TypertCodec.Create(CarrierJson.Default.Int32);
            registry.Register(
                owner,
                new TypertContribution(
                    "carrier",
                    new[] { false, true }
                        .Select(stream =>
                            new TypertInvocationDescriptor(
                                "carrier#" + (stream ? "watch" : "read"),
                                "carrier",
                                "carrier",
                                stream ? "watch" : "read",
                                [
                                    new("id", "id", codec)
                                    {
                                        Lookup = "id"
                                    }
                                ],
                                codec)
                            {
                                IsStream = stream
                            })
                        .ToArray()));
            registry.RegisterLookup(
                owner,
                "id",
                new(
                    "id",
                    "id",
                    "System.Int32",
                    "System.Int32",
                    value =>
                    {
                        service.Lookups++;
                        return ValueTask.FromResult(value);
                    }));
            owner.Provide("carrier", service);
            var gateway = new TypertGateway(owner, registry);
            if (events)
                gateway.RegisterRemoteEvents(
                    owner,
                    TypertRemoteEvents.FromCordis(owner, [new("carrier/changed", [codec])]),
                    new("test-home"));
            result = new(owner, gateway, options ?? new(TypertPrincipal.Owner));
            return Task.CompletedTask;
        });
        return result;
    }

    private static async Task<JsonDocument> Read(ClientWebSocket socket)
    {
        var bytes = new byte[4096];
        var read = await socket.ReceiveAsync(bytes.AsMemory(), CancellationToken.None);
        Assert.True(read.EndOfMessage);
        return JsonDocument.Parse(bytes.AsMemory(0, read.Count));
    }

    private sealed class CarrierService : ITypertRemoteService
    {
        public int Calls;
        public int Lookups;
        public TypertPeer? LastPeer;

        public TypertRemoteBinding TypertRemote => new(
            "carrier",
            "carrier",
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["read"] = (_, _, _) =>
                {
                    Calls++;
                    LastPeer = TypertInvocation.Current?.Peer;
                    return Task.FromResult<object?>(1);
                }
            },
            new Dictionary<string, TypertStreamInvoker>
            {
                ["watch"] = (_, _, token) => Watch(token)
            });

        private async IAsyncEnumerable<object?> Watch([EnumeratorCancellation] CancellationToken token)
        {
            Calls++;
            LastPeer = TypertInvocation.Current?.Peer;
            token.ThrowIfCancellationRequested();
            yield return 1;
            await Task.CompletedTask;
        }
    }

    private sealed class SocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;
        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) => Task.FromResult(socket);
    }

    private sealed class BlockedSocket : WebSocket
    {
        private readonly CancellationTokenSource aborted = new();
        private bool read;
        private WebSocketState state = WebSocketState.Open;

        public TaskCompletionSource Sending
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SendReleased
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override WebSocketState State => state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override void Abort()
        {
            if (state == WebSocketState.Aborted)
                return;
            state = WebSocketState.Aborted;
            aborted.Cancel();
        }

        public override void Dispose()
        {
            Abort();
            aborted.Dispose();
        }

        public override Task CloseAsync(WebSocketCloseStatus status, string? reason, CancellationToken token) =>
            CloseOutputAsync(status, reason, token);

        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? reason, CancellationToken token)
        {
            state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken token)
        {
            if (!read)
            {
                read = true;
                var bytes = Encoding.UTF8.GetBytes(
                    "{\"type\":\"open\",\"streamId\":\"blocked\",\"endpoint\":\"carrier/watch\",\"payload\":{\"args\":{\"id\":1}}}");
                bytes.AsSpan().CopyTo(buffer.AsSpan());
                return new(bytes.Length, WebSocketMessageType.Text, true);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
            await Task.Delay(Timeout.Infinite, linked.Token);
            throw new InvalidOperationException();
        }

        public override async Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType type,
            bool end,
            CancellationToken token)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
            Sending.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, linked.Token);
            }
            finally
            {
                SendReleased.TrySetResult();
            }
        }
    }
}

[JsonSerializable(typeof(int))]
internal partial class CarrierJson : JsonSerializerContext;
