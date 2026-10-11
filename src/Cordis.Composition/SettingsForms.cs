using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>The ordinary Settings service consumed by the standard Remote controller.</summary>
public interface ISettingsProvider
{
    /// <summary>Read one coherent redacted form snapshot.</summary>
    Task<SettingsDescribeSnapshot> DescribeAsync();

    /// <summary>Merge editable fields in one configuration commit.</summary>
    Task UpdateAsync(string ns, JsonElement patch, [DisallowNull] double? expectedRevision = null!);

    /// <summary>Reset live fields against inheritance, then merge the supplied section. Ordinary configuration is retained.</summary>
    Task ReplaceAsync(string ns, JsonElement section, [DisallowNull] double? expectedRevision = null!);

    /// <summary>Apply ordered form edits in one configuration commit.</summary>
    Task MutateAsync(string ns, IReadOnlyList<SettingsPathOp> ops, [DisallowNull] double? expectedRevision = null!);
}

/// <summary>One JSON form edit. Set requires a value, including explicit null; unset omits it.</summary>
/// <param name="Op">Either set or unset.</param>
/// <param name="Path">Object keys or canonical array indexes. An empty path addresses the live form.</param>
public sealed record SettingsPathOp(string Op, IReadOnlyList<string> Path)
{
    /// <summary>The set value. Undefined denotes an omitted field, independently of JSON null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Value
    {
        get;
        init;
    }
}

/// <summary>A refusal to overwrite an observed form that has changed.</summary>
public sealed class SettingsConflictException(string ns, double expected, double actual) : Exception(
    $"settings namespace \"{ns}\" changed since it was read (expected revision {expected}, now {actual})")
{
    /// <summary>The addressed namespace.</summary>
    public string Namespace
    {
        get;
    } = ns;

    /// <summary>The caller's prior observation.</summary>
    public double Expected
    {
        get;
    } = expected;

    /// <summary>The current observation in this Settings owner.</summary>
    public double Actual
    {
        get;
    } = actual;
}

/// <summary>Schema-derived live forms. Observation state belongs to this service; configuration remains owned by the profile editor.</summary>
public sealed class SettingsForms : Service, ISettingsProvider
{
    private readonly State state;

    /// <summary>Mount one Settings owner over an existing profile editor. The optional policy restricts host visibility; otherwise all declared live fields are offered.</summary>
    public SettingsForms(
        Context context,
        PluginConfigurationOperations operations,
        Func<string, SettingsPolicy>? policy = null,
        bool writable = true,
        bool hasDocument = true) : base(context, "settings")
    {
        ArgumentNullException.ThrowIfNull(operations);
        state = new(operations, policy, writable, hasDocument);
        context.Effect(() =>
        {
            operations.Changed += Invalidate;
            return () =>
            {
                state.Closed = true;
                operations.Changed -= Invalidate;
            };
        });
        context.Events.On(
            "app-boot/config-reload",
            (_, _) =>
            {
                Invalidate("reload");
                return null;
            });
    }

    private SettingsForms(SettingsForms provider, Context caller) : base(provider, caller) => state = provider.state;

    /// <summary>Share observation state while retaining the caller's Context for instance policy.</summary>
    protected override Service CreateView(Context caller) => new SettingsForms(this, caller);

    /// <summary>Whether this owner accepts form writes.</summary>
    public bool Writable => state.Writable;

    /// <summary>Whether the host owns a local configuration document.</summary>
    public bool HasDocument => state.HasDocument;

    /// <summary>Register one plugin instance's page policy. Policy changes notify without changing its configuration revision.</summary>
    public Action Configure(bool autoGenerate = true, Fiber? owner = null)
    {
        Context.VerifyAccess();
        var fiber = owner ?? Context.Fiber;
        if (!state.Presentations.TryAdd(fiber, autoGenerate))
            throw new InvalidOperationException(
                "Settings presentation is already configured for this plugin instance.");
        Invalidate("presentation");
        var active = true;
        return () =>
        {
            Context.VerifyAccess();
            if (!active)
                return;
            active = false;
            state.Presentations.Remove(fiber);
            Invalidate("presentation");
        };
    }

    /// <inheritdoc />
    public Task<SettingsDescribeSnapshot> DescribeAsync()
    {
        ObjectDisposedException.ThrowIf(state.Closed, this);
        return state.Operations.ReadSettingsDescriptionAsync(this);
    }

    /// <inheritdoc />
    public Task UpdateAsync(string ns, JsonElement patch, [DisallowNull] double? expectedRevision = null!) =>
        WriteAsync(new SettingsUpdate(this, ns, expectedRevision, patch.Clone()));

    /// <inheritdoc />
    public Task ReplaceAsync(string ns, JsonElement section, [DisallowNull] double? expectedRevision = null!) =>
        WriteAsync(new SettingsReplace(this, ns, expectedRevision, section.Clone()));

    /// <inheritdoc />
    public Task MutateAsync(
        string ns,
        IReadOnlyList<SettingsPathOp> ops,
        [DisallowNull] double? expectedRevision = null!)
    {
        ArgumentNullException.ThrowIfNull(ops);
        var detached = ops
            .Select(op => op with
            {
                Path = op.Path.ToArray(),
                Value = op.Value.ValueKind == JsonValueKind.Undefined ? default : op.Value.Clone()
            })
            .ToArray();
        return WriteAsync(new SettingsMutate(this, ns, expectedRevision, detached));
    }

    private async Task WriteAsync(SettingsChange change)
    {
        ObjectDisposedException.ThrowIf(state.Closed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Namespace);
        if (!Writable)
            throw new InvalidOperationException("The settings profile is read-only.");
        if (change.Expected is { } expected && !double.IsFinite(expected))
            throw new ArgumentOutOfRangeException(nameof(change), "Expected revision must be a finite number.");
        await state.Operations.WriteSettingsAsync(change);
    }

    internal SettingsPolicy Policy(Entry entry) => state.Policy?.Invoke(entry.Options.Id) ??
        new(entry.Fiber!.ConfigDescription!.Properties.Keys);

    internal bool AutoGenerate(Entry entry) => state.Presentations.GetValueOrDefault(entry.Fiber!, true);

    internal double Observe(Entry entry, string fingerprint)
    {
        var auto = AutoGenerate(entry);
        state.Revisions.TryGetValue(entry.Id, out var previous);
        var revision = previous is null ? 0 : previous.Revision + (previous.Fingerprint == fingerprint ? 0 : 1);
        state.Revisions[entry.Id] = new(fingerprint, revision, entry.Options.Id, auto);
        if (previous is null || previous.Fingerprint != fingerprint || previous.AutoGenerate != auto)
            Provider.Events.Emit("settings/document-updated", entry.Options.Id, revision);
        return revision;
    }

    internal void ObserveUnavailable(IReadOnlySet<string> active)
    {
        foreach (var (id, prior) in state.Revisions.ToArray())
        {
            if (active.Contains(id) || prior.Fingerprint is null)
                continue;
            var next = prior with
            {
                Fingerprint = null,
                Revision = prior.Revision + 1
            };
            state.Revisions[id] = next;
            Provider.Events.Emit("settings/document-updated", prior.Namespace, next.Revision);
        }
    }

    private void Invalidate(string reason)
    {
        if (state.Closed || Interlocked.Exchange(ref state.Scheduled, 1) != 0)
            return;
        if (ExecutionContext.IsFlowSuppressed())
            Schedule();
        else
        {
            using var flow = ExecutionContext.SuppressFlow();
            Schedule();
        }

        void Schedule() => _ = Task.Run(async () =>
        {
            Interlocked.Exchange(ref state.Scheduled, 0);
            if (state.Closed)
                return;
            try
            {
                await DescribeAsync();
            }
            catch (Exception error)
            {
                if (state.Closed)
                    return;
                try
                {
                    await Provider.RunAsync(context =>
                    {
                        context.Logger.Warn("Settings observation failed: " + error.Message);
                        return Task.CompletedTask;
                    });
                }
                catch
                {
                    /* An observation diagnostic cannot become an unobserved task failure. */
                }
            }
        });
    }

    private sealed record Observation(string? Fingerprint, double Revision, string Namespace, bool AutoGenerate);

    private sealed class State(
        PluginConfigurationOperations operations,
        Func<string, SettingsPolicy>? policy,
        bool writable,
        bool hasDocument)
    {
        internal readonly PluginConfigurationOperations Operations = operations;
        internal readonly Func<string, SettingsPolicy>? Policy = policy;
        internal readonly bool Writable = writable;
        internal readonly bool HasDocument = hasDocument;
        internal readonly Dictionary<string, Observation> Revisions = new(StringComparer.Ordinal);
        internal readonly Dictionary<Fiber, bool> Presentations = new(ReferenceEqualityComparer.Instance);
        internal volatile bool Closed;
        internal int Scheduled;
    }
}

internal abstract record SettingsChange(SettingsForms Owner, string Namespace, double? Expected);

internal sealed record SettingsUpdate(SettingsForms Owner, string Namespace, double? Expected, JsonElement Patch)
    : SettingsChange(Owner, Namespace, Expected);

internal sealed record SettingsReplace(SettingsForms Owner, string Namespace, double? Expected, JsonElement Section)
    : SettingsChange(Owner, Namespace, Expected);

internal sealed record SettingsMutate(
    SettingsForms Owner,
    string Namespace,
    double? Expected,
    IReadOnlyList<SettingsPathOp> Operations) : SettingsChange(Owner, Namespace, Expected);
