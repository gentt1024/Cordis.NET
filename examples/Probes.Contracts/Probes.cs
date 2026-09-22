using Cordis;

namespace Cordis.Example.Probes;

public interface IProbeRegistry
{
    string Version { get; }
    IAsyncDisposable Register(string name, Func<string> read);
    IReadOnlyDictionary<string, string> Snapshot();
}

// A plain shared query capability needs no service views or mutable State wrapper.
public interface IProbeFormatter
{
    string Format(string value);
}

public sealed record ProbeChanged(string Id, string Value);

public static class ProbeContract
{
    public const string Name = "probes";
    public const string Formatter = "probe-formatter";
    public static readonly EventKey<ProbeChanged> Changed = new("probes/changed");
}

public static class ProbeContextExtensions
{
    extension(Context context)
    {
        // Declaration and access remain separate: this never inserts Inject.
        public IProbeRegistry Probes => context.Reflect.Read<IProbeRegistry>(ProbeContract.Name);
        public IProbeFormatter ProbeFormatter => context.Reflect.Read<IProbeFormatter>(ProbeContract.Formatter);
    }
}
