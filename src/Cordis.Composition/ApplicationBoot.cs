using Cordis;
namespace Cordis.Composition;

/// <summary>
/// A current entry failure or pending dependency, retaining the original exception for short-lived debugging.
/// </summary>
/// <param name="Id">The id value.</param>
/// <param name="Module">The module value.</param>
/// <param name="State">The state value.</param>
/// <param name="Error">The error value.</param>
/// <param name="Missing">The missing value.</param>
/// <param name="Required">The required value.</param>
public sealed record EntryDiagnostic(string Id, string Module, FiberState? State, Exception? Error, IReadOnlyList<string> Missing, bool Required)
{
    /// <summary>
    /// The operation that failed, when the loader can identify it.
    /// </summary>
    public string? Phase { get; init; }

    /// <summary>Copies this diagnostic to values suitable for retention after a collectible plugin is unloaded.</summary>
    /// <remarks>The exception is rendered to text, and dependency names are copied. The original diagnostic and exception are unchanged.</remarks>
    public EntryDiagnosticSnapshot ToSnapshot() => new(Id, Module, State,
        Error is null ? null : StartupException.DescribeError(Error), Array.AsReadOnly(Missing.ToArray()), Required) { Phase = Phase };
}

/// <summary>A diagnostic containing only names, state and error text; it does not retain plugin exceptions or runtime objects.</summary>
/// <param name="Id">The qualified Loader entry identity.</param>
/// <param name="Module">The configured module name.</param>
/// <param name="State">The observed fiber state, or null when no fiber was created.</param>
/// <param name="Error">Rendered error evidence, or null for a pending dependency without an error.</param>
/// <param name="Missing">Names of dependencies unavailable at the time of observation.</param>
/// <param name="Required">Whether the caller's startup policy requires this entry to activate.</param>
public sealed record EntryDiagnosticSnapshot(string Id, string Module, FiberState? State, string? Error, IReadOnlyList<string> Missing, bool Required)
{
    /// <summary>The operation that failed, when known.</summary>
    public string? Phase { get; init; }
}
/// <summary>
/// Represents the startup exception component.
/// </summary>
public sealed class StartupException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StartupException"/> type.
    /// </summary>
    public StartupException(IReadOnlyList<EntryDiagnostic> diagnostics)
        : this(Format(diagnostics), diagnostics) { }
    /// <summary>
    /// Initializes a new instance of the <see cref="StartupException"/> type.
    /// </summary>
    public StartupException(string message, IReadOnlyList<EntryDiagnostic> diagnostics)
        : base(message, diagnostics.Any(d => d.Error is not null) ? new AggregateException(diagnostics.Where(d => d.Error is not null).Select(d => d.Error!)) : null) => Diagnostics = diagnostics;
    /// <summary>
    /// Gets the diagnostics value.
    /// </summary>
    public IReadOnlyList<EntryDiagnostic> Diagnostics { get; }
    /// <summary>
    /// Gets the configuration path value.
    /// </summary>
    public string? ConfigurationPath { get; internal set; }
    /// <summary>
    /// Gets the startup messages value.
    /// </summary>
    public IReadOnlyList<Exception> StartupMessages { get; internal set; } = [];
    /// <summary>
    /// Gets the startup logs value.
    /// </summary>
    public IReadOnlyList<LogMessage> StartupLogs { get; internal set; } = [];
    private static string Format(IReadOnlyList<EntryDiagnostic> diagnostics)
    {
        var lines = new List<string> { "Required plugins did not activate: " + string.Join(", ", diagnostics.Where(d => d.Required).Select(d => d.Id)) };
        foreach (var diagnostic in diagnostics)
        {
            var detail = Describe(diagnostic);
            lines.Add($"{diagnostic.Id} ({diagnostic.Module}): {detail}");
        }
        return string.Join(Environment.NewLine, lines);
    }
    internal static string Describe(EntryDiagnostic diagnostic)
    {
        var detail = diagnostic.Error is { } error ? DescribeError(error) : (diagnostic.State == FiberState.Pending
            ? $"pending (waiting for service{(diagnostic.Missing.Count == 1 ? "" : "s")}: {(diagnostic.Missing.Count == 0 ? "unknown" : string.Join(", ", diagnostic.Missing))})"
            : diagnostic.State is null ? "failed to import" : "fiber state " + diagnostic.State);
        return diagnostic.Phase is null ? detail : diagnostic.Phase + ": " + detail;
    }
    internal static string DescribeError(Exception error)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance); var lines = new List<string>();
        void Visit(Exception current)
        {
            if (!seen.Add(current)) return;
            if (current is not AggregateException && current.InnerException is null) { lines.Add(current.ToString()); return; }
            var message = current.Message;
            if (current is AggregateException aggregate)
            {
                // AggregateException.Message appends every member's message. Members are
                // rendered separately below so shared errors retain one diagnostic copy.
                var suffix = string.Concat(aggregate.InnerExceptions.Select(member => " (" + member.Message + ")"));
                if (message.EndsWith(suffix, StringComparison.Ordinal)) message = message[..^suffix.Length];
            }
            // A platform adapter may customize its diagnostic heading (for example a
            // script engine's SyntaxError). Preserve that first line without re-expanding
            // the complete inner-error graph through Exception.ToString().
            var heading = current is AggregateException ? current.GetType().FullName + ": " + message
                : current.ToString().Split('\n')[0].TrimEnd('\r') + (message.IndexOf('\n') is var newline && newline >= 0 ? Environment.NewLine + message[(newline + 1)..] : "");
            lines.Add(heading + (current.StackTrace is { } stack ? Environment.NewLine + stack : ""));
            if (current.InnerException is { } cause) Visit(cause);
            if (current is AggregateException members) foreach (var member in members.InnerExceptions) Visit(member);
        }
        Visit(error); return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Represents the application boot component.
/// </summary>
public static class ApplicationBoot
{
    /// <summary>Boot with the DSH compatibility policy: default required entry names and the dshHomePath service.</summary>
    /// <remarks>Only entries present in the mounted tree are audited. Missing names in the required set do not install or require new entries. The caller owns the returned context.</remarks>
    public static async Task<Context> BootAsync(string configurationPath, IModuleResolver resolver, List<EntryOptions>? patches = null,
        Func<Context, Task>? prepare = null, IReadOnlySet<string>? required = null, IExpressionEvaluator? evaluator = null, Action<string>? warn = null, DshHomePaths? homePaths = null)
        => await BootCoreAsync(configurationPath, resolver, patches, prepare, required ?? DshRequiredEntries, evaluator, warn, homePaths ?? new DshHomePaths());

    /// <summary>Prepare and mount a generic application without DSH services or default required entries.</summary>
    /// <remarks>
    /// Waits for current Loader work to settle; pending dependencies remain legal unless their present entry is explicitly required.
    /// Optional failures are reported through warn. Required failures dispose the context and throw StartupException with original evidence.
    /// The caller owns the returned context. The resolver retains its existing caller-owned lifetime.
    /// </remarks>
    public static Task<Context> BootGenericAsync(string configurationPath, IModuleResolver resolver, List<EntryOptions>? patches = null,
        Func<Context, Task>? prepare = null, IReadOnlySet<string>? required = null, IExpressionEvaluator? evaluator = null, Action<string>? warn = null)
        => BootCoreAsync(configurationPath, resolver, patches, prepare, required, evaluator, warn, null);

    private static async Task<Context> BootCoreAsync(string configurationPath, IModuleResolver resolver, List<EntryOptions>? patches,
        Func<Context, Task>? prepare, IReadOnlySet<string>? required, IExpressionEvaluator? evaluator, Action<string>? warn, DshHomePaths? homePaths)
    {
        var messages = new List<Exception>(); var logs = new List<LogMessage>(); var collecting = true;
        void Collect(Exception error) { if (collecting) messages.Add(error); }
        var context = new Context(Collect); Loader? loader = null; LogSubscription? collector = null; var stage = "host preparation failed";
        try
        {
            await context.RunAsync(async _ =>
            {
                collector = context.Logger.Subscribe(new DelegateLogExporter(logs.Add, (int)LogLevel.Warn));
                if (homePaths is not null) context.Provide("dshHomePath", (DshHomePath)homePaths.PathOf);
                loader = new Loader(context, resolver, new Uri(Path.GetFullPath(configurationPath)), evaluator, Collect);
                if (prepare is not null) await prepare(context);
            });
            stage = "plugin tree failed to load";
            try { await MountAsync(loader!, configurationPath, patches); }
            catch { if (await IsInstalledAsync(context)) throw; collecting = false; return context; }
            if (!await IsInstalledAsync(context)) { collecting = false; return context; }
            var optional = await AuditAsync(loader!, required);
            if (optional.Count > 0) (warn ?? Console.Error.WriteLine)($"cordis: warning: {optional.Count} entr{(optional.Count == 1 ? "y" : "ies")} did not activate" + Environment.NewLine
                + string.Join(Environment.NewLine, optional.Select(d => $"{d.Id} ({d.Module}): {StartupException.Describe(d)}")) + Environment.NewLine);
            collecting = false;
            return context;
        }
        catch (Exception error)
        {
            await context.DisposeAsync();
            if (error is StartupException startup) { startup.ConfigurationPath = configurationPath; startup.StartupMessages = messages.ToArray(); startup.StartupLogs = logs.ToArray(); throw; }
            throw new InvalidOperationException($"{stage}: {StartupException.DescribeError(error)}", error);
        }
        finally
        {
            collecting = false;
            try { if (collector is not null) await collector.DisposeAsync(); }
            finally
            {
                // Runtime and Loader keep Collect for their lifetime. Release the startup
                // evidence retained by its closure after any StartupException owns copies.
                messages.Clear();
                logs.Clear();
            }
        }
    }
    private static async Task<bool> IsInstalledAsync(Context context)
    {
        var installed = false;
        try { await context.RunAsync(_ => { installed = context.Get("loader") is not null; return Task.CompletedTask; }); }
        catch (ObjectDisposedException) { }
        return installed;
    }
    /// <summary>
    /// Gets the dsh required entries value.
    /// </summary>
    public static IReadOnlySet<string> DshRequiredEntries { get; } = new HashSet<string>(["agent-loop", "webserver", "modules", "connection", "headless-runner", "acp", "sdk-jsonrpc-server"], StringComparer.Ordinal);
    /// <summary>
    /// Performs the audit async operation.
    /// </summary>
    public static async Task<IReadOnlyList<EntryDiagnostic>> AuditAsync(Loader loader, IReadOnlySet<string>? required = null)
    {
        await loader.WaitAsync();
        return await AuditCurrentAsync(loader, required);
    }
    private static async Task<IReadOnlyList<EntryDiagnostic>> AuditCurrentAsync(Loader loader, IReadOnlySet<string>? required = null)
    {
        var diagnostics = new List<EntryDiagnostic>();
        await loader.Context.RunAsync(_ =>
        {
            foreach (var entry in loader.Entries())
            {
                Exception? error = entry.LastError;
                string? phase = error is null ? null : entry.Fiber?.FailurePhase ?? (entry.Fiber is null ? "module resolution" : null);
                try { if (entry.Disabled) continue; } catch (Exception failure) { error = failure; phase = "disabled expression failed"; }
                if (error is null && entry.Fiber?.State == FiberState.Active) continue;
                var missing = entry.Fiber is { } fiber ? fiber.Inject.Keys.Where(name => !fiber.Context.Reflect.IsAvailable(name)).ToArray() : [];
                diagnostics.Add(new(entry.Id, entry.Options.Name, entry.Fiber?.State, error, missing, required?.Contains(entry.Options.Id) == true) { Phase = phase });
            }
            return Task.CompletedTask;
        });
        if (diagnostics.Any(d => d.Required)) throw new StartupException(diagnostics);
        return diagnostics;
    }
    /// <summary>
    /// Performs the mount async operation.
    /// </summary>
    public static async Task<Include> MountAsync(Loader loader, string configurationPath, List<EntryOptions>? patches = null)
    {
        var row = new EntryOptions { Id = "root", Name = "cordis:include", Config = new EntryOptions { ["path"] = new Uri(Path.GetFullPath(configurationPath)).AbsoluteUri, ["patches"] = patches ?? [] } };
        await loader.CreateAsync(row); await loader.WaitAsync(); var entry = loader.Resolve("root");
        if (entry.Fiber?.State != FiberState.Active) throw new StartupException([new(entry.Id, entry.Options.Name, entry.Fiber?.State, entry.LastError, [], true)]);
        return (Include)entry.Subtree!;
    }
    /// <summary>
    /// Reconciles async.
    /// </summary>
    public static async Task<IReadOnlyList<EntryDiagnostic>> ReconcileAsync(Include include, List<EntryOptions> patches, IReadOnlySet<string>? required = null)
    {
        IReadOnlyList<EntryDiagnostic> result = [];
        await include.Context.RunAsync(async _ => result = await ReconcileCoreAsync(include, patches, required));
        return result;
    }
    private static async Task<IReadOnlyList<EntryDiagnostic>> ReconcileCoreAsync(Include include, List<EntryOptions> patches, IReadOnlySet<string>? required)
    {
        var loader = include.Loader;
        // Snapshot before draining work: a failure still in flight belongs to this
        // reconciliation, while an already-failed removed row is only historical.
        var previous = await AuditCurrentAsync(loader);
        var snapshots = loader.Entries().ToDictionary(e => e.Id, e => (Entry: e, Fiber: e.Fiber, Options: ConfigurationFile.Write(e.Options, true)));
        var priorFibers = snapshots.Values.Where(s => s.Fiber is not null).Select(s => (Fiber: s.Fiber!, Failed: s.Fiber!.State is FiberState.Failed or FiberState.Disposed)).ToArray();
        if (include.Owner is { } owner)
        {
            var config = owner.Options.Config is IDictionary<string, object?> raw ? new EntryOptions(raw) : new EntryOptions { ["path"] = include.Options.Path };
            config["patches"] = patches;
            await owner.UpdateAsync(new EntryOptions { Config = config });
        }
        else await include.UpdateOptionsAsync(include.Options with { Patches = patches });
        var failures = await AuditAsync(loader);
        var introduced = failures.Where(failure => required?.Contains(failure.Id.Split(EntryTree.Separator)[^1]) == true
            || !previous.Any(old => old.Id == failure.Id && old.Module == failure.Module && old.State == failure.State && old.Error?.Message == failure.Error?.Message && old.Missing.SequenceEqual(failure.Missing)
                && snapshots.TryGetValue(old.Id, out var snapshot) && ReferenceEquals(snapshot.Entry, loader.Resolve(old.Id)) && ReferenceEquals(snapshot.Fiber, loader.Resolve(old.Id).Fiber)
                && snapshot.Options == ConfigurationFile.Write(loader.Resolve(old.Id).Options, true))).ToArray();
        if (introduced.Length > 0) throw new StartupException(introduced.Select(d => d with { Required = true }).ToArray());
        foreach (var prior in priorFibers) try { await prior.Fiber.WaitAsync(); } catch when (prior.Failed) { }
        return failures;
    }
}
