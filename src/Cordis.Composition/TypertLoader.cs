namespace Cordis.Composition;

/// <summary>Resolve an explicitly exported native Typert artifact for a Loader module request.</summary>
/// <remarks>The adapter owns package resolution; a null result means that the module does not contribute Typert artifacts.</remarks>
public interface ITypertArtifactResolver
{
    /// <summary>Resolve the contribution using the composition's import anchor.</summary>
    ValueTask<TypertContribution?> ResolveAsync(
        string specifier,
        Uri baseUri,
        CancellationToken cancellationToken = default);
}

/// <summary>AOT-compatible artifact routing through explicitly registered native factories.</summary>
public sealed class StaticTypertArtifactResolver : ITypertArtifactResolver
{
    private readonly Dictionary<string, Func<TypertContribution>> factories = new(StringComparer.Ordinal);

    /// <summary>Register one exact module request's exported artifact factory.</summary>
    public void Register(string specifier, Func<TypertContribution> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specifier);
        ArgumentNullException.ThrowIfNull(factory);
        factories.Add(specifier, factory);
    }

    /// <summary>Resolve an exact registered request without assembly reflection.</summary>
    public ValueTask<TypertContribution?> ResolveAsync(
        string specifier,
        Uri baseUri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(factories.TryGetValue(specifier, out var factory) ? factory() : null);
    }
}

/// <summary>Discover and reconcile generated artifacts against live Loader entries.</summary>
/// <remarks>One contribution per exact module request is owned by this loader's Fiber. Multiple live entries share it;
/// removal of the final entry withdraws it. Resolution results are cached only for this activation, matching the pinned loader's restart boundary.</remarks>
public sealed class TypertLoader
{
    private readonly Context context;
    private readonly Loader loader;
    private readonly TypertRegistry registry;
    private readonly ITypertArtifactResolver resolver;
    private readonly HashSet<string> configured;
    private readonly Dictionary<string, EffectHandle> registered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<TypertContribution?>> artifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> pending = new(StringComparer.Ordinal);
    private readonly HashSet<string> dirty = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken cancellationToken;
    private Task? queued;
    private bool active = true;

    private TypertLoader(
        Context context,
        Loader loader,
        TypertRegistry registry,
        ITypertArtifactResolver resolver,
        IEnumerable<string> packages)
    {
        this.context = context;
        this.loader = loader;
        this.registry = registry;
        this.resolver = resolver;
        configured = new(packages, StringComparer.Ordinal);
        cancellationToken = lifetime.Token;
        context.Effect(
            () => new Cleanup(async () =>
            {
                active = false;
                dirty.Clear();
                try
                {
                    lifetime.Cancel();
                }
                catch (Exception error)
                {
                    Report(error);
                }
                finally
                {
                    lifetime.Dispose();
                }

                foreach (var registration in registered.Values.Reverse().ToArray())
                {
                    try
                    {
                        await registration.DisposeAsync();
                    }
                    catch (Exception error)
                    {
                        Report(error);
                    }
                }

                registered.Clear();
                artifacts.Clear();
            }),
            "typert loader lifetime");
        context.On(
            "internal/plugin",
            (_, args) =>
            {
                if (args[0] is not Fiber fiber ||
                    !fiber.Parent.Metadata.TryGetValue(Entry.MetadataKey, out var value) || value is not Entry entry)
                    return null;
                dirty.Add(entry.Options.Name);
                if (queued is null)
                    queued = FlushAfterTurnAsync();
                return null;
            },
            new EventOptions(Global: true));
    }

    /// <summary>Subscribe before scanning current entries. An invalid initial contributor fails activation loudly.</summary>
    public static async Task<TypertLoader> StartAsync(
        Context context,
        Loader loader,
        TypertRegistry registry,
        ITypertArtifactResolver resolver,
        IEnumerable<string>? packages = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(resolver);
        var result = new TypertLoader(context, loader, registry, resolver, packages ?? []);
        foreach (var package in result.configured)
            result.dirty.Add(package);
        foreach (var entry in loader.Entries())
            result.dirty.Add(entry.Options.Name);
        var failures = new List<Exception>();
        await Task.WhenAll(result.Flush(failures.Add));
        if (failures.Count != 0)
            throw new AggregateException("Typert initial contributors failed to register.", failures);
        return result;
    }

    /// <summary>Wait for currently queued entry reconciliation and imports to settle.</summary>
    public async Task WaitForIdleAsync()
    {
        while (active && (queued is not null || pending.Count != 0 || dirty.Count != 0))
        {
            if (queued is { } flush)
                await flush;
            await Task.WhenAll(pending.Values.Select(task => ObserveAsync(task, Report)).ToArray());
            if (dirty.Count != 0)
                await Task.WhenAll(Flush(Report));
        }
    }

    private async Task FlushAfterTurnAsync()
    {
        await Task.Yield();
        queued = null;
        if (active)
            await Task.WhenAll(Flush(Report));
    }

    private IReadOnlyList<Task> Flush(Action<Exception> onError)
    {
        var tasks = new List<Task>();
        foreach (var name in dirty.ToArray())
        {
            dirty.Remove(name);
            try
            {
                var task = ProcessOne(name);
                if (task is not null)
                    tasks.Add(ObserveAsync(task, onError));
            }
            catch (Exception error)
            {
                onError(error);
            }
        }

        return tasks;
    }

    private Task? ProcessOne(string name)
    {
        if (!Qualifies(name))
        {
            if (registered.Remove(name, out var registration))
                return registration.DisposeAsync().AsTask();
            return null;
        }

        if (registered.ContainsKey(name) || pending.ContainsKey(name))
            return null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add(name, completion.Task);
        _ = LoadAndRegisterAsync(name, completion);
        return completion.Task;
    }

    private async Task LoadAndRegisterAsync(string name, TaskCompletionSource completion)
    {
        try
        {
            if (!artifacts.TryGetValue(name, out var loading))
            {
                loading = resolver.ResolveAsync(name, loader.BaseUri, cancellationToken).AsTask();
                artifacts.Add(name, loading);
            }

            var contribution = await loading;
            if (!active || !Qualifies(name) || registered.ContainsKey(name))
            {
                completion.TrySetResult();
                return;
            }

            if (contribution is null)
            {
                if (configured.Contains(name))
                    throw new InvalidOperationException($"Configured Typert package '{name}' exports no artifact.");
            }
            else
                registered.Add(name, registry.Register(context, contribution));

            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (!active)
        {
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
        finally
        {
            pending.Remove(name);
        }
    }

    private bool Qualifies(string name) => configured.Contains(name) || loader
        .Entries()
        .Any(entry =>
            entry.Options.Name == name && entry.Fiber is { Uid: not null } && !entry.Disabled);

    private void Report(Exception error)
    {
        try
        {
            context.Logger.Error("Typert entry reconciliation failed: " + error);
        }
        catch (Exception)
        {
            /* Diagnostics cannot prevent other contributors from reconciling. */
        }
    }

    private static async Task ObserveAsync(Task task, Action<Exception> report)
    {
        try
        {
            await task;
        }
        catch (Exception error)
        {
            report(error);
        }
    }

    private sealed class Cleanup(Func<Task> cleanup) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(cleanup());
    }
}
