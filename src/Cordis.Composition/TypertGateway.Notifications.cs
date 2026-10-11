using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Cordis.Composition;

public sealed partial class TypertGateway
{
    private RemoteEventRegistration? remoteEvents;

    /// <summary>Register the sole application-selected notification source under its owner's Fiber.</summary>
    /// <remarks>
    /// Use the Gateway's Cordis execution domain. Withdrawal immediately removes the registration and
    /// closes all observers, then cancels and joins the source. Source cleanup is cooperative; an
    /// iterator that ignores cancellation can leave owner cleanup pending. No event replay is stored.
    /// Unexpected enumeration failures also fail owner cleanup: an async iterator's finally can run
    /// inside MoveNextAsync, so a read failure cannot prove that its cleanup succeeded. This can block
    /// replacement after an ordinary read failure even when the subsequent DisposeAsync succeeds.
    /// </remarks>
    public EffectHandle RegisterRemoteEvents(Context owner, TypertRemoteEventSource source)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(source);
        EnsureNotificationContext(owner, nameof(owner));
        _ = caller.Get<object>("typert", strict: false);
        if (remoteEvents is not null)
            throw new InvalidOperationException("The Gateway already has a selected Remote event source.");
        return owner.Effect(
            () =>
            {
                var registration = new RemoteEventRegistration(this, source);
                remoteEvents = registration;
                registration.Start();
                return (IAsyncDisposable)registration;
            },
            "typert Gateway Remote event source");
    }

    internal void EnsureNotificationContext(Context context, string parameterName)
    {
        if (!ReferenceEquals(context.Root, caller.Root))
            throw new ArgumentException(
                "Notification services and sources must share the Gateway's Cordis root.",
                parameterName);
    }

    internal async IAsyncEnumerable<TypertRemoteNotification> WatchRemoteEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        RemoteEventRegistration registration = null!;
        Channel<TypertRemoteNotification> client = null!;
        await caller.RunAsync(_ =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            registration = remoteEvents ?? throw NotificationFault(
                "gateway/service-unavailable",
                "The selected Remote event source is unavailable.");
            client = registration.Subscribe();
            return Task.CompletedTask;
        });
        try
        {
            registration.Check();
            yield return new("ready");
            await foreach (var frame in client.Reader.ReadAllAsync(cancellationToken))
            {
                registration.Check();
                yield return frame;
            }
        }
        finally
        {
            await caller.RunAsync(_ =>
            {
                registration.Unsubscribe(client);
                return Task.CompletedTask;
            });
        }
    }

    private static RemoteError NotificationFault(string code, string message, Exception? cause = null) =>
        Fault(code, "remoteNotifications/watch", message, cause);

    private sealed class RemoteEventRegistration : IAsyncDisposable
    {
        private readonly TypertGateway gateway;
        private readonly CancellationTokenSource lifetime = new();
        private readonly IAsyncEnumerable<TypertRemoteEventFrame> source;
        private readonly HashSet<Channel<TypertRemoteNotification>> clients = [];
        private Task done = Task.CompletedTask;
        private Exception? ended;
        private bool disposed;

        public RemoteEventRegistration(TypertGateway gateway, TypertRemoteEventSource factory)
        {
            this.gateway = gateway;
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

        public void Start() => done = ConsumeAsync();

        public Channel<TypertRemoteNotification> Subscribe()
        {
            Check();
            var client = Channel.CreateUnbounded<TypertRemoteNotification>(
                new()
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });
            clients.Add(client);
            return client;
        }

        public void Unsubscribe(Channel<TypertRemoteNotification> client)
        {
            clients.Remove(client);
            client.Writer.TryComplete();
        }

        public void Check()
        {
            if (ended is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ended).Throw();
        }

        private async Task ConsumeAsync()
        {
            Exception? failure = null;
            Exception? ownerFailure = null;
            IAsyncEnumerator<TypertRemoteEventFrame>? iterator = null;
            try
            {
                iterator = source.GetAsyncEnumerator(lifetime.Token);
                while (await iterator.MoveNextAsync())
                {
                    if (lifetime.IsCancellationRequested)
                        return;
                    var frame = iterator.Current;
                    ArgumentNullException.ThrowIfNull(frame);
                    ArgumentException.ThrowIfNullOrWhiteSpace(frame.Event);
                    ArgumentNullException.ThrowIfNull(frame.Args);
                    var arguments = frame
                        .Args.Select(argument =>
                        {
                            if (argument.ValueKind == System.Text.Json.JsonValueKind.Undefined)
                                throw new System.Text.Json.JsonException("Remote event arguments must be JSON values.");
                            return argument.Clone();
                        })
                        .ToArray();
                    var notification = new TypertRemoteNotification("event", frame.Event, Array.AsReadOnly(arguments));
                    foreach (var client in clients)
                        client.Writer.TryWrite(notification);
                }

                if (!lifetime.IsCancellationRequested)
                    failure = NotificationFault(
                        "gateway/internal",
                        "The selected Remote event source ended unexpectedly.");
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                failure = error is RemoteError
                    ? error
                    : NotificationFault(
                        "gateway/internal",
                        "The selected Remote event source failed: " + error.Message,
                        error);
                // An async iterator's finally can fail inside MoveNextAsync before cancellation.
                // Preserve every unexpected source error for its owner; read and cleanup failures
                // are indistinguishable here, even if the later DisposeAsync succeeds.
                ownerFailure = error;
            }
            finally
            {
                if (ReferenceEquals(gateway.remoteEvents, this))
                    gateway.remoteEvents = null;
                End(
                    failure ?? NotificationFault(
                        "gateway/service-unavailable",
                        "The selected Remote event source was removed."));
                if (iterator is not null)
                {
                    try
                    {
                        await iterator.DisposeAsync();
                    }
                    catch (Exception cleanupFailure) when (ownerFailure is not null)
                    {
                        throw new AggregateException(ownerFailure, cleanupFailure);
                    }
                }
            }

            if (ownerFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ownerFailure).Throw();
        }

        private void End(Exception reason)
        {
            ended ??= reason;
            foreach (var client in clients)
                client.Writer.TryComplete(ended);
            clients.Clear();
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
                return;
            disposed = true;
            if (ReferenceEquals(gateway.remoteEvents, this))
                gateway.remoteEvents = null;
            End(NotificationFault("gateway/service-unavailable", "The selected Remote event source was removed."));
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
    }
}
