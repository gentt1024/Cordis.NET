using System.Text.Json;

namespace Cordis.Composition;

/// <summary>Fiber-owned generated package, schema, Remote and dependency-provider registry.</summary>
/// <remarks>Use the host Context execution domain for reads and mutations. Registration never stores live service instances.</remarks>
public sealed class TypertRegistry(Context context)
{
    private readonly Dictionary<string, TypertContribution> packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SchemaEntry> schemas = new(StringComparer.Ordinal);
    private readonly DescriptorStore local = new("local");
    private readonly DescriptorStore remotes = new("remote");
    private readonly Dictionary<string, object> remotePackages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderEntry<TypertLookupProvider>> lookups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypertLookupDefinition> definitions = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ProviderEntry<Func<object?, ValueTask<object?>>>> lookupResolvers =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, ProviderEntry<TypertHostContextAdapter>> hosts = new(StringComparer.Ordinal);

    private readonly Dictionary<string, ProviderEntry<Func<object?, ValueTask<Context?>>>> hostResolvers =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, ProviderEntry<TypertClientContextAdapter>>
        clients = new(StringComparer.Ordinal);

    private readonly List<Action<TypertRegistryChange>> listeners = [];
    private readonly Dictionary<string, object> lookupTokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> hostTokens = new(StringComparer.Ordinal);

    internal object? LocalToken(string endpoint)
    {
        EnsureAccess();
        return local.Token(endpoint);
    }

    internal object? LookupToken(string key)
    {
        EnsureAccess();
        return lookupTokens.TryGetValue(key, out var token) ? token : null;
    }

    internal object? HostContextToken(string key)
    {
        EnsureAccess();
        return hostTokens.TryGetValue(key, out var token) ? token : null;
    }

    /// <summary>Register a complete generated contribution atomically under the caller's Fiber.</summary>
    public EffectHandle Register(Context owner, TypertContribution contribution)
    {
        EnsureOwner(owner);
        ArgumentNullException.ThrowIfNull(contribution);
        contribution = Snapshot(contribution);
        ValidateIdentity(contribution.Package, "package");
        if (contribution.Face is not ("host" or "client"))
            throw new ArgumentException("A Typert face must be host or client.", nameof(contribution));
        var packageKey = contribution.Package + "#" + contribution.Face;
        if (packages.ContainsKey(packageKey))
            throw new InvalidOperationException($"Typert package face '{packageKey}' is already registered.");
        var entries = contribution
            .Schemas.Select(factory => new SchemaEntry(contribution.Package + "#" + factory.Name, factory))
            .ToArray();
        var batch = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            ValidateIdentity(entry.Factory.Name, "schema");
            ArgumentNullException.ThrowIfNull(entry.Factory.Create);
            if (!batch.Add(entry.Key) || schemas.ContainsKey(entry.Key))
                throw new InvalidOperationException($"Typert schema '{entry.Key}' is already registered.");
        }

        var descriptors = contribution.Invocations.ToArray();
        local.Validate(descriptors);
        var token = new object();
        return owner.Effect(
            () =>
            {
                packages.Add(packageKey, contribution);
                foreach (var entry in entries)
                    schemas.Add(entry.Key, entry);
                local.Commit(token, descriptors);
                EmitDescriptors("local", descriptors);
                return (Action)(() =>
                {
                    if (ReferenceEquals(packages.GetValueOrDefault(packageKey), contribution))
                        packages.Remove(packageKey);
                    foreach (var entry in entries)
                        if (ReferenceEquals(schemas.GetValueOrDefault(entry.Key), entry))
                            schemas.Remove(entry.Key);
                    EmitDescriptors("local", local.Withdraw(token, descriptors));
                });
            },
            "typert.register()");
    }

    /// <summary>Register consumer-selected Remote descriptors under the caller's Fiber.</summary>
    public EffectHandle RegisterRemotes(Context owner, TypertRemoteContribution contribution)
    {
        EnsureOwner(owner);
        ArgumentNullException.ThrowIfNull(contribution);
        contribution = contribution with
        {
            Descriptors = SnapshotDescriptors(contribution.Descriptors)
        };
        ValidateIdentity(contribution.Package, "Remote package");
        if (remotePackages.ContainsKey(contribution.Package))
            throw new InvalidOperationException(
                $"Typert Remote package '{contribution.Package}' is already registered.");
        var descriptors = contribution.Descriptors.ToArray();
        remotes.Validate(descriptors);
        var token = new object();
        return owner.Effect(
            () =>
            {
                remotePackages.Add(contribution.Package, token);
                remotes.Commit(token, descriptors);
                EmitDescriptors("remote", descriptors);
                return (Action)(() =>
                {
                    if (remotePackages.TryGetValue(contribution.Package, out var current) &&
                        ReferenceEquals(current, token))
                        remotePackages.Remove(contribution.Package);
                    EmitDescriptors("remote", remotes.Withdraw(token, descriptors));
                });
            },
            "typert.remotes.register()");
    }

    /// <summary>Get the currently registered local invocation.</summary>
    public TypertInvocationDescriptor? GetLocal(string endpoint)
    {
        EnsureAccess();
        return local.Get(endpoint);
    }

    /// <summary>Check the strict local endpoint tombstone, retained after withdrawal.</summary>
    public bool HasSeenLocal(string endpoint)
    {
        EnsureAccess();
        return local.HasSeen(endpoint);
    }

    /// <summary>List live local invocation descriptors in registration order.</summary>
    public IReadOnlyList<TypertInvocationDescriptor> ListLocal()
    {
        EnsureAccess();
        return local.List();
    }

    /// <summary>Get a consumer-selected Remote invocation.</summary>
    public TypertInvocationDescriptor? GetRemote(string endpoint)
    {
        EnsureAccess();
        return remotes.Get(endpoint);
    }

    /// <summary>List live consumer-selected Remote descriptors.</summary>
    public IReadOnlyList<TypertInvocationDescriptor> ListRemotes()
    {
        EnsureAccess();
        return remotes.List();
    }

    /// <summary>Get the reflected package contribution by package and face.</summary>
    public TypertContribution? GetPackage(string package, string face = "host")
    {
        EnsureAccess();
        return packages.GetValueOrDefault(package + "#" + face);
    }

    /// <summary>List registered package contributions.</summary>
    public IReadOnlyList<TypertContribution> ListPackages()
    {
        EnsureAccess();
        return packages.Values.ToArray();
    }

    /// <summary>Get a success-cached schema projection by package#schema key.</summary>
    public JsonElement? GetSchema(string key)
    {
        EnsureAccess();
        return schemas.TryGetValue(key, out var entry) ? entry.Value : null;
    }

    /// <summary>Resolve a required registered schema projection.</summary>
    public JsonElement ResolveSchema(string key) =>
        GetSchema(key) ?? throw new KeyNotFoundException($"Typert schema '{key}' is unavailable.");

    /// <summary>Subscribe with caller-Fiber ownership. An observer failure never changes a committed operation.</summary>
    public EffectHandle Subscribe(Context owner, Action<TypertRegistryChange> listener)
    {
        EnsureOwner(owner);
        ArgumentNullException.ThrowIfNull(listener);
        return owner.Effect(
            () =>
            {
                listeners.Add(listener);
                return (Action)(() => listeners.Remove(listener));
            },
            "typert registry subscription");
    }

    /// <summary>Register an object lookup provider while retaining its stable declaration across unload.</summary>
    public EffectHandle RegisterLookup(Context owner, string key, TypertLookupProvider provider)
    {
        EnsureOwner(owner);
        ArgumentNullException.ThrowIfNull(provider);
        ValidateIdentity(key, "lookup key");
        ValidateIdentity(provider.Parameter, "lookup parameter");
        ValidateWire(provider.Wire, "lookup wire");
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.HostTypeSymbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider.WireTypeSymbol);
        ArgumentNullException.ThrowIfNull(provider.Resolve);
        var definition = new TypertLookupDefinition(
            key,
            provider.Parameter,
            provider.Wire,
            provider.HostTypeSymbol,
            provider.WireTypeSymbol);
        if (definitions.TryGetValue(key, out var previous) && previous != definition)
            throw new InvalidOperationException(
                $"Typert lookup '{key}' changed its wire declaration during this registry lifetime.");
        return RegisterProvider(owner, lookups, "lookup", key, provider, () => definitions[key] = definition);
    }

    /// <summary>Override a lookup's default policy for the calling Fiber without redefining its wire declaration.</summary>
    public EffectHandle ConfigureLookup(Context owner, string key, Func<object?, ValueTask<object?>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return RegisterProvider(owner, lookupResolvers, "lookup", key, resolver);
    }

    /// <summary>Get the live provider and its optional composition-owned resolver policy.</summary>
    public TypertLookupProvider? GetLookup(string key)
    {
        EnsureAccess();
        if (!lookups.TryGetValue(key, out var entry))
            return null;
        return lookupResolvers.TryGetValue(key, out var resolver)
            ? entry.Provider with
            {
                Resolve = resolver.Provider
            }
            : entry.Provider;
    }

    /// <summary>List retained lookup declarations, including unloaded providers.</summary>
    public IReadOnlyList<TypertLookupDefinition> LookupDefinitions()
    {
        EnsureAccess();
        return definitions.Values.ToArray();
    }

    /// <summary>Register one live Host wire-to-Context adapter.</summary>
    public EffectHandle RegisterHostContext(Context owner, string key, TypertHostContextAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ValidateWire(adapter.Wire, "Context wire");
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter.WireTypeSymbol);
        ArgumentNullException.ThrowIfNull(adapter.Resolve);
        return RegisterProvider(owner, hosts, "host-context", key, adapter);
    }

    /// <summary>Override a Host Context provider's default resolution policy.</summary>
    public EffectHandle ConfigureHostContext(Context owner, string key, Func<object?, ValueTask<Context?>> resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        return RegisterProvider(owner, hostResolvers, "host-context", key, resolver);
    }

    /// <summary>Get the live Host adapter and its optional composition-owned resolver.</summary>
    public TypertHostContextAdapter? GetHostContext(string key)
    {
        EnsureAccess();
        if (!hosts.TryGetValue(key, out var entry))
            return null;
        return hostResolvers.TryGetValue(key, out var resolver)
            ? entry.Provider with
            {
                Resolve = resolver.Provider
            }
            : entry.Provider;
    }

    /// <summary>Register one Client identity and Context projection adapter.</summary>
    public EffectHandle RegisterClientContext(Context owner, string key, TypertClientContextAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(adapter.Identity);
        ArgumentNullException.ThrowIfNull(adapter.Resolve);
        return RegisterProvider(owner, clients, "client-context", key, adapter);
    }

    /// <summary>Get the current Client Context adapter.</summary>
    public TypertClientContextAdapter? GetClientContext(string key)
    {
        EnsureAccess();
        return clients.GetValueOrDefault(key)?.Provider;
    }

    private EffectHandle RegisterProvider<T>(
        Context owner,
        Dictionary<string, ProviderEntry<T>> table,
        string kind,
        string key,
        T provider,
        Action? commit = null)
    {
        EnsureOwner(owner);
        ValidateIdentity(key, kind + " key");
        if (table.ContainsKey(key))
            throw new InvalidOperationException($"Typert {kind} provider '{key}' is already registered.");
        var entry = new ProviderEntry<T>(provider);
        return owner.Effect(
            () =>
            {
                commit?.Invoke();
                table.Add(key, entry);
                Emit(new(kind, key));
                return (Action)(() =>
                {
                    if (!ReferenceEquals(table.GetValueOrDefault(key), entry))
                        return;
                    table.Remove(key);
                    Emit(new(kind, key));
                });
            },
            $"typert.{kind}.register({key})");
    }

    private void EnsureAccess() => _ = context.Get<object>("typert", strict: false);

    private void EnsureOwner(Context owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        EnsureAccess();
        if (!ReferenceEquals(owner.Root, context.Root))
            throw new ArgumentException(
                "Typert registration owners must share the registry's Cordis root.",
                nameof(owner));
    }

    private void EmitDescriptors(string kind, IEnumerable<TypertInvocationDescriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
            Emit(new(kind, descriptor.Endpoint));
    }

    private void Emit(TypertRegistryChange change)
    {
        if (change.Kind == "lookup")
            lookupTokens[change.Key] = new object();
        else if (change.Kind == "host-context")
            hostTokens[change.Key] = new object();
        foreach (var listener in listeners.ToArray())
        {
            try
            {
                listener(change);
            }
            catch (Exception error)
            {
                try
                {
                    context.Logger.Warn($"Typert {change.Kind} observer for '{change.Key}' failed: {error}");
                }
                catch (Exception)
                {
                    /* A diagnostic observer cannot undo a committed registration. */
                }
            }
        }
    }

    private static void ValidateIdentity(string value, string subject)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('#', StringComparison.Ordinal))
            throw new ArgumentException($"Typert {subject} must be nonempty and contain no '#'.");
    }

    private static TypertContribution Snapshot(TypertContribution contribution) => contribution with
    {
        Schemas = Array.AsReadOnly(contribution.Schemas.ToArray()),
        Invocations = SnapshotDescriptors(contribution.Invocations),
        Model = contribution.Model with
        {
            Services = Array.AsReadOnly(
                contribution
                    .Model.Services.Select(service => service with
                    {
                        Members = Array.AsReadOnly(service.Members.ToArray()),
                        Types = Array.AsReadOnly(service.Types.ToArray()),
                    })
                    .ToArray()),
            Events = Array.AsReadOnly(contribution.Model.Events.ToArray()),
            Objects = Array.AsReadOnly(
                contribution
                    .Model.Objects.Select(value => value with
                    {
                        Members = Array.AsReadOnly(value.Members.ToArray()),
                        Types = Array.AsReadOnly(value.Types.ToArray()),
                    })
                    .ToArray()),
        },
    };

    private static IReadOnlyList<TypertInvocationDescriptor> SnapshotDescriptors(
        IReadOnlyList<TypertInvocationDescriptor> descriptors) =>
        Array.AsReadOnly(
            descriptors
                .Select(descriptor => descriptor with
                {
                    Parameters = Array.AsReadOnly(descriptor.Parameters.ToArray()),
                })
                .ToArray());

    private static void ValidateWire(string value, string subject)
    {
        if (string.IsNullOrEmpty(value) || value is "." or ".." || !value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '_' or '$' or '.' or '-'))
            throw new ArgumentException($"Typert {subject} must use RPC endpoint segment characters.");
    }

    private sealed record ProviderEntry<T>(T Provider);

    private sealed class SchemaEntry(string key, TypertSchemaFactory factory)
    {
        private JsonElement? value;

        public string Key
        {
            get;
        } = key;

        public TypertSchemaFactory Factory
        {
            get;
        } = factory;

        public JsonElement Value => value ??= Factory.Create().Clone();
    }

    private sealed class DescriptorStore(string kind)
    {
        private readonly Dictionary<string, DescriptorEntry> entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DescriptorEntry> ids = new(StringComparer.Ordinal);
        private readonly HashSet<string> history = new(StringComparer.Ordinal);

        public void Validate(IReadOnlyList<TypertInvocationDescriptor> descriptors)
        {
            var endpoints = new HashSet<string>(StringComparer.Ordinal);
            var batchIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var descriptor in descriptors)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);
                ValidateIdentity(descriptor.Service, "service");
                ValidateWire(descriptor.Namespace, "namespace");
                ValidateWire(descriptor.Method, "method");
                ValidateCodec(descriptor.Result);
                if (descriptor.Implementation is { } implementation)
                    ValidateWire(implementation, "implementation method");
                if (!endpoints.Add(descriptor.Endpoint) || entries.ContainsKey(descriptor.Endpoint))
                    throw new InvalidOperationException(
                        $"Typert {kind} endpoint '{descriptor.Endpoint}' is already registered.");
                if (!batchIds.Add(descriptor.Id) || ids.ContainsKey(descriptor.Id))
                    throw new InvalidOperationException(
                        $"Typert {kind} invocation id '{descriptor.Id}' is already registered.");
                var wires = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parameter in descriptor.Parameters)
                {
                    ValidateWire(parameter.Name, "parameter");
                    ValidateWire(parameter.Wire, "wire field");
                    ValidateCodec(parameter.Codec);
                    if (!wires.Add(parameter.Wire))
                        throw new ArgumentException(
                            $"Typert invocation '{descriptor.Id}' repeats wire field '{parameter.Wire}'.");
                    if (parameter.Lookup is { } lookup)
                    {
                        ValidateIdentity(lookup, "lookup key");
                        if (parameter.AcceptsUndefined)
                            throw new ArgumentException("A Typert lookup identity cannot be omitted.");
                    }
                }

                if (descriptor.Invocation is { } receiver)
                {
                    ValidateIdentity(receiver.Context, "Context key");
                    ValidateWire(receiver.Wire, "Context wire");
                    ValidateCodec(receiver.Codec);
                    if (!wires.Add(receiver.Wire) || descriptor.Scope is not null)
                        throw new ArgumentException(
                            "A Typert Context receiver cannot repeat a wire field or have a direct scope projection.");
                }

                if (descriptor.Scope is { } scope)
                {
                    var lookupParameters =
                        descriptor.Parameters.Where(parameter => parameter.Lookup is not null).ToArray();
                    if (lookupParameters.Length != 1 || lookupParameters[0].Lookup != scope.Context ||
                        lookupParameters[0].Wire != scope.Wire)
                        throw new ArgumentException(
                            "A Typert direct scope projection must select its only lookup parameter.");
                }

                if (descriptor.Uplink is not null && !descriptor.IsStream)
                    throw new ArgumentException("Typert uplink codecs require a stream invocation.");
                if (descriptor.Uplink is { } uplink)
                    ValidateCodec(uplink);
            }
        }

        public void Commit(object owner, IEnumerable<TypertInvocationDescriptor> descriptors)
        {
            foreach (var descriptor in descriptors)
            {
                var entry = new DescriptorEntry(owner, descriptor);
                entries.Add(descriptor.Endpoint, entry);
                ids.Add(descriptor.Id, entry);
                history.Add(descriptor.Endpoint);
            }
        }

        public IReadOnlyList<TypertInvocationDescriptor> Withdraw(
            object owner,
            IEnumerable<TypertInvocationDescriptor> descriptors)
        {
            var removed = new List<TypertInvocationDescriptor>();
            foreach (var descriptor in descriptors)
                if (entries.TryGetValue(descriptor.Endpoint, out var entry) && ReferenceEquals(entry.Owner, owner))
                {
                    entries.Remove(descriptor.Endpoint);
                    if (ReferenceEquals(ids.GetValueOrDefault(descriptor.Id), entry))
                        ids.Remove(descriptor.Id);
                    removed.Add(descriptor);
                }

            return removed;
        }

        public TypertInvocationDescriptor? Get(string endpoint) => entries.GetValueOrDefault(endpoint)?.Descriptor;
        public object? Token(string endpoint) => entries.GetValueOrDefault(endpoint);
        public bool HasSeen(string endpoint) => history.Contains(endpoint);

        public IReadOnlyList<TypertInvocationDescriptor> List() =>
            entries.Values.Select(entry => entry.Descriptor).ToArray();

        private sealed record DescriptorEntry(object Owner, TypertInvocationDescriptor Descriptor);

        private static void ValidateCodec(TypertCodec codec)
        {
            ArgumentNullException.ThrowIfNull(codec);
            if (string.IsNullOrEmpty(codec.TypeSymbol))
                throw new ArgumentException("A strict Typert codec needs a nonempty type symbol.");
        }
    }
}
