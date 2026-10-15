using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Cordis.Composition;

public sealed partial class TypertConnection
{
    private readonly object streamGate = new();
    private Mux? mux;

    internal TypertStreamCall Open(
        string endpoint,
        Func<JsonElement> arguments,
        CancellationToken token,
        IDisposable owner)
    {
        Mux selected;
        lock (streamGate)
        {
            if (mux is null || mux.Finished)
            {
                var connectionLease = lifetime.Enter() ??
                    throw new InvalidOperationException("The Remote Connection is closed.");
                mux = new(this, connectionLease);
            }

            selected = mux;
        }

        return selected.Open(endpoint, arguments, token, owner);
    }

    private sealed class Mux
    {
        private readonly ClientWebSocket socket = new();
        private readonly SemaphoreSlim sendGate = new(1, 1);
        private readonly object gate = new();
        private readonly Dictionary<string, TypertStreamCall> streams = [];
        private readonly HashSet<Task> pendingCalls = [];
        private readonly Task connected;
        private readonly TypertCallLifetime.Lease lease;
        private Exception? failure;

        public Mux(TypertConnection connection, TypertCallLifetime.Lease lease)
        {
            this.lease = lease;
            foreach (var header in connection.http.DefaultRequestHeaders)
                socket.Options.SetRequestHeader(header.Key, string.Join(", ", header.Value));
            var url = new UriBuilder(connection.remoteBase)
            {
                Scheme = connection.remoteBase.Scheme == "https" ? "wss" : "ws",
                Path = connection.remoteBase.AbsolutePath.TrimEnd('/') + "/remote.mux"
            };
            connected = socket.ConnectAsync(url.Uri, lease.Token);
            _ = ReceiveAsync();
        }

        public bool Finished
        {
            get
            {
                lock (gate)
                    return failure is not null;
            }
        }

        public TypertStreamCall Open(
            string endpoint,
            Func<JsonElement> arguments,
            CancellationToken token,
            IDisposable owner)
        {
            var id = Guid.NewGuid().ToString();
            var call = new TypertStreamCall(token, owner);
            lock (gate)
            {
                if (failure is not null)
                {
                    call.Fail(failure);
                    call.Start(() => Task.CompletedTask);
                }
                else
                {
                    pendingCalls.RemoveWhere(task => task.IsCompleted);
                    pendingCalls.Add(call.Completion);
                    streams.Add(id, call);
                    call.Start(() => RunAsync(id, endpoint, arguments, call));
                }
            }

            return call;
        }

        private async Task RunAsync(string id, string endpoint, Func<JsonElement> arguments, TypertStreamCall call)
        {
            var opened = false;
            Task? input = null;
            try
            {
                // Encoding belongs to the logical call, before entering the physical carrier.
                var encoded = arguments();
                await connected.WaitAsync(call.Token).ConfigureAwait(false);
                await SendAsync(
                        writer =>
                        {
                            writer.WriteString("type", "open");
                            writer.WriteString("streamId", id);
                            writer.WriteString("endpoint", endpoint);
                            writer.WriteStartObject("payload");
                            writer.WritePropertyName("args");
                            encoded.WriteTo(writer);
                            writer.WriteEndObject();
                        },
                        call.Token)
                    .ConfigureAwait(false);
                opened = true;
                input = PumpInputAsync(id, call);
                await call.Terminated.WaitAsync(call.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (call.Token.IsCancellationRequested)
            {
                call.Fail(new OperationCanceledException(call.Token));
            }
            catch (Exception error)
            {
                call.Fail(
                    error is WebSocketException or HttpRequestException
                        ? new RemoteStreamCarrierException("Remote stream connection failed.", error)
                        : error);
            }
            finally
            {
                call.CloseInput();
                if (input is not null)
                    await input.ConfigureAwait(false);
                if (opened && !call.RemoteTerminal && socket.State == WebSocketState.Open)
                    try
                    {
                        await SendAsync(
                                writer =>
                                {
                                    writer.WriteString("type", "cancel");
                                    writer.WriteString("streamId", id);
                                },
                                lease.Token)
                            .ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        FailAll(
                            new RemoteStreamCarrierException("Remote stream cancellation could not be sent.", error));
                        socket.Abort();
                    }

                lock (gate)
                    streams.Remove(id);
            }
        }

        private async Task PumpInputAsync(string id, TypertStreamCall call)
        {
            try
            {
                await foreach (var value in call.Input.ReadAllAsync(call.Token).ConfigureAwait(false))
                {
                    if (call.RemoteTerminal)
                        return;
                    await SendAsync(
                            writer =>
                            {
                                writer.WriteString("type", value.HasValue ? "item" : "end");
                                writer.WriteString("streamId", id);
                                if (value is { ValueKind: not JsonValueKind.Undefined } item)
                                {
                                    writer.WritePropertyName("value");
                                    item.WriteTo(writer);
                                }
                            },
                            call.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (call.Token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                FailAll(new RemoteStreamCarrierException("Remote stream input could not be sent.", error));
                socket.Abort();
            }
        }

        private async Task SendAsync(Action<Utf8JsonWriter> content, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                content(writer);
                writer.WriteEndObject();
            }

            await sendGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                await socket
                    .SendAsync(bytes.ToArray().AsMemory(), WebSocketMessageType.Text, true, lease.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        private async Task ReceiveAsync()
        {
            try
            {
                await connected.ConfigureAwait(false);
                var buffer = new byte[8192];
                while (true)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult received;
                    do
                    {
                        received = await socket.ReceiveAsync(buffer.AsMemory(), lease.Token).ConfigureAwait(false);
                        if (received.MessageType != WebSocketMessageType.Text)
                            throw new RemoteStreamCarrierException(
                                "Remote stream socket closed or sent a non-text frame.");
                        message.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);

                    using var document = JsonDocument.Parse(message.ToArray());
                    Dispatch(document.RootElement);
                }
            }
            catch (Exception error)
            {
                FailAll(
                    error is RemoteStreamCarrierException
                        ? error
                        : new RemoteStreamCarrierException("Remote stream carrier failed.", error));
            }
            finally
            {
                // Failure closes admission before taking this snapshot. Connection disposal
                // owns the receive pump and every admitted call's remaining send/cleanup.
                socket.Abort();
                Task[] pending;
                lock (gate)
                    pending = pendingCalls.ToArray();
                await Task.WhenAll(pending).ConfigureAwait(false);
                socket.Dispose();
                sendGate.Dispose();
                lease.Dispose();
            }
        }

        private void Dispatch(JsonElement frame)
        {
            if (frame.ValueKind != JsonValueKind.Object ||
                !frame.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                !frame.TryGetProperty("streamId", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(id.GetString()))
                throw new JsonException("Invalid Remote stream frame.");
            var kind = type.GetString();
            var expected = kind switch
            {
                "item" => new[] { "type", "streamId", "value" },
                "end" => ["type", "streamId"],
                "error" => ["type", "streamId", "error"],
                _ => throw new JsonException("Unknown Remote stream frame.")
            };
            if (frame.EnumerateObject().Any(field => !expected.Contains(field.Name, StringComparer.Ordinal)))
                throw new JsonException("Unexpected Remote stream field.");
            RemoteError? failure = null;
            if (kind == "error")
            {
                if (!frame.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object ||
                    !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String ||
                    !error.TryGetProperty("message", out var text) || text.ValueKind != JsonValueKind.String ||
                    !error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Invalid Remote stream failure.");
                if (error.EnumerateObject().Any(field => field.Name is not ("code" or "message" or "details")))
                    throw new JsonException("Unexpected Remote stream error field.");
                failure = new(code.GetString()!, text.GetString()!, details.Clone());
            }

            TypertStreamCall? call;
            lock (gate)
                streams.TryGetValue(id.GetString()!, out call);
            if (call is null)
                return;
            if (kind == "item")
                call.Push(frame.TryGetProperty("value", out var value) ? value.Clone() : default);
            else
                call.End(failure);
        }

        private void FailAll(Exception error)
        {
            lock (gate)
            {
                failure ??= error;
                foreach (var call in streams.Values)
                    call.Fail(error);
            }
        }
    }
}

internal sealed class TypertStreamCall : IAsyncDisposable
{
    private readonly Channel<JsonElement> inbox = Channel.CreateUnbounded<JsonElement>(
        new()
        {
            SingleReader = false,
            SingleWriter = false
        });

    private readonly Channel<JsonElement?> outbox = Channel.CreateUnbounded<JsonElement?>();
    private bool inputEnded;
    private bool inputClosed;
    private readonly CancellationTokenSource cancellation;
    private readonly IDisposable owner;
    private readonly object gate = new();
    private readonly TaskCompletionSource terminated = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int remoteTerminal;
    private Exception? failure;
    private bool settled;
    private Task? disposal;

    public TypertStreamCall(CancellationToken token, IDisposable owner)
    {
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Token = cancellation.Token;
        this.owner = owner;
    }

    public CancellationToken Token
    {
        get;
    }

    public Task Terminated => terminated.Task;
    public Task Completion => completion.Task;
    public bool RemoteTerminal => Volatile.Read(ref remoteTerminal) != 0;
    internal ChannelReader<JsonElement?> Input => outbox.Reader;

    internal void CheckWritable()
    {
        lock (gate)
            if (inputEnded || inputClosed || Token.IsCancellationRequested || settled)
                throw new InvalidOperationException("The Remote stream uplink is closed.");
    }

    internal void Send(JsonElement value)
    {
        lock (gate)
        {
            CheckWritable();
            outbox.Writer.TryWrite(value);
        }
    }

    internal void EndInput()
    {
        lock (gate)
        {
            if (inputClosed || Token.IsCancellationRequested || settled)
                return;
            if (inputEnded)
                return;
            inputEnded = true;
            outbox.Writer.TryWrite(null);
            outbox.Writer.TryComplete();
        }
    }

    internal void CloseInput()
    {
        lock (gate)
        {
            inputClosed = true;
            outbox.Writer.TryComplete();
            while (outbox.Reader.TryRead(out _))
            {
            }
        }
    }

    public void Start(Func<Task> run) => _ = RunAsync(run);

    private async Task RunAsync(Func<Task> run)
    {
        try
        {
            await run().ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                settled = true;
                cancellation.Dispose();
            }

            owner.Dispose();
            completion.TrySetResult();
        }
    }

    public void Push(JsonElement value) => inbox.Writer.TryWrite(value);

    public void End(Exception? error)
    {
        Interlocked.Exchange(ref remoteTerminal, 1);
        CloseInput();
        inbox.Writer.TryComplete(error);
    }

    public void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref failure, error, null);
        CloseInput();
        inbox.Writer.TryComplete(error);
        while (inbox.Reader.TryRead(out _))
        {
        }

        terminated.TrySetResult();
    }

    public async ValueTask<JsonElement?> ReadAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Token);
        try
        {
            if (Volatile.Read(ref failure) is { } before)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(before);
            var available = await inbox.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
            if (Volatile.Read(ref failure) is { } after)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(after);
            if (available && inbox.Reader.TryRead(out var value))
                return value;
            terminated.TrySetResult();
            return null;
        }
        catch
        {
            terminated.TrySetResult();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is null)
            {
                disposal = DisposeAfterCompletionAsync();
                if (!settled)
                    cancellation.Cancel();
            }

            return new(disposal);
        }
    }

    private async Task DisposeAfterCompletionAsync()
    {
        await completion.Task.ConfigureAwait(false);
        lock (gate)
            cancellation.Dispose();
    }
}
