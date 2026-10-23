using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Cordis.Composition;

public sealed partial class TypertGateway
{
    private RemoteEventRegistration? remoteEvents;
    private readonly Dictionary<string, EventClient> eventClients = [];

    /// <summary>Own the sole application-selected event source and its stable Host facts.</summary>
    /// <remarks>Source withdrawal cancels pending waterfalls and drains the source. Borrowed event
    /// Contexts and remote Client scopes retain their owners. No notification replay is stored.</remarks>
    public EffectHandle RegisterRemoteEvents(
        Context owner,
        TypertRemoteEventSource source,
        TypertRemoteEventHost host)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(host.Home);
        EnsureNotificationContext(owner, nameof(owner));
        _ = caller.Get<object>("typert", strict: false);
        if (remoteEvents is not null)
            throw new InvalidOperationException("The Gateway already has a selected Remote event source.");
        return owner.Effect(
            () =>
            {
                var registration = new RemoteEventRegistration(this, source, host);
                remoteEvents = registration;
                registration.Start();
                return (IAsyncDisposable)registration;
            },
            "typert Gateway Remote event source");
    }

    internal void EnsureNotificationContext(Context context, string parameterName)
    {
        if (!ReferenceEquals(context.Root, caller.Root))
            throw new ArgumentException("Remote event owners must share the Gateway's Cordis root.", parameterName);
    }

    private async IAsyncEnumerable<TypertRemoteResult> ReadRemoteEventsAsync(
        JsonElement arguments,
        TypertPeer peer,
        [EnumeratorCancellation] CancellationToken token)
    {
        EventClient client = null!;
        await caller.RunAsync(_ =>
        {
            AdmitPeer(peer, TypertEventProtocol.Stream);
            token.ThrowIfCancellationRequested();
            if (!TypertEventProtocol.Exact(arguments))
                throw Fault(
                    "gateway/arguments-invalid",
                    TypertEventProtocol.Stream,
                    "Forwarded Remote events require an empty args object.");
            var registration = remoteEvents ?? throw Fault(
                "gateway/service-unavailable",
                TypertEventProtocol.Stream,
                "The selected Remote event source is unavailable.");
            client = registration.Subscribe(peer);
            return Task.CompletedTask;
        });
        try
        {
            token.ThrowIfCancellationRequested();
            if (client.Owner.Ended)
                yield break;
            yield return TypertRemoteResult.Success(
                TypertEventProtocol.Json(
                    new()
                    {
                        ["type"] = "ready",
                        ["clientId"] = client.Id,
                        ["host"] = new JsonObject
                        {
                            ["home"] = client.Owner.Host.Home
                        }
                    }));
            await foreach (var frame in client.Queue.Reader.ReadAllAsync(token))
            {
                token.ThrowIfCancellationRequested();
                if (client.Owner.Ended)
                    yield break;
                yield return TypertRemoteResult.Success(frame);
            }
        }
        finally
        {
            await caller.RunAsync(_ =>
            {
                client.Owner.Unsubscribe(client);
                return Task.CompletedTask;
            });
        }
    }

    private Task RunEventCallback(Func<Task> callback) => caller.RunAsync(_ => callback());

    private async Task ReceiveRemoteEventResultAsync(JsonElement arguments, TypertPeer peer)
    {
        var result = TypertEventProtocol.ReadResult(arguments);
        if (!eventClients.TryGetValue(result.Client, out var client))
            throw new InvalidOperationException("Remote event result identifies no active event stream.");
        if (!ReferenceEquals(client.Peer, peer))
            throw Fault(
                "gateway/interactions-forbidden",
                TypertEventProtocol.Result,
                "The result must come from the event subscription Peer.");
        await client.Owner.ReceiveAsync(client, result.Event, result.Outcome, result.Error);
    }

    private bool AllowsEvent(TypertPeer peer, string name, IReadOnlyList<TypertAccessResource> resources)
    {
        try
        {
            return caller
                .Get<TypertRemoteAccess>("remoteAccess", strict: false)
                ?.Decide(new(peer.Principal, "event:" + name, resources)) is TypertAccessDecision.Allow;
        }
        catch (Exception)
        {
            return false;
        }
    }


    private sealed class EventClient(RemoteEventRegistration owner, TypertPeer peer)
    {
        internal TypertPeer Peer
        {
            get;
        } = peer;

        internal bool Answers => Peer.Principal.Kind == "owner";

        internal string Id
        {
            get;
        } = Guid.NewGuid().ToString();

        internal RemoteEventRegistration Owner
        {
            get;
        } = owner;

        internal Channel<JsonElement> Queue
        {
            get;
        } = Channel.CreateUnbounded<JsonElement>(
            new()
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });

        internal HashSet<PendingEvent> Deliveries
        {
            get;
        } = [];
    }

    private sealed class PendingEvent(TypertRemoteEventInvocation source, JsonElement frame, string id)
    {
        internal string Id
        {
            get;
        } = id;

        internal TypertRemoteEventInvocation Source
        {
            get;
        } = source;

        internal JsonElement Frame
        {
            get;
        } = frame;

        internal HashSet<EventClient> Deliveries
        {
            get;
        } = [];

        internal EffectHandle? ContextEffect
        {
            get;
            set;
        }

        internal CancellationTokenRegistration Cancellation
        {
            get;
            set;
        }
    }

    private sealed class RemoteEventRegistration : IAsyncDisposable
    {
        private readonly TypertGateway gateway;
        private readonly CancellationTokenSource lifetime = new();
        private readonly IAsyncEnumerable<TypertRemoteEventDispatch> source;
        private readonly HashSet<EventClient> clients = [];
        private readonly Dictionary<string, PendingEvent> pending = [];
        private Task done = Task.CompletedTask;
        private Task? disposal;
        private bool ended;

        internal RemoteEventRegistration(
            TypertGateway gateway,
            TypertRemoteEventSource factory,
            TypertRemoteEventHost host)
        {
            this.gateway = gateway;
            Host = host;
            try
            {
                source = factory(lifetime.Token) ??
                    throw new ArgumentException("The event source returned no iterable.");
            }
            catch
            {
                lifetime.Dispose();
                throw;
            }
        }

        internal TypertRemoteEventHost Host
        {
            get;
        }

        internal bool Ended => ended;

        internal void Start() => done = ConsumeAsync();

        internal EventClient Subscribe(TypertPeer peer)
        {
            if (ended)
                throw new InvalidOperationException("The event source has ended.");
            var client = new EventClient(this, peer);
            clients.Add(client);
            gateway.eventClients.Add(client.Id, client);
            foreach (var item in pending.Values)
                Deliver(item, client);
            return client;
        }

        internal void Unsubscribe(EventClient client)
        {
            gateway.eventClients.Remove(client.Id);
            clients.Remove(client);
            foreach (var item in client.Deliveries.ToArray())
                RemoveDelivery(item, client);
            client.Queue.Writer.TryComplete();
            // Disconnect is not a next reply. Pending work can reach a later generation.
        }

        private static void Deliver(PendingEvent item, EventClient client)
        {
            if (!client.Answers || client.Peer.CancellationToken.IsCancellationRequested)
                return;
            item.Deliveries.Add(client);
            client.Deliveries.Add(item);
            client.Queue.Writer.TryWrite(item.Frame);
        }

        private static void RemoveDelivery(PendingEvent item, EventClient client)
        {
            item.Deliveries.Remove(client);
            client.Deliveries.Remove(item);
        }

        internal async Task ReceiveAsync(
            EventClient client,
            string id,
            TypertRemoteEventOutcome? outcome,
            Exception? error)
        {
            if (!pending.TryGetValue(id, out var item) || !item.Deliveries.Contains(client))
                return;
            RemoveDelivery(item, client);
            if (error is not null)
                await FinishAsync(item, null, error);
            else if (outcome is TypertRemoteEventOutcome.Result || item.Deliveries.Count == 0)
                await FinishAsync(item, outcome, null);
        }

        private async Task StartAsync(TypertRemoteEventInvocation invocation)
        {
            try
            {
                ArgumentException.ThrowIfNullOrEmpty(invocation.Event);
                ArgumentException.ThrowIfNullOrEmpty(invocation.AgentId);
                gateway.EnsureNotificationContext(invocation.Context, nameof(invocation));
                TypertEventProtocol.Request(invocation.Request);
                var id = Guid.NewGuid().ToString();
                var item = new PendingEvent(
                    invocation,
                    TypertEventProtocol.Json(
                        new()
                        {
                            ["type"] = "waterfall",
                            ["event"] = invocation.Event,
                            ["eventId"] = id,
                            ["agentId"] = invocation.AgentId,
                            ["request"] = TypertEventProtocol.Node(invocation.Request)
                        }),
                    id);
                try
                {
                    item.ContextEffect = invocation.Context.Effect(
                        () => new EventCancellation(this, item),
                        "Remote event Context lifetime");
                }
                catch
                {
                    invocation.Resolve(new TypertRemoteEventOutcome.Next());
                    return;
                }

                pending.Add(id, item);
                item.Cancellation = invocation.CancellationToken.Register(() => Cancel(item));
                if (invocation.CancellationToken.IsCancellationRequested)
                    await FinishAsync(item, null, new OperationCanceledException(invocation.CancellationToken));
                else
                    foreach (var client in clients)
                        Deliver(item, client);
            }
            catch (Exception error)
            {
                invocation.Reject(error);
            }
        }

        private void Cancel(PendingEvent item)
        {
            // The callback can run off-domain. The returned work is observed by its continuation.
            var task = gateway.RunEventCallback(() => FinishAsync(
                item,
                null,
                new OperationCanceledException("Remote event Context or request was cancelled.")));
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }

        private async Task FinishAsync(PendingEvent item, TypertRemoteEventOutcome? outcome, Exception? error)
        {
            if (!pending.Remove(item.Id))
                return;
            item.Cancellation.Unregister();
            var cancellation = TypertEventProtocol.Json(
                new()
                {
                    ["type"] = "cancel",
                    ["eventId"] = item.Id
                });
            foreach (var client in item.Deliveries.ToArray())
            {
                RemoveDelivery(item, client);
                client.Queue.Writer.TryWrite(cancellation);
            }

            if (item.ContextEffect is not null)
                await item.ContextEffect.DisposeAsync();
            if (error is not null)
                item.Source.Reject(error);
            else
                item.Source.Resolve(outcome!);
        }

        private async Task ConsumeAsync()
        {
            Exception? failure = null;
            try
            {
                await foreach (var dispatch in source.WithCancellation(lifetime.Token))
                {
                    if (lifetime.IsCancellationRequested)
                    {
                        if (dispatch is TypertRemoteEventInvocation abandoned)
                            abandoned.Reject(new OperationCanceledException(lifetime.Token));
                        break;
                    }

                    if (dispatch is TypertRemoteEventInvocation invocation)
                        await StartAsync(invocation);
                    else if (dispatch is TypertRemoteEventFrame frame)
                    {
                        ArgumentException.ThrowIfNullOrEmpty(frame.Event);
                        var args = new JsonArray();
                        foreach (var argument in frame.Args)
                        {
                            if (argument.ValueKind == JsonValueKind.Undefined)
                                throw new JsonException("Remote event arguments must be JSON values.");
                            TypertCodec.ValidateUplinkItem(argument);
                            args.Add(TypertEventProtocol.Node(argument));
                        }

                        var wire = TypertEventProtocol.Json(
                            new()
                            {
                                ["type"] = "emit",
                                ["event"] = frame.Event,
                                ["args"] = args
                            });
                        IReadOnlyList<TypertAccessResource>? resources = null;
                        var resolved = false;
                        foreach (var client in clients)
                        {
                            if (client.Peer.CancellationToken.IsCancellationRequested)
                                continue;
                            if (!client.Answers)
                            {
                                if (!resolved)
                                {
                                    resources = EventResources(frame, lifetime.Token);
                                    resolved = true;
                                }

                                if (resources is null || !gateway.AllowsEvent(client.Peer, frame.Event, resources))
                                    continue;
                            }

                            client.Queue.Writer.TryWrite(wire);
                        }
                    }
                    else
                        throw new InvalidOperationException("Unknown Remote event dispatch.");
                }

                if (!lifetime.IsCancellationRequested)
                    throw new InvalidOperationException("The Remote event source ended unexpectedly.");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                failure = error;
                throw;
            }
            finally
            {
                await EndAsync(failure ?? new OperationCanceledException("The Remote event source was removed."));
            }
        }

        private static IReadOnlyList<TypertAccessResource>? EventResources(
            TypertRemoteEventFrame frame,
            CancellationToken token)
        {
            try
            {
                if (frame.ResourceReaders is null || frame.ResourceReaders.Count == 0)
                    return [TypertAccessResource.Global];
                var resources = new List<TypertAccessResource>();
                var arguments = Array.AsReadOnly(frame.Args.Select(value => (object?)value).ToArray());
                foreach (var (kind, reader) in frame.ResourceReaders)
                {
                    if (string.IsNullOrEmpty(kind) || kind == "global" || kind.Contains('#', StringComparison.Ordinal))
                        return null;
                    var ids = reader(arguments, token);
                    if (ids is null || ids.Count == 0)
                        return null;
                    foreach (var id in ids)
                    {
                        if (string.IsNullOrEmpty(id))
                            return null;
                        resources.Add(TypertAccessResource.Create(kind, id));
                    }
                }

                return resources.AsReadOnly();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private async Task EndAsync(Exception reason)
        {
            if (ended)
                return;
            ended = true;
            if (ReferenceEquals(gateway.remoteEvents, this))
                gateway.remoteEvents = null;
            foreach (var item in pending.Values.ToArray())
                await FinishAsync(item, null, reason);
            foreach (var client in clients.ToArray())
                Unsubscribe(client);
        }

        public ValueTask DisposeAsync() => new(disposal ??= DisposeCoreAsync());

        private async Task DisposeCoreAsync()
        {
            await EndAsync(new OperationCanceledException("The Remote event source was removed."));
            try
            {
                await lifetime.CancelAsync();
            }
            finally
            {
                try
                {
                    await done;
                }
                finally
                {
                    lifetime.Dispose();
                }
            }
        }

        private sealed class EventCancellation(RemoteEventRegistration owner, PendingEvent item) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => new(
                owner.FinishAsync(
                    item,
                    null,
                    new OperationCanceledException("The Remote event Context was released.")));
        }
    }
}
