using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Cordis;
/// <summary>
/// A registration and its serial load/unload transitions. Pending is settled, not an error.
/// Its Context is stable across activations, as in the frozen DSH implementation.
/// </summary>
public sealed class Fiber : IAsyncDisposable
{
    private const string Inactive = "__INACTIVE__";
    private readonly Runtime _runtime;
    private readonly Context _parent;
    private readonly List<EffectHandle> _effects = [];
    private EffectHandle? _ownership;
    private long _uid;
    private int _state;
    private string _epoch;
    private Task? _inertia;
    private Exception? _error;
    private object? _rawConfig;
    private object? _config;
    internal Fiber(Runtime runtime, Context root)
    {
        _runtime = runtime;
        _parent = root;
        Context = root;
        _uid = 0;
        _state = (int)FiberState.Active;
        _epoch = "";
        Inject = new Dictionary<string, object?>();
    }

    internal Fiber(Runtime runtime, Context parent, PluginDefinition definition, IReadOnlyDictionary<string, object?> inject, long uid, object? rawConfig)
    {
        _runtime = runtime;
        _parent = parent;
        Definition = definition;
        Inject = new Dictionary<string, object?>(inject);
        _uid = uid;
        _epoch = Inactive;
        _rawConfig = rawConfig;
        Context = new Context(runtime, parent, this);
        foreach (var pair in Inject)
            if (pair.Value is not null)
                Context.AddIntercept(pair.Key, pair.Value);
    }

    internal PluginDefinition? Definition
    {
        get;
    }
    /// <summary>
    /// Gets the inject value.
    /// </summary>
    public IDictionary<string, object?> Inject
    {
        get;
    }
    internal List<EventHook> UpdateHooks { get; } = [];
    internal Dictionary<string, ServiceEntry> Store { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Gets the parent value.
    /// </summary>
    public Context Parent => _parent;
    /// <summary>
    /// Gets the error value.
    /// </summary>
    public Exception? Error => _error;
    internal CordisExecutionContext Execution => _runtime.Execution;
    /// <summary>
    /// Gets the context value.
    /// </summary>
    public Context Context
    {
        get;
    }
    /// <summary>
    /// Gets the name value.
    /// </summary>
    public string Name => Definition?.Name ?? (ReferenceEquals(_parent.Fiber, this) ? "root" : _parent.Fiber.Name);

    /// <summary>
    /// Gets the uid value.
    /// </summary>
    public long? Uid
    {
        get
        {
            long value = Volatile.Read(ref _uid);
            return value >= 0 ? value : null;
        }
    }

    /// <summary>
    /// Gets the state value.
    /// </summary>
    public FiberState State => (FiberState)Volatile.Read(ref _state);
    /// <summary>
    /// Gets the config value.
    /// </summary>
    public object? Config => Volatile.Read(ref _config);
    /// <summary>
    /// Gets the raw config value.
    /// </summary>
    public object? RawConfig => Volatile.Read(ref _rawConfig);

    internal void AttachOwnership() => _ownership = _parent.Effect(() => new AsyncCleanup(DisposeOwnedAsync), "ctx.plugin()");
    internal void AssertCanCreateEffect()
    {
        if (_uid < 0 || State == FiberState.Unloading || Context.Root.IsClosing)
            throw new CordisException("INACTIVE_EFFECT", "Cannot create effect on an inactive context.");
    }

    internal EffectHandle NewEffect(string label)
    {
        AssertCanCreateEffect();
        var handle = new EffectHandle(this, label);
        _effects.Add(handle); // Visible BEFORE setup runs; setup may reenter disposal.
        return handle;
    }

    /// <summary>Snapshot the remaining top-level effect groups in registration order.</summary>
    public IReadOnlyList<EffectMetadata> GetEffects()
    {
        Context.VerifyAccess();
        return Array.AsReadOnly(_effects.Select(effect => effect.Describe()).ToArray());
    }

    internal void RemoveEffect(EffectHandle handle) => _effects.Remove(handle);
    internal void Report(Exception error) => _runtime.Report(error);
    internal void Refresh()
    {
        var owners = new List<string>();
        foreach (string dependency in Inject.Keys)
        {
            ServiceEntry? entry = _runtime.Resolve(Context, dependency, strict: true);
            if (entry is not null && entry.Check is not null)
            {
                try
                {
                    if (!entry.Check(Context))
                        entry = null;
                }
                catch (Exception error)
                {
                    Report(error);
                    entry = null;
                }
            }

            if (entry is null)
            {
                SetEpoch(Inactive);
                return;
            }

            owners.Add(entry.Owner.Uid!.Value.ToString(CultureInfo.InvariantCulture));
        }

        SetEpoch(owners.Count == 0 ? "" : ":" + string.Join(":", owners));
    }

    private void SetEpoch(string epoch)
    {
        string previous = _epoch;
        if (epoch == previous)
            return;
        _epoch = epoch;
        if (_inertia is not null)
            return;
        if (epoch != Inactive && previous == Inactive)
        {
            _inertia = LoadAsync();
            SetState(FiberState.Loading);
        }
        else
        {
            _inertia = UnloadAsync();
            SetState(FiberState.Unloading);
        }
    }

    private void SetState(FiberState state)
    {
        FiberState previous = State;
        Volatile.Write(ref _state, (int)state);
        if (previous != state)
            Context.Events.Emit("internal/status", this, previous);
        if (previous != state && (previous == FiberState.Active || state == FiberState.Active))
            _runtime.NotifyOwner(this);
    }

    private FiberState StableState() => _uid < 0 ? FiberState.Disposed : _error is not null ? FiberState.Failed : _epoch == Inactive ? FiberState.Pending : FiberState.Active;
    private async Task LoadAsync()
    {
        string epoch = _epoch;
        foreach (var name in Inject.Keys)
            if (_runtime.Resolve(Context, name, true) is { } entry)
                Store[name] = entry;
        try
        {
            await Task.Yield();
            if (_epoch == epoch)
            {
                _config = ResolveConfig(_rawConfig);
                if (Definition is not null)
                    await Definition.Apply(Context, _config);
                _error = null;
            }
        }
        catch (Exception error)
        {
            _error = error;
            _epoch = Inactive;
            Report(error);
        }

        if (_epoch == epoch)
        {
            _inertia = null;
            SetState(StableState());
        }
        else
        {
            _inertia = UnloadAsync();
            SetState(FiberState.Unloading);
        }
    }

    private async Task UnloadAsync()
    {
        EffectHandle[] effects = _effects.AsEnumerable().Reverse().ToArray();
        _effects.Clear();
        await Task.Yield();
        // One failed sibling must not starve others. Launch each in reverse registration order,
        // but do not add a false global serial-cleanup guarantee across asynchronous groups.
        await Task.WhenAll(effects.Select(DisposeAndReportAsync));
        Store.Clear();
        if (_epoch == Inactive)
        {
            _inertia = null;
            SetState(StableState());
        }
        else
        {
            _inertia = LoadAsync();
            SetState(FiberState.Loading);
        }
    }

    private async Task DisposeAndReportAsync(EffectHandle effect)
    {
        try
        {
            await effect.JoinCoreAsync();
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    /// <summary>Wait for current lifecycle work. A missing required service may leave Pending.</summary>
    public Task WaitAsync() => Execution.RunAsync(WaitCoreAsync);
    internal async Task WaitCoreAsync()
    {
        while (_inertia is not null)
            await _inertia;
        if (_error is not null)
            ExceptionDispatchInfo.Capture(_error).Throw();
    }

    /// <summary>Reapply current raw configuration, retaining this registration and Context.</summary>
    public Task RestartAsync() => Execution.RunAsync(() =>
    {
        BeginRestart();
        return WaitCoreAsync();
    });
    private void BeginRestart()
    {
        ObjectDisposedException.ThrowIf(Context.Root.IsClosed, Context.Root);
        if (_uid < 0)
            throw new CordisException("INACTIVE_EFFECT", "The fiber was disposed.");
        SetEpoch(Inactive);
        Refresh();
    }

    /// <summary>
    /// Start an update; await WaitAsync separately. For an Active fiber, schema rejection leaves
    /// Config/activation untouched but retains the new RawConfig, matching the pinned baseline.
    /// Update and config hooks preserve their waterfall veto/continuation contract.
    /// </summary>
    public void Update(object? configuration, bool noSave = false)
    {
        Context.VerifyAccess();
        if (_uid < 0)
            throw new CordisException("INACTIVE_EFFECT", "The fiber was disposed.");
        _rawConfig = configuration;
        if (State != FiberState.Active)
        {
            _error = null;
            BeginRestart();
            return;
        }

        var resolved = ResolveConfig(configuration);
        Context.Events.WaterfallWith(this, "internal/update", () =>
        {
            _config = resolved;
            _error = null;
            BeginRestart();
            return Undefined.Value;
        }, resolved, noSave);
    }

    private object? ResolveConfig(object? raw)
    {
        var resolved = Context.Events.WaterfallWith(this, "internal/config", () => raw, raw);
        return Definition is null ? resolved : Definition.ResolveConfig(resolved);
    }

    /// <summary>
    /// Dispose a non-root fiber. The root fiber follows upstream's restart/cleanup semantics;
    /// Context.DisposeAsync additionally ends the .NET execution lifetime.
    /// </summary>
    public ValueTask DisposeAsync() => new(Execution.RunAsync(DisposeCoreAsync));
    internal Task DisposeCoreAsync()
    {
        if (_ownership is not null)
            return _ownership.DisposeCoreAsync();
        BeginRestart();
        return WaitCoreAsync();
    }

    private async Task DisposeOwnedAsync()
    {
        _uid = -1;
        Context.Events.EmitContained("internal/plugin", this);
        _runtime.Remove(this);
        SetEpoch(Inactive);
        if (_inertia is null)
        {
            _inertia = UnloadAsync();
            SetState(FiberState.Unloading);
        }

        while (_inertia is not null)
            await _inertia;
    }
}
