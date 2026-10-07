using Cordis;
using Cordis.Clr;
using Microsoft.AspNetCore.Http;

namespace DeploymentPlugin;

public sealed class Entry : IClrPluginModule
{
    public IPlugin CreatePlugin() =>
        new Plugin<object?>
        {
            Inject = ["trace"],
            Apply = (context, _) =>
            {
                var trace = context.Get<List<string>>("trace")!;
                var http = new DefaultHttpContext();
                http.Response.StatusCode = 202;
                http.Request.Path = "/from-plugin";
                context.Provide(
                    "deployment-result",
                    (Func<string>)(() => $"{http.Response.StatusCode}:{http.Request.Path}"));
                context.Effect(() => (Action)(() => trace.Add("disposed")));
            },
        };
}
