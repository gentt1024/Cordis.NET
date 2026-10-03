using Cordis;
using Cordis.Clr;
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
    public static WeakReference? LastFailure { get; private set; }
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
        return new Plugin<object?> { Apply = (_, _) => { } };
    }
}

public sealed class ConfigurationEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() => new Plugin<CollectibleSettings>
    {
        Configuration = new ConfigSchema<CollectibleSettings>(raw => raw is string value
            ? ConfigResult<CollectibleSettings>.Success(new(value)) : ConfigResult<CollectibleSettings>.Failure("expected string"),
            ConfigDescriptor.Object(("value", ConfigDescriptor.String().Volatile()), ("marker", ConfigDescriptor.Any().Optional().Volatile())))
            .WithVolatile("value", settings => settings.Value).WithVolatile("marker", settings => settings.Marker),
        Apply = (ctx, _) =>
        {
            ctx.Provide("configured-reference", ctx.Fiber.GetConfigReference<TypedMarker?>("marker"));
            ctx.Provide("configured-value", ctx.Fiber.GetConfigReference<string>("value"));
        }
    };
}

public sealed class PrimitiveConfigurationEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() => new Plugin<Dictionary<string, object?>>
    {
        Configuration = new ConfigSchema<Dictionary<string, object?>>(raw => raw is Dictionary<string, object?> value
            ? ConfigResult<Dictionary<string, object?>>.Success(value)
            : ConfigResult<Dictionary<string, object?>>.Failure("expected plain dictionary"),
            ConfigDescriptor.Object(("value", ConfigDescriptor.Number().Volatile())))
            .WithVolatile("value", settings => (int)settings["value"]!),
        Apply = (ctx, _) => ctx.Provide("primitive-reference", ctx.Fiber.GetConfigReference<int>("value")),
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
    public IPlugin CreatePlugin() => new Plugin<object?>
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
                var stream = File.Open(Path.Combine(directory, $"resource-{ctx.Fiber.Uid}.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                trace.Add("start:" + Version + ":" + config);
                return (Action)(() => { stream.Dispose(); trace.Add("stop:" + Version); });
            });
            ctx.Provide("versioned", new VersionedService(resource));
#if VERSION_BAD
            await Task.Yield();
            if (Equals(config, "direct-config")) throw new InvalidOperationException("bad candidate activation");
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
