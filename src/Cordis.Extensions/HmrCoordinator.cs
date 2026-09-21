using Cordis.Composition;

namespace Cordis.Extensions;

/// <summary>Serializes mutations and file reloads. The host decides how code is replaced or restarted.</summary>
public sealed class HmrCoordinator : IAsyncDisposable
{
    private readonly Func<string, FileSystemWatcher> createWatcher;
    private readonly Func<string, FileAttributes> readAttributes;
    /// <summary>
    /// Initializes a new instance of the <see cref="HmrCoordinator"/> type.
    /// </summary>
    public HmrCoordinator(Func<string, FileSystemWatcher>? watcherFactory = null, Func<string, FileAttributes>? readAttributes = null)
    {
        createWatcher = watcherFactory ?? (directory => new FileSystemWatcher(directory));
        this.readAttributes = readAttributes ?? File.GetAttributes;
    }
    private readonly object _gate = new();
    private readonly AsyncLocal<bool> _executing = new();
    private Task _tail = Task.CompletedTask;
    private bool _closing;
    private readonly CancellationTokenSource _stopReadiness = new();
    private readonly Dictionary<string, ConfigWatch> _watches = new(PathComparer);
    private sealed record Module(Func<Task> Replace);
    private readonly Dictionary<string, Module> _modules = new(PathComparer);
    private readonly Dictionary<string, HashSet<string>> _dependencies = new(PathComparer);
    private sealed record LoaderBinding(Loader Loader, Func<string, Uri, ValueTask<string?>> Locate);
    private readonly List<LoaderBinding> _loaders = [];
    private readonly HashSet<string> _framework = new(PathComparer);
    private readonly List<FileSystemWatcher> _moduleWatchers = [];
    private readonly HashSet<string> _changed = new(PathComparer);
    private Timer? _dispatchTimer;
    /// <summary>
    /// Gets the debounce value.
    /// </summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromMilliseconds(100);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Gets the error value.
    /// </summary>
    public event Action<Exception>? Error;
    /// <summary>
    /// Gets the warning value.
    /// </summary>
    public event Action<object?>? Warning;
    /// <summary>
    /// Gets the changed value.
    /// </summary>
    public event Action<string>? Changed;
    /// <summary>
    /// Gets the reloaded value.
    /// </summary>
    public event Action<string>? Reloaded;
    /// <summary>
    /// Gets the restart host value.
    /// </summary>
    public Func<Task>? RestartHost { get; set; }
    /// <summary>
    /// Gets the application ready value.
    /// </summary>
    public Task<bool> ApplicationReady { get; set; } = Task.FromResult(true);

    /// <summary>
    /// Runs exclusive async.
    /// </summary>
    public Task RunExclusiveAsync(Func<Task> operation)
    {
        if (_executing.Value) return Task.FromException(new InvalidOperationException("HMR transactions cannot be nested"));
        lock (_gate)
        {
            var predecessor = _tail;
            var task = Run();
            _tail = IgnoreFailure(task);
            return task;
            async Task Run()
            {
                // Always enqueue: no user code executes under the gate.
                await Task.Yield();
                await predecessor.ConfigureAwait(false);
                if (_closing) throw new ObjectDisposedException(nameof(HmrCoordinator));
                _executing.Value = true;
                try { await operation().ConfigureAwait(false); }
                finally { _executing.Value = false; }
            }
        }
    }

    private static async Task IgnoreFailure(Task task) { try { await task.ConfigureAwait(false); } catch { } }

    /// <summary>
    /// Watches config.
    /// </summary>
    public IAsyncDisposable WatchConfig(string filename, Func<Task> refresh, bool refreshExisting = true)
    {
        var path = ResolvePath(filename, readAttributes);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            if (_watches.ContainsKey(path)) throw new InvalidOperationException($"config path already registered: {filename}");
            var watch = new ConfigWatch(this, path, refresh);
            _watches.Add(path, watch);
            try { watch.Start(refreshExisting); }
            catch { _watches.Remove(path); watch.Close(); throw; }
            return watch;
        }
    }

    /// <summary>
    /// Performs the register module operation.
    /// </summary>
    public void RegisterModule(string filename, Func<Task> replace, IEnumerable<string>? dependencies = null)
    {
        var path = CanonicalPath(filename);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _modules[path] = new(replace);
            if (dependencies is not null || !_dependencies.ContainsKey(path)) RegisterDependencies(path, dependencies ?? []);
        }
    }
    /// <summary>Declare direct native dependencies, including nodes that do not export a plugin. Cycles are supported.</summary>
    public void RegisterDependencies(string filename, IEnumerable<string> dependencies)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            _dependencies[CanonicalPath(filename)] = new(dependencies.Select(CanonicalPath), PathComparer);
        }
    }
    /// <summary>Returns the explicitly declared direct dependencies; unknown modules have none.</summary>
    public IReadOnlyList<string> GetLinked(string filename)
    { lock (_gate) return _dependencies.TryGetValue(CanonicalPath(filename), out var linked) ? linked.ToArray() : []; }
    /// <summary>
    /// Performs the register framework operation.
    /// </summary>
    public void RegisterFramework(string filename)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_closing, this); _framework.Add(CanonicalPath(filename)); } }

    /// <summary>
    /// Check current Loader entry resolution during reload. The host supplies a non-loading module
    /// locator; returning null denotes an uncached/non-plugin module. Paths use the same explicit
    /// native graph as RegisterModule. Resolution errors warn without disturbing running fibers.
    /// </summary>
    public IDisposable TrackLoader(Loader loader, Func<string, Uri, ValueTask<string?>> locateModule)
    {
        ArgumentNullException.ThrowIfNull(loader); ArgumentNullException.ThrowIfNull(locateModule);
        var binding = new LoaderBinding(loader, locateModule);
        lock (_gate) { ObjectDisposedException.ThrowIf(_closing, this); _loaders.Add(binding); }
        return new Registration(() => { lock (_gate) _loaders.Remove(binding); });
    }
    private sealed class Registration(Action remove) : IDisposable
    {
        private Action? remove = remove;
        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }

    private async Task CheckEntriesAsync()
    {
        LoaderBinding[] bindings;
        lock (_gate) bindings = [.. _loaders];
        foreach (var binding in bindings)
        {
            (string Name, Uri Base)[] entries = [];
            await binding.Loader.Context.RunAsync(_ =>
            {
                entries = binding.Loader.Entries().Select(entry => (entry.Options.Name, entry.Parent.Tree.BaseUri)).Distinct().ToArray();
                return Task.CompletedTask;
            });
            foreach (var (name, basis) in entries)
            {
                if (name.StartsWith("cordis:", StringComparison.Ordinal)) continue;
                try
                {
                    var path = await binding.Locate(name, basis).ConfigureAwait(false);
                    if (path is null) continue;
                    _ = CanonicalPath(path);
                }
                catch (Exception error) { HmrDiagnostics.Report(Warn, error); }
            }
        }
    }

    private HashSet<string> DependencyClosure(string filename)
    {
        var result = new HashSet<string>(PathComparer);
        var pending = new Stack<string>(); pending.Push(filename);
        while (pending.TryPop(out var path))
        {
            if (_framework.Contains(path) || !result.Add(path)) continue;
            if (_dependencies.TryGetValue(path, out var linked)) foreach (var child in linked) pending.Push(child);
        }
        return result;
    }

    /// <summary>
    /// Watches modules.
    /// </summary>
    public IDisposable WatchModules(string directory)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closing, this);
            var watcher = createWatcher(CanonicalPath(directory));
            try
            {
                ObjectDisposedException.ThrowIf(_closing, this);
                watcher.IncludeSubdirectories = true;
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName;
                watcher.Changed += (_, e) => Stash(e.FullPath);
                watcher.Created += (_, e) => Stash(e.FullPath);
                watcher.Deleted += (_, e) => Stash(e.FullPath);
                watcher.Renamed += (_, e) => { Stash(e.OldFullPath); Stash(e.FullPath); };
                watcher.Error += (_, e) => Report(e.GetException());
                ActivateWatcher(watcher);
                _moduleWatchers.Add(watcher);
                return watcher;
            }
            catch { watcher.Dispose(); throw; }
        }
    }

    private void ActivateWatcher(FileSystemWatcher watcher)
    {
        // EnableRaisingEvents is non-virtual and a missing root does not fail uniformly on
        // every OS. Validate the named root after construction, before publishing an active
        // watch, so initialization errors retain the same ownership/cleanup contract.
        if ((readAttributes(watcher.Path) & FileAttributes.Directory) == 0)
            throw new IOException($"Watch root is not a directory: {watcher.Path}");
        watcher.EnableRaisingEvents = true;
    }

    private void Stash(string path)
    {
        lock (_gate)
        {
            if (_closing) return;
            _changed.Add(path);
            _dispatchTimer ??= new Timer(_ =>
            {
                string[] files;
                lock (_gate) { files = [.. _changed]; _changed.Clear(); }
                Observe(NotifyChangedAsync(files));
            });
            _dispatchTimer.Change(Debounce, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Performs the notify changed async operation.
    /// </summary>
    public Task NotifyChangedAsync(string filename) => NotifyChangedAsync([filename]);
    /// <summary>
    /// Performs the notify changed async operation.
    /// </summary>
    public Task NotifyChangedAsync(IEnumerable<string> filenames) => RunExclusiveAsync(async () =>
    {
        if (!await ReadyAsync().ConfigureAwait(false)) return;
        await CheckEntriesAsync().ConfigureAwait(false);
        var paths = new HashSet<string>(filenames.Select(CanonicalPath), PathComparer);
        bool frameworkChanged;
        (string Entry, Module Module, HashSet<string> Dependencies)[] modules;
        lock (_gate)
        {
            paths.ExceptWith(_watches.Keys);
            frameworkChanged = paths.Overlaps(_framework);
            modules = _modules.Select(pair => (pair.Key, pair.Value, DependencyClosure(pair.Key))).ToArray();
        }
        if (frameworkChanged)
        {
            if (RestartHost is null) throw new InvalidOperationException("A framework change requires the host restart hook.");
            await RestartHost().ConfigureAwait(false);
            return;
        }
        foreach (var (entry, module, dependencies) in modules)
        {
            if (_closing) return;
            if (!dependencies.Overlaps(paths)) continue;
            await module.Replace().ConfigureAwait(false);
            Reloaded?.Invoke(entry);
        }
        foreach (var path in paths)
            if (!modules.Any(module => module.Dependencies.Contains(path))) Changed?.Invoke(path);
    });

    internal static string CanonicalPath(string filename)
        => ResolvePath(filename, File.GetAttributes);

    private static string ResolvePath(string filename, Func<string, FileAttributes> read)
    {
        var full = Path.GetFullPath(filename);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes;
            try { attributes = read(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }

    private void Warn(object? value) { try { Warning?.Invoke(value); } catch { /* Diagnostic observers cannot poison the reload queue. */ } }
    private void Report(Exception error)
    {
        HmrDiagnostics.Report(Warn, error);
        try { Error?.Invoke(error); } catch { /* Diagnostic observers cannot poison the reload queue. */ }
    }
    private async void Observe(Task task) { try { await task.ConfigureAwait(false); } catch (Exception error) { Report(error); } }
    private async Task<bool> ReadyAsync()
    {
        try { return await ApplicationReady.WaitAsync(_stopReadiness.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stopReadiness.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        ConfigWatch[] watches;
        Task tail;
        lock (_gate) { _closing = true; watches = [.. _watches.Values]; tail = _tail; }
        await _stopReadiness.CancelAsync();
        _dispatchTimer?.Dispose();
        foreach (var watcher in _moduleWatchers) watcher.Dispose();
        foreach (var watcher in watches) await watcher.DisposeAsync().ConfigureAwait(false);
        if (!_executing.Value) await tail.ConfigureAwait(false);
        _modules.Clear();
        _dependencies.Clear();
        _loaders.Clear();
        _framework.Clear();
        _moduleWatchers.Clear();
        _changed.Clear();
        RestartHost = null;
        Changed = null;
        Reloaded = null;
        Error = null;
        Warning = null;
    }

    private sealed class ConfigWatch(HmrCoordinator owner, string filename, Func<Task> refresh) : IAsyncDisposable
    {
        private FileSystemWatcher? _watcher;
        private readonly object _sync = new();
        private Task _running = Task.CompletedTask;
        private bool _dirty;
        private bool _closed;
        private bool _processing;

        internal void Start(bool refreshExisting)
        {
            var directory = Path.GetDirectoryName(filename) ?? Path.GetPathRoot(filename)!;
            while (true)
            {
                try
                {
                    if ((owner.readAttributes(directory) & FileAttributes.Directory) == 0)
                        throw new IOException($"Config watch parent is not a directory: {directory}");
                    break;
                }
                catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
                {
                    var parent = Path.GetDirectoryName(directory);
                    if (parent is null) throw;
                    directory = parent;
                }
            }
            var created = owner.createWatcher(directory);
            // Native watcher activation is synchronous, but a factory can reenter teardown.
            // Do not publish or enable a resource whose owner closed while it was being created.
            lock (_sync)
            {
                if (_closed || owner._closing) { created.Dispose(); throw new ObjectDisposedException(nameof(HmrCoordinator)); }
                _watcher = created;
            }
            _watcher.IncludeSubdirectories = true;
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite;
            _watcher.Changed += OnChange;
            _watcher.Created += OnChange;
            _watcher.Deleted += OnChange;
            _watcher.Renamed += (_, e) => { Check(e.FullPath); Check(e.OldFullPath); };
            _watcher.Error += (_, e) => owner.Report(e.GetException());
            owner.ActivateWatcher(_watcher);
            if (refreshExisting && File.Exists(filename)) Signal();
        }

        private void OnChange(object sender, FileSystemEventArgs e) => Check(e.FullPath);
        private void Check(string path)
        {
            // Parent creation can contain an already-populated subtree.
            var canonical = CanonicalPath(path);
            if (PathComparer.Equals(canonical, filename) || filename.StartsWith(canonical + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) Signal();
        }

        private void Signal()
        {
            lock (_sync)
            {
                if (_closed) return;
                _dirty = true;
                if (_processing) return;
                _processing = true;
                // Explicitly remove the caller transaction from background watch work.
                using (ExecutionContext.SuppressFlow()) _running = Task.Run(Process);
            }
        }

        private async Task Process()
        {
            while (true)
            {
                lock (_sync)
                {
                    if (!_dirty) { _processing = false; return; }
                    _dirty = false;
                }
                try
                {
                    await owner.RunExclusiveAsync(async () =>
                    {
                        if (await owner.ReadyAsync().ConfigureAwait(false)) await refresh().ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                catch (Exception error) { owner.Warn($"failed to refresh config: {filename}"); owner.Report(error); }
            }
        }

        internal void Close() { lock (_sync) { _closed = true; _watcher?.Dispose(); } }
        public async ValueTask DisposeAsync()
        {
            Close();
            lock (owner._gate) owner._watches.Remove(filename);
            if (!owner._executing.Value) await _running.ConfigureAwait(false);
        }
    }
}
