namespace Cordis;
/// <summary>JavaScript undefined, distinct from an explicitly returned null.</summary>
public sealed class Undefined
{
    private Undefined()
    {
    }

    /// <summary>
    /// Gets the value value.
    /// </summary>
    public static Undefined Value { get; } = new();

    /// <summary>
    /// Returns a string representation of this instance.
    /// </summary>
    public override string ToString() => "undefined";
}

/// <summary>
/// Represents the cordis event handler component.
/// </summary>
/// <param name="context">The context value.</param>
/// <param name="arguments">The arguments value.</param>
public delegate object? CordisEventHandler(EventContext context, object?[] arguments);
/// <summary>
/// Represents the event options component.
/// </summary>
/// <param name="Prepend">The prepend value.</param>
/// <param name="Global">The global value.</param>
public sealed record EventOptions(bool Prepend = false, bool Global = false);
/// <summary>
/// Represents the event context component.
/// </summary>
/// <param name="receiver">The receiver value.</param>
/// <param name="next">The next value.</param>
public sealed class EventContext(object? receiver, Func<object?>? next = null)
{
    /// <summary>
    /// Gets the receiver value.
    /// </summary>
    public object? Receiver { get; } = receiver;

    /// <summary>
    /// Performs the next operation.
    /// </summary>
    public object? Next() => next is null ? Undefined.Value : next();
}

internal sealed record EventHook(Context Owner, CordisEventHandler Handler, EventOptions Options);
/// <summary>
/// Represents the events service service.
/// </summary>
/// <param name="context">The context value.</param>
public sealed class EventsService(Context context)
{
    /// <summary>
    /// Gets the listener counts value.
    /// </summary>
    public IReadOnlyDictionary<string, int> ListenerCounts => context._runtime.Hooks.Where(pair => pair.Value.Count != 0).ToDictionary(pair => pair.Key, pair => pair.Value.Count);

    /// <summary>
    /// Determines whether is bailed.
    /// </summary>
    public static bool IsBailed(object? value) => value is not null && value is not Undefined && value is not false;
    /// <summary>
    /// Performs the on operation.
    /// </summary>
    public EffectHandle On(string name, CordisEventHandler listener, EventOptions? options = null)
    {
        context.VerifyAccess();
        options ??= new();
        context.Fiber.AssertCanCreateEffect();
        var intercepted = BailWith(context, "internal/listener", name, listener, options);
        if (intercepted is EffectHandle replacement)
            return replacement;
        bool localUpdate = name == "internal/update" && !options.Global;
        var effect = context.Effect(() =>
        {
            var hook = new EventHook(context, listener, options);
            var hooks = localUpdate ? context.Fiber.UpdateHooks : context._runtime.Hooks.GetValueOrDefault(name);
            if (hooks is null)
                context._runtime.Hooks[name] = hooks = [];
            if (options.Prepend)
                hooks.Insert(0, hook);
            else
                hooks.Add(hook);
            return (Action)(() => hooks.Remove(hook));
        }, $"ctx.on({Logger.FormatData(name)})");
        if (localUpdate)
            context.Fiber.RemoveEffect(effect);
        return effect;
    }

    /// <summary>
    /// Performs the once operation.
    /// </summary>
    public EffectHandle Once(string name, CordisEventHandler listener, EventOptions? options = null)
    {
        EffectHandle? handle = null;
        handle = On(name, (evt, args) =>
        {
            _ = handle!.DisposeAsync();
            return listener(evt, args);
        }, options);
        return handle;
    }

    private EventHook[] Dispatch(string mode, object? receiver, string name, object?[] args)
    {
        context.VerifyAccess();
        if (!name.StartsWith("internal/", StringComparison.Ordinal))
            Emit("internal/dispatch", mode, name, args, receiver);
        IEnumerable<EventHook> candidates = context._runtime.Hooks.GetValueOrDefault(name) ?? [];
        if (name == "internal/update" && receiver is Fiber updated)
        {
            var globals = candidates.ToArray();
            candidates = globals.Where(h => h.Options.Prepend).Concat(updated.UpdateHooks).Concat(globals.Where(h => !h.Options.Prepend));
        }

        return candidates.Where(hook =>
        {
            if (name == "internal/update" && !hook.Options.Global && receiver is Fiber fiber && !ReferenceEquals(hook.Owner.Fiber, fiber))
                return false;
            if (hook.Options.Global)
                return true;
            return receiver switch
            {
                Context ctx => ctx.Filter?.Invoke(hook.Owner) ?? true,
                Service service => service.Filter(hook.Owner),
                _ => true
            };
        }).ToArray();
    }

    /// <summary>
    /// Emits the requested value.
    /// </summary>
    public void Emit(string name, params object?[] args) => EmitWith(null, name, args);
    /// <summary>
    /// Emits with.
    /// </summary>
    public void EmitWith(object? receiver, string name, params object?[] args)
    {
        foreach (var hook in Dispatch("emit", receiver, name, args))
            Invoke(hook, receiver, args);
    }

    internal void EmitContained(string name, params object?[] args)
    {
        foreach (var hook in Dispatch("emit", null, name, args))
        {
            try
            {
                var value = Invoke(hook, null, args);
                if (value is Task task)
                    _ = ObserveAsync(task);
            }
            catch (Exception error)
            {
                context._runtime.Report(error);
            }
        }
    }

    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception error)
        {
            context._runtime.Report(error);
        }
    }

    /// <summary>
    /// Performs the parallel async operation.
    /// </summary>
    public Task ParallelAsync(string name, params object?[] args) => ParallelWithAsync(null, name, args);
    /// <summary>
    /// Performs the parallel with async operation.
    /// </summary>
    public async Task ParallelWithAsync(object? receiver, string name, params object?[] args)
    {
        var tasks = Dispatch("emit", receiver, name, args).Select(async hook =>
        {
            try
            {
                await AwaitResult(Invoke(hook, receiver, args));
                return (Exception?)null;
            }
            catch (Exception e)
            {
                return e;
            }
        }).ToArray();
        var errors = (await Task.WhenAll(tasks)).OfType<Exception>().ToArray();
        if (errors.Length != 0)
            throw new AggregateException(errors);
    }

    /// <summary>
    /// Performs the serial async operation.
    /// </summary>
    public Task<object?> SerialAsync(string name, params object?[] args) => SerialWithAsync(null, name, args);
    /// <summary>
    /// Performs the serial with async operation.
    /// </summary>
    public async Task<object?> SerialWithAsync(object? receiver, string name, params object?[] args)
    {
        foreach (var hook in Dispatch("serial", receiver, name, args))
        {
            var result = await AwaitResult(Invoke(hook, receiver, args));
            if (IsBailed(result))
                return result;
        }

        return Undefined.Value;
    }

    /// <summary>
    /// Performs the bail operation.
    /// </summary>
    public object? Bail(string name, params object?[] args) => BailWith(null, name, args);
    /// <summary>
    /// Performs the bail with operation.
    /// </summary>
    public object? BailWith(object? receiver, string name, params object?[] args)
    {
        foreach (var hook in Dispatch("bail", receiver, name, args))
        {
            var result = Invoke(hook, receiver, args);
            if (IsBailed(result))
                return result;
        }

        return Undefined.Value;
    }

    /// <summary>
    /// Performs the waterfall operation.
    /// </summary>
    public object? Waterfall(string name, Func<object?> next, params object?[] args) => WaterfallWith(null, name, next, args);
    /// <summary>
    /// Performs the waterfall with operation.
    /// </summary>
    public object? WaterfallWith(object? receiver, string name, Func<object?> next, params object?[] args)
    {
        var hooks = Dispatch("waterfall", receiver, name, args);
        int index = 0;
        object? Invoke() => index == hooks.Length ? next() : InvokeHook(hooks[index++], receiver, args, Invoke);
        return Invoke();
    }

    private static object? Invoke(EventHook hook, object? receiver, object?[] args) => InvokeHook(hook, receiver, args, null);
    private static object? InvokeHook(EventHook hook, object? receiver, object?[] args, Func<object?>? next)
    {
        var reflect = hook.Owner.Reflect;
        return hook.Handler(new(reflect.Trace(receiver), next), args.Select(reflect.Trace).ToArray());
    }

    private static async Task<object?> AwaitResult(object? value)
    {
        if (value is Task<object?> result)
            return await result;
        if (value is ValueTask<object?> valueResult)
            return await valueResult;
        if (value is Task task)
        {
            await task;
            return Undefined.Value;
        }

        if (value is ValueTask valueTask)
        {
            await valueTask;
            return Undefined.Value;
        }

        return value;
    }
}
