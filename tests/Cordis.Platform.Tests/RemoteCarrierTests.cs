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

    private static async Task<TypertHostConnection> Connect(Context root, CarrierService service)
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
            result = new(owner, new TypertGateway(owner, registry));
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

        public TypertRemoteBinding TypertRemote => new(
            "carrier",
            "carrier",
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["read"] = (_, _, _) =>
                {
                    Calls++;
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
