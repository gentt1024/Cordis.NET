using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Cordis.Composition;

/// <summary>One host-selected emit event and the explicit codec for each original argument.</summary>
public sealed record TypertRemoteEventDefinition(string Event, IReadOnlyList<TypertCodec> Args)
{
    /// <summary>The projected request codec for a scoped waterfall; null for an emit.</summary>
    public TypertCodec? Request
    {
        get;
        internal init;
    }

    /// <summary>The business result codec for a scoped waterfall.</summary>
    public TypertCodec? Result
    {
        get;
        internal init;
    }

    internal Func<Context, string, Action<TypertRemoteEventDispatch>, EffectHandle>? Subscribe
    {
        get;
        init;
    }
}

/// <summary>Adapters for a host's explicit selection of Cordis emit notifications.</summary>
public static class TypertRemoteEvents
{
    /// <summary>Create a source that attaches selected listeners before returning its iterable.</summary>
    /// <remarks>
    /// The event names, argument count and codecs belong to the host. Encoding failure ends the source
    /// with gateway/event-invalid and leaves the triggering emit operation's result intact. The source
    /// forwards only selected declarations; it does not select product events or serialize undeclared CLR objects.
    /// </remarks>
    public static TypertRemoteEventSource FromCordis(
        Context owner,
        IReadOnlyList<TypertRemoteEventDefinition> events)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(events);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var definitions = events
            .Select(definition =>
            {
                ArgumentNullException.ThrowIfNull(definition);
                ArgumentException.ThrowIfNullOrWhiteSpace(definition.Event);
                ArgumentNullException.ThrowIfNull(definition.Args);
                if (!names.Add(definition.Event))
                    throw new ArgumentException("The selected event list contains a duplicate name.", nameof(events));
                var codecs = definition.Args.ToArray();
                foreach (var codec in codecs)
                    ArgumentNullException.ThrowIfNull(codec);
                return definition with
                {
                    Args = Array.AsReadOnly(codecs)
                };
            })
            .ToArray();
        return cancellationToken => Open(owner, definitions, cancellationToken);
    }

    /// <summary>Bind a selected scoped declaration to the Host's subject projection.</summary>
    public static TypertRemoteEventDefinition Waterfall<TSubject, TRequest, TResult>(
        string name,
        TypertCodec request,
        TypertCodec result,
        Func<TSubject, TypertRemoteEventTarget> project)
        where TSubject : class
    {
        ArgumentNullException.ThrowIfNull(project);
        return new(name, [])
        {
            Request = request,
            Result = result,
            Subscribe = (owner, eventName, publish) => owner.On(
                eventName,
                (evt, args) => Invoke(eventName, evt, args, publish))
        };

        async Task<object?> Invoke(
            string eventName,
            EventContext evt,
            object?[] args,
            Action<TypertRemoteEventDispatch> publish)
        {
            if (args.Length != 3 || args[0] is not TSubject subject || !ReferenceEquals(evt.Receiver, subject))
                return await TypertRemoteEventsClient.AwaitAsync(evt.Next());
            var target = project(subject);
            var payload = request.Encode((TRequest)args[1]!);
            TypertEventProtocol.Request(payload);
            var completion =
                new TaskCompletionSource<TypertRemoteEventOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            publish(
                new TypertRemoteEventInvocation(
                    eventName,
                    target.Context,
                    target.AgentId,
                    payload,
                    (CancellationToken)args[2]!,
                    value => completion.TrySetResult(value),
                    error => completion.TrySetException(error)));
            return await completion.Task is TypertRemoteEventOutcome.Result answer
                ? result.Decode(answer.Value)
                : await TypertRemoteEventsClient.AwaitAsync(evt.Next());
        }
    }

    /// <summary>Await an event result through the existing Cordis waterfall dispatcher.</summary>
    public static async Task<T> AwaitResult<T>(object? value) => (T)(await TypertRemoteEventsClient.AwaitAsync(value))!;

    private static IAsyncEnumerable<TypertRemoteEventDispatch> Open(
        Context owner,
        IReadOnlyList<TypertRemoteEventDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var queue = Channel.CreateUnbounded<TypertRemoteEventDispatch>(
            new()
            {
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        var listeners = new List<EffectHandle>();
        var ended = false;
        try
        {
            foreach (var definition in definitions)
            {
                if (definition.Subscribe is { } subscribe)
                {
                    listeners.Add(
                        subscribe(
                            owner,
                            definition.Event,
                            frame =>
                            {
                                if (ended || !queue.Writer.TryWrite(frame))
                                    throw new InvalidOperationException("The Remote event source has ended.");
                            }));
                    continue;
                }

                listeners.Add(
                    owner.On(
                        definition.Event,
                        (_, arguments) =>
                        {
                            if (ended)
                                return Undefined.Value;
                            try
                            {
                                if (arguments.Length != definition.Args.Count)
                                    throw new JsonException(
                                        "The event argument count differs from the host-selected contract.");
                                var values = new JsonElement[arguments.Length];
                                for (var index = 0;index < arguments.Length;index++)
                                {
                                    if (arguments[index] is Undefined)
                                        throw new JsonException("Remote event arguments must be JSON values.");
                                    var value = definition.Args[index].Encode(arguments[index]);
                                    if (value.ValueKind == JsonValueKind.Undefined)
                                        throw new JsonException("Remote event arguments must be JSON values.");
                                    values[index] = value.Clone();
                                }

                                queue.Writer.TryWrite(
                                    new TypertRemoteEventFrame(definition.Event, Array.AsReadOnly(values)));
                            }
                            catch (Exception error)
                            {
                                ended = true;
                                queue.Writer.TryComplete(
                                    new RemoteError(
                                        "gateway/event-invalid",
                                        "The selected Cordis event '" + definition.Event + "' could not be encoded: " +
                                        error.Message,
                                        innerException: error));
                            }

                            return Undefined.Value;
                        }));
            }
        }
        catch (Exception error)
        {
            ended = true;
            queue.Writer.TryComplete(error);
        }

        return Iterate(cancellationToken);

        async IAsyncEnumerable<TypertRemoteEventDispatch> Iterate(
            [EnumeratorCancellation] CancellationToken lifetime)
        {
            try
            {
                await foreach (var frame in queue.Reader.ReadAllAsync(lifetime))
                    yield return frame;
            }
            finally
            {
                ended = true;
                queue.Writer.TryComplete();
                await owner.RunAsync(async _ =>
                {
                    foreach (var listener in listeners.AsEnumerable().Reverse())
                        await listener.DisposeAsync();
                });
            }
        }
    }
}
