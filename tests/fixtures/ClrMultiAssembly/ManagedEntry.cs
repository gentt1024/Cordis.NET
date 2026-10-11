using Cordis;
using Cordis.Clr;

namespace IndependentBundle;

public sealed class Entry : IClrPluginModule
{
    public IPlugin CreatePlugin() => new Plugin<object?>
    {
#if DELAYED
        Name = "delayed",
        Apply = static (_, observer) => ((Action<string>)observer!)(ReadPrivate()),
#else
#if PRIVATE
        Name = ReadPrivate(),
#else
        Name = "entry-a",
#endif
        Apply = static (_, _) =>
        {
        },
#endif
    };

#if PRIVATE
    private static string ReadPrivate() =>
#if BRIDGE
        Bridge.Marker.Value();
#else
        Dependency.Marker.Value();
#endif
#endif
}

public sealed class WrongEntry
{
}
