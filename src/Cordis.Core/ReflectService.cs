namespace Cordis;

/// <summary>
/// Represents the accessor component.
/// </summary>
/// <param name="Get">The get value.</param>
/// <param name="Set">The set value.</param>
public sealed record Accessor(Func<Context, object?, object?> Get, Func<Context, object?, object?, bool>? Set = null);
/// <summary>Named services, realm identity, explicit accessors and AOT-safe service views.</summary>
public sealed class ReflectService(Context context)
{
    /// <summary>
    /// Determines whether is available.
    /// </summary>
    public bool IsAvailable(string name)
    {
        var impl = context._runtime.Resolve(context, name, true);
        if (impl is null)
            return false;
        try
        {
            return impl.Check?.Invoke(context) ?? true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines whether has.
    /// </summary>
    public bool Has(string name) => context._runtime.ServiceNames.Contains(name) || context._runtime.Accessors.ContainsKey(name);
    /// <summary>
    /// Gets the requested value.
    /// </summary>
    public object? Get(string name, bool strict = true)
    {
        context.VerifyAccess();
        return Trace(context._runtime.Resolve(context, name, strict)?.Value);
    }

    /// <summary>
    /// Gets the requested type.
    /// </summary>
    public T? Get<T>(string name, bool strict = true) => (T?)Get(name, strict);
    /// <summary>
    /// Cast property-style access to the contract type after the existing injection, interception
    /// and caller tracing rules. The type does not participate in service identity or add Inject.
    /// A missing/null reference stays null; incompatible values throw InvalidCastException
    /// (null to a non-nullable value type throws). Reacquire views after each activation.
    /// </summary>
    public T Read<T>(string name, object? receiver = null) => (T)Read(name, receiver)!;
    /// <summary>Property-style access checks declared injection and inherits the provider snapshot.</summary>
    public object? Read(string name, object? receiver = null)
    {
        context.VerifyAccess();
        if (context._runtime.Accessors.TryGetValue(name, out var accessor))
            return accessor.Get(context, receiver);
        if (context.Fiber.Definition is null)
            return Get(name, false);
        return context.Events.Waterfall("internal/get", () =>
        {
            var fiber = (context.ShadowProvider ?? context).Fiber;
            while (true)
            {
                if (fiber.Store.TryGetValue(name, out var entry))
                    return Trace(entry.Value);
                if (fiber.Inject.ContainsKey(name))
                    throw new InvalidOperationException($"Cannot get required service '{name}' in inactive context.");
                if (fiber.Definition is null || !ReferenceEquals(fiber.Parent.Realm(name), context.Realm(name)))
                    throw new InvalidOperationException($"Cannot get property '{name}' without inject.");
                fiber = fiber.Parent.Fiber;
            }
        }, context, name);
    }

    /// <summary>
    /// Sets the requested value.
    /// </summary>
    public void Set(string name, object? value)
    {
        context.VerifyAccess();
        SetCore(name, value);
    }

    /// <summary>
    /// Performs the write operation.
    /// </summary>
    public void Write(string name, object? value, object? receiver = null)
    {
        context.VerifyAccess();
        if (context._runtime.Accessors.TryGetValue(name, out var accessor))
        {
            if (accessor.Set?.Invoke(context, value, receiver) != true)
                throw new InvalidOperationException($"Property '{name}' is read-only.");
            return;
        }

        var error = new InvalidOperationException($"Cannot set '{name}' without provide.");
        context.Events.Waterfall("internal/set", () =>
        {
            SetCore(name, value, error);
            return true;
        }, context, name, value, error);
    }

    private void SetCore(string name, object? value, InvalidOperationException? missing = null)
    {
        var entry = context._runtime.Resolve(context, name, false) ?? throw missing ?? new InvalidOperationException($"Cannot set '{name}' without provide.");
        if (!ReferenceEquals(entry.Owner, context.Fiber))
            throw new InvalidOperationException($"Cannot set '{name}' in multiple fibers.");
        entry.Value = value;
    }

    /// <summary>
    /// Provides the requested value.
    /// </summary>
    public EffectHandle Provide(string name, object? value, Func<bool>? check = null) => ProvideCore(name, value, check is null ? null : _ => check());
    /// <summary>
    /// Provides the requested value.
    /// </summary>
    public EffectHandle Provide(string name, object? value, Func<Context, bool> check) => ProvideCore(name, value, check);
    private EffectHandle ProvideCore(string name, object? value, Func<Context, bool>? check)
    {
        context.VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return context.Effect(() =>
        {
            if (context._runtime.Accessors.ContainsKey(name))
                throw new InvalidOperationException($"Property '{name}' is already an accessor.");
            context._runtime.ServiceNames.Add(name);
            var realm = context.Realm(name);
            if (context._runtime.Services.ContainsKey(realm))
                throw new InvalidOperationException($"Service '{name}' is already registered.");
            var entry = new ServiceEntry(name, context, value, check);
            context._runtime.Services.Add(realm, entry);
            context.Fiber.Store[name] = entry;
            context._runtime.Notify(context, name);
            return new AsyncCleanup(async () =>
            {
                context._runtime.Services.Remove(context.Realm(name));
                var consumers = context._runtime.Notify(context, name);
                await Task.WhenAll(consumers.Where(f => !ReferenceEquals(f, context.Fiber)).Select(async fiber =>
                {
                    try
                    {
                        await fiber.WaitCoreAsync();
                    }
                    catch (Exception)
                    {
                    }
                }));
                context.Fiber.Store.Remove(name);
            });
        }, $"ctx.provide({Logger.FormatData(name)})");
    }

    /// <summary>
    /// Performs the notify operation.
    /// </summary>
    public IReadOnlyList<Fiber> Notify(params string[] names)
    {
        context.VerifyAccess();
        return names.SelectMany(name => context._runtime.Notify(context, name)).Distinct().ToArray();
    }

    /// <summary>
    /// Performs the accessor operation.
    /// </summary>
    public EffectHandle Accessor(string name, Accessor accessor) => context.Effect(() =>
    {
        if (Has(name))
            throw new InvalidOperationException($"Property '{name}' is already declared.");
        context._runtime.Accessors.Add(name, accessor);
        return (Action)(() => context._runtime.Accessors.Remove(name));
    }, $"ctx.accessor({Logger.FormatData(name)})");
    /// <summary>
    /// Performs the mixin operation.
    /// </summary>
    public EffectHandle Mixin(IReadOnlyDictionary<string, Accessor> members) => context.Effect(() => members.Select(pair => (IAsyncDisposable)Accessor(pair.Key, pair.Value)).ToArray(), "ctx.mixin()");
    /// <summary>
    /// Performs the trace operation.
    /// </summary>
    public object? Trace(object? value) => value is IContextualService service ? service.ForContext(context) : value;
    /// <summary>
    /// Performs the bind operation.
    /// </summary>
    public Func<object?[], object?> Bind(Func<object?[], object?> callback) => args => callback(args.Select(Trace).ToArray());
}

/// <summary>Explicit replacement for JavaScript proxies, usable without generated code or reflection.</summary>
public interface IContextualService
{
    /// <summary>
    /// Performs the for context operation.
    /// </summary>
    object ForContext(Context caller);
}

/// <summary>Service ownership belongs to its provider; each view uses its calling context for effects.</summary>
public abstract class Service : IContextualService
{
    private readonly Context _caller;
    /// <summary>
    /// Initializes a new instance of the <see cref="Service"/> type.
    /// </summary>
    protected Service(Context context, string name)
    {
        Context = context;
        _caller = context;
        Provider = context;
        Name = name;
        context.Provide(name, this, caller => ((Service)ForContext(caller)).Check());
    }

    /// <summary>
    /// Gets the context value.
    /// </summary>
    protected Context Context
    {
        get; private set;
    }
    /// <summary>
    /// Gets the provider value.
    /// </summary>
    public Context Provider
    {
        get;
    }
    /// <summary>
    /// Gets the name value.
    /// </summary>
    public string Name
    {
        get;
    }

    /// <summary>
    /// Performs the check operation.
    /// </summary>
    protected virtual bool Check() => true;
    /// <summary>
    /// Initializes a new instance of the <see cref="Service"/> type.
    /// </summary>
    protected Service(Service provider, Context caller)
    {
        Provider = provider.Provider;
        _caller = caller;
        Name = provider.Name;
        Context = caller.Extend();
        Context.ShadowProvider = Provider;
    }

    /// <summary>Create an explicit typed view sharing the original service's mutable state.</summary>
    protected abstract Service CreateView(Context caller);
    /// <summary>
    /// Performs the for context operation.
    /// </summary>
    public object ForContext(Context caller) => CreateView(caller);
    /// <summary>
    /// Performs the filter operation.
    /// </summary>
    public bool Filter(Context target) => ReferenceEquals(target.Realm(Name), Context.Realm(Name));
    /// <summary>
    /// Resolves config.
    /// </summary>
    public IReadOnlyDictionary<string, object?> ResolveConfig(IReadOnlyDictionary<string, object?>? @base = null, IReadOnlyDictionary<string, object?>? head = null)
    {
        var result = new Dictionary<string, object?>();
        void Merge(object? source)
        {
            if (source is IEnumerable<KeyValuePair<string, object?>> pairs)
                foreach (var pair in pairs)
                    result[pair.Key] = pair.Value;
        }

        Merge(@base);
        foreach (var item in Context.Intercepts(Name))
            Merge(item);
        Merge(head);
        return result;
    }

    /// <summary>
    /// Performs the associate operation.
    /// </summary>
    protected object? Associate(string member) => _caller.Reflect.Read($"{Name}.{member}", this);
    /// <summary>
    /// Performs the associate operation.
    /// </summary>
    protected void Associate(string member, object? value) => _caller.Reflect.Write($"{Name}.{member}", value, this);
    /// <summary>
    /// Creates the requested type.
    /// </summary>
    protected T Extend<T>(Action<T> configure)
        where T : Service
    {
        var extended = (T)MemberwiseClone();
        configure(extended);
        return extended;
    }

    /// <summary>
    /// Provides r service.
    /// </summary>
    protected object? ProviderService(string name) => Provider.Reflect.Read(name);
}

/// <summary>Service base for mutable data shared by all caller views. A view never copies State.</summary>
public abstract class Service<TState> : Service where TState : class
{
    /// <summary>
    /// Performs the service operation.
    /// </summary>
    protected Service(Context context, string name, TState state) : base(context, name) => State = state;
    /// <summary>
    /// Performs the service operation.
    /// </summary>
    protected Service(Service<TState> provider, Context caller) : base(provider, caller) => State = provider.State;
    /// <summary>
    /// Gets the state value.
    /// </summary>
    protected TState State
    {
        get;
    }
}

/// <summary>
/// Represents the tracked callback component.
/// </summary>
/// <param name="receiver">The receiver value.</param>
/// <param name="arguments">The arguments value.</param>
public delegate object? TrackedCallback(object receiver, object?[] arguments);
/// <summary>
/// Explicit AOT-safe associated object. Dynamic members are accessed through Get/Set/Call;
/// state and receiver identity survive context tracing without generating a CLR proxy.
/// </summary>
public class TrackedObject : IContextualService
{
    private readonly Dictionary<string, object?> _members = new(StringComparer.Ordinal);
    /// <summary>
    /// Gets the context value.
    /// </summary>
    protected Context Context
    {
        get; private set;
    }
    /// <summary>
    /// Gets the association value.
    /// </summary>
    public string Association
    {
        get;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TrackedObject"/> type.
    /// </summary>
    public TrackedObject(Context context, string association) => (Context, Association) = (context, association);
    /// <summary>
    /// Performs the for context operation.
    /// </summary>
    public virtual object ForContext(Context caller)
    {
        var view = (TrackedObject)MemberwiseClone();
        view.Context = caller;
        return view;
    }

    /// <summary>
    /// Gets the requested value.
    /// </summary>
    public object? Get(string name) => Context.Reflect.Has($"{Association}.{name}") ? Context.Reflect.Read($"{Association}.{name}", this) : Context.Reflect.Trace(_members.GetValueOrDefault(name));
    /// <summary>
    /// Sets the requested value.
    /// </summary>
    public void Set(string name, object? value)
    {
        if (Context.Reflect.Has($"{Association}.{name}"))
            Context.Reflect.Write($"{Association}.{name}", value, this);
        else
            _members[name] = value;
    }

    /// <summary>
    /// Performs the call operation.
    /// </summary>
    public object? Call(string name, params object?[] arguments) => Context.Reflect.Trace(Get(name) switch
    {
        TrackedCallback callback => callback(this, arguments),
        Func<object?[], object?> callback => callback(arguments),
        _ => throw new InvalidOperationException($"Member '{Association}.{name}' is not callable."),
    });
}
