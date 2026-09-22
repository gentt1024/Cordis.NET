using System.Text.Json.Serialization;
using Cordis;
using Cordis.Composition;

namespace Cordis.Example.Probes;

public sealed record ProbeOptions(string Prefix = "status");

[JsonSerializable(typeof(ProbeOptions))]
internal partial class ProbeJsonContext : JsonSerializerContext;

public static class ProbeModule
{
#if PROBE_V2
    private const string Version = "v2";
#else
    private const string Version = "v1";
#endif
    public static IPlugin Create() => new Plugin<ProbeOptions>
    {
        Name = "probe-provider",
        Config = ConfigBinding.FromJsonTypeInfo(ProbeJsonContext.Default.ProbeOptions,
            validate: options => string.IsNullOrWhiteSpace(options.Prefix) ? ["$.Prefix must not be blank"] : []),
        Apply = (ctx, options) =>
        {
            _ = new ProbeService(ctx);
            ctx.Provide(ProbeContract.Formatter, new ProbeFormatter(options.Prefix));
        },
    };

    public static List<EntryOptions> ReadPatch() => PatchResources.Read(typeof(ProbeModule).Assembly, "Probes.cordis.patch.yml");

    private sealed class ProbeFormatter(string prefix) : IProbeFormatter
    {
        public string Format(string value) => prefix + ":" + value;
    }

    private sealed class ProbeState
    {
        internal Dictionary<string, Func<string>> Readers { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ProbeService : Service<ProbeState>, IProbeRegistry
    {
        public ProbeService(Context context) : base(context, ProbeContract.Name, new()) { }
        private ProbeService(ProbeService provider, Context caller) : base(provider, caller) { }
        protected override Service CreateView(Context caller) => new ProbeService(this, caller);
        public string Version => ProbeModule.Version;

        public IAsyncDisposable Register(string name, Func<string> read)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(read);
            return Context.Effect(() =>
            {
                State.Readers.Add(name, read);
                return (Action)(() => State.Readers.Remove(name));
            }, "probe:" + name);
        }

        public IReadOnlyDictionary<string, string> Snapshot()
            => State.Readers.ToDictionary(pair => pair.Key, pair => pair.Value(), StringComparer.Ordinal);
    }
}
