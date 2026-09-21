using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
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

/// <summary>The assembly path is relative to the bundle root; the complete directory is copied.</summary>
public sealed record ClrModuleDefinition(string BundleDirectory, string AssemblyPath, string EntryType);

/// <summary>Unload request, garbage collection and shadow deletion are independently observable.</summary>
public sealed class ClrUnloadObservation
{
    internal ClrUnloadObservation(WeakReference context, string directory)
        => (LoadContext, ShadowDirectory) = (context, directory);

    /// <summary>
    /// Gets the load context value.
    /// </summary>
    public WeakReference LoadContext { get; }
    /// <summary>
    /// Gets the shadow directory value.
    /// </summary>
    public string ShadowDirectory { get; }
    /// <summary>
    /// Gets the unload requested value.
    /// </summary>
    public bool UnloadRequested { get; internal set; }
    /// <summary>
    /// Gets the is collected value.
    /// </summary>
    public bool IsCollected => !LoadContext.IsAlive;
    /// <summary>
    /// Gets the shadow deleted value.
    /// </summary>
    public bool ShadowDeleted => !Directory.Exists(ShadowDirectory);

    /// <summary>Does not force GC. A remaining plugin reference can legitimately delay collection.</summary>
    public bool TryDeleteShadow()
    {
        if (!UnloadRequested || !IsCollected) return false;
        try { Directory.Delete(ShadowDirectory, recursive: true); return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
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
    private readonly SemaphoreSlim gate = new(1);
    private readonly SemaphoreSlim mutation = new(1);
    private sealed class ReplacementScope { public bool Active = true; }
    private readonly AsyncLocal<ReplacementScope?> replacementScope = new();
    private readonly string shadowRoot;
    private bool disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ClrModuleResolver"/> type.
    /// </summary>
    public ClrModuleResolver(string shadowRoot, IEnumerable<Assembly>? sharedContracts = null)
    {
        this.shadowRoot = Path.GetFullPath(shadowRoot);
        shared = new[] { typeof(IPlugin).Assembly, typeof(IClrPluginModule).Assembly }
            .Concat(sharedContracts ?? []).Distinct()
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
            if (!definitions.TryGetValue(specifier, out var definition)) throw new KeyNotFoundException($"No CLR module mapping for '{specifier}'.");
            var source = Path.GetFullPath(definition.BundleDirectory);
            var path = Path.GetFullPath(definition.AssemblyPath, source);
            var relative = Path.GetRelativePath(source, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("The entry assembly must be inside its bundle directory.", nameof(definition));
            if (!File.Exists(path)) throw new FileNotFoundException("The plugin entry assembly is missing.", path);
            return ValueTask.FromResult<string?>(path);
        }
        finally { gate.Release(); }
    }

    /// <summary>Register before resolving. Loaded mappings can only change through ReplaceAsync.</summary>
    public void Register(string specifier, ClrModuleDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specifier);
        gate.Wait();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loaded.ContainsKey(specifier)) throw new InvalidOperationException("Use ReplaceAsync to change a loaded module.");
            definitions[specifier] = definition;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Resolves async.
    /// </summary>
    public async ValueTask<IPlugin> ResolveAsync(string specifier, Uri baseUri, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (loaded.TryGetValue(specifier, out var lease)) return lease.Plugin!;
            if (!definitions.TryGetValue(specifier, out var definition))
                throw new KeyNotFoundException($"No CLR module mapping for '{specifier}'.");
            lease = Prepare(definition);
            loaded.Add(specifier, lease);
            return lease.Plugin!;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// Loads the candidate before stopping live code. The callback owns fiber teardown, raw-config
    /// transfer and activation. ResolveAsync remains usable from the callback and observes the old
    /// mapping until commit. Replacement and external disposal serialize; nested replacement or
    /// disposal from the callback rejects rather than waiting on itself. Callback failure keeps the
    /// old mapping but cannot undo the callback's effects.
    /// </summary>
    public async ValueTask<ClrUnloadObservation> ReplaceAsync(string specifier, ClrModuleDefinition definition,
        Func<IPlugin, IPlugin, ValueTask> switchFibers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(switchFibers);
        if (replacementScope.Value is { Active: true }) throw new InvalidOperationException("CLR replacements cannot be nested.");
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Lease previous, candidate;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!loaded.TryGetValue(specifier, out previous!)) throw new InvalidOperationException("Resolve the module before replacing it.");
                candidate = Prepare(definition);
            }
            finally { gate.Release(); }
            var scope = new ReplacementScope(); replacementScope.Value = scope;
            try
            {
                try { await switchFibers(previous.Plugin!, candidate.Plugin!).ConfigureAwait(false); }
                catch
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try { Retire(candidate); } finally { gate.Release(); }
                    throw;
                }
                // Once switching has begun cancellation must not abandon a live candidate.
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    loaded[specifier] = candidate;
                    definitions[specifier] = definition;
                    return Retire(previous);
                }
                finally { gate.Release(); }
            }
            finally { scope.Active = false; replacementScope.Value = null; }
        }
        finally { mutation.Release(); }
    }

    private Lease Prepare(ClrModuleDefinition definition)
    {
        var source = Path.GetFullPath(definition.BundleDirectory);
        var shadowRelative = Path.GetRelativePath(source, shadowRoot);
        if (!Path.IsPathRooted(shadowRelative) && shadowRelative != ".." && !shadowRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The shadow directory must be outside the source bundle.", nameof(definition));
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The bundle root must be a regular directory.");
        var main = Path.GetFullPath(definition.AssemblyPath, source);
        var relative = Path.GetRelativePath(source, main);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The entry assembly must be inside its bundle directory.", nameof(definition));
        if (!File.Exists(main)) throw new FileNotFoundException("The plugin entry assembly is missing.", main);
        var destination = Path.Combine(shadowRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        BundleContext? context = null;
        try
        {
            CopyDirectory(source, destination);
            var shadowMain = Path.Combine(destination, relative);
            context = new BundleContext(shadowMain, shared);
            var assembly = context.LoadFromAssemblyPath(shadowMain);
            var type = assembly.GetType(definition.EntryType, throwOnError: true)!;
            if (Activator.CreateInstance(type) is not IClrPluginModule module)
                throw new InvalidOperationException($"'{definition.EntryType}' must implement the shared IClrPluginModule contract.");
            var plugin = module.CreatePlugin() ?? throw new InvalidOperationException("The module returned no plugin.");
            return new Lease(context, plugin, destination);
        }
        catch
        {
            if (context is null) Directory.Delete(destination, recursive: true);
            else
            {
                var observation = new ClrUnloadObservation(new WeakReference(context), destination) { UnloadRequested = true };
                unloads.Add(observation);
                context.Unload();
            }
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Plugin bundles must contain regular files and directories: {entry.FullName}");
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo directory)
            {
                Directory.CreateDirectory(target);
                CopyDirectory(directory.FullName, target);
            }
            else File.Copy(entry.FullName, target);
        }
    }

    private ClrUnloadObservation Retire(Lease lease)
    {
        lease.Plugin = null;
        var context = lease.Context!;
        lease.Context = null;
        var observation = new ClrUnloadObservation(new WeakReference(context), lease.Directory) { UnloadRequested = true };
        unloads.Add(observation);
        context.Unload();
        return observation;
    }

    /// <summary>
    /// Releases resources used by this instance.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (replacementScope.Value is { Active: true })
            throw new InvalidOperationException("Cannot dispose the CLR resolver from its replacement callback; dispose it after replacement completes.");
        await mutation.WaitAsync().ConfigureAwait(false);
        try
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (disposed) return;
                disposed = true;
                foreach (var lease in loaded.Values) Retire(lease);
                loaded.Clear();
            }
            finally { gate.Release(); }
        }
        finally { mutation.Release(); }
    }

    private sealed class Lease(BundleContext context, IPlugin plugin, string directory)
    {
        public BundleContext? Context = context;
        public IPlugin? Plugin = plugin;
        public string Directory = directory;
    }

    private sealed class BundleContext(string main, IReadOnlyDictionary<string, Assembly> shared)
        : AssemblyLoadContext($"Cordis:{Guid.NewGuid():N}", isCollectible: true)
    {
        private readonly AssemblyDependencyResolver resolver = new(main);
        private readonly string directory = Path.GetDirectoryName(main)!;

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (shared.TryGetValue(assemblyName.Name!, out var contract)) return contract;
            var path = resolver.ResolveAssemblyToPath(assemblyName);
            if (path is null)
            {
                var local = Path.Combine(directory, assemblyName.Name + ".dll");
                if (File.Exists(local)) path = local;
            }
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? 0 : LoadUnmanagedDllFromPath(path);
        }
    }
}
