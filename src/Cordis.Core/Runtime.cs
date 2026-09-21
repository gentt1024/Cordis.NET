namespace Cordis;

internal sealed class Runtime
{
    internal readonly CordisExecutionContext Execution = new();
    internal readonly Dictionary<object, ServiceEntry> Services = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<string, Accessor> Accessors = new(StringComparer.Ordinal);
    internal readonly HashSet<string> ServiceNames = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, List<EventHook>> Hooks = new(StringComparer.Ordinal);
    internal readonly Dictionary<object, PluginRuntime> Plugins = new(ReferenceEqualityComparer.Instance);
    internal readonly Dictionary<long, ILogExporter> Exporters = [];
    internal readonly List<LogMessage> LogBuffer = [];
    internal long ExporterCounter, MessageCounter;
    internal int BufferSize = 1000;
    internal Context Root = null!;
    private long _counter;
    private readonly Action<Exception>? _report;
    internal Runtime(Action<Exception>? report) => _report = report;
    internal void Report(Exception error)
    {
        _report?.Invoke(error);
        Root.Logger.Error(error);
    }

    internal Fiber Register(Context parent, PluginDefinition definition, object? config)
    {
        parent.Fiber.AssertCanCreateEffect();
        if (!Plugins.TryGetValue(definition.Identity, out PluginRuntime? plugin))
        {
            plugin = new PluginRuntime(definition);
            Plugins.Add(definition.Identity, plugin);
        }

        var fiber = new Fiber(this, parent, plugin.Definition, definition.Inject, ++_counter, config);
        plugin.MutableFibers.Add(fiber);
        fiber.AttachOwnership();
        try
        {
            parent.Events.Emit("internal/plugin", fiber);
        }
        catch
        {
            _ = fiber.DisposeAsync();
            throw;
        }

        foreach (var pair in fiber.Inject)
            if (pair.Value is not null)
                fiber.Context.AddIntercept(pair.Key, pair.Value);
        if (fiber.Uid is not null && parent.Fiber.State != FiberState.Unloading)
            fiber.Refresh();
        return fiber;
    }

    internal void Remove(Fiber fiber)
    {
        object identity = fiber.Definition!.Identity;
        if (!Plugins.TryGetValue(identity, out PluginRuntime? plugin))
            return;
        plugin.MutableFibers.Remove(fiber);
        if (plugin.Fibers.Count == 0)
            Plugins.Remove(identity);
    }

    internal Fiber[] Notify(Context context, string name)
    {
        Fiber[] fibers = Plugins.Values.SelectMany(p => p.Fibers).Where(f => f.Inject.ContainsKey(name) && ReferenceEquals(f.Context.Realm(name), context.Realm(name))).ToArray();
        foreach (Fiber fiber in fibers)
            fiber.Refresh();
        var receiver = context.Extend();
        receiver.Filter = target => ReferenceEquals(target.Realm(name), context.Realm(name));
        context.Events.EmitWith(receiver, "internal/service", name, Resolve(context, name, false)?.Value);
        return fibers;
    }

    internal void NotifyOwner(Fiber owner)
    {
        foreach (var entry in Services.Values.Where(s => ReferenceEquals(s.Owner, owner)).ToArray())
            Notify(entry.Context, entry.Name);
    }

    internal ServiceEntry? Resolve(Context context, string name, bool strict)
    {
        if (!Services.TryGetValue(context.Realm(name), out ServiceEntry? service))
            return null;
        return !strict || service.Owner.State == FiberState.Active ? service : null;
    }
}

internal sealed class ServiceEntry(string name, Context context, object? value, Func<Context, bool>? check)
{
    internal string Name { get; } = name;
    internal Context Context { get; } = context;
    internal Fiber Owner => Context.Fiber;
    internal object? Value { get; set; } = value;
    internal Func<Context, bool>? Check { get; } = check;
}
