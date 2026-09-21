using Cordis;
using Cordis.Clr;
using Cordis.Fixtures;
using Cordis.Fixtures.Private;

namespace VersionedPlugin;

public sealed class InvalidEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() => null!;
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
