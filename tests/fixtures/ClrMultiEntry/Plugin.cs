using System.Reflection;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.Clr;
using Cordis.Composition;

namespace IndependentMultiEntry;

internal static class BundleState
{
    internal static readonly object Identity = new();
    internal static readonly Assembly Assembly = typeof(BundleState).Assembly;
    internal static readonly int Version = Assembly.GetName().Version!.Major;
}

public sealed class First : IClrPluginModule, IClrTypertModule
{
    public IPlugin CreatePlugin() => Entries.Create("first");
    public TypertContribution CreateTypertContribution() => FirstRemoteTypert.Contribution("multi-entry.first");
}

public sealed class Second : IClrPluginModule, IClrTypertModule
{
    public IPlugin CreatePlugin() => Entries.Create("second");
    public TypertContribution CreateTypertContribution() => SecondRemoteTypert.Contribution("multi-entry.second");
}

public sealed record EchoRequest(int Delta);

[RemoteService("first.remote", typeof(BundleJson), Namespace = "first")]
public sealed partial class FirstRemote(int value, TaskCompletionSource? entered, TaskCompletionSource? release)
{
    [RemoteMethod]
    public Task<int> Echo(EchoRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(value + request.Delta + BundleState.Version * 100);
    }

    [RemoteMethod]
    public async Task<int> Wait(EchoRequest request, CancellationToken cancellationToken)
    {
        if (entered is null || release is null)
            throw new InvalidOperationException("The host did not supply an asynchronous invocation fixture.");
        entered.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        return value + request.Delta + BundleState.Version * 100;
    }
}

[RemoteService("second.remote", typeof(BundleJson), Namespace = "second")]
public sealed partial class SecondRemote(int value)
{
    [RemoteMethod]
    public Task<int> Echo(EchoRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(value + request.Delta + BundleState.Version * 100);
    }
}

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(EchoRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
public partial class BundleJson : JsonSerializerContext;

internal static class Entries
{
    internal static IPlugin Create(string name) => new Plugin<object?>
    {
        Inject = name == "second" && BundleState.Version == 2 ? ["trace", "late"] : ["trace"],
        Apply = (context, raw) =>
        {
            if (name == "second" && BundleState.Version == 3)
                throw new InvalidOperationException("Candidate second entry refused activation.");
            var trace = context.Get<List<string>>("trace")!;
            context.Provide(name + ".identity", BundleState.Identity);
            context.Provide(name + ".assembly", BundleState.Assembly);
            context.Provide(name + ".value", Convert.ToInt32(raw));
            context.Provide(name + ".version", BundleState.Version);
            context.Provide(
                name + ".remote",
                name == "first"
                    ? (object)new FirstRemote(
                        Convert.ToInt32(raw),
                        context.Get<TaskCompletionSource>("remote-entered"),
                        context.Get<TaskCompletionSource>("remote-release"))
                    : new SecondRemote(Convert.ToInt32(raw)));
            trace.Add(name + ":apply:" + Convert.ToInt32(raw));
            context.Effect(() => (Action)(() => trace.Add(name + ":dispose")));
        },
    };
}
