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

/// <summary>Optional generated Typert artifact export of an explicitly named CLR module entry.</summary>
/// <remarks>The factory runs in the same bundle load context as CreatePlugin. It does not activate a Fiber.</remarks>
public interface IClrTypertModule
{
    /// <summary>Return this entry's generated package contribution.</summary>
    TypertContribution CreateTypertContribution();
}

/// <summary>The assembly path is relative to the bundle root; the complete directory is copied.</summary>
public sealed record ClrModuleDefinition(string BundleDirectory, string AssemblyPath, string EntryType);

/// <summary>Unload request, garbage collection and shadow deletion are independently observable.</summary>
public sealed class ClrUnloadObservation
{
    internal ClrUnloadObservation(WeakReference context, string directory) =>
        (LoadContext, ShadowDirectory) = (context, directory);

    /// <summary>
    /// Gets the load context value.
    /// </summary>
    public WeakReference LoadContext
    {
        get;
    }

    /// <summary>
    /// Gets the shadow directory value.
    /// </summary>
    public string ShadowDirectory
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
    /// Gets the shadow deleted value.
    /// </summary>
    public bool ShadowDeleted => !Directory.Exists(ShadowDirectory);

    /// <summary>Does not force GC. Retained references or an interrupted unload can prevent shadow deletion even after the context wrapper is collected.</summary>
    public bool TryDeleteShadow()
    {
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
public sealed class ClrModuleResolver : IModuleResolver, ITypertArtifactResolver, IAsyncDisposable
{
    private readonly Dictionary<string, ClrModuleDefinition> definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Lease> loaded = new(StringComparer.Ordinal);

    private readonly Dictionary<string, Bundle> bundles = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly HashSet<string> replacingTargets = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

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

    /// <summary>
    /// Initializes a new instance of the <see cref="ClrModuleResolver"/> type.
    /// </summary>
    public ClrModuleResolver(string shadowRoot, IEnumerable<Assembly>? sharedContracts = null)
    {
        this.shadowRoot = Path.GetFullPath(shadowRoot);
        shared = new[]
            {
                typeof(IPlugin).Assembly,
                typeof(IClrPluginModule).Assembly,
                typeof(ITypertArtifactResolver).Assembly
            }
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
            if (bundles.TryGetValue(BundleSource(definition), out var bundle) && bundle.Replacing)
                throw new InvalidOperationException(
                    "Cannot register another export while its CLR bundle is being replaced.");
            if (replacingTargets.Contains(BundleSource(definition)) ||
                definitions.TryGetValue(specifier, out var current) &&
                bundles.TryGetValue(BundleSource(current), out var previous) && previous.Replacing)
                throw new InvalidOperationException("Cannot change an export while its CLR bundle is being replaced.");
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
            return GetLease(specifier).Plugin!;
        }
        finally
        {
            gate.Release();
        }
    }

    async ValueTask<TypertContribution?> ITypertArtifactResolver.ResolveAsync(
        string specifier,
        Uri baseUri,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var lease = GetLease(specifier);
            var definition = definitions[specifier];
            var key = DefinitionExport(definition);
            if (lease.Bundle.Artifacts.TryGetValue(key, out var artifact))
                return artifact;
            artifact = lease.Bundle.Modules[key] is IClrTypertModule module
                ? module.CreateTypertContribution() ??
                throw new InvalidOperationException("The CLR Typert module returned no contribution.")
                : null;
            lease.Bundle.Artifacts.Add(key, artifact);
            return artifact;
        }
        finally
        {
            gate.Release();
        }
    }

    private Lease GetLease(string specifier)
    {
        if (loaded.TryGetValue(specifier, out var lease))
            return lease;
        if (!definitions.TryGetValue(specifier, out var definition))
            throw new KeyNotFoundException($"No CLR module mapping for '{specifier}'.");
        lease = Prepare(definition);
        loaded.Add(specifier, lease);
        bundles[lease.Bundle.Source] = lease.Bundle;
        return lease;
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
        Dictionary<string, ClrModuleDefinition> replacements;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            replacements = definitions.TryGetValue(specifier, out var current)
                ? definitions
                    .Where(pair => bundles.Comparer.Equals(BundleSource(pair.Value), BundleSource(current)) &&
                        DefinitionExport(pair.Value) == DefinitionExport(current))
                    .ToDictionary(pair => pair.Key, _ => definition, StringComparer.Ordinal)
                : new Dictionary<string, ClrModuleDefinition>(StringComparer.Ordinal)
                {
                    [specifier] = definition
                };
        }
        finally
        {
            gate.Release();
        }

        return await ReplaceAsync(
                replacements,
                pairs => switchFibers(pairs.First().Key, pairs.First().Value),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Prepare and switch every registered export of one CLR bundle in a shared candidate load context.</summary>
    /// <remarks>All registered specifiers for the current bundle must be supplied, including unloaded exports.
    /// The callback owns Fiber switching and recovery; only route publication is atomic. Candidate side effects
    /// and product-wide rollback are not a transaction. Registering another alias during the callback is refused.</remarks>
    public async ValueTask<ClrUnloadObservation> ReplaceAsync(
        IReadOnlyDictionary<string, ClrModuleDefinition> replacements,
        Func<IReadOnlyDictionary<IPlugin, IPlugin>, ValueTask> switchFibers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        ArgumentNullException.ThrowIfNull(switchFibers);
        if (replacements.Count == 0)
            throw new ArgumentException("At least one registered CLR export must be replaced.", nameof(replacements));
        replacements = replacements.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (replacementScope.Value is { Active: true })
            throw new InvalidOperationException("CLR replacements cannot be nested.");
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = new Dictionary<string, Lease>(StringComparer.Ordinal);
            var pairs = new Dictionary<IPlugin, IPlugin>(ReferenceEqualityComparer.Instance);
            Bundle previousBundle, candidateBundle;
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                var first = replacements.Keys.FirstOrDefault(loaded.ContainsKey);
                if (first is null)
                    throw new InvalidOperationException("Resolve the module before replacing it.");
                previousBundle = loaded[first].Bundle;
                var registered = definitions
                    .Where(pair => bundles.Comparer.Equals(BundleSource(pair.Value), previousBundle.Source))
                    .Select(pair => pair.Key)
                    .ToHashSet(StringComparer.Ordinal);
                if (!registered.SetEquals(replacements.Keys))
                    throw new InvalidOperationException(
                        "Replace every registered export of the shared CLR bundle together.");
                var sources = replacements.Values.Select(BundleSource).Distinct(bundles.Comparer).ToArray();
                if (sources.Length != 1)
                    throw new ArgumentException(
                        "Replacement exports must belong to one candidate CLR bundle.",
                        nameof(replacements));
                if (bundles.TryGetValue(sources[0], out var existing) && !ReferenceEquals(existing, previousBundle))
                    throw new InvalidOperationException(
                        "The candidate bundle already belongs to another loaded generation.");
                candidateBundle = null!;
                try
                {
                    foreach (var (specifier, definition) in replacements)
                    {
                        var candidate = Prepare(definition, candidateBundle, reuseCurrent: false);
                        candidateBundle = candidate.Bundle;
                        candidates.Add(specifier, candidate);
                        if (!loaded.TryGetValue(specifier, out var lease))
                            continue;
                        if (pairs.TryGetValue(lease.Plugin!, out var duplicate) &&
                            !ReferenceEquals(duplicate, candidate.Plugin))
                            throw new InvalidOperationException(
                                "Aliases of one CLR export must retain the same replacement export.");
                        pairs[lease.Plugin!] = candidate.Plugin!;
                    }
                }
                catch
                {
                    foreach (var candidate in candidates.Values)
                        Retire(candidate);
                    throw;
                }

                previousBundle.Replacing = true;
                replacingTargets.Add(candidateBundle.Source);
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
                    await switchFibers(new System.Collections.ObjectModel.ReadOnlyDictionary<IPlugin, IPlugin>(pairs))
                        .ConfigureAwait(false);
                }
                catch
                {
#pragma warning disable CA2016 // Rollback must finish even when replacement is canceled.
                    await gate.WaitAsync().ConfigureAwait(false);
#pragma warning restore CA2016
                    try
                    {
                        foreach (var candidate in candidates.Values)
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
                    // A callback may import another alias of an already cached old export.
                    var retiring =
                        loaded.Values.Where(lease => ReferenceEquals(lease.Bundle, previousBundle)).ToArray();
                    foreach (var (specifier, definition) in replacements)
                        definitions[specifier] = definition;
                    foreach (var (specifier, candidate) in candidates)
                        loaded[specifier] = candidate;
                    bundles[candidateBundle.Source] = candidateBundle;
                    ClrUnloadObservation? observation = null;
                    foreach (var lease in retiring)
                        observation = Retire(lease) ?? observation;
                    return observation!;
                }
                finally
                {
                    gate.Release();
                }
            }
            finally
            {
                await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    previousBundle.Replacing = false;
                    replacingTargets.Remove(candidateBundle.Source);
                }
                finally
                {
                    gate.Release();
                }

                scope.Active = false;
                replacementScope.Value = null;
            }
        }
        finally
        {
            mutation.Release();
        }
    }

    private static string BundleSource(ClrModuleDefinition definition) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(definition.BundleDirectory));

    private static (string Assembly, string Entry) ExportKey(string assembly, string entry) =>
        (OperatingSystem.IsWindows() ? assembly.ToUpperInvariant() : assembly, entry);

    private static (string Assembly, string Entry) DefinitionExport(ClrModuleDefinition definition)
    {
        var source = BundleSource(definition);
        return ExportKey(
            Path.GetRelativePath(source, Path.GetFullPath(definition.AssemblyPath, source)),
            definition.EntryType);
    }

    private Lease Prepare(ClrModuleDefinition definition, Bundle? bundle = null, bool reuseCurrent = true)
    {
        var source = BundleSource(definition);
        var shadowRelative = Path.GetRelativePath(source, shadowRoot);
        if (!Path.IsPathRooted(shadowRelative) && shadowRelative != ".." && !shadowRelative.StartsWith(
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
        if (bundle is null && reuseCurrent)
            bundles.TryGetValue(source, out bundle);
        if (reuseCurrent && bundle is null && replacingTargets.Contains(source))
            throw new InvalidOperationException("Cannot import the candidate CLR bundle before replacement commits.");
        if (reuseCurrent && bundle is { Replacing: true } &&
            !bundle.Plugins.ContainsKey(ExportKey(relative, definition.EntryType)))
            throw new InvalidOperationException(
                "Cannot import an uncached export while its CLR bundle is being replaced.");
        var fresh = bundle is null;
        var destination = bundle?.Directory ?? Path.Combine(shadowRoot, Guid.NewGuid().ToString("N"));
        BundleContext? context = bundle?.Context;
        try
        {
            if (fresh)
            {
                Directory.CreateDirectory(destination);
                CopyDirectory(source, destination);
            }

            var shadowMain = Path.Combine(destination, relative);
            context ??= new BundleContext(shadowMain, shared);
            bundle ??= new Bundle(source, context, destination);
            var key = ExportKey(relative, definition.EntryType);
            if (!bundle.Plugins.TryGetValue(key, out var plugin))
            {
                var assembly = context.LoadFromAssemblyPath(shadowMain);
                var type = assembly.GetType(definition.EntryType, throwOnError: true)!;
                if (Activator.CreateInstance(type) is not IClrPluginModule module)
                    throw new InvalidOperationException(
                        $"'{definition.EntryType}' must implement the shared IClrPluginModule contract.");
                plugin = module.CreatePlugin() ?? throw new InvalidOperationException("The module returned no plugin.");
                bundle.Plugins.Add(key, plugin);
                bundle.Modules.Add(key, module);
            }

            bundle.References++;
            return new Lease(bundle, plugin);
        }
        catch
        {
            if (!fresh)
                throw;
            if (context is null)
                Directory.Delete(destination, recursive: true);
            else
            {
                RequestUnload(context, destination);
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
            else
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

    private ClrUnloadObservation? Retire(Lease lease)
    {
        lease.Plugin = null;
        var bundle = lease.Bundle;
        if (--bundle.References != 0)
            return null;
        if (bundles.TryGetValue(bundle.Source, out var current) && ReferenceEquals(bundle, current))
            bundles.Remove(bundle.Source);
        bundle.Plugins.Clear();
        bundle.Modules.Clear();
        bundle.Artifacts.Clear();
        var context = bundle.Context!;
        bundle.Context = null;
        return RequestUnload(context, bundle.Directory);
    }

    private ClrUnloadObservation RequestUnload(BundleContext context, string directory)
    {
        var observation = new ClrUnloadObservation(new WeakReference(context), directory)
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

    private sealed class Bundle(string source, BundleContext context, string directory)
    {
        public string Source = source;
        public BundleContext? Context = context;
        public string Directory = directory;
        public readonly Dictionary<(string Assembly, string Entry), IPlugin> Plugins = [];
        public readonly Dictionary<(string Assembly, string Entry), IClrPluginModule> Modules = [];
        public readonly Dictionary<(string Assembly, string Entry), TypertContribution?> Artifacts = [];
        public int References;
        public bool Replacing;
    }

    private sealed class Lease(Bundle bundle, IPlugin plugin)
    {
        public Bundle Bundle = bundle;
        public IPlugin? Plugin = plugin;
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
