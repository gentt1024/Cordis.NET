using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Composition;

/// <summary>The resolved Client Context, projected request fields and Host cancellation for a waterfall.</summary>
public sealed record TypertRemoteEventRequest(Context Agent, JsonElement Data, CancellationToken CancellationToken);

/// <summary>Client-owned event listeners and fixed event generations over a borrowed Connection.</summary>
public sealed class TypertRemoteEventsClient : IAsyncDisposable
{
    internal static readonly object NextMarker = new();
    private readonly Context owner;
    private readonly TypertRegistry registry;
    private readonly TypertConnection connection;
    private readonly TypertCallLifetime lifetime = new();
    private readonly string prefix = "internal/remote-events/" + Guid.NewGuid() + "/";
    private int running;

    /// <summary>Create in the Client owner's domain. Registry, Connection and Context are borrowed.</summary>
    public TypertRemoteEventsClient(Context owner, TypertRegistry registry, TypertConnection connection)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }

    /// <summary>Own one selected event listener in its calling Fiber, with normal Cordis filtering and next semantics.</summary>
    public EffectHandle On(Context caller, string eventName, CordisEventHandler listener)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        if (!ReferenceEquals(caller.Root, owner.Root))
            throw new ArgumentException("Remote event listeners must share their Client's root.", nameof(caller));
        ObjectDisposedException.ThrowIf(!lifetime.Active, this);
        return caller.On(prefix + eventName, listener);
    }

    /// <summary>Run one Connection generation, joining active handlers and replies before it ends.</summary>
    /// <remarks>The surrounding Connection controller owns retries. A new generation retains listeners,
    /// but opens a new event client identity. A cancelled handler must settle before its owned Context is released.</remarks>
    public async Task RunAsync(Action<TypertRemoteEventHost> ready, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ready);
        using var call = lifetime.Enter() ?? throw new ObjectDisposedException(nameof(TypertRemoteEventsClient));
        using var carrier = connection.Enter();
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("The Remote event Client already has an active generation.");
        try
        {
            using var signal = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                call.Token,
                carrier.Token);
            await owner.RunAsync(_ => PumpAsync(ready, signal.Token));
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private async Task PumpAsync(Action<TypertRemoteEventHost> ready, CancellationToken token)
    {
        using var failed = new CancellationTokenSource();
        using var signal = CancellationTokenSource.CreateLinkedTokenSource(token, failed.Token);
        using var lease = lifetime.Enter() ?? throw new ObjectDisposedException(nameof(TypertRemoteEventsClient));
        await using var stream = connection.Open(
            TypertEventProtocol.Stream,
            () => TypertEventProtocol.Json(new()),
            signal.Token,
            lease);
        var active = new Dictionary<string, CancellationTokenSource>();
        var tasks = new HashSet<Task>();
        Exception? failure = null;
        string? client = null;
        try
        {
            while (await stream.ReadAsync(signal.Token) is { } value)
            {
                if (client is null)
                {
                    if (!TypertEventProtocol.Exact(value, "type", "clientId", "host") ||
                        TypertEventProtocol.Text(value, "type") != "ready" ||
                        !TypertEventProtocol.Exact(value.GetProperty("host"), "home"))
                        throw new JsonException("The Remote event stream did not begin with ready.");
                    client = TypertEventProtocol.Text(value, "clientId");
                    ready(new(TypertEventProtocol.Text(value.GetProperty("host"), "home", empty: true)));
                    continue;
                }

                var type = TypertEventProtocol.Text(value, "type");
                if (type == "cancel" && TypertEventProtocol.Exact(value, "type", "eventId"))
                {
                    if (active.TryGetValue(TypertEventProtocol.Text(value, "eventId"), out var invocation))
                        invocation.Cancel();
                    continue;
                }

                if (type == "emit" && TypertEventProtocol.Exact(value, "type", "event", "args") &&
                    value.GetProperty("args").ValueKind == JsonValueKind.Array)
                {
                    var eventName = TypertEventProtocol.Text(value, "event");
                    var arguments = value.GetProperty("args");
                    TypertCodec.ValidateUplinkItem(arguments);
                    _ = DeliverAsync(eventName, arguments.EnumerateArray().Select(x => (object?)x.Clone()).ToArray());
                    continue;
                }

                if (type != "waterfall" || !TypertEventProtocol.Exact(
                        value,
                        "type",
                        "event",
                        "eventId",
                        "agentId",
                        "request"))
                    throw new JsonException("Invalid Remote event frame.");
                var id = TypertEventProtocol.Text(value, "eventId");
                var name = TypertEventProtocol.Text(value, "event");
                var agent = TypertEventProtocol.Text(value, "agentId");
                var request = value.GetProperty("request").Clone();
                TypertEventProtocol.Request(request);
                var controller = CancellationTokenSource.CreateLinkedTokenSource(signal.Token);
                active[id] = controller;
                var task = AnswerAsync(name, id, client, agent, request, controller.Token);
                tasks.Add(task);
                _ = ObserveAsync(task, id, controller);
            }

            if (!token.IsCancellationRequested)
                throw new RemoteStreamCarrierException("The Remote event stream ended unexpectedly.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure ??= error;
        }
        finally
        {
            foreach (var controller in active.Values.ToArray())
                controller.Cancel();
            await Task.WhenAll(tasks.ToArray().Select(ObserveCompletion));
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();

        async Task ObserveAsync(Task task, string id, CancellationTokenSource controller)
        {
            try
            {
                await task;
            }
            catch (Exception error)
            {
                if (!controller.IsCancellationRequested)
                {
                    failure ??= error;
                    failed.Cancel();
                }
            }
            finally
            {
                if (active.TryGetValue(id, out var current) && ReferenceEquals(current, controller))
                    active.Remove(id);
                tasks.Remove(task);
                controller.Dispose();
            }
        }
    }

    private async Task DeliverAsync(string eventName, object?[] arguments)
    {
        try
        {
            await owner.Events.ParallelAsync(prefix + eventName, arguments);
        }
        catch (Exception error)
        {
            Report(eventName, error);
        }
    }

    private async Task AnswerAsync(
        string name,
        string id,
        string client,
        string agent,
        JsonElement request,
        CancellationToken token)
    {
        TypertResolvedContext? resolved = null;
        try
        {
            resolved = registry.GetClientContext("agent")?.Resolve(agent);
        }
        catch (Exception error)
        {
            Report(name, error);
        }

        try
        {
            var outcome = new JsonObject
            {
                ["kind"] = "next"
            };
            if (resolved is not null)
            {
                try
                {
                    var target = resolved.Value;
                    var raw = target.Events.WaterfallWith(
                        target,
                        prefix + name,
                        () => NextMarker,
                        new TypertRemoteEventRequest(target, request, token));
                    var result = await AwaitAsync(raw);
                    if (!ReferenceEquals(result, NextMarker))
                    {
                        outcome = new()
                        {
                            ["kind"] = "result"
                        };
                        if (result is not Undefined)
                        {
                            var value = result is null ? TypertEventProtocol.JsonNull :
                                result is JsonElement json ? json :
                                throw new JsonException("Remote event results require their declared JSON projection.");
                            TypertCodec.ValidateUplinkItem(value);
                            if (value.ValueKind != JsonValueKind.Undefined)
                                outcome["value"] = TypertEventProtocol.Node(value);
                        }
                    }
                }
                catch (Exception error)
                {
                    if (token.IsCancellationRequested)
                        return;
                    outcome = new()
                    {
                        ["kind"] = "rejected",
                        ["error"] = Rejection(error)
                    };
                }
            }

            if (token.IsCancellationRequested)
                return;
            var response = await connection.CallAsync(
                TypertEventProtocol.Result,
                TypertEventProtocol.Json(
                    new()
                    {
                        ["clientId"] = client,
                        ["eventId"] = id,
                        ["outcome"] = outcome
                    }),
                token);
            if (!response.Ok)
                throw new InvalidOperationException(response.Error!.Message);
        }
        finally
        {
            resolved?.Dispose();
        }
    }

    private static JsonObject Rejection(Exception error)
    {
        var value = new JsonObject
        {
            ["name"] = error is TypertRemoteEventException remote ? remote.RemoteName : error.GetType().Name,
            ["message"] = error.Message
        };
        var code = error is RemoteError rpc ? rpc.Code : (error as TypertRemoteEventException)?.Code;
        var details = error is RemoteError failure ? failure.Details : (error as TypertRemoteEventException)?.Details;
        if (code is not null)
            value["code"] = code;
        if (details is { } data)
            value["details"] = TypertEventProtocol.Node(data);
        return value;
    }

    internal static async Task<object?> AwaitAsync(object? value) => value switch
    {
        Task<object?> task => await task,
        ValueTask<object?> task => await task,
        _ => value
    };

    private static async Task ObserveCompletion(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static void Report(string name, Exception error) => Trace.TraceError("Remote event {0}: {1}", name, error);

    /// <summary>Withdraw this Client and await its running generation, handlers and replies.</summary>
    public ValueTask DisposeAsync() => lifetime.DisposeAsync();
}
