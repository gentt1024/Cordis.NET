namespace Cordis;

/// <summary>
/// Associates an event name with exactly one payload argument. Identity remains the ordinal
/// string name, shared with raw listeners; this key neither declares nor restricts dispatch mode.
/// Use the raw API for existing multiple-argument contracts; no tuple/DTO repacking is implicit.
/// </summary>
public sealed class EventKey<T>
{
    /// <summary>Create a single-payload contract for an existing or new event name.</summary>
    public EventKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The existing event table key; two keys with this name share listeners.</summary>
    public string Name { get; }

    internal T Payload(object?[] arguments)
    {
        if (arguments.Length != 1)
            throw new ArgumentException($"Event '{Name}' expects exactly one payload argument; received {arguments.Length}.");
        if (arguments[0] is T value) return value;
        if (arguments[0] is null && default(T) is null) return default!;
        throw new ArgumentException($"Event '{Name}' payload is incompatible with {typeof(T).FullName}.");
    }
}

public sealed partial class Context
{
    /// <summary>Own a typed listener using the existing event table, filtering and caller tracing.
    /// Result values, including tasks and undefined/null/false, keep the raw dispatcher semantics.</summary>
    public EffectHandle On<T>(EventKey<T> key, Func<EventContext, T, object?> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(listener);
        return On(key.Name, (evt, args) => listener(evt, key.Payload(args)), options);
    }

    /// <summary>Own an observer returning Undefined.Value. It does not call Waterfall next.</summary>
    public EffectHandle On<T>(EventKey<T> key, Action<EventContext, T> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return On(key, (evt, value) => { listener(evt, value); return Undefined.Value; }, options);
    }

    /// <summary>Own an asynchronous observer. Parallel/Serial await it; Emit/Bail/Waterfall
    /// retain their original task handling. Use awaited dispatch to observe async failures.</summary>
    public EffectHandle On<T>(EventKey<T> key, Func<EventContext, T, Task?> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return On(key, (evt, value) => (object?)listener(evt, value), options);
    }

    /// <summary>Own an asynchronous result listener without replacing null/false/undefined values.</summary>
    public EffectHandle On<T>(EventKey<T> key, Func<EventContext, T, Task<object?>?> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return On(key, (evt, value) => (object?)listener(evt, value), options);
    }

    /// <summary>Own a typed listener removed before its first invocation, using the raw Once path.</summary>
    public EffectHandle Once<T>(EventKey<T> key, Func<EventContext, T, object?> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(listener);
        return Once(key.Name, (evt, args) => listener(evt, key.Payload(args)), options);
    }

    /// <summary>Own a one-shot observer returning Undefined.Value without implicit Waterfall next.</summary>
    public EffectHandle Once<T>(EventKey<T> key, Action<EventContext, T> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Once(key, (evt, value) => { listener(evt, value); return Undefined.Value; }, options);
    }

    /// <summary>Own a one-shot async observer; awaiting is determined by the existing dispatch mode.</summary>
    public EffectHandle Once<T>(EventKey<T> key, Func<EventContext, T, Task?> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Once(key, (evt, value) => (object?)listener(evt, value), options);
    }

    /// <summary>Own a one-shot async result listener, preserving raw result semantics.</summary>
    public EffectHandle Once<T>(EventKey<T> key, Func<EventContext, T, Task<object?>?> listener, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return Once(key, (evt, value) => (object?)listener(evt, value), options);
    }

    /// <summary>Emit one payload synchronously. Receiver is separate and retains filtering/tracing.</summary>
    public void Emit<T>(EventKey<T> key, T payload, object? receiver = null)
        => Events.EmitWith(receiver, EventName(key), [payload]);

    /// <summary>Start listeners in dispatch order, await all, and aggregate failures as on the raw path.</summary>
    public Task ParallelAsync<T>(EventKey<T> key, T payload, object? receiver = null)
        => Events.ParallelWithAsync(receiver, EventName(key), [payload]);

    /// <summary>Await listeners serially until a raw bailing result, retaining special-value distinctions.</summary>
    public Task<object?> SerialAsync<T>(EventKey<T> key, T payload, object? receiver = null)
        => Events.SerialWithAsync(receiver, EventName(key), [payload]);

    /// <summary>Return the first raw bailing result synchronously; async listeners are not awaited.</summary>
    public object? Bail<T>(EventKey<T> key, T payload, object? receiver = null)
        => Events.BailWith(receiver, EventName(key), [payload]);

    /// <summary>Run explicit next/veto continuation semantics. An observer does not automatically continue.</summary>
    public object? Waterfall<T>(EventKey<T> key, Func<object?> next, T payload, object? receiver = null)
        => Events.WaterfallWith(receiver, EventName(key), next, [payload]);

    private static string EventName<T>(EventKey<T> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.Name;
    }
}
