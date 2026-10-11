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
/// removal of the final entry withdraws it. Resolution results are cached for this activation unless the caller
/// explicitly suspends selected requests at a native module-generation boundary.</remarks>
public sealed class TypertLoader
{
    private readonly Context context;
    private readonly Loader loader;
    private readonly TypertRegistry registry;
    private readonly ITypertArtifactResolver resolver;
    private readonly HashSet<string> configured;
    private readonly Dictionary<string, EffectHandle> registered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<TypertContribution?>> artifacts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Import> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> generations = new(StringComparer.Ordinal);
    private readonly HashSet<string> suspended = new(StringComparer.Ordinal);
    private readonly HashSet<string> mutating = new(StringComparer.Ordinal);
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
                foreach (var import in pending.Values)
                    import.Completion.TrySetResult();
                pending.Clear();
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
            await Task.WhenAll(pending.Values.Select(import => ObserveAsync(import.Completion.Task, Report)).ToArray());
            if (dirty.Count != 0)
                await Task.WhenAll(Flush(Report));
        }
    }

    /// <summary>Withdraw selected exact Loader requests and invalidate their cached artifacts and pending imports.</summary>
    /// <remarks>Call after candidate preparation and before retiring affected plugins. Unfinished resolver code is
    /// detached, not awaited or forcibly stopped. Same-request reentry from registration or withdrawal observers
    /// throws InvalidOperationException.</remarks>
    public Task SuspendAsync(IEnumerable<string> specifiers)
    {
        var names = SelectNames(specifiers);
        return context.RunAsync(_ =>
        {
            EnsureLifecycleAccess(names);
            return SuspendCoreAsync(names);
        });
    }

    /// <summary>Reload selected exact Loader requests after the caller confirms route commit or successful recovery.</summary>
    /// <remarks>Registration failures propagate and withdraw the selected batch. A concurrent suspension invalidates
    /// this operation; it cannot revive a newer generation. Same-request observer reentry throws InvalidOperationException.</remarks>
    public Task ResumeAsync(IEnumerable<string> specifiers)
    {
        var names = SelectNames(specifiers);
        return context.RunAsync(async owner =>
        {
            EnsureLifecycleAccess(names);
            var captured = names.ToDictionary(name => name, Generation, StringComparer.Ordinal);
            foreach (var name in names)
            {
                suspended.Remove(name);
                dirty.Remove(name);
            }

            try
            {
                var tasks = new List<Task>();
                foreach (var name in names)
                    if (ProcessOne(name) is { } task)
                        tasks.Add(task);
                // Observe every completion even when one failure closes and detaches the remaining imports.
                _ = ObserveAsync(
                    Task.WhenAll(tasks),
                    static _ =>
                    {
                    });
                var remaining = new List<Task>(tasks);
                while (remaining.Count != 0)
                {
                    var settled = await Task.WhenAny(remaining);
                    remaining.Remove(settled);
                    await settled;
                }

                if (!active || names.Any(name => Generation(name) != captured[name] || suspended.Contains(name)))
                    throw new OperationCanceledException(
                        "Typert resume was superseded by another lifecycle operation.");
            }
            catch
            {
                try
                {
                    await SuspendCoreAsync(names.Where(name => Generation(name) == captured[name]).ToArray());
                }
                catch (Exception error)
                {
                    Report(error);
                }

                throw;
            }
        });
    }

    private async Task SuspendCoreAsync(IReadOnlyList<string> names)
    {
        var withdrawals = new List<(string Name, EffectHandle Registration)>();
        foreach (var name in names)
        {
            suspended.Add(name);
            generations[name] = Generation(name) + 1;
            artifacts.Remove(name);
            dirty.Remove(name);
            if (pending.Remove(name, out var import))
                import.Completion.TrySetResult();
            if (registered.Remove(name, out var registration))
                withdrawals.Add((name, registration));
        }

        mutating.UnionWith(names);
        Exception? primary = null;
        try
        {
            foreach (var (_, registration) in withdrawals)
            {
                try
                {
                    await registration.DisposeAsync();
                }
                catch (Exception error)
                {
                    primary ??= error;
                }
            }
        }
        finally
        {
            mutating.ExceptWith(names);
        }

        if (primary is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
    }

    private static string[] SelectNames(IEnumerable<string> specifiers)
    {
        ArgumentNullException.ThrowIfNull(specifiers);
        var names = specifiers.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var name in names)
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return names;
    }

    private void EnsureLifecycleAccess(IReadOnlyList<string> names)
    {
        if (!active)
            throw new InvalidOperationException("The Typert loader activation has ended.");
        if (names.Any(mutating.Contains))
            throw new InvalidOperationException(
                "Typert lifecycle operations cannot reenter a request's registration or withdrawal.");
    }

    private long Generation(string name) => generations.GetValueOrDefault(name);

    private bool IsCurrent(string name, Import import) =>
        active && Generation(name) == import.Generation && !suspended.Contains(name) &&
        ReferenceEquals(pending.GetValueOrDefault(name), import);

    private async Task WithdrawAsync(string name, EffectHandle registration)
    {
        mutating.Add(name);
        try
        {
            await registration.DisposeAsync();
        }
        finally
        {
            mutating.Remove(name);
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
                return WithdrawAsync(name, registration);
            return null;
        }

        if (registered.ContainsKey(name))
            return null;
        if (pending.TryGetValue(name, out var existing))
            return existing.Completion.Task;
        var import = new Import(Generation(name));
        pending.Add(name, import);
        _ = LoadAndRegisterAsync(name, import);
        return import.Completion.Task;
    }

    private async Task LoadAndRegisterAsync(string name, Import import)
    {
        var completion = import.Completion;
        try
        {
            if (!artifacts.TryGetValue(name, out var loading))
            {
                // Loader imports its builtins directly, without a module/artifact resolver.
                loading = name.StartsWith("cordis:", StringComparison.Ordinal) && loader.Builtins.ContainsKey(name[7..])
                    ? Task.FromResult<TypertContribution?>(null)
                    : resolver.ResolveAsync(name, loader.BaseUri, cancellationToken).AsTask();
                if (IsCurrent(name, import))
                    artifacts.Add(name, loading);
            }

            var contribution = await loading;
            if (!IsCurrent(name, import) || !Qualifies(name) || registered.ContainsKey(name))
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
            {
                EffectHandle registration;
                mutating.Add(name);
                try
                {
                    registration = registry.Register(context, contribution);
                }
                finally
                {
                    mutating.Remove(name);
                }

                if (IsCurrent(name, import))
                    registered.Add(name, registration);
                else
                    await WithdrawAsync(name, registration);
            }

            completion.TrySetResult();
        }
        catch (OperationCanceledException) when (!active)
        {
            completion.TrySetResult();
        }
        catch (Exception error)
        {
            if (IsCurrent(name, import))
                completion.TrySetException(error);
            else
                completion.TrySetResult();
        }
        finally
        {
            if (ReferenceEquals(pending.GetValueOrDefault(name), import))
                pending.Remove(name);
        }
    }

    private bool Qualifies(string name) => !suspended.Contains(name) && (configured.Contains(name) || loader
        .Entries()
        .Any(entry =>
            entry.Options.Name == name && entry.Fiber is { Uid: not null } && !entry.Disabled));

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

    private sealed class Import(long generation)
    {
        public long Generation
        {
            get;
        } = generation;

        public TaskCompletionSource Completion
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
