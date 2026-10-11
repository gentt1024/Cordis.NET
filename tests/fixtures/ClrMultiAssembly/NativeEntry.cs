using System.Runtime.InteropServices;
using Cordis;
using Cordis.Clr;

namespace IndependentBundle;

public sealed class NativeEntry : IClrPluginModule
{
#if WINDOWS
    private const string Library = "CordisBundleNative.dll";
#if SECOND
    [DllImport(Library, EntryPoint = "timeGetTime")]
    private static extern uint Probe();
#else
    [DllImport(Library, EntryPoint = "GetFileVersionInfoSizeW", CharSet = CharSet.Unicode)]
    private static extern uint Probe(string filename, out uint handle);
#endif
#else
    private const string Library = "libCordisBundleNative.so";
    [DllImport(Library, EntryPoint = "cos")]
    private static extern double Probe(double value);
#endif

    private static string ReadNative()
    {
#if WINDOWS
#if SECOND
        var valid = Probe() != 0;
#else
        var valid = Probe(Environment.ProcessPath!, out _) != 0;
#endif
#else
        var valid = Probe(0) == 1;
#endif
        if (!valid)
            throw new InvalidOperationException("The private native library probe failed.");
#if SECOND
        return "native-b";
#else
        return "native-a";
#endif
    }

    public IPlugin CreatePlugin() => new Plugin<object?>
    {
#if DELAYED
        Name = "delayed",
        Apply = static (_, observer) => ((Action<string>)observer!)(ReadNative()),
#else
        Name = ReadNative(),
        Apply = static (_, _) =>
        {
        },
#endif
    };
}
