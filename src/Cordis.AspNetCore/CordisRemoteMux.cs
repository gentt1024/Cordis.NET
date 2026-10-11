using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Text.Json;
using Cordis.Composition;
using Microsoft.AspNetCore.Http;

namespace Cordis.AspNetCore;

internal sealed class CordisRemoteMux(
    HttpContext http, TypertHostConnection connection, Func<HttpContext, string, Task<bool>> authorize) : IAsyncDisposable
{
    private readonly SemaphoreSlim sends = new(1, 1);
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
    private readonly object gate = new();
    private readonly Dictionary<string, Call> calls = [];
    private readonly HashSet<Task> pendingCalls = [];
    private WebSocket? socket;

    public async Task RunAsync()
    {
        socket = await http.WebSockets.AcceptWebSocketAsync();
        EffectHandle? binding = null;
        try
        {
            await connection.Peer.Context.RunAsync(owner =>
            {
                binding = owner.Effect(() => new PeerBinding(this), "Remote socket bound to Peer");
                return Task.CompletedTask;
            });
            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult read;
                do
                {
                    read = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token);
                    if (read.MessageType == WebSocketMessageType.Close)
                        return;
                    if (read.MessageType != WebSocketMessageType.Text)
                    {
                        await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text messages required");
                        return;
                    }
                    message.Write(buffer, 0, read.Count);
                } while (!read.EndOfMessage);

                try
                {
                    using var document = JsonDocument.Parse(message.ToArray());
                    Dispatch(document.RootElement, checked((int)message.Length));
                }
                catch (JsonException)
                {
                    await CloseAsync(WebSocketCloseStatus.PolicyViolation, "invalid Remote stream request");
                    return;
                }
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The physical request owns draining the calls after carrier failure.
        }
        finally
        {
            await lifetime.CancelAsync();
            Task[] pending;
            lock (gate)
                pending = pendingCalls.ToArray();
            await Task.WhenAll(pending);
            if (binding is not null)
                await connection.Peer.Context.RunAsync(async _ => await binding.DisposeAsync());
        }
    }

    private void Dispatch(JsonElement frame, int bytes)
    {
        if (frame.ValueKind != JsonValueKind.Object ||
            !frame.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !frame.TryGetProperty("streamId", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(id.GetString()))
            throw new JsonException("Invalid Remote stream frame.");
        var kind = type.GetString();
        var fields = kind switch
        {
            "open" => new[] { "type", "streamId", "endpoint", "payload" },
            "item" => ["type", "streamId", "value"],
            "end" or "cancel" => ["type", "streamId"],
            _ => throw new JsonException("Unknown Remote stream frame.")
        };
        if (frame.EnumerateObject().Any(field => !fields.Contains(field.Name, StringComparer.Ordinal)))
            throw new JsonException("Unexpected Remote stream field.");
        if (kind == "open" && !frame.TryGetProperty("payload", out _))
            throw new JsonException("Remote open requires a payload field.");
        if (kind == "item" && frame.TryGetProperty("value", out var inputValue))
            TypertCodec.ValidateUplinkItem(inputValue);
        var streamId = id.GetString()!;
        lock (gate)
        {
            if (kind == "open")
            {
                if (calls.ContainsKey(streamId) || !frame.TryGetProperty("endpoint", out var endpoint) ||
                    endpoint.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(endpoint.GetString()))
                    throw new JsonException("Invalid or duplicate Remote open.");
                var args = frame.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                    payload.TryGetProperty("args", out var arguments) ? arguments.Clone() : default;
                var call = new Call(lifetime.Token, endpoint.GetString()!);
                pendingCalls.RemoveWhere(task => task.IsCompleted);
                pendingCalls.Add(call.Completion);
                calls.Add(streamId, call);
                call.Start(() => PumpAsync(streamId, endpoint.GetString()!, args, call));
            }
            else if (calls.TryGetValue(streamId, out var call))
            {
                if (kind == "cancel")
                    call.Cancel();
                else if (kind == "end")
                    call.End();
                else
                    call.Push(frame.TryGetProperty("value", out var value) ? value.Clone() : default, bytes);
            }
        }
    }

    private async Task PumpAsync(string id, string endpoint, JsonElement arguments, Call call)
    {
        try
        {
            if (!await authorize(http, endpoint))
                throw new RemoteError("gateway/forbidden", "Remote endpoint is not authorized.");
            await foreach (var item in connection.StreamAsync(endpoint, arguments, call.Token, call.ReadUplink()))
            {
                if (call.Token.IsCancellationRequested)
                    break;
                await SendAsync(writer =>
                {
                    writer.WriteString("type", item.Ok ? "item" : "error");
                    writer.WriteString("streamId", id);
                    if (item.Ok)
                    {
                        if (item.Value is { } value)
                        {
                            writer.WritePropertyName("value");
                            value.WriteTo(writer);
                        }
                    }
                    else
                        WriteError(writer, item.Error!);
                });
                if (!item.Ok)
                    return;
            }
            if (call.Error is { } error)
                throw error;
            if (!call.Token.IsCancellationRequested)
                await SendAsync(writer => { writer.WriteString("type", "end"); writer.WriteString("streamId", id); });
        }
        catch (Exception error)
        {
            if ((!call.Token.IsCancellationRequested || call.Error is not null) && !lifetime.IsCancellationRequested)
                try
                {
                    await SendAsync(writer =>
                    {
                        writer.WriteString("type", "error");
                        writer.WriteString("streamId", id);
                        WriteError(writer, TypertRemoteResult.Failure(call.Error ?? error).Error!);
                    });
                }
                catch (Exception sendError) when (sendError is WebSocketException or OperationCanceledException)
                {
                    socket?.Abort();
                }
        }
        finally
        {
            lock (gate)
                calls.Remove(id);
        }
    }

    private static void WriteError(Utf8JsonWriter writer, TypertRemoteFailure error)
    {
        writer.WriteStartObject("error");
        writer.WriteString("code", error.Code);
        writer.WriteString("message", error.Message);
        writer.WritePropertyName("details");
        if (error.Details is { ValueKind: JsonValueKind.Object } details)
            details.WriteTo(writer);
        else
        {
            writer.WriteStartObject();
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private async Task SendAsync(Action<Utf8JsonWriter> content)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            content(writer);
            writer.WriteEndObject();
        }
        await sends.WaitAsync(lifetime.Token);
        try
        {
            await socket!.SendAsync(buffer.ToArray().AsMemory(), WebSocketMessageType.Text, true, lifetime.Token);
        }
        finally { sends.Release(); }
    }

    private async ValueTask CloseAsync(WebSocketCloseStatus status, string reason)
    {
        await sends.WaitAsync(CancellationToken.None);
        try
        {
            if (socket?.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(status, reason, http.RequestAborted);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            socket?.Abort();
        }
        finally
        {
            sends.Release();
            await lifetime.CancelAsync();
        }
    }

    public ValueTask DisposeAsync()
    {
        socket?.Dispose();
        lifetime.Dispose();
        sends.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class PeerBinding(CordisRemoteMux owner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => owner.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "peer left");
    }

    private sealed class Call(CancellationToken token, string endpoint) : IDisposable
    {
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => cancellation.Token;
        public Task Completion => completion.Task;
        private readonly object inputGate = new();
        private readonly Channel<(JsonElement Value, int Bytes)> input = Channel.CreateUnbounded<(JsonElement, int)>();
        private bool ended;
        private bool inputClosed;
        private int bufferedBytes;
        private int consumed;

        public void Push(JsonElement value, int bytes)
        {
            lock (inputGate)
            {
                if (inputClosed || Error is not null)
                    return;
                if (ended)
                    Fail(InputError("gateway/protocol", "Remote stream uplink item after end."));
                else if (bytes > 262144 - bufferedBytes)
                    Fail(InputError("gateway/uplink-overflow", "Remote stream uplink exceeded 262144 buffered bytes."));
                else
                {
                    bufferedBytes += bytes;
                    input.Writer.TryWrite((value, bytes));
                }
            }
        }

        private RemoteError InputError(string code, string message)
        {
            using var details = JsonDocument.Parse("{\"endpoint\":" + JsonSerializer.Serialize(endpoint, TypertJson.Default.String) + "}");
            return new(code, message, details.RootElement);
        }

        public void End()
        {
            lock (inputGate)
            {
                ended = true;
                input.Writer.TryComplete();
            }
        }

        public async IAsyncEnumerable<JsonElement> ReadUplink([EnumeratorCancellation] CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref consumed, 1) != 0)
                throw new InvalidOperationException("A Remote stream has one uplink consumer.");
            try
            {
                while (await input.Reader.WaitToReadAsync(token))
                {
                    (JsonElement Value, int Bytes) item;
                    lock (inputGate)
                    {
                        if (!input.Reader.TryRead(out item))
                            continue;
                        bufferedBytes -= item.Bytes;
                    }
                    token.ThrowIfCancellationRequested();
                    yield return item.Value;
                }
            }
            finally { CloseInput(); }
        }

        private void CloseInput()
        {
            lock (inputGate)
            {
                inputClosed = true;
                input.Writer.TryComplete();
                while (input.Reader.TryRead(out _))
                {
                }
                bufferedBytes = 0;
            }
        }

        public RemoteError? Error
        {
            get; private set;
        }
        public void Cancel() => cancellation.Cancel();
        public void Fail(RemoteError error)
        {
            Error ??= error;
            CloseInput();
            Cancel();
        }
        public void Start(Func<Task> pump) => _ = RunAsync(pump);
        private async Task RunAsync(Func<Task> pump)
        {
            try
            {
                await pump();
            }
            finally { Dispose(); completion.TrySetResult(); }
        }
        public void Dispose()
        {
            CloseInput();
            cancellation.Dispose();
        }
    }
}
