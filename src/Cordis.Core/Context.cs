namespace Cordis;
/// <summary>
/// A context owned by a fiber. Enter a root with RunAsync; plugin callbacks already execute there.
/// Scopes share a runtime while resolving each service by its isolation label.
/// </summary>
public sealed partial class Context : IAsyncDisposable
{
    internal readonly Runtime _runtime;
    private Context? _scopeParent;
    private readonly Dictionary<string, object> _realms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object?> _intercepts = new(StringComparer.Ordinal);
    private Task? _disposeTask;
    private bool _closed;
    private bool _closing;
    internal bool IsClosing => _closing;
    internal bool IsClosed => _closed;

    /// <summary>Create a root. The optional diagnostic sink must not throw.</summary>
    public Context(Action<Exception>? reportError = null)
    {
        _runtime = new Runtime(reportError);
        Root = this;
        Fiber = new Fiber(_runtime, this);
        _runtime.Root = this;
    }

    internal Context(Runtime runtime, Context parent, Fiber fiber)
    {
        _runtime = runtime;
        Root = parent.Root;
        _scopeParent = parent;
        Fiber = fiber;
    }

    /// <summary>
    /// Gets the root value.
    /// </summary>
    public Context Root
    {
        get;
    }
    /// <summary>
    /// Gets the fiber value.
    /// </summary>
    public Fiber Fiber
    {
        get;
    }

    /// <summary>
    /// Execute one asynchronous operation in this context's serialized execution domain.
    /// Synchronous reentry is allowed. Awaited operations yield to other queued callbacks.
    /// Do not synchronously block on lifecycle Tasks or use async void callbacks.
    /// </summary>
    public Task RunAsync(Func<Context, Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _runtime.Execution.RunAsync(() =>
        {
            ObjectDisposedException.ThrowIf(Root._closed, Root);
            return operation(this);
        });
    }

    /// <summary>Register immediately; WaitAsync observes current settlement, not eventual readiness.</summary>
    public Fiber Plugin<T>(Plugin<T> plugin, object? configuration = null)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(plugin);
        return _runtime.Register(this, plugin.Capture(), configuration);
    }

    /// <summary>Create a child plugin whose lifecycle follows these required services.</summary>
    public Fiber Inject(IReadOnlyList<string> dependencies, Action<Context> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Plugin(new Plugin<object?> { Inject = dependencies, Apply = (ctx, _) => callback(ctx), });
    }

    /// <summary>Async counterpart of Inject, with the same child ownership.</summary>
    public Fiber Inject(IReadOnlyList<string> dependencies, Func<Context, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return Plugin(new Plugin<object?> { Inject = dependencies, ApplyAsync = (ctx, _) => callback(ctx), });
    }

    /// <summary>
    /// Explicit service lookup. T checks the returned value, and is NOT part of service identity.
    /// It does not impose injection metadata; Reflect.Read implements property-style access.
    /// </summary>
    public T? Get<T>(string name, bool strict = true)
    {
        VerifyAccess();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        object? value = Reflect.Get(name, strict);
        return value is null ? default : (T)value;
    }

    /// <summary>Publish a named service. Disposal unpublishes it; it does not dispose the value.</summary>
    public EffectHandle Provide(string name, object? value, Func<bool>? check = null) => Reflect.Provide(name, value, check);
    /// <summary>
    /// Provides the requested value.
    /// </summary>
    public EffectHandle Provide(string name, object? value, Func<Context, bool> check) => Reflect.Provide(name, value, check);
    /// <summary>Run setup now and own the returned synchronous cleanup function.</summary>
    public EffectHandle Effect(Func<Action> setup, string label = "anonymous")
    {
        ArgumentNullException.ThrowIfNull(setup);
        return CreateEffect(effect => effect.Collect(setup()), label);
    }

    /// <summary>Run setup now and own the returned IDisposable.</summary>
    public EffectHandle Effect(Func<IDisposable> setup, string label = "anonymous")
    {
        ArgumentNullException.ThrowIfNull(setup);
        return CreateEffect(effect => effect.Collect(setup()), label);
    }

    /// <summary>Run setup now and own the returned async cleanup object or nested effect handle.</summary>
    public EffectHandle Effect(Func<IAsyncDisposable> setup, string label = "anonymous")
    {
        ArgumentNullException.ThrowIfNull(setup);
        return CreateEffect(effect => effect.Collect(setup()), label);
    }

    /// <summary>Begin setup now; Ready observes setup and disposal waits for it to finish.</summary>
    public EffectHandle Effect(Func<Task<IAsyncDisposable>> setup, string label = "anonymous")
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(setup);
        EffectHandle effect = Fiber.NewEffect(label);
        effect.StartAsync(setup);
        return effect;
    }

    /// <summary>Collect cleanup values as yielded, and clean them in reverse yield order.</summary>
    public EffectHandle Effect(Func<IEnumerable<IAsyncDisposable>> setup, string label = "anonymous")
    {
        ArgumentNullException.ThrowIfNull(setup);
        return CreateEffect(effect =>
        {
            foreach (IAsyncDisposable item in setup())
                effect.Collect(item);
        }, label);
    }

    private EffectHandle CreateEffect(Action<EffectHandle> setup, string label)
    {
        VerifyAccess();
        EffectHandle effect = Fiber.NewEffect(label);
        effect.Start(() => setup(effect));
        return effect;
    }

    /// <summary>
    /// Performs the effect operation.
    /// </summary>
    public EffectHandle Effect(Func<IAsyncEnumerable<IAsyncDisposable>> setup, string label = "anonymous")
    {
        VerifyAccess();
        EffectHandle effect = Fiber.NewEffect(label);
        effect.StartStream(setup);
        return effect;
    }

    internal void VerifyAccess()
    {
        _runtime.Execution.VerifyAccess();
        ObjectDisposedException.ThrowIf(Root._closed, Root);
    }

    /// <summary>
    /// End this .NET root execution lifetime (an adapter, unlike the reusable root Fiber restart).
    /// Child contexts dispose their owning fiber. Concurrent root disposal callers join one Task.
    /// </summary>
    public ValueTask DisposeAsync() => new(_runtime.Execution.RunAsync(() =>
    {
        if (!ReferenceEquals(this, Root))
            return Fiber.DisposeCoreAsync();
        return _disposeTask ??= CloseRootAsync();
    }));
    private async Task CloseRootAsync()
    {
        _closing = true;
        try
        {
            await Fiber.DisposeCoreAsync();
        }
        finally
        {
            _closed = true;
        }
    }
}

internal sealed class AsyncCleanup(Func<Task> cleanup) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => new(cleanup());
}
