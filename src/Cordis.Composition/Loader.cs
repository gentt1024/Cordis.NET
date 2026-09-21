using Cordis;
namespace Cordis.Composition;

/// <summary>
/// Represents the entry tree component.
/// </summary>
public class EntryTree
{
    /// <summary>
    /// Gets the separator value.
    /// </summary>
    public const char Separator = ':';
    /// <summary>
    /// Gets the context value.
    /// </summary>
    public Context Context { get; }
    /// <summary>
    /// Gets the loader value.
    /// </summary>
    public Loader Loader { get; protected set; } = null!;
    /// <summary>
    /// Gets the owner value.
    /// </summary>
    public Entry? Owner { get; }
    /// <summary>
    /// Gets the base uri value.
    /// </summary>
    public Uri BaseUri { get; }
    /// <summary>
    /// Gets the root value.
    /// </summary>
    public EntryGroup Root { get; }
    /// <summary>
    /// Gets the store value.
    /// </summary>
    public Dictionary<string, Entry> Store { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Gets the enable logs value.
    /// </summary>
    public bool EnableLogs { get; set; }
    /// <summary>
    /// Initializes a new instance of the <see cref="EntryTree"/> type.
    /// </summary>
    public EntryTree(Context context, Loader loader, Uri baseUri, Entry? owner = null)
    { Context = context; Loader = loader; BaseUri = baseUri; Owner = owner; Root = new EntryGroup(context, this, owner); }
    /// <summary>
    /// Initializes a new instance of the <see cref="EntryTree"/> type.
    /// </summary>
    protected EntryTree(Context context, Uri baseUri)
    { Context = context; BaseUri = baseUri; Root = new EntryGroup(context, this); }
    /// <summary>
    /// Performs the entries operation.
    /// </summary>
    public IEnumerable<Entry> Entries()
    { foreach (var entry in Store.Values.ToArray()) { yield return entry; if (entry.Subtree is not null) foreach (var child in entry.Subtree.Entries()) yield return child; } }
    /// <summary>
    /// Waits for async.
    /// </summary>
    public async Task WaitAsync()
    {
        while (true)
        {
            var entries = Entries().ToArray();
            var tasks = entries.Select(e => e.Initializing).Where(t => t is not null).Cast<Task>().ToArray();
            foreach (var task in tasks) try { await task; } catch (Exception error) { Loader.Report(error); }
            var observed = entries.Select(entry => (Entry: entry, Fiber: entry.Fiber, State: entry.Fiber?.State)).ToArray();
            foreach (var item in observed) if (item.Fiber is { } fiber) try { await fiber.WaitAsync(); item.Entry.LastError = null; } catch (Exception error) { item.Entry.LastError = error; }
            var current = Entries().ToArray();
            if (entries.SequenceEqual(current) && current.All(entry => entry.Initializing is null
                && entry.Fiber?.State is not (FiberState.Loading or FiberState.Unloading))
                && observed.All(item => ReferenceEquals(item.Fiber, item.Entry.Fiber) && item.State == item.Entry.Fiber?.State)) return;
        }
    }
    /// <summary>
    /// Performs the ensure id operation.
    /// </summary>
    public string EnsureId(EntryOptions options)
    { if (options.Id.Length == 0) { do { options.Id = Guid.NewGuid().ToString("N")[..8]; } while (Store.ContainsKey(options.Id)); } return options.Id; }
    /// <summary>
    /// Resolves the requested value.
    /// </summary>
    public Entry Resolve(string id)
    {
        var parts = id.Split(Separator); var tree = this;
        foreach (var part in parts[..^1]) tree = tree.Store.GetValueOrDefault(part)?.Subtree ?? throw new KeyNotFoundException($"Cannot resolve entry {id}.");
        return tree.Store.GetValueOrDefault(parts[^1]) ?? throw new KeyNotFoundException($"Cannot resolve entry {id}.");
    }
    /// <summary>
    /// Resolves group.
    /// </summary>
    public EntryGroup ResolveGroup(string? id) => string.IsNullOrEmpty(id) ? Root : Resolve(id).Subgroup ?? throw new InvalidOperationException($"Entry {id} is not a group.");
    /// <summary>
    /// Creates async.
    /// </summary>
    public Task<string> CreateAsync(EntryOptions options, string? parent = null, int position = int.MaxValue) => InDomain(async () =>
    { var group = ResolveGroup(parent); group.Data.Insert(Math.Clamp(position, 0, group.Data.Count), options); group.Tree.Write(); return await group.CreateAsync(options); });
    /// <summary>
    /// Removes async.
    /// </summary>
    public Task RemoveAsync(string id) => Context.RunAsync(async _ => { var entry = Resolve(id); await entry.Parent.RemoveAsync(entry.Options.Id); entry.Parent.Tree.Write(); });
    /// <summary>
    /// Updates async.
    /// </summary>
    public Task UpdateAsync(string id, EntryOptions options, string? parent = null, int position = int.MaxValue, bool move = false) => Context.RunAsync(async _ =>
    {
        var entry = Resolve(id); var source = entry.Parent;
        if (move) { var target = ResolveGroup(parent); source.Data.Remove(entry.Options); target.Data.Insert(Math.Clamp(position, 0, target.Data.Count), entry.Options); entry.Parent = target; target.Tree.Write(); }
        source.Tree.Write(); await entry.UpdateAsync(options, force: true);
    });
    private async Task<T> InDomain<T>(Func<Task<T>> action) { T result = default!; await Context.RunAsync(async _ => result = await action()); return result; }
    /// <summary>
    /// Performs the write operation.
    /// </summary>
    public virtual void Write() { }
}

/// <summary>
/// Represents the entry group component.
/// </summary>
/// <param name="context">The context value.</param>
/// <param name="tree">The tree value.</param>
/// <param name="owner">The owner value.</param>
public sealed class EntryGroup(Context context, EntryTree tree, Entry? owner = null)
{
    /// <summary>
    /// Gets the context value.
    /// </summary>
    public Context Context { get; } = context;
    /// <summary>
    /// Gets the tree value.
    /// </summary>
    public EntryTree Tree { get; } = tree;
    /// <summary>
    /// Gets the owner value.
    /// </summary>
    public Entry? Owner { get; } = owner;
    /// <summary>
    /// Gets the data value.
    /// </summary>
    public List<EntryOptions> Data { get; private set; } = [];
    /// <summary>
    /// Creates async.
    /// </summary>
    public async Task<string> CreateAsync(EntryOptions options)
    {
        var id = Tree.EnsureId(options);
        if (!Tree.Store.TryGetValue(id, out var entry)) Tree.Store[id] = entry = new Entry(Tree.Loader, this);
        entry.Parent = this; await entry.UpdateAsync(options, create: true, force: true); return entry.Id;
    }
    /// <summary>
    /// Removes async.
    /// </summary>
    public async Task RemoveAsync(string id, bool disposing = false)
    {
        if (!Tree.Store.Remove(id, out var entry)) return;
        entry.Removing = true;
        if (entry.Fiber is not null) await entry.Fiber.DisposeAsync();
        if (!disposing) Data.Remove(entry.Options);
    }
    /// <summary>
    /// Updates async.
    /// </summary>
    public Task UpdateAsync(List<EntryOptions> data) => Context.RunAsync(async _ =>
    {
        var old = Data.ToArray(); Data = data;
        var ids = data.Select(row => row.Id).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal);
        foreach (var row in old) if (!ids.Contains(row.Id)) await RemoveAsync(row.Id);
        await Task.WhenAll(data.Select(async row => { try { await CreateAsync(row); } catch (Exception error) { Tree.Loader.Report(error); } }));
    });
    /// <summary>
    /// Stops async.
    /// </summary>
    public async Task StopAsync() { foreach (var row in Data.ToArray()) await RemoveAsync(row.Id, true); }
}

/// <summary>
/// Represents the entry component.
/// </summary>
public sealed class Entry
{
    internal const string MetadataKey = "cordis.entry";
    private readonly Dictionary<string, object> localRealms = new(StringComparer.Ordinal);
    /// <summary>
    /// Gets the loader value.
    /// </summary>
    public Loader Loader { get; }
    /// <summary>
    /// Gets the context value.
    /// </summary>
    public Context Context { get; private set; }
    /// <summary>
    /// Gets the parent value.
    /// </summary>
    public EntryGroup Parent { get; internal set; }
    /// <summary>
    /// Gets the options value.
    /// </summary>
    public EntryOptions Options { get; private set; } = new();
    /// <summary>
    /// Gets the fiber value.
    /// </summary>
    public Fiber? Fiber { get; internal set; }
    /// <summary>
    /// Gets the subgroup value.
    /// </summary>
    public EntryGroup? Subgroup { get; internal set; }
    /// <summary>
    /// Gets the subtree value.
    /// </summary>
    public EntryTree? Subtree { get; internal set; }
    /// <summary>
    /// Gets the last error value.
    /// </summary>
    public Exception? LastError { get; internal set; }
    internal Task? Initializing { get; private set; }
    internal bool Removing { get; set; }
    internal bool IsTreeCarrier { get; private set; }
    /// <summary>
    /// Gets the id value.
    /// </summary>
    public string Id => Parent.Tree.Owner is { } owner ? owner.Id + EntryTree.Separator + Options.Id : Options.Id;
    /// <summary>
    /// Gets the disabled value.
    /// </summary>
    public bool Disabled
    {
        get
        {
            if (Options.Group) return false;
            for (Entry? row = this; row is not null; row = row.Parent.Owner)
                if (Data.Truthy(Data.Interpolate(row.Options.Disabled, row.Context, Loader.ExpressionEvaluator))) return true;
            return false;
        }
    }
    internal Entry(Loader loader, EntryGroup parent) { Loader = loader; Parent = parent; Context = parent.Context.Extend(); Context.Metadata[MetadataKey] = this; }
    private void PatchContext()
    {
        Context.Reparent(Parent.Context);
        var realms = new Dictionary<string, object>(StringComparer.Ordinal);
        if (Options.GetValueOrDefault("isolate") is IDictionary<string, object?> isolates)
            foreach (var pair in isolates)
            {
                if (!Data.Truthy(pair.Value)) continue;
                if (pair.Value is true) { if (!localRealms.TryGetValue(pair.Key, out var realm)) localRealms[pair.Key] = realm = new object(); realms[pair.Key] = realm; }
                else realms[pair.Key] = Loader.NamedRealm(pair.Key, Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)!);
            }
        Context.SetIsolations(realms);
        Context.SetIntercepts(Options.GetValueOrDefault("intercept") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>());
    }
    /// <summary>
    /// Refreshes async.
    /// </summary>
    public Task RefreshAsync() => Context.RunAsync(async _ => { if (Fiber is null && !Disabled) await InitAsync(); });
    /// <summary>
    /// Updates async.
    /// </summary>
    public Task UpdateAsync(EntryOptions options, bool create = false, bool force = false) => Context.RunAsync(_ => UpdateCoreAsync(options, create, force));
    private async Task UpdateCoreAsync(EntryOptions options, bool create, bool force)
    {
        var previous = new EntryOptions(Options);
        var oldConfig = Options.RawConfig;
        if (create) Options = options;
        else foreach (var pair in options) { if (pair.Value is null) Options.Remove(pair.Key); else Options[pair.Key] = pair.Value; }
        if (Subgroup is not null && Options.Config is IEnumerable<object?>) Options.Config = Data.Entries(Options.Config);
        if (Disabled) { if (Fiber is not null) { Removing = true; await Fiber.DisposeAsync(); Fiber = null; Removing = false; } return; }
        PatchContext();
        if (Fiber?.Uid is not null)
        {
            if (!force && Data.DeepEquals(previous, Options)) return;
            if (!Data.DeepEquals(oldConfig, Options.RawConfig) || Options.Group) Fiber.Update(Options.RawConfig, true);
        }
        else await InitAsync();
    }
    /// <summary>
    /// Performs the init async operation.
    /// </summary>
    public async Task InitAsync()
    {
        try { await (Initializing ??= InitializeAsync()); } finally { Initializing = null; }
        if (Fiber is not null) _ = NotifySettledAsync(Fiber);
    }
    private async Task NotifySettledAsync(Fiber fiber)
    {
        try { await fiber.WaitAsync(); } catch (Exception error) { LastError = error; }
        try { await Loader.Context.RunAsync(_ => { if (!Loader.HasTasks) Loader.Context.Reflect.Notify("loader"); return Task.CompletedTask; }); }
        catch (ObjectDisposedException) { /* Root shutdown owns the final notification boundary. */ }
    }
    private async Task InitializeAsync()
    {
        IPlugin plugin;
        try { plugin = await Loader.ResolveAsync(Options.Name, Parent.Tree.BaseUri); }
        catch (Exception error) { LastError = error; Loader.Report(error); return; }
        PatchContext(); LastError = null; Removing = false; IsTreeCarrier = plugin is ITreeCarrierPlugin;
        if (ReferenceEquals(plugin, Loader.Builtins["group"])) Options.Config = Data.Entries(Options.Config);
        Fiber = Context.Plugin(plugin, Options.RawConfig);
        // A resolved import still crosses the JavaScript import/activation checkpoint.
        // Do not await the entire apply: a plugin may intentionally remain Loading.
        await Task.Yield();
    }
}

/// <summary>
/// Represents the loader component.
/// </summary>
public sealed class Loader : EntryTree
{
    private readonly IModuleResolver resolver;
    private readonly Dictionary<(string, string), object> realms = [];
    private readonly Dictionary<Fiber, Entry> roots = [];
    /// <summary>
    /// Gets the expression evaluator value.
    /// </summary>
    public IExpressionEvaluator? ExpressionEvaluator { get; }
    /// <summary>
    /// Gets the diagnostic value.
    /// </summary>
    public Action<Exception>? Diagnostic { get; }
    /// <summary>
    /// Gets the builtins value.
    /// </summary>
    public Dictionary<string, IPlugin> Builtins { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Initializes a new instance of the <see cref="Loader"/> type.
    /// </summary>
    public Loader(Context context, IModuleResolver resolver, Uri? baseUri = null, IExpressionEvaluator? expressionEvaluator = null, Action<Exception>? diagnostic = null)
        : base(context, baseUri ?? new Uri(Path.GetFullPath(Environment.CurrentDirectory) + Path.DirectorySeparatorChar))
    {
        Loader = this; this.resolver = resolver; ExpressionEvaluator = expressionEvaluator; Diagnostic = diagnostic;
        Builtins["group"] = new TreeCarrierPlugin(new Plugin<object?>
        {
            Name = "group",
            ApplyAsync = async (ctx, raw) =>
        {
            var row = roots[ctx.Fiber]; var group = new EntryGroup(ctx, row.Parent.Tree, row); row.Subgroup = group;
            ctx.Effect(() => new Cleanup(group.StopAsync));
            ctx.On("internal/update", (e, args) => { _ = group.UpdateAsync(Data.Entries(args[0])); return null; });
            await group.UpdateAsync(Data.Entries(raw));
        }
        });
        Builtins["include"] = new TreeCarrierPlugin(new Plugin<object?>
        {
            Name = "include",
            ApplyAsync = async (ctx, raw) =>
        {
            var row = roots[ctx.Fiber]; var config = IncludeOptions.From(raw); var include = new Include(ctx, this, config, row.Parent.Tree.BaseUri, row); row.Subtree = include;
            ctx.Effect(() => new Cleanup(include.StopAsync));
            ctx.On("internal/update", (e, args) => { var updated = IncludeOptions.From(args[0]); if (updated.Path != include.Options.Path) return e.Next(); _ = include.UpdateOptionsAsync(updated); return null; });
            await include.StartAsync();
        }
        });
        context.Provide("loader", this, caller => !caller.Intercepts("loader").OfType<IReadOnlyDictionary<string, object?>>().Any(config => config.GetValueOrDefault("await") is true) || !HasTasks);
        context.On("internal/plugin", (_, args) =>
        {
            if (args[0] is not Fiber fiber || !fiber.Parent.Metadata.TryGetValue(Entry.MetadataKey, out var value) || value is not Entry entry) return null;
            if (fiber.Uid is null)
            {
                if (roots.Remove(fiber) && !entry.Removing && !entry.Disabled && entry.Parent.Tree.Context.Fiber.State is not (FiberState.Unloading or FiberState.Disposed)) { entry.Options.Disabled = true; entry.Parent.Tree.Write(); }
                return null;
            }
            if (roots.TryGetValue(fiber.Parent.Fiber, out var parentEntry) && ReferenceEquals(parentEntry, entry)) return null;
            roots[fiber] = entry;
            if (entry.Options.GetValueOrDefault("inject") is IEnumerable<object?> list) foreach (var name in list.Cast<string>()) fiber.Inject[name] = null;
            else if (entry.Options.GetValueOrDefault("inject") is IDictionary<string, object?> map) foreach (var pair in map) fiber.Inject[pair.Key] = pair.Value;
            return null;
        }, new EventOptions { Global = true });
        context.On("internal/config", (e, _) =>
        {
            var config = e.Next();
            return e.Receiver is Fiber fiber && roots.TryGetValue(fiber, out var entry) && !entry.IsTreeCarrier
                ? Data.Interpolate(config, fiber.Context, ExpressionEvaluator) : config;
        }, new EventOptions { Global = true });
        context.On("internal/update", (e, args) =>
        {
            if (e.Receiver is Fiber fiber && roots.TryGetValue(fiber, out var entry) && args[1] is not true) { entry.Options.Config = args[0]; entry.Parent.Tree.Write(); }
            return e.Next();
        }, new EventOptions { Global = true, Prepend = true });
    }
    /// <summary>
    /// Gets the has tasks value.
    /// </summary>
    public bool HasTasks => Entries().Any(entry => entry.Initializing is not null || entry.Fiber?.State is FiberState.Loading or FiberState.Unloading);
    /// <summary>Replace all loader-owned roots for a module after a CLR candidate is prepared.</summary>
    public Task ReplacePluginAsync(IPlugin previous, IPlugin replacement) => Context.RunAsync(async _ =>
    {
        var fibers = Context.Registry.Get(previous)?.Fibers.ToArray() ?? [];
        var rows = fibers.Select(f => (Fiber: f, Entry: roots.GetValueOrDefault(f), Parent: f.Parent, Raw: roots.TryGetValue(f, out var entry) ? entry.Options.RawConfig : f.RawConfig)).ToArray();
        foreach (var row in rows) if (row.Entry is not null) row.Entry.Removing = true;
        foreach (var row in rows)
            try { await row.Fiber.DisposeAsync(); } catch (Exception error) { Report(error); }
        var activated = new List<(Fiber Fiber, Entry? Entry)>();
        try
        {
            foreach (var row in rows)
            {
                if (row.Parent.Fiber.Uid is null) continue;
                var fiber = row.Parent.Plugin(replacement, row.Raw);
                activated.Add((fiber, row.Entry));
                if (row.Entry is not null) row.Entry.Fiber = fiber;
            }
            await Task.WhenAll(activated.Select(row => row.Fiber.WaitAsync()));
        }
        catch
        {
            foreach (var row in activated)
                try { await row.Fiber.DisposeAsync(); } catch (Exception error) { Report(error); }
            var restored = new List<Fiber>();
            foreach (var row in rows)
            {
                if (row.Parent.Fiber.Uid is null) continue;
                try
                {
                    var fiber = row.Parent.Plugin(previous, row.Raw); restored.Add(fiber);
                    if (row.Entry is not null) row.Entry.Fiber = fiber;
                }
                catch (Exception error) { Report(error); }
            }
            foreach (var fiber in restored) try { await fiber.WaitAsync(); } catch (Exception error) { Report(error); }
            throw;
        }
        finally { foreach (var row in rows) if (row.Entry is not null) row.Entry.Removing = false; }
    });
    internal object NamedRealm(string name, string label) { if (!realms.TryGetValue((name, label), out var realm)) realms[(name, label)] = realm = new object(); return realm; }
    internal void Report(Exception error) { Diagnostic?.Invoke(error); }
    internal ValueTask<IPlugin> ResolveAsync(string name, Uri baseUri) => name.StartsWith("cordis:", StringComparison.Ordinal)
        ? ValueTask.FromResult(Builtins.GetValueOrDefault(name[7..]) ?? throw new FileNotFoundException($"Unknown builtin {name}.")) : resolver.ResolveAsync(name, baseUri);
    /// <summary>
    /// Locates the requested value.
    /// </summary>
    public string? Locate(Fiber fiber) { while (true) { if (roots.TryGetValue(fiber, out var entry)) return entry.Id; if (fiber.Parent.Fiber == fiber) return null; fiber = fiber.Parent.Fiber; } }
    private sealed class Cleanup(Func<Task> cleanup) : IAsyncDisposable { public ValueTask DisposeAsync() => new(cleanup()); }
}
