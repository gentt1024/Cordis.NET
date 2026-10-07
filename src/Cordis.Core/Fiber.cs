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
    // DisposeCoreAsync releases this handle through its internal disposal entry point.
#pragma warning disable CA2213
    private EffectHandle? _ownership;
#pragma warning restore CA2213
    private long _uid;
    private int _state;
    private string _epoch;
    private Task? _inertia;
    private Exception? _error;
    private string? _failurePhase;
    private object? _rawConfig;
    private object? _config;
    private ConfigurationCell? _configurationCell;
    private readonly Dictionary<string, object> _configurationReferences = new(StringComparer.Ordinal);

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

    internal Fiber(
        Runtime runtime,
        Context parent,
        PluginDefinition definition,
        IReadOnlyDictionary<string, object?> inject,
        long uid,
        object? rawConfig)
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

    internal List<EventHook> UpdateHooks
    {
        get;
    } = [];

    internal Dictionary<string, ServiceEntry> Store
    {
        get;
    } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the parent value.
    /// </summary>
    public Context Parent => _parent;

    /// <summary>
    /// Gets the error value.
    /// </summary>
    public Exception? Error => _error;

    /// <summary>The activation operation that produced Error: "configuration" or "apply", or null when no activation error is retained.</summary>
    /// <remarks>This value identifies where the error occurred, regardless of its exception type. A successful activation or an update that clears Error also clears this value.</remarks>
    public string? FailurePhase => Volatile.Read(ref _failurePhase);

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

    /// <summary>The optional captured description of the Fiber's captured validator.</summary>
    public ConfigDescriptor? ConfigDescription => Definition?.Configuration?.Descriptor;

    /// <summary>Whether this activation has captured live configuration projections.</summary>
    public bool HasConfigReferences => Definition?.Configuration?.Bindings.Count > 0;

    /// <summary>Read one immutable published reference snapshot without retaining the effective configuration.</summary>
    public IReadOnlyDictionary<string, object?> ConfigurationValues =>
        _configurationCell?.State.Values ?? System.Collections.ObjectModel.ReadOnlyDictionary<string, object?>.Empty;

    /// <summary>Run the active plugin's normal configuration hooks and validator without publishing or saving.</summary>
    /// <remarks>Call within the owning execution domain. Validation callbacks may have their own effects.</remarks>
    public object? ValidateConfiguration(object? raw)
    {
        Context.VerifyAccess();
        if (State != FiberState.Active)
            throw new InvalidOperationException("Configuration validation requires an active plugin.");
        return ResolveConfig(raw);
    }

    /// <summary>Obtain an identity-stable readonly reference to an explicitly projected volatile field.</summary>
    public ConfigReference<T> GetConfigReference<T>(string path)
    {
        Context.VerifyAccess();
        ArgumentNullException.ThrowIfNull(path);
        var binding = Definition?.Configuration?.Bindings.SingleOrDefault(binding => binding.Path == path) ??
            throw new KeyNotFoundException($"No volatile configuration field '{path}' is declared.");
        if (binding.ValueType != typeof(T))
            throw new InvalidCastException($"The configuration reference '{path}' has another value type.");
        if (_configurationCell is null || !_configurationCell.State.Values.ContainsKey(path))
            throw new InvalidOperationException(
                $"The configuration field '{path}' cannot be snapshotted as its declared type.");
        if (!_configurationReferences.TryGetValue(path, out var reference))
            _configurationReferences.Add(path, reference = binding.CreateReference(_configurationCell));
        return (ConfigReference<T>)reference;
    }

    /// <summary>Obtain the stable reference for an explicitly declared root whole-value boundary.</summary>
    public ConfigReference<T> GetConfigReference<T>() => GetConfigReference<T>("");

    /// <summary>Obtain a stable reference by its exact nested object-key path.</summary>
    public ConfigReference<T> GetConfigReference<T>(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return GetConfigReference<T>(ConfigBinding.DisplayPath(path));
    }

    /// <summary>Retain a Loader's latest raw input, including rejected candidates, without changing activation or effective values.</summary>
    public void RetainRawConfiguration(object? raw)
    {
        Context.VerifyAccess();
        _rawConfig = raw;
    }

    /// <summary>Convert an opted-in effective value to detached raw persistence data; legacy plugins retain their existing value.</summary>
    public object? SimplifyConfiguration(object? effective)
    {
        Context.VerifyAccess();
        if (Definition?.Configuration is not { } schema)
            return effective;
        return schema.Simplify is null
            ? schema.Descriptor.Simplify(effective)
            : ConfigSnapshots.Create(schema.Simplify(effective));
    }

    /// <summary>Validate once and detach all volatile field candidates without publishing raw, effective, or reference values.</summary>
    /// <remarks>False requests an ordinary lifecycle update. Validation exceptions propagate; no effective state changes.</remarks>
    public bool TryPrepareConfigurationUpdate(object? raw, out ConfigurationUpdate? candidate)
    {
        Context.VerifyAccess();
        candidate = null;
        if (State != FiberState.Active || _uid < 0 || Definition?.Configuration is not { } schema ||
            _configurationCell is not { } cell)
            return false;
        var previous = cell.State;
        var resolved = ResolveConfig(raw);
        if (schema.Bindings.Count == 0 || schema.Descriptor.HasBlockedVolatilePlacement() ||
            !schema.TryProject(resolved, out var values) || values.Count != previous.Values.Count ||
            !(schema.OrdinaryEquality?.Invoke(Config, resolved) ?? schema.Descriptor.EffectiveEquals(Config, resolved)))
            return false;
        candidate = new ConfigurationUpdate(this, cell, previous, values, resolved, raw);
        return true;
    }

    internal bool CommitConfiguration(ConfigurationCell cell, ConfigurationState previous, ConfigurationState next)
    {
        if (State != FiberState.Active || _uid < 0 || !ReferenceEquals(cell, _configurationCell) ||
            !ReferenceEquals(previous, cell.State))
            return false;
        // One publication switches every field snapshot together. Effective config
        // keeps its activation identity; retired cells never receive later generations.
        cell.State = next;
        return true;
    }

    private void PublishConfiguration(object? resolved)
    {
        if (Definition?.Configuration is { } schema)
        {
            if (schema.Descriptor.HasBlockedVolatilePlacement())
                throw new ConfigurationValidationException(
                    ["Volatile fields require a fixed object path without an enclosing volatile field."]);
            schema.Descriptor.ValidateBindings(schema.Bindings);
            if (!schema.TryProject(resolved, out var values))
                throw new ConfigurationValidationException(
                    ["Volatile fields must project to their declared readonly snapshot types."]);
            _configurationCell = new(new(values));
            _configurationReferences.Clear();
        }

        _config = resolved;
    }

    /// <summary>
    /// Gets the raw config value.
    /// </summary>
    public object? RawConfig => Volatile.Read(ref _rawConfig);

    internal void AttachOwnership() =>
        _ownership = _parent.Effect(() => new AsyncCleanup(DisposeOwnedAsync), "ctx.plugin()");

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

    private FiberState StableState() =>
        _uid < 0 ? FiberState.Disposed :
        _error is not null ? FiberState.Failed :
        _epoch == Inactive ? FiberState.Pending : FiberState.Active;

    private async Task LoadAsync()
    {
        string epoch = _epoch;
        var phase = "configuration";
        foreach (var name in Inject.Keys)
            if (_runtime.Resolve(Context, name, true) is { } entry)
                Store[name] = entry;
        try
        {
            await Task.Yield();
            if (_epoch == epoch)
            {
                PublishConfiguration(ResolveConfig(_rawConfig));
                phase = "apply";
                if (Definition is not null)
                    await Definition.Apply(Context, _config);
                _error = null;
                _failurePhase = null;
            }
        }
        catch (Exception error)
        {
            _error = error;
            _failurePhase = phase;
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
    public Task RestartAsync() =>
        Execution.RunAsync(() =>
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
            _failurePhase = null;
            BeginRestart();
            return;
        }

        var resolved = ResolveConfig(configuration);
        Context.Events.WaterfallWith(
            this,
            "internal/update",
            () =>
            {
                PublishConfiguration(resolved);
                _error = null;
                _failurePhase = null;
                BeginRestart();
                return Undefined.Value;
            },
            resolved,
            noSave);
    }

    private object? ResolveConfig(object? raw)
    {
        var input = Context.Events.WaterfallWith(this, "internal/config", () => raw, raw);
        var resolved = Definition is null ? input : Definition.ResolveConfig(input);
        if (Definition?.Configuration is { } schema)
            schema.Descriptor.ResolveLazy(schema.DescriptionData is null ? input : schema.DescriptionData(resolved));
        return resolved;
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
