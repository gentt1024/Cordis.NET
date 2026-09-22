using Cordis;
using Cordis.Clr;
using Cordis.Example.Probes;

namespace Cordis.ProbeFixture;

public sealed class Entry : IClrPluginModule
{
    public IPlugin CreatePlugin() => ProbeModule.Create();
}

public sealed class RejectedEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() => new Plugin<object?>
    {
        Name = "rejected-probe",
        Config = _ => ConfigResult<object?>.Failure("candidate binding rejected"),
        Apply = (_, _) => { },
    };
}

public sealed class FailingEntry : IClrPluginModule
{
    public IPlugin CreatePlugin() => new Plugin<object?>
    {
        Name = "optional-failure",
        Apply = (ctx, _) =>
        {
            var error = new ProbeActivationException("collectible startup failure");
            ctx.Logger.Error(error);
            throw error;
        },
    };
}

public sealed class ProbeActivationException(string message) : Exception(message);
