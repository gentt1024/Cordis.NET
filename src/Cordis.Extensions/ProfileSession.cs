using Cordis.Composition;

namespace Cordis.Extensions;

/// <summary>A running profile with explicit module resolution and optional configuration HMR.</summary>
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
    private bool disposed;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private ProfileSession(ProfileLaunch launch, IModuleResolver resolver, bool enableHmr, IReadOnlySet<string>? required,
        Action<Exception>? diagnostic, Func<Task<DeploymentGeneration>>? refreshDeployment)
    {
        // Command-line overlays belong to the launch, not subsequent caller mutations.
        this.launch = launch with
        {
            Overlays = launch.Overlays.Select(layer => layer with
            { Patches = ProfileComposition.Flatten([layer]) }).ToArray()
        };
        this.required = required;
        this.diagnostic = diagnostic;
        successfulMappings = MappingSnapshot();
        acceptedInstallation = new(launch.InstallationBundles, StringComparer.Ordinal);
        acceptedLocal = new(launch.LocalBundles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        packages = (resolver as DeploymentModuleResolver)?.Packages;
        this.refreshDeployment = refreshDeployment;
        if (refreshDeployment is not null && packages is null)
            throw new ArgumentException("A deployment refresh requires a DeploymentModuleResolver.", nameof(refreshDeployment));
        profilePaths = [Path.Combine(launch.Profile.Directory, "package.json"),
            launch.Profile.UserLayer.Source, Path.Combine(launch.Home, "cordis.patch.yml"),
            .. launch.RuntimeIdentity is null ? Array.Empty<string>()
                : [Path.Combine(launch.Profile.Directory, DshProfilePolicy.CompatibilityFilename)]];
        Context = new Context(Report);
        Hmr = enableHmr ? queue : null;
        queue.Error += Report;
        queue.Warning += LogWarning;
    }

    /// <summary>
    /// Gets the context value.
    /// </summary>
    public Context Context { get; }
    /// <summary>
    /// Gets the loader value.
    /// </summary>
    public Loader Loader { get; private set; } = null!;
    /// <summary>
    /// Gets the include value.
    /// </summary>
    public Include Include { get; private set; } = null!;
    /// <summary>Profile mutations sharing this session's lifecycle queue and refresh ownership.</summary>
    public PluginConfigurationOperations ConfigurationOperations { get; private set; } = null!;
    /// <summary>Null when automatic watching was not requested. Manual refresh remains available.</summary>
    public HmrCoordinator? Hmr { get; }
    /// <summary>
    /// Gets the requires restart value.
    /// </summary>
    public bool RequiresRestart { get; private set; }
    /// <summary>
    /// Gets the last error value.
    /// </summary>
    public Exception? LastError { get; private set; }
    /// <summary>
    /// Gets the last successful refresh value.
    /// </summary>
    public DateTimeOffset? LastSuccessfulRefresh { get; private set; }
    /// <summary>
    /// Gets the refreshed value.
    /// </summary>
    public event Action? Refreshed;
    /// <summary>
    /// Gets the restart required value.
    /// </summary>
    public event Action? RestartRequired;
    /// <summary>
    /// Gets the error value.
    /// </summary>
    public event Action<Exception>? Error;
    /// <summary>The latest manifest selection, including skipped bundles.</summary>
    public IReadOnlyList<string> SelectedBundles { get; private set; } = [];
    /// <summary>Bundles that contributed layers to the latest successful composition.</summary>
    public IReadOnlyList<string> LoadedBundles { get; private set; } = [];
    /// <summary>Current bundle failures, in manifest order.</summary>
    public IReadOnlyList<SkippedBundle> SkippedBundles { get; private set; } = [];
    /// <summary>
    /// Gets the warning value.
    /// </summary>
    public event Action<string>? Warning;

    /// <summary>Mount current bundle/profile/home/launch layers; no resolver or package manager is created implicitly.</summary>
    public static async Task<ProfileSession> StartAsync(string configurationPath, ProfileLaunch launch, IModuleResolver resolver,
        IExpressionEvaluator? evaluator = null, bool enableHmr = false, IReadOnlySet<string>? required = null,
        Func<Context, Task>? prepare = null, Action<Exception>? diagnostic = null,
        Func<Task<DeploymentGeneration>>? refreshDeployment = null, Task<bool>? applicationReady = null)
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
                if (session.Hmr is not null) ctx.Provide("hmr", session.Hmr);
                session.Loader = new Loader(ctx, resolver, new Uri(Path.GetFullPath(configurationPath)), evaluator, session.Report);
                if (prepare is not null) await prepare(ctx);
            });
            session.Include = session.launch.RuntimeIdentity is { } runtime
                ? await DshProfilePolicy.MountAsync(session.Loader, configurationPath, session.launch.Profile.Directory, runtime,
                    ProfileComposition.Flatten(refresh.Layers), session.packages, session.launch.CompatibilityWarning, session.launch.ManifestLocator, session.launch.CompatibilityPackageName)
                : await ApplicationBoot.MountAsync(session.Loader, configurationPath, ProfileComposition.Flatten(refresh.Layers));
            await session.AcceptBundlesAsync(refresh);
            session.ConfigurationOperations = new(session.launch, session.Include)
            {
                RunExclusiveAsync = session.queue.RunExclusiveAsync,
                ReconcileAsync = async requiredEntries =>
                {
                    await session.RefreshCoreAsync(requiredEntries);
                    if (session.RequiresRestart) throw new DeploymentRestartRequiredException("The profile deployment requires a host restart before configuration can be reconciled.");
                    return await ApplicationBoot.AuditAsync(session.Loader, session.required);
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

    private static async Task<bool> WaitForReadinessAsync(Task<bool> boot, Task<bool>? application)
        => await boot.ConfigureAwait(false) && (application is null || await application.ConfigureAwait(false));

    /// <summary>Serialize an explicit refresh with automatic configuration and code reloads.</summary>
    public Task RefreshAsync() => queue.RunExclusiveAsync(() => RefreshCoreAsync());

    private async Task RefreshCoreAsync(IReadOnlySet<string>? editedEntries = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            var inputs = await ProfileInputSnapshotAsync();
            var mappings = MappingSnapshot();
            if (inputs != successfulProfileInputs || mappings != successfulMappings)
            {
                if (refreshDeployment is not null)
                {
                    try { packages!.Replace(await refreshDeployment()); }
                    catch (DeploymentRestartRequiredException)
                    { await RequestRestartAsync(); return; }
                }
                else if (MappingsRequireRestart()) { await RequestRestartAsync(); return; }
                // Bundle selection only changes composition. An additive resolver generation
                // can make its new code available without restarting the existing modules.
                var refresh = await ProfileComposition.RefreshAsync(launch);
                var requiredEntries = editedEntries is null ? required
                    : new HashSet<string>((required ?? new HashSet<string>()).Concat(editedEntries), StringComparer.Ordinal);
                await WarnInactiveAsync(await ApplicationBoot.ReconcileAsync(Include, ProfileComposition.Flatten(refresh.Layers), requiredEntries));
                await AcceptBundlesAsync(refresh);
                successfulProfileInputs = inputs;
                successfulMappings = mappings;
                acceptedInstallation = new(launch.InstallationBundles, StringComparer.Ordinal);
                acceptedLocal = new(launch.LocalBundles ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            }
            foreach (var include in await IncludesAsync())
            {
                var text = await File.ReadAllTextAsync(include.Filename);
                if (successfulInputs.TryGetValue(include.Filename, out var previous) && text == previous) continue;
                // Include contains refresh errors; validate structure here so a rejected input is
                // never recorded as successful and callers can observe/retry that same input.
                ConfigurationFile.ParseEntries(text, Path.GetExtension(include.Filename) == ".json");
                await include.RefreshAsync();
                await WarnInactiveAsync(await ApplicationBoot.AuditAsync(Loader, required));
                successfulInputs[include.Filename] = text;
            }
            await SynchronizeWatchesAsync();
            RequiresRestart = false;
            LastError = null;
            LastSuccessfulRefresh = DateTimeOffset.UtcNow;
            Refreshed?.Invoke();
        }
        catch (Exception error)
        {
            // The watcher queue reports failures; direct callers also receive the exception.
            LastError = error;
            throw;
        }
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
        if (Hmr is null || disposed) return;
        var paths = profilePaths.Concat((await IncludesAsync()).Select(include => include.Filename))
            .Select(HmrCoordinator.CanonicalPath).ToHashSet(PathComparer);
        foreach (var path in watches.Keys.Where(path => !paths.Contains(path)).ToArray())
        {
            await watches[path].DisposeAsync();
            watches.Remove(path);
        }
        foreach (var path in paths)
            if (!watches.ContainsKey(path)) watches.Add(path, queue.WatchConfig(path, () => RefreshCoreAsync()));
    }

    private async Task<string> ProfileInputSnapshotAsync()
    {
        var values = new List<object?>();
        for (var index = 0; index < profilePaths.Length; index++)
        {
            var path = profilePaths[index];
            try
            {
                var text = await File.ReadAllTextAsync(path);
                // DSH's manifest watcher reacts to bundle selection, not package-manager
                // dependency/lock bookkeeping. Resolution generation updates remain explicit.
                if (index == 0)
                {
                    var manifest = ConfigurationFile.Parse(text, true) as EntryOptions
                        ?? throw new FormatException($"Manifest {path} must be a JSON object.");
                    values.Add(new PackageManifest(manifest).Bundles.ToList());
                }
                else values.Add(text);
            }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { values.Add(null); }
            catch (Exception error) when (path == Path.Combine(launch.Profile.Directory, DshProfilePolicy.CompatibilityFilename)
                && error is IOException or UnauthorizedAccessException)
            {
                // Bad grant data must remain non-authorizing and non-rewritable, not prevent boot.
                // Admission emits the diagnostic; this marker makes unreadable/readable transitions refreshable.
                values.Add(new EntryOptions { ["unreadableCompatibility"] = error.GetType().Name });
            }
        }
        return ConfigurationFile.Write(values, true);
    }

    private string MappingSnapshot() => ConfigurationFile.Write(new object?[]
    {
        launch.InstallationBundles.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => (object?)pair.Value),
        launch.LocalBundles?.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => (object?)pair.Value)
    }, true);

    private bool MappingsRequireRestart()
    {
        static bool Changed(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string>? after)
            => before.Any(pair => after is null || !after.TryGetValue(pair.Key, out var path)
                || !PathComparer.Equals(Path.GetFullPath(pair.Value), Path.GetFullPath(path)));
        return Changed(acceptedInstallation, launch.InstallationBundles) || Changed(acceptedLocal, launch.LocalBundles);
    }

    private async Task RequestRestartAsync()
    {
        RequiresRestart = true;
        RestartRequired?.Invoke();
        if (queue.RestartHost is { } restart) await restart();
    }

    private void Report(Exception error)
    {
        LastError = error;
        try { diagnostic?.Invoke(error); } catch { }
        try { Error?.Invoke(error); } catch { }
    }

    private async void LogWarning(object? value)
    {
        try { await Context.RunAsync(ctx => { ctx.Logger.Warn(value); return Task.CompletedTask; }); }
        catch (ObjectDisposedException) { }
        catch (Exception error) { Report(error); }
    }

    private async Task WarnInactiveAsync(IReadOnlyList<EntryDiagnostic> entries)
    {
        if (entries.Count == 0) return;
        foreach (var entry in entries)
        {
            var message = $"{entry.Id} ({entry.Module}): {entry.Error?.Message ?? "inactive"}";
            await Context.RunAsync(ctx => { ctx.Logger.Warn(message); return Task.CompletedTask; });
            try { Warning?.Invoke(message); } catch { }
        }
    }
    private async Task AcceptBundlesAsync(ProfileRefresh refresh)
    {
        SelectedBundles = refresh.SelectedBundles;
        LoadedBundles = refresh.CurrentBundles;
        SkippedBundles = refresh.SkippedBundles;
        foreach (var skipped in SkippedBundles)
        {
            var message = $"skipping profile bundle {ConfigurationFile.Write(skipped.Name, true).TrimEnd()}: {skipped.Reason}";
            await Context.RunAsync(ctx => { ctx.Logger.Warn(message); return Task.CompletedTask; });
            try { Warning?.Invoke(message); } catch { }
        }
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
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
        if (ConfigurationOperations is null) return;
        var stopped = ConfigurationOperations.StopPackageOperationsAsync();
        if (!queue.IsExecuting) await stopped;
        else _ = ObservePackageShutdownAsync(stopped);
    }

    private async Task ObservePackageShutdownAsync(Task stopped)
    {
        // Disposal from an applying operation cannot await itself. Admission is already
        // closed synchronously; report any eventual cleanup error without retaining it globally.
        try { await stopped; }
        catch (Exception error) { Report(error); }
    }
}
