using Cordis.Composition;

namespace Cordis.Extensions;

/// <summary>A running profile owning its context, loader and watchers, with explicit module resolution and optional configuration HMR.</summary>
/// <remarks>The caller retains ownership of the resolver and must keep it available until session disposal finishes.</remarks>
public sealed class ProfileSession : IAsyncDisposable
{
    private readonly HmrCoordinator queue = new();
    private readonly ProfileLaunch launch;
    private readonly IReadOnlySet<string>? required;
    private readonly Action<Exception>? diagnostic;
    private readonly Dictionary<string, IAsyncDisposable> watches = new(PathComparer);
    private readonly Dictionary<string, string?> successfulInputs = new(PathComparer);
    private string successfulMappings;
    private Dictionary<string, string> acceptedInstallation;
    private Dictionary<string, string> acceptedLocal;
    private readonly DeploymentPackageResolver? packages;
    private readonly Func<Task<DeploymentGeneration>>? refreshDeployment;
    private readonly string[] profilePaths;
    private string? successfulProfileInputs;
    private Exception? retirementFailure;
    private bool disposed;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private ProfileSession(
        ProfileLaunch launch,
        IModuleResolver resolver,
        bool enableHmr,
        IReadOnlySet<string>? required,
        Action<Exception>? diagnostic,
        Func<Task<DeploymentGeneration>>? refreshDeployment)
    {
        // Command-line overlays belong to the launch, not subsequent caller mutations.
        this.launch = launch with
        {
            Overlays = launch
                .Overlays.Select(layer => layer with
                {
                    Patches = ProfileComposition.Flatten([layer])
                })
                .ToArray()
        };
        this.required = required;
        this.diagnostic = diagnostic;
        successfulMappings = MappingSnapshot();
        acceptedInstallation = new(launch.InstallationBundles, StringComparer.Ordinal);
        acceptedLocal = new(launch.LocalBundles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        packages = (resolver as DeploymentModuleResolver)?.Packages;
        this.refreshDeployment = refreshDeployment;
        if (refreshDeployment is not null && packages is null)
            throw new ArgumentException(
                "A deployment refresh requires a DeploymentModuleResolver.",
                nameof(refreshDeployment));
        profilePaths =
        [
            Path.Combine(launch.Profile.Directory, "package.json"),
            launch.Profile.UserLayer.Source,
            Path.Combine(launch.Home, "cordis.patch.yml"),
            .. launch.RuntimeIdentity is null
                ? Array.Empty<string>()
                : [Path.Combine(launch.Profile.Directory, DshProfilePolicy.CompatibilityFilename)]
        ];
        Context = new Context(Report);
        Hmr = enableHmr ? queue : null;
        queue.Error += Report;
        queue.Warning += LogWarning;
    }

    /// <summary>
    /// The session-owned application context. Disposing it also stops session background work.
    /// </summary>
    public Context Context
    {
        get;
    }

    /// <summary>
    /// The session-owned loader using the caller's resolver.
    /// </summary>
    public Loader Loader
    {
        get;
        private set;
    } = null!;

    /// <summary>
    /// The mounted root include reconciled by this session.
    /// </summary>
    public Include Include
    {
        get;
        private set;
    } = null!;

    /// <summary>Profile mutations sharing this session's lifecycle queue and refresh ownership.</summary>
    public PluginConfigurationOperations ConfigurationOperations
    {
        get;
        private set;
    } = null!;

    /// <summary>Null when automatic watching was not requested. Manual refresh remains available.</summary>
    public HmrCoordinator? Hmr
    {
        get;
    }

    /// <summary>
    /// Whether the latest refresh requires a host restart. A successful subsequent refresh clears this flag.
    /// </summary>
    public bool RequiresRestart
    {
        get;
        private set;
    }

    /// <summary>
    /// The most recently reported failure, cleared by a successful refresh. Retaining plugin exceptions can delay CLR unload.
    /// </summary>
    public Exception? LastError
    {
        get;
        private set;
    }

    /// <summary>
    /// UTC time of successful startup or the latest successful refresh.
    /// </summary>
    public DateTimeOffset? LastSuccessfulRefresh
    {
        get;
        private set;
    }

    /// <summary>
    /// Runs synchronously after a successful refresh inside the lifecycle queue. Handlers must not synchronously re-enter that queue.
    /// </summary>
    public event Action? Refreshed;

    /// <summary>
    /// Runs synchronously when reconciliation requires restart, inside the lifecycle queue.
    /// </summary>
    public event Action? RestartRequired;

    /// <summary>
    /// Reports failures synchronously on the emitting operation. Handler exceptions are ignored to preserve the primary failure.
    /// </summary>
    public event Action<Exception>? Error;

    /// <summary>The latest manifest selection, including skipped bundles.</summary>
    public IReadOnlyList<string> SelectedBundles
    {
        get;
        private set;
    } = [];

    /// <summary>Bundles that contributed layers to the latest successful composition.</summary>
    public IReadOnlyList<string> LoadedBundles
    {
        get;
        private set;
    } = [];

    /// <summary>Current bundle failures, in manifest order.</summary>
    public IReadOnlyList<SkippedBundle> SkippedBundles
    {
        get;
        private set;
    } = [];

    /// <summary>
    /// Reports warnings synchronously on the emitting operation. Handler exceptions are ignored.
    /// </summary>
    public event Action<string>? Warning;

    /// <summary>Mount current bundle/profile/home/launch layers; no resolver or package manager is created implicitly.</summary>
    public static async Task<ProfileSession> StartAsync(
        string configurationPath,
        ProfileLaunch launch,
        IModuleResolver resolver,
        IExpressionEvaluator? evaluator = null,
        bool enableHmr = false,
        IReadOnlySet<string>? required = null,
        Func<Context, Task>? prepare = null,
        Action<Exception>? diagnostic = null,
        Func<Task<DeploymentGeneration>>? refreshDeployment = null,
        Task<bool>? applicationReady = null)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(resolver);
        var session = new ProfileSession(launch, resolver, enableHmr, required, diagnostic, refreshDeployment);
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.queue.ApplicationReady = WaitForReadinessAsync(ready.Task, applicationReady);
        try
        {
            var inputs = await session.ProfileInputSnapshotAsync();
            var refresh = await ProfileComposition.RefreshAsync(session.launch);
            await session.Context.RunAsync(async ctx =>
            {
                // External Context disposal must close the session's background watchers too.
                ctx.Effect(() => session.queue);
                ctx.Provide("profileContext", session.launch);
                var homePaths = new DshHomePaths(session.launch.Home);
                ctx.Provide("dshHomePath", (DshHomePath)homePaths.PathOf);
                if (session.Hmr is not null)
                    ctx.Provide("hmr", session.Hmr);
                session.Loader = new Loader(
                    ctx,
                    resolver,
                    new Uri(Path.GetFullPath(configurationPath)),
                    evaluator,
                    session.Report);
                if (prepare is not null)
                    await prepare(ctx);
            });
            session.Include = session.launch.RuntimeIdentity is { } runtime
                ? await DshProfilePolicy.MountAsync(
                    session.Loader,
                    configurationPath,
                    session.launch.Profile.Directory,
                    runtime,
                    ProfileComposition.Flatten(refresh.Layers),
                    session.packages,
                    session.launch.CompatibilityWarning,
                    session.launch.ManifestLocator,
                    session.launch.CompatibilityPackageName)
                : await ApplicationBoot.MountAsync(
                    session.Loader,
                    configurationPath,
                    ProfileComposition.Flatten(refresh.Layers));
            await session.AcceptBundlesAsync(refresh);
            session.ConfigurationOperations = new(session.launch, session.Include)
            {
                RunExclusiveAsync = session.queue.RunExclusiveAsync,
                ReconcileAsync = async requiredEntries =>
                {
                    var result = await session.RefreshCoreAsync(requiredEntries);
                    if (result.RequiresRestart)
                        throw new DeploymentRestartRequiredException(
                            "The profile deployment requires a host restart before configuration can be reconciled.",
                            result.RestartReason ?? result.Pending[0].Reason);
                    return result.Warnings;
                },
                ReconcileCandidateAsync = async (candidate, requiredEntries) =>
                {
                    var result = await session.RefreshCoreAsync(requiredEntries, candidate);
                    if (result.RequiresRestart)
                        throw new DeploymentRestartRequiredException(
                            "The profile deployment requires a host restart before configuration can be reconciled.",
                            result.RestartReason ?? result.Pending[0].Reason);
                    return result.Warnings;
                },
            };
            await session.Context.RunAsync(ctx =>
            {
                ctx.Effect(() => new PackageShutdown(session), "profile package operations");
                return Task.CompletedTask;
            });
            await session.WarnInactiveAsync(await ApplicationBoot.AuditAsync(session.Loader, required));
            session.successfulProfileInputs = inputs;
            await session.SynchronizeWatchesAsync();
            session.LastSuccessfulRefresh = DateTimeOffset.UtcNow;
            ready.TrySetResult(true);
            return session;
        }
        catch
        {
            ready.TrySetResult(false);
            await session.DisposeAsync();
            throw;
        }
    }

    private static async Task<bool> WaitForReadinessAsync(Task<bool> boot, Task<bool>? application) =>
        await boot.ConfigureAwait(false) && (application is null || await application.ConfigureAwait(false));

    /// <summary>Serialize an explicit refresh with automatic configuration and code reloads.</summary>
    public async Task<ProfileReloadResult> RefreshAsync()
    {
        var result = new ProfileReloadResult([], []);
        await queue.RunExclusiveAsync(async () => result = await RefreshCoreAsync());
        return result;
    }

    private async Task<ProfileReloadResult> RefreshCoreAsync(
        IReadOnlySet<string>? editedEntries = null,
        ProfileCandidate? candidate = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            if (retirementFailure is not null)
                throw new InvalidOperationException(
                    "A previous profile retirement did not complete; a host restart is required before retrying.",
                    retirementFailure);
            var priorEntries = new Dictionary<string, EntrySnapshot>(StringComparer.Ordinal);
            await Context.RunAsync(_ =>
            {
                foreach (var entry in Loader.Entries())
                    priorEntries.Add(
                        entry.Id,
                        new(
                            entry,
                            entry.Fiber,
                            entry.Fiber?.State,
                            ConfigurationFile.Write(entry.Options, true),
                            entry.LastError));
                return Task.CompletedTask;
            });
            var priorFailures = await ApplicationBoot.AuditAsync(Loader);
            var inputs = await ProfileInputSnapshotAsync();
            var mappings = MappingSnapshot();
            var refresh = candidate?.Composition ?? await ProfileComposition.RefreshAsync(launch);
            var patches = ProfileComposition.Flatten(refresh.Layers);
            var configurationChanged = candidate is not null || inputs != successfulProfileInputs ||
                mappings != successfulMappings ||
                ConfigurationFile.Write(patches, true) != ConfigurationFile.Write(Include.Options.Patches ?? [], true);
            var requiredEntries = new HashSet<string>(required ?? new HashSet<string>(), StringComparer.Ordinal);
            if (editedEntries is not null)
                requiredEntries.UnionWith(editedEntries);
            requiredEntries.UnionWith(NewBundleRequiredIds(refresh));
            DeploymentGeneration? successor = null;
            try
            {
                if (refreshDeployment is not null)
                {
                    successor = await refreshDeployment();
                    packages!.ValidateReplacement(successor);
                    configurationChanged |= !SameGeneration(packages.Generation, successor);
                }
                else if (MappingRestartReason() is { } reason)
                {
                    return await RequestRestartAsync(reason);
                }
            }
            catch (DeploymentRestartRequiredException error)
            {
                return await RequestRestartAsync(error.Reason);
            }

            if (successor is not null)
            {
                var basis = candidate is null
                    ? ConfigurationFile.ParseEntries(
                        await File.ReadAllTextAsync(Include.Filename),
                        Path.GetExtension(Include.Filename) == ".json")
                    : ConfigurationFile.ParseEntries(candidate.ConfigurationJson, true);
                var rows = candidate is null ? EntryPatches.Apply(basis, patches) : basis;
                var stopped = new List<StoppedRow>();
                var cleanupFailed = false;
                try
                {
                    await StopDroppedRowsAsync(rows, stopped, () => cleanupFailed = true);
                    packages!.Replace(successor);
                }
                catch (Exception error)
                {
                    if (cleanupFailed)
                        retirementFailure = error;
                    else
                        await RestoreRowsAsync(stopped);
                    if (error is DeploymentRestartRequiredException restart)
                        return await RequestRestartAsync(restart.Reason);
                    throw;
                }
            }

            if (configurationChanged)
            {
                _ = candidate is null
                    ? await ApplicationBoot.ReconcileAsync(Include, patches, requiredEntries)
                    : await ApplicationBoot.ReconcileAsync(Include, candidate, requiredEntries);
                await AcceptBundlesAsync(refresh);
                successfulProfileInputs = candidate is null ? inputs : null;
                successfulMappings = mappings;
                acceptedInstallation = new(launch.InstallationBundles, StringComparer.Ordinal);
                acceptedLocal = new(launch.LocalBundles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            }

            foreach (var include in candidate is null ? await IncludesAsync() : Array.Empty<Include>())
            {
                var text = await File.ReadAllTextAsync(include.Filename);
                if (successfulInputs.TryGetValue(include.Filename, out var previous) && text == previous)
                    continue;
                // Include contains refresh errors; validate structure here so a rejected input is
                // never recorded as successful and callers can observe/retry that same input.
                ConfigurationFile.ParseEntries(text, Path.GetExtension(include.Filename) == ".json");
                await include.RefreshAsync();
                successfulInputs[include.Filename] = text;
                configurationChanged = true;
            }

            var warnings = await RetryFailedImportsAsync(priorEntries, requiredEntries);
            await JudgeRefreshAsync(priorEntries, priorFailures, warnings);
            await WarnInactiveAsync(warnings);
            await SynchronizeWatchesAsync();
            RequiresRestart = false;
            LastError = null;
            LastSuccessfulRefresh = DateTimeOffset.UtcNow;
            if (configurationChanged)
                await Context.RunAsync(context =>
                {
                    context.Events.Emit("app-boot/config-reload");
                    return Task.CompletedTask;
                });
            Refreshed?.Invoke();
            return new(warnings, []);
        }
        catch (Exception error)
        {
            // The watcher queue reports failures; direct callers also receive the exception.
            LastError = error;
            throw;
        }
    }

    private sealed record StoppedRow(EntryGroup Group, EntryOptions Options, int Index);

    private sealed record EntrySnapshot(Entry Entry, Fiber? Fiber, FiberState? State, string Options, Exception? Error);

    private async Task StopDroppedRowsAsync(
        IReadOnlyList<EntryOptions> rows,
        List<StoppedRow> stopped,
        Action cleanupFailed)
    {
        await Context.RunAsync(async _ =>
        {
            var listed = RowIds(rows);
            var dropped = Include.Store.Values.Where(entry => !listed.Contains(entry.Options.Id)).ToHashSet();
            var topmost = dropped
                .Where(entry =>
                {
                    for (var owner = entry.Parent.Owner;owner is not null;owner = owner.Parent.Owner)
                        if (dropped.Contains(owner))
                            return false;
                    return true;
                })
                .ToArray();
            // Capture every original position before any removal shifts another row's index.
            var snapshots = topmost
                .Select(entry => (Entry: entry, Snapshot: new StoppedRow(
                    entry.Parent,
                    Data.Entries(Data.Clone(new[] { entry.Options }))[0],
                    entry.Parent.Data.IndexOf(entry.Options))))
                .ToArray();
            var droppedFibers = dropped.Select(entry => entry.Fiber).OfType<Fiber>().ToHashSet();
            var fibers = Loader
                .Entries()
                .Select(entry => entry.Fiber)
                .OfType<Fiber>()
                .Distinct()
                .Select(fiber => (Fiber: fiber, Failed: fiber.State is FiberState.Failed or FiberState.Disposed,
                    PriorErrors: droppedFibers.Contains(fiber) ? 0 : fiber.CleanupErrors.Count))
                .ToArray();
            try
            {
                foreach (var (entry, snapshot) in snapshots)
                {
                    stopped.Add(snapshot);
                    await entry.Parent.RemoveAsync(entry.Options.Id);
                }

                foreach (var (fiber, failed, _) in fibers)
                    try
                    {
                        await fiber.WaitAsync();
                    }
                    catch when (failed)
                    {
                    }

                await Loader.WaitAsync();
                var errors = fibers
                    .SelectMany(item => item.Fiber.CleanupErrors.Skip(item.PriorErrors))
                    .Distinct()
                    .ToArray();
                if (errors.Length != 0)
                    throw new AggregateException("Profile row retirement did not complete.", errors);
            }
            catch
            {
                // A rejected disposer may still own effects. Recreating rows could overlap those effects.
                cleanupFailed();
                throw;
            }
        });
    }

    private async Task RestoreRowsAsync(IReadOnlyList<StoppedRow> stopped)
    {
        try
        {
            await Context.RunAsync(async _ =>
            {
                foreach (var row in stopped.OrderBy(row => row.Index))
                {
                    row.Group.Data.Insert(Math.Clamp(row.Index, 0, row.Group.Data.Count), row.Options);
                    await row.Group.CreateAsync(row.Options);
                }

                await Loader.WaitAsync();
                foreach (var warning in await ApplicationBoot.AuditAsync(Loader))
                    Report(new StartupException([warning]));
            });
        }
        catch (Exception recovery)
        {
            // Publication remains the primary failure even when old code cannot start again.
            Report(recovery);
        }
    }

    private static HashSet<string> RowIds(IEnumerable<EntryOptions> rows)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void Visit(IEnumerable<EntryOptions> entries)
        {
            foreach (var row in entries)
            {
                ids.Add(row.Id);
                if (row.Name is "cordis:group" || row.Group)
                    Visit(Data.Entries(row.Config));
            }
        }

        Visit(rows);
        return ids;
    }

    private static bool SameGeneration(DeploymentGeneration before, DeploymentGeneration after) =>
        before.ProfilesDirectory == after.ProfilesDirectory && before.ProfileDirectory == after.ProfileDirectory &&
        before.Entries.SequenceEqual(after.Entries) && before.LinkedRoots.SequenceEqual(after.LinkedRoots) &&
        before.LocalPackages.Count == after.LocalPackages.Count && before.LocalPackages.All(pair =>
            after.LocalPackages.TryGetValue(pair.Key, out var path) && pair.Value == path);

    private HashSet<string> NewBundleRequiredIds(ProfileRefresh refresh)
    {
        var requiredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in refresh.CurrentBundles.Except(SelectedBundles, StringComparer.Ordinal))
        {
            if (!launch.InstallationBundles.TryGetValue(name, out var directory) &&
                !(launch.LocalBundles?.TryGetValue(name, out directory) ?? false))
                continue;
            var sources = PackageManifest
                .Read(Path.Combine(directory!, "package.json"))
                .BundlePatchFiles
                .Select(path => Path.GetFullPath(Path.Combine(directory!, path)))
                .ToHashSet(PathComparer);
            foreach (var patch in refresh
                         .Layers.Where(layer => sources.Contains(layer.Source))
                         .SelectMany(layer => layer.Patches))
                if (patch.TryGetValue("insert", out var inserted) && Data.Truthy(inserted))
                    requiredIds.UnionWith(RowIds(Data.Entries(inserted)));
        }

        return requiredIds;
    }

    private async Task<IReadOnlyList<EntryDiagnostic>> RetryFailedImportsAsync(
        IReadOnlyDictionary<string, EntrySnapshot> priorEntries,
        IReadOnlySet<string>? requiredEntries)
    {
        await Context.RunAsync(async _ =>
        {
            foreach (var entry in Loader.Entries().Where(entry => entry.Fiber is null && entry.LastError is not null))
                if (priorEntries.TryGetValue(entry.Id, out var prior) && prior.Fiber is null &&
                    ReferenceEquals(prior.Entry, entry) && ReferenceEquals(prior.Error, entry.LastError))
                    await entry.RefreshAsync();
        });
        return await ApplicationBoot.AuditAsync(Loader, requiredEntries);
    }

    private async Task JudgeRefreshAsync(
        IReadOnlyDictionary<string, EntrySnapshot> priorEntries,
        IReadOnlyList<EntryDiagnostic> priorFailures,
        IReadOnlyList<EntryDiagnostic> failures)
    {
        EntryDiagnostic[] introduced = [];
        await Context.RunAsync(_ =>
        {
            introduced = failures
                .Where(failure =>
                {
                    if (!priorEntries.TryGetValue(failure.Id, out var snapshot) || snapshot.State == FiberState.Active)
                        return true;
                    var entry = Loader.Resolve(failure.Id);
                    return !ReferenceEquals(snapshot.Entry, entry) || !ReferenceEquals(snapshot.Fiber, entry.Fiber) ||
                        snapshot.State != entry.Fiber?.State ||
                        snapshot.Options != ConfigurationFile.Write(entry.Options, true) ||
                        !priorFailures.Any(old => old.Id == failure.Id && old.Module == failure.Module &&
                            old.State == failure.State && old.Error?.Message == failure.Error?.Message &&
                            old.Missing.SequenceEqual(failure.Missing));
                })
                .Select(failure => failure with
                {
                    Required = true
                })
                .ToArray();
            return Task.CompletedTask;
        });
        if (introduced.Length != 0)
            throw new StartupException(introduced);
    }

    private async Task<Include[]> IncludesAsync()
    {
        Include[] includes = [];
        await Context.RunAsync(_ =>
        {
            includes = Loader.Entries().Select(entry => entry.Subtree).OfType<Include>().Distinct().ToArray();
            return Task.CompletedTask;
        });
        return includes;
    }

    private async Task SynchronizeWatchesAsync()
    {
        if (Hmr is null || disposed)
            return;
        var paths = profilePaths
            .Concat((await IncludesAsync()).Select(include => include.Filename))
            .Select(HmrCoordinator.CanonicalPath)
            .ToHashSet(PathComparer);
        foreach (var path in watches.Keys.Where(path => !paths.Contains(path)).ToArray())
        {
            await watches[path].DisposeAsync();
            watches.Remove(path);
        }

        foreach (var path in paths)
            if (!watches.ContainsKey(path))
                watches.Add(path, queue.WatchConfig(path, () => RefreshCoreAsync()));
    }

    private async Task<string> ProfileInputSnapshotAsync()
    {
        var values = new List<object?>();
        for (var index = 0;index < profilePaths.Length;index++)
        {
            var path = profilePaths[index];
            try
            {
                var text = await File.ReadAllTextAsync(path);
                // DSH's manifest watcher reacts to bundle selection, not package-manager
                // dependency/lock bookkeeping. Resolution generation updates remain explicit.
                if (index == 0)
                {
                    var manifest = ConfigurationFile.Parse(text, true) as EntryOptions ??
                        throw new FormatException($"Manifest {path} must be a JSON object.");
                    values.Add(new PackageManifest(manifest).Bundles.ToList());
                }
                else
                    values.Add(text);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
            {
                values.Add(null);
            }
            catch (Exception error) when (path == Path.Combine(
                                              launch.Profile.Directory,
                                              DshProfilePolicy.CompatibilityFilename) &&
                                          error is IOException or UnauthorizedAccessException)
            {
                // Bad grant data must remain non-authorizing and non-rewritable, not prevent boot.
                // Admission emits the diagnostic; this marker makes unreadable/readable transitions refreshable.
                values.Add(
                    new EntryOptions
                    {
                        ["unreadableCompatibility"] = error.GetType().Name
                    });
            }
        }

        return ConfigurationFile.Write(values, true);
    }

    private string MappingSnapshot() =>
        ConfigurationFile.Write(
            new object?[]
            {
                launch
                    .InstallationBundles.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
                launch
                    .LocalBundles?.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
            },
            true);

    private string? MappingRestartReason()
    {
        static bool Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string>? after) =>
            before.Any(pair =>
                after is null || !after.TryGetValue(pair.Key, out var path) || !PathComparer.Equals(
                    Path.GetFullPath(pair.Value),
                    Path.GetFullPath(path)));

        if (Changed(acceptedInstallation, launch.InstallationBundles))
            return "host-module";
        return acceptedLocal.Any(pair => launch.LocalBundles?.TryGetValue(pair.Key, out var path) == true &&
            !PathComparer.Equals(Path.GetFullPath(pair.Value), Path.GetFullPath(path)))
            ? "package-moved"
            : null;
    }

    private async Task<ProfileReloadResult> RequestRestartAsync(string reason)
    {
        RequiresRestart = true;
        RestartRequired?.Invoke();
        if (queue.RestartHost is { } restart)
            await restart();
        return new([], [], reason);
    }

    private void Report(Exception error)
    {
        LastError = error;
        try
        {
            diagnostic?.Invoke(error);
        }
        catch
        {
        }

        try
        {
            Error?.Invoke(error);
        }
        catch
        {
        }
    }

    private async void LogWarning(object? value)
    {
        try
        {
            await Context.RunAsync(ctx =>
            {
                ctx.Logger.Warn(value);
                return Task.CompletedTask;
            });
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception error)
        {
            Report(error);
        }
    }

    private async Task WarnInactiveAsync(IReadOnlyList<EntryDiagnostic> entries)
    {
        if (entries.Count == 0)
            return;
        foreach (var entry in entries)
        {
            var message = $"{entry.Id} ({entry.Module}): {entry.Error?.Message ?? "inactive"}";
            await Context.RunAsync(ctx =>
            {
                ctx.Logger.Warn(message);
                return Task.CompletedTask;
            });
            try
            {
                Warning?.Invoke(message);
            }
            catch
            {
            }
        }
    }

    private async Task AcceptBundlesAsync(ProfileRefresh refresh)
    {
        SelectedBundles = refresh.SelectedBundles;
        LoadedBundles = refresh.CurrentBundles;
        SkippedBundles = refresh.SkippedBundles;
        foreach (var skipped in SkippedBundles)
        {
            var message =
                $"skipping profile bundle {ConfigurationFile.Write(skipped.Name, true).TrimEnd()}: {skipped.Reason}";
            await Context.RunAsync(ctx =>
            {
                ctx.Logger.Warn(message);
                return Task.CompletedTask;
            });
            try
            {
                Warning?.Invoke(message);
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Stops package preparation and the lifecycle queue, then disposes the owned context and releases watcher references. The caller's resolver is not disposed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        // Package preparation runs outside the HMR queue. Stop its children before closing
        // that queue or the borrowed resolver; admitted publication must be allowed to settle.
        await StopPackagesAsync();
        disposed = true;
        // HmrCoordinator avoids waiting on its current operation when disposal originates there.
        await queue.DisposeAsync();
        watches.Clear();
        await Context.DisposeAsync();
        Error = null;
        Warning = null;
        Refreshed = null;
        RestartRequired = null;
    }

    private sealed class PackageShutdown(ProfileSession session) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(session.StopPackagesAsync());
    }

    private async Task StopPackagesAsync()
    {
        if (ConfigurationOperations is null)
            return;
        var stopped = ConfigurationOperations.StopPackageOperationsAsync();
        if (!queue.IsExecuting)
            await stopped;
        else
            _ = ObservePackageShutdownAsync(stopped);
    }

    private async Task ObservePackageShutdownAsync(Task stopped)
    {
        // Disposal from an applying operation cannot await itself. Admission is already
        // closed synchronously; report any eventual cleanup error without retaining it globally.
        try
        {
            await stopped;
        }
        catch (Exception error)
        {
            Report(error);
        }
    }
}
