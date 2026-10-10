using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Cordis.Composition;

namespace Cordis.Clr;

/// <summary>A shared contract implemented by an explicitly named DLL entry point.</summary>
public interface IClrPluginModule
{
    /// <summary>
    /// Creates plugin.
    /// </summary>
    IPlugin CreatePlugin();
}

/// <summary>Controls the physical files used by an independent collectible load context.</summary>
public enum ClrModuleLoadMode
{
    /// <summary>Loads the complete bundle in place; its owner retains unchanged files for all consumers.</summary>
    StableDirectory,

    /// <summary>Copies a complete, quiescent bundle for callers that must subsequently rebuild its source.</summary>
    ShadowCopy
}

/// <summary>The assembly path is relative to the bundle root.</summary>
public sealed record ClrModuleDefinition(string BundleDirectory, string AssemblyPath, string EntryType)
{
    /// <summary>
    /// Defaults to StableDirectory. The directory owner must retain every file,
    /// without modification, for all managed, native, resource and worker uses, including delayed loads.
    /// A directory name or version is not verification. The resolver never deletes caller-owned files.
    /// </summary>
    public ClrModuleLoadMode LoadMode
    {
        get;
        init;
    } = ClrModuleLoadMode.StableDirectory;
}

/// <summary>Unload request, garbage collection and shadow deletion are independently observable.</summary>
public sealed class ClrUnloadObservation
{
    internal ClrUnloadObservation(WeakReference context, string directory, string? shadowDirectory) =>
        (LoadContext, LoadDirectory, ShadowDirectory) = (context, directory, shadowDirectory);

    /// <summary>The directory used for loading; its owner determines its retention lifetime.</summary>
    public string LoadDirectory
    {
        get;
    }

    /// <summary>
    /// Gets the load context value.
    /// </summary>
    public WeakReference LoadContext
    {
        get;
    }

    /// <summary>
    /// The resolver-owned copy, or null when loading a stable source directory.
    /// </summary>
    public string? ShadowDirectory
    {
        get;
    }

    /// <summary>
    /// Records an attempted unload request; an observer can throw before native release completes.
    /// </summary>
    public bool UnloadRequested
    {
        get;
        internal set;
    }

    /// <summary>Error text from a cooperative unload observer failure, without retaining the exception or its collectible type.</summary>
    public string? UnloadError
    {
        get;
        internal set;
    }

    /// <summary>
    /// Observes collection of the managed load-context wrapper, not release of its native loader.
    /// </summary>
    public bool IsCollected => !LoadContext.IsAlive;

    /// <summary>
    /// True when no resolver-owned shadow remains, including when no copy was created.
    /// </summary>
    public bool ShadowDeleted => !Directory.Exists(ShadowDirectory);

    /// <summary>Explicitly attempt physical deletion after all file consumers have stopped. Does not force GC.</summary>
    /// <remarks>The caller must first stop Workers and every other consumer of this copy. Collection alone
    /// cannot establish that precondition. Returns true without deleting anything for stable directories.</remarks>
    public bool TryDeleteShadow()
    {
        if (ShadowDirectory is null)
            return true;
        if (!UnloadRequested || !IsCollected)
            return false;
        try
        {
            Directory.Delete(ShadowDirectory, recursive: true);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>
/// Explicit module mapping for ordinary CLR deployments. The owner must stop all fibers before
/// disposing this resolver. It neither discovers packages nor restores NuGet at runtime.
/// </summary>
[RequiresDynamicCode("Loading plugin DLLs requires the ordinary CLR; use static modules in Native AOT.")]
[RequiresUnreferencedCode("Plugin entry point types are named explicitly in external DLLs.")]
public sealed class ClrModuleResolver : IModuleResolver, IAsyncDisposable
{
    private readonly Dictionary<string, ClrModuleDefinition> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lease> loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Assembly> shared;
    // Late callers still enter these gates to observe disposal; no WaitHandle is allocated.
#pragma warning disable CA2213
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim mutation = new(1);
#pragma warning restore CA2213

    private sealed class ReplacementScope
    {
        public bool Active = true;
    }

    private readonly AsyncLocal<ReplacementScope?> replacementScope = new();
    private readonly string shadowRoot;
    private bool disposed;

    /// <summary>Load stable artifacts without creating runtime copies. Explicit ShadowCopy uses the temporary directory.</summary>
    public ClrModuleResolver(IEnumerable<Assembly>? sharedContracts = null)
        : this(Path.Combine(Path.GetTempPath(), "cordis-shadow"), sharedContracts)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ClrModuleResolver"/> type.
    /// </summary>
    public ClrModuleResolver(string shadowRoot, IEnumerable<Assembly>? sharedContracts = null)
    {
        this.shadowRoot = Path.GetFullPath(shadowRoot);
        shared = new[] { typeof(IPlugin).Assembly, typeof(IClrPluginModule).Assembly }
            .Concat(sharedContracts ?? [])
            .Distinct()
            .ToDictionary(a => a.GetName().Name!, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the unloads value.
    /// </summary>
    public IReadOnlyList<ClrUnloadObservation> Unloads => unloads.AsReadOnly();

    private readonly List<ClrUnloadObservation> unloads = [];

    /// <summary>Locate an explicitly registered module without importing it; suitable for HMR Loader tracking.</summary>
    public ValueTask<string?> LocateAsync(string specifier, Uri baseUri)
    {
        gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!definitions.TryGetValue(specifier, out var definition))
                throw new KeyNotFoundException($"No CLR module mapping for '{specifier}'.");
            var source = Path.GetFullPath(definition.BundleDirectory);
            var path = Path.GetFullPath(definition.AssemblyPath, source);
            var relative = Path.GetRelativePath(source, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
                throw new ArgumentException(
                    "The entry assembly must be inside its bundle directory.",
                    nameof(definition));
            if (!File.Exists(path))
                throw new FileNotFoundException("The plugin entry assembly is missing.", path);
            return ValueTask.FromResult<string?>(path);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Register before resolving. Loaded mappings can only change through ReplaceAsync.</summary>
    public void Register(string specifier, ClrModuleDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specifier);
        gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loaded.ContainsKey(specifier))
                throw new InvalidOperationException("Use ReplaceAsync to change a loaded module.");
            definitions[specifier] = definition;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Resolves async.
    /// </summary>
    public async ValueTask<IPlugin> ResolveAsync(
        string specifier,
        Uri baseUri,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loaded.TryGetValue(specifier, out var lease))
                return lease.Plugin!;
            if (!definitions.TryGetValue(specifier, out var definition))
                throw new KeyNotFoundException($"No CLR module mapping for '{specifier}'.");
            lease = Prepare(definition);
            loaded.Add(specifier, lease);
            return lease.Plugin!;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Loads the candidate before stopping live code. The callback owns fiber teardown, raw-config
    /// transfer and activation. ResolveAsync remains usable from the callback and observes the old
    /// mapping until commit. Replacement and external disposal serialize; nested replacement or
    /// disposal from the callback rejects rather than waiting on itself. Callback failure keeps the
    /// old mapping but cannot undo the callback's effects.
    /// </summary>
    public async ValueTask<ClrUnloadObservation> ReplaceAsync(
        string specifier,
        ClrModuleDefinition definition,
        Func<IPlugin, IPlugin, ValueTask> switchFibers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(switchFibers);
        if (replacementScope.Value is { Active: true })
            throw new InvalidOperationException("CLR replacements cannot be nested.");
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Lease previous, candidate;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!loaded.TryGetValue(specifier, out previous!))
                    throw new InvalidOperationException("Resolve the module before replacing it.");
                candidate = Prepare(definition);
            }
            finally
            {
                gate.Release();
            }

            var scope = new ReplacementScope();
            replacementScope.Value = scope;
            try
            {
                try
                {
                    await switchFibers(previous.Plugin!, candidate.Plugin!).ConfigureAwait(false);
                }
                catch
                {
#pragma warning disable CA2016 // Rollback must finish even when replacement is canceled.
                    await gate.WaitAsync().ConfigureAwait(false);
#pragma warning restore CA2016
                    try
                    {
                        Retire(candidate);
                    }
                    finally
                    {
                        gate.Release();
                    }

                    throw;
                }

                // Once switching has begun cancellation must not abandon a live candidate.
#pragma warning disable CA2016
                await gate.WaitAsync().ConfigureAwait(false);
#pragma warning restore CA2016
                try
                {
                    loaded[specifier] = candidate;
                    definitions[specifier] = definition;
                    return Retire(previous);
                }
                finally
                {
                    gate.Release();
                }
            }
            finally
            {
                scope.Active = false;
                replacementScope.Value = null;
            }
        }
        finally
        {
            mutation.Release();
        }
    }

    private Lease Prepare(ClrModuleDefinition definition)
    {
        if (!Enum.IsDefined(definition.LoadMode))
            throw new ArgumentOutOfRangeException(nameof(definition), "Unknown CLR module load mode.");
        var source = Path.GetFullPath(definition.BundleDirectory);
        var shadowRelative = Path.GetRelativePath(source, shadowRoot);
        if (definition.LoadMode == ClrModuleLoadMode.ShadowCopy &&
            !Path.IsPathRooted(shadowRelative) && shadowRelative != ".." && !shadowRelative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new ArgumentException("The shadow directory must be outside the source bundle.", nameof(definition));
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The bundle root must be a regular directory.");
        var main = Path.GetFullPath(definition.AssemblyPath, source);
        var relative = Path.GetRelativePath(source, main);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            throw new ArgumentException("The entry assembly must be inside its bundle directory.", nameof(definition));
        if (!File.Exists(main))
            throw new FileNotFoundException("The plugin entry assembly is missing.", main);
        var shadowDirectory = definition.LoadMode == ClrModuleLoadMode.ShadowCopy
            ? Path.Combine(shadowRoot, Guid.NewGuid().ToString("N"))
            : null;
        var destination = shadowDirectory ?? source;
        if (shadowDirectory is not null)
            Directory.CreateDirectory(shadowDirectory);
        BundleContext? context = null;
        try
        {
            PrepareDirectory(source, shadowDirectory);
            var shadowMain = Path.Combine(destination, relative);
            context = new BundleContext(shadowMain, shared);
            var assembly = context.LoadFromAssemblyPath(shadowMain);
            var type = assembly.GetType(definition.EntryType, throwOnError: true)!;
            if (Activator.CreateInstance(type) is not IClrPluginModule module)
                throw new InvalidOperationException(
                    $"'{definition.EntryType}' must implement the shared IClrPluginModule contract.");
            var plugin = module.CreatePlugin() ?? throw new InvalidOperationException("The module returned no plugin.");
            return new Lease(context, plugin, destination, shadowDirectory);
        }
        catch
        {
            if (context is null)
            {
                if (shadowDirectory is not null)
                    Directory.Delete(shadowDirectory, recursive: true);
            }
            else
            {
                RequestUnload(context, destination, shadowDirectory);
            }

            throw;
        }
    }

    private static void PrepareDirectory(string source, string? destination)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Plugin bundles must contain regular files and directories: {entry.FullName}");
            var target = destination is null ? null : Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                if (target is not null)
                    Directory.CreateDirectory(target);
                PrepareDirectory(directory.FullName, target);
            }
            else if (target is not null)
                File.Copy(entry.FullName, target);
        }
    }

    /// <summary>Remove an explicit module mapping after its owner has stopped all fibers and released application references.</summary>
    /// <remarks>Requests cooperative unload; it does not force collection or remove source files. Serialized with replacement and disposal.</remarks>
    public async ValueTask<ClrUnloadObservation?> RemoveAsync(
        string specifier,
        CancellationToken cancellationToken = default)
    {
        if (replacementScope.Value is { Active: true })
            throw new InvalidOperationException("Cannot remove a module inside its replacement callback.");
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                definitions.Remove(specifier);
                return loaded.Remove(specifier, out var lease) ? Retire(lease) : null;
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            mutation.Release();
        }
    }

    private ClrUnloadObservation Retire(Lease lease)
    {
        lease.Plugin = null;
        var context = lease.Context!;
        lease.Context = null;
        return RequestUnload(context, lease.Directory, lease.ShadowDirectory);
    }

    private ClrUnloadObservation RequestUnload(BundleContext context, string directory, string? shadowDirectory)
    {
        var observation = new ClrUnloadObservation(new WeakReference(context), directory, shadowDirectory)
        {
            UnloadRequested = true
        };
        unloads.Add(observation);
        try
        {
            context.Unload();
        }
        catch (Exception error)
        {
            observation.UnloadError = DescribeUnloadError(error);
        }

        return observation;
    }

    private static string DescribeUnloadError(Exception error)
    {
        try
        {
            return error.ToString();
        }
        catch
        {
            return error.GetType().FullName ?? "Unload observer failed.";
        }
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (replacementScope.Value is { Active: true })
            throw new InvalidOperationException(
                "Cannot dispose the CLR resolver from its replacement callback; dispose it after replacement completes.");
        await mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed)
                    return;
                disposed = true;
                foreach (var lease in loaded.Values)
                    Retire(lease);
                loaded.Clear();
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            mutation.Release();
        }
    }

    private sealed class Lease(BundleContext context, IPlugin plugin, string directory, string? shadowDirectory)
    {
        public BundleContext? Context = context;
        public IPlugin? Plugin = plugin;
        public string Directory = directory;
        public string? ShadowDirectory = shadowDirectory;
    }

    private sealed class BundleContext(string main, IReadOnlyDictionary<string, Assembly> shared)
        : AssemblyLoadContext($"Cordis:{Guid.NewGuid():N}", isCollectible: true)
    {
        private static readonly HashSet<string> FrameworkAssemblies = ReadFrameworkAssemblies();
        private readonly AssemblyDependencyResolver resolver = new(main);
        private readonly string directory = Path.GetDirectoryName(main)!;

        private static HashSet<string> ReadFrameworkAssemblies()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                typeof(object).Assembly.GetName().Name!
            };
            AddCoreFrameworkAssemblies(names);
            AddHostFrameworkAssemblies(names);
            AddSharedFrameworkAssemblies(names);
            return names;
        }

        private static void AddCoreFrameworkAssemblies(HashSet<string> names)
        {
            // SDK reference names survive bundling. CoreLib.Location may be empty,
            // and the TPA list also contains private application dependencies.
            using var stream =
                typeof(ClrModuleResolver).Assembly.GetManifestResourceStream(
                    "Cordis.Clr.CoreFrameworkAssemblies.txt") ??
                throw new InvalidOperationException("The core framework assembly manifest is missing.");
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } name)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
        }

        private static void AddHostFrameworkAssemblies(HashSet<string> names)
        {
            // Self-contained hosts have no shared-framework deps paths. The shipped
            // build target captures only their SDK-resolved framework references.
            using var stream = Assembly.GetEntryAssembly()?.GetManifestResourceStream("Cordis.Clr.HostFrameworks.txt");
            if (stream is null)
                return;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                var separator = line.IndexOf('|');
                if (separator > 0 && separator < line.Length - 1)
                    names.Add(line[(separator + 1)..]);
            }
        }

        private static void AddSharedFrameworkAssemblies(HashSet<string> names)
        {
            // hostpolicy lists app and resolved framework deps, not additional deps.
            // Accept only shared/<framework>/<version>/<framework>.deps.json;
            // application manifests cannot admit arbitrary private dependencies.
            var depsFiles = AppContext.GetData("APP_CONTEXT_DEPS_FILES") as string ?? "";
            var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
            var applicationDeps =
                entryName is null ? null : Path.Combine(AppContext.BaseDirectory, entryName + ".deps.json");
            var pathComparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            foreach (var deps in depsFiles.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(deps, applicationDeps, pathComparison))
                    continue;
                var versionDirectory = Path.GetDirectoryName(deps);
                var frameworkDirectory = versionDirectory is null ? null : Directory.GetParent(versionDirectory);
                if (frameworkDirectory?.Parent?.Name != "shared" ||
                    Path.GetFileName(deps) != frameworkDirectory.Name + ".deps.json")
                    continue;
                using var manifest = JsonDocument.Parse(File.ReadAllText(deps));
                if (!manifest.RootElement.TryGetProperty("runtimeTarget", out var runtimeTarget) ||
                    !runtimeTarget.TryGetProperty("name", out var targetName) ||
                    !manifest.RootElement.TryGetProperty("targets", out var targets) ||
                    !targets.TryGetProperty(targetName.GetString()!, out var target))
                    continue;
                foreach (var library in target.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("runtime", out var runtime))
                        continue;
                    foreach (var asset in runtime.EnumerateObject())
                    {
                        if (asset.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            names.Add(Path.GetFileNameWithoutExtension(asset.Name));
                    }
                }
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (shared.TryGetValue(assemblyName.Name!, out var contract))
                return contract;
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                var local = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(local))
                    path = local;
            }

            if (path is not null)
                return LoadFromAssemblyPath(path);
            if (FrameworkAssemblies.Contains(assemblyName.Name!))
                return null;
            throw new FileNotFoundException(
                $"Private dependency '{assemblyName}' is absent from the plugin bundle and is not a shared contract.",
                assemblyName.Name + ".dll");
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
