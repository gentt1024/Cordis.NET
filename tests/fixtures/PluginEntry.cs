using Cordis;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Fixtures;
using Cordis.Fixtures.Private;
using System.Runtime.Loader;

namespace VersionedPlugin;

public sealed class InvalidEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() => null!;
}

public sealed record TypedMarker(string Value);

public sealed record CollectibleSettings(string Value, TypedMarker? Marker = null);

public sealed class CollectibleUnloadException(string message) : Exception(message);

public sealed class UnloadFailureEntry : IClrPluginModule
{
    public static WeakReference? LastFailure
    {
        get;
        private set;
    }

    public IPlugin CreatePlugin()
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(UnloadFailureEntry).Assembly)!;
        Action<AssemblyLoadContext>? handler = null;
        handler = owner =>
        {
            owner.Unloading -= handler;
            var error = new CollectibleUnloadException("collectible unload observer rejected");
            LastFailure = new WeakReference(error);
            throw error;
        };
        context.Unloading += handler;
        return new Plugin<object?>
        {
            Apply = (_, _) =>
            {
            }
        };
    }
}

public sealed class ConfigurationEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() =>
        new Plugin<CollectibleSettings>
        {
            Configuration = new ConfigSchema<CollectibleSettings>(
                    raw =>
                        raw is string value
                            ? ConfigResult<CollectibleSettings>.Success(new(value))
                            : ConfigResult<CollectibleSettings>.Failure("expected string"),
                    ConfigDescriptor.Object(
                        ("value", ConfigDescriptor.String().Volatile()),
                        ("marker", ConfigDescriptor.Any().Optional().Volatile())))
                .WithVolatile("value", settings => settings.Value)
                .WithVolatile("marker", settings => settings.Marker),
            Apply = (ctx, _) =>
            {
                ctx.Provide("configured-reference", ctx.Fiber.GetConfigReference<TypedMarker?>("marker"));
                ctx.Provide("configured-value", ctx.Fiber.GetConfigReference<string>("value"));
            }
        };
}

public sealed class PrimitiveConfigurationEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() =>
        new Plugin<Dictionary<string, object?>>
        {
            Configuration = new ConfigSchema<Dictionary<string, object?>>(
                raw => raw is Dictionary<string, object?> value
                    ? ConfigResult<Dictionary<string, object?>>.Success(value)
                    : ConfigResult<Dictionary<string, object?>>.Failure("expected plain dictionary"),
                ConfigDescriptor.Object(("value", ConfigDescriptor.Number().Volatile()))).WithVolatile(
                "value",
                settings => (int)settings["value"]!),
            Apply = (ctx, _) => ctx.Provide("primitive-reference", ctx.Fiber.GetConfigReference<int>("value")),
        };
}

public sealed class ComposedConfigurationEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() =>
        new Plugin<CollectibleSettings>
        {
            Configuration = ConfigObject<CollectibleSettings>
                .Create(raw =>
                    raw is IReadOnlyDictionary<string, object?> map && map.GetValueOrDefault("value") is string value
                        ? ConfigResult<CollectibleSettings>.Success(new(value))
                        : ConfigResult<CollectibleSettings>.Failure("expected value map"))
                .Field("value", ConfigDescriptor.String().Volatile(), settings => settings.Value)
                .Field("marker", ConfigDescriptor.Any().Optional().Volatile(), settings => settings.Marker)
                .Build(),
            Apply = (ctx, _) =>
            {
                ctx.Provide("configured-reference", ctx.Fiber.GetConfigReference<TypedMarker?>("marker"));
                ctx.Provide("configured-value", ctx.Fiber.GetConfigReference<string>("value"));
            },
        };
}

public sealed class Entry : IClrPluginModule
{
#if VERSION_BAD
    private const string Version = "bad-v2";
#elif VERSION_TWO
    private const string Version = "v2";
#else
    private const string Version = "v1";
#endif
    public IPlugin CreatePlugin() =>
        new Plugin<object?>
        {
            Name = "versioned-fixture",
            Inject = ["trace"],
#if VERSION_BAD
            ApplyAsync = async (ctx, config) =>
#else
            Apply = (ctx, config) =>
#endif
            {
                var trace = ctx.Get<List<string>>("trace")!;
                var directory = Path.GetDirectoryName(typeof(Entry).Assembly.Location)!;
                var resource = File.ReadAllText(Path.Combine(directory, "assets", "resource.txt")).Trim();
                ctx.Effect(() =>
                {
                    var stream = File.Open(
                        Path.Combine(directory, $"resource-{ctx.Fiber.Uid}.lock"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    trace.Add("start:" + Version + ":" + config);
                    return (Action)(() =>
                    {
                        stream.Dispose();
                        trace.Add("stop:" + Version);
                    });
                });
                ctx.Provide("versioned", new VersionedService(resource));
#if VERSION_BAD
                await Task.Yield();
                if (Equals(config, "direct-config"))
                    throw new InvalidOperationException("bad candidate activation");
#endif
            }
        };

    private sealed class VersionedService(string resource) : IVersionedService
    {
        public string Version => Entry.Version;
        public string Resource => resource;
        public string Dependency => PrivateDependency.Value;
    }
}

public sealed class RetirementEntry : IClrPluginModule
{
#if VERSION_BAD
    private const string Version = "bad-v2";
#elif VERSION_TWO
    private const string Version = "v2";
#else
    private const string Version = "v1";
#endif

    public IPlugin CreatePlugin() => new Plugin<string>
    {
        Inject = ["retirement-probe"],
        Apply = (ctx, id) =>
        {
            var probe = ctx.Get<RetirementProbe>("retirement-probe")!;
            if (Version == "v1" && id == "b" && probe.FailRecovery)
                throw new InvalidOperationException("V1 recovery refused");
            var service = new RetirementService(probe, id);
            ctx.Effect(() => (IAsyncDisposable)service);
            ctx.Provide("retirement-" + id, service);
            if (Version == "bad-v2" && id == "b")
                throw new InvalidOperationException("retirement candidate activation failed");
        }
    };

    private sealed class RetirementService : IRetirementService
    {
        private readonly RetirementProbe probe;
        private readonly string id;
        private readonly object gate = new();
        private readonly FileStream? resource;
        private bool accepting = true;
        private Task<string>? active;

        public RetirementService(RetirementProbe probe, string id)
        {
            this.probe = probe;
            this.id = id;
            if (probe.ExclusiveResources)
                resource = File.Open(
                    Path.Combine(probe.ResourceDirectory, id + ".lock"),
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            probe.Tick += OnTick;
            probe.History.Enqueue("start:" + Version + ":" + id);
            probe.Contributions.Enqueue(new(Version, id, this));
        }

        public string VerifyReady()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(!accepting || resource is { CanRead: false }, this);
                return Version;
            }
        }

        public string Execute()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(
                    !accepting || !probe.PerformBusiness(id, () => probe.History.Enqueue("call:" + Version + ":" + id)),
                    this);
                return Version;
            }
        }

        public Task<string> ExecuteAsync()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(
                    !accepting || !probe.PerformBusiness(
                        id,
                        () =>
                        {
                        }),
                    this);
                return active = RunCallAsync();
            }
        }

        private async Task<string> RunCallAsync()
        {
            probe.Entered.TrySetResult();
            await probe.Release.Task;
            ObjectDisposedException.ThrowIf(
                !probe.CompleteBusiness(id, () => probe.History.Enqueue("drained:" + Version + ":" + id)),
                this);
            return Version;
        }

        public Action CaptureCallback() => () => Execute();

        private void OnTick()
        {
            lock (gate)
                if (accepting)
                    probe.PerformBusiness(id, () => probe.History.Enqueue("tick:" + Version + ":" + id));
        }

        public void CloseAdmission()
        {
            lock (gate)
                accepting = false;
        }

        public async ValueTask DisposeAsync()
        {
            if (Version == "v1" && id == "a" && probe.FailStop)
                throw new InvalidOperationException("old generation refused to stop");
            if (Version == "bad-v2" && id == "b" && probe.FailCandidateStop)
                throw new InvalidOperationException("candidate generation refused to stop");
            CloseAdmission();
            probe.Tick -= OnTick;
            probe.Stopping.TrySetResult();
            try
            {
                if (active is not null)
                    await active;
            }
            finally
            {
                resource?.Dispose();
                probe.History.Enqueue("stop:" + Version + ":" + id);
            }
        }
    }
}
