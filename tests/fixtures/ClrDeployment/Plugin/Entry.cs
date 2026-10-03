using System.Text.Json;
using Cordis;
using Cordis.Clr;

namespace DeploymentPlugin;

public sealed class Entry : IClrPluginModule
{
    public IPlugin CreatePlugin() => new Plugin<object?>
    {
        Inject = ["trace"],
        Apply = (context, _) =>
        {
            var trace = context.Get<List<string>>("trace")!;
            context.Provide("deployment-result", (Func<string>)(() => JsonSerializer.Serialize(17)));
            context.Effect(() => (Action)(() => trace.Add("disposed")));
        },
    };
}
