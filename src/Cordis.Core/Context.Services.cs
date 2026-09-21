namespace Cordis;

public sealed partial class Context
{
    internal Context? ShadowProvider
    {
        get; set;
    }

    /// <summary>
    /// Returns a string representation of this instance.
    /// </summary>
    public override string ToString() => $"Context <{Fiber.Name}>";
    /// <summary>
    /// Determines whether is.
    /// </summary>
    public static bool Is(object? value) => value is Context;
    private string? _baseUrl;
    private bool _hasBaseUrl;
    private Func<Context, bool>? _filter;
    private bool _hasFilter;
    /// <summary>
    /// Gets the base url value.
    /// </summary>
    public string? BaseUrl
    {
        get => _hasBaseUrl ? _baseUrl : _scopeParent?.BaseUrl;
        set
        {
            _hasBaseUrl = true;
            _baseUrl = value;
        }
    }

    /// <summary>
    /// Gets the filter value.
    /// </summary>
    public Func<Context, bool>? Filter
    {
        get => _hasFilter ? _filter : _scopeParent?.Filter;
        set
        {
            _hasFilter = true;
            _filter = value;
        }
    }

    /// <summary>
    /// Gets the metadata value.
    /// </summary>
    public IDictionary<string, object?> Metadata { get; } = new Dictionary<string, object?>();
    /// <summary>
    /// Gets the registry value.
    /// </summary>
    public RegistryService Registry => new(this);
    /// <summary>
    /// Gets the reflect value.
    /// </summary>
    public ReflectService Reflect => new(this);
    /// <summary>
    /// Gets the events value.
    /// </summary>
    public EventsService Events => new(this);
    /// <summary>
    /// Gets the logger value.
    /// </summary>
    public LoggerService Logger => new(this);

    /// <summary>
    /// Creates the requested value.
    /// </summary>
    public Context Extend() => new(_runtime, this, Fiber);
    /// <summary>
    /// Determines whether isolate.
    /// </summary>
    public Context Isolate(string name, object? label = null)
    {
        var context = Extend();
        context._realms[name] = label ?? new object();
        return context;
    }

    /// <summary>
    /// Intercepts the requested value.
    /// </summary>
    public Context Intercept(string name, object? config)
    {
        var context = Extend();
        context._intercepts[name] = config;
        return context;
    }

    /// <summary>
    /// Gets the scope parent value.
    /// </summary>
    public Context? ScopeParent => _scopeParent;

    /// <summary>
    /// Performs the reparent operation.
    /// </summary>
    public void Reparent(Context parent)
    {
        VerifyAccess();
        if (!ReferenceEquals(parent.Root, Root))
            throw new ArgumentException("Contexts must share a root.", nameof(parent));
        for (var ancestor = parent; ancestor is not null; ancestor = ancestor._scopeParent)
            if (ReferenceEquals(ancestor, this))
                throw new ArgumentException("Context ancestry cannot contain a cycle.", nameof(parent));
        RebindRealms(() => _scopeParent = parent);
    }

    /// <summary>
    /// Sets isolations.
    /// </summary>
    public void SetIsolations(IReadOnlyDictionary<string, object> realms)
    {
        VerifyAccess();
        RebindRealms(() =>
        {
            _realms.Clear();
            foreach (var pair in realms)
                _realms[pair.Key] = pair.Value;
        });
    }

    private void RebindRealms(Action mutation)
    {
        var bindings = _runtime.Services.ToArray();
        mutation();
        foreach (var pair in bindings)
            if (!ReferenceEquals(pair.Key, pair.Value.Context.Realm(pair.Value.Name)))
                _runtime.Services.Remove(pair.Key);
        foreach (var pair in bindings)
        {
            var realm = pair.Value.Context.Realm(pair.Value.Name);
            if (!_runtime.Services.TryGetValue(realm, out var existing))
                _runtime.Services.Add(realm, pair.Value);
            else if (!ReferenceEquals(existing, pair.Value))
                throw new InvalidOperationException($"Service '{pair.Value.Name}' is already registered in the destination realm.");
        }

        foreach (var fiber in _runtime.Plugins.Values.SelectMany(p => p.Fibers).ToArray())
            fiber.Refresh();
    }

    /// <summary>
    /// Sets intercepts.
    /// </summary>
    public void SetIntercepts(IReadOnlyDictionary<string, object?> configs)
    {
        VerifyAccess();
        _intercepts.Clear();
        foreach (var pair in configs)
            _intercepts[pair.Key] = pair.Value;
    }

    /// <summary>
    /// Finds metadata.
    /// </summary>
    public object? FindMetadata(string name) => Metadata.TryGetValue(name, out var value) ? value : _scopeParent?.FindMetadata(name);
    internal object Realm(string name) => _realms.TryGetValue(name, out var label) ? label : _scopeParent is not null ? _scopeParent.Realm(name) : _realms[name] = new object();
    /// <summary>
    /// Intercepts s.
    /// </summary>
    public IEnumerable<object?> Intercepts(string name)
    {
        if (_scopeParent is not null)
            foreach (var item in _scopeParent.Intercepts(name))
                yield return item;
        if (_intercepts.TryGetValue(name, out var config))
            yield return config;
    }

    internal void AddIntercept(string name, object? config) => _intercepts[name] = config;
    /// <summary>
    /// Performs the plugin operation.
    /// </summary>
    public Fiber Plugin(IPlugin plugin, object? configuration = null)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(plugin);
        return _runtime.Register(this, new PluginDefinition(plugin.Identity, plugin.Name, plugin.Dependencies, plugin.ResolveConfig, plugin.ApplyAsync), configuration);
    }

    /// <summary>
    /// Injects the requested value.
    /// </summary>
    public Fiber Inject(IReadOnlyDictionary<string, object?> dependencies, Action<Context> callback) => Plugin(new Plugin<object?> { InjectConfig = dependencies, Apply = (ctx, _) => callback(ctx) });
    /// <summary>
    /// Injects the requested value.
    /// </summary>
    public Fiber Inject(IReadOnlyDictionary<string, object?> dependencies, Func<Context, Task> callback) => Plugin(new Plugin<object?> { InjectConfig = dependencies, ApplyAsync = (ctx, _) => callback(ctx) });
    /// <summary>
    /// Gets the requested value.
    /// </summary>
    public object? Get(string name, bool strict = true) => Reflect.Get(name, strict);
    /// <summary>
    /// Sets the requested value.
    /// </summary>
    public void Set(string name, object? value) => Reflect.Set(name, value);
    /// <summary>
    /// Performs the on operation.
    /// </summary>
    public EffectHandle On(string name, CordisEventHandler callback, EventOptions? options = null) => Events.On(name, callback, options);
    /// <summary>
    /// Performs the once operation.
    /// </summary>
    public EffectHandle Once(string name, CordisEventHandler callback, EventOptions? options = null) => Events.Once(name, callback, options);
    /// <summary>
    /// Emits the requested value.
    /// </summary>
    public void Emit(string name, params object?[] args) => Events.Emit(name, args);
    /// <summary>
    /// Performs the parallel async operation.
    /// </summary>
    public Task ParallelAsync(string name, params object?[] args) => Events.ParallelAsync(name, args);
    /// <summary>
    /// Performs the serial async operation.
    /// </summary>
    public Task<object?> SerialAsync(string name, params object?[] args) => Events.SerialAsync(name, args);
    /// <summary>
    /// Performs the bail operation.
    /// </summary>
    public object? Bail(string name, params object?[] args) => Events.Bail(name, args);
    /// <summary>
    /// Performs the waterfall operation.
    /// </summary>
    public object? Waterfall(string name, Func<object?> next, params object?[] args) => Events.Waterfall(name, next, args);
}

/// <summary>Registry identity is the entry callback, never the display name.</summary>
public sealed class RegistryService(Context context)
{
    /// <summary>
    /// Gets the count value.
    /// </summary>
    public int Count => context._runtime.Plugins.Count;
    /// <summary>
    /// Gets the values value.
    /// </summary>
    public IEnumerable<PluginRuntime> Values => context._runtime.Plugins.Values;

    /// <summary>
    /// Gets the requested value.
    /// </summary>
    public PluginRuntime? Get(IPlugin plugin) => context._runtime.Plugins.GetValueOrDefault(plugin.Identity);
    /// <summary>
    /// Determines whether has.
    /// </summary>
    public bool Has(IPlugin plugin) => Get(plugin) is not null;
    /// <summary>
    /// Deletes async.
    /// </summary>
    public async Task<bool> DeleteAsync(IPlugin plugin)
    {
        bool removed = false;
        await context.RunAsync(async _ =>
        {
            if (Get(plugin) is not { } runtime)
                return;
            removed = true;
            foreach (var fiber in runtime.Fibers.ToArray())
                await fiber.DisposeAsync();
        });
        return removed;
    }
}

/// <summary>
/// Represents the plugin runtime component.
/// </summary>
public sealed class PluginRuntime
{
    internal PluginRuntime(PluginDefinition definition) => Definition = definition;
    internal PluginDefinition Definition
    {
        get;
    }
    internal List<Fiber> MutableFibers { get; } = [];
    /// <summary>
    /// Gets the name value.
    /// </summary>
    public string? Name => Definition.Name;
    /// <summary>
    /// Gets the identity value.
    /// </summary>
    public object Identity => Definition.Identity;
    /// <summary>
    /// Gets the fibers value.
    /// </summary>
    public IReadOnlyList<Fiber> Fibers => MutableFibers;
}
