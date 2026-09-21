using Cordis.Composition;
using Cordis.Extensions;
using Cordis.JavaScript;
using Xunit;
namespace Cordis.Platform.Tests;

public sealed class DshHomePathTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cordis-home-" + Guid.NewGuid().ToString("N"));
    public DshHomePathTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string At(string name) => Path.Combine(directory, name);
    private static StaticModuleResolver Resolver() => new StaticModuleResolver().Register("capture", new Plugin<object?> { Inject = ["dshHomePath"], Apply = (ctx, config) => ctx.Provide("captured", config) });
    private string Config()
    {
        var file = At("cordis.yml"); File.WriteAllText(file, "- id: capture\n  name: capture\n  config:\n    path: !!js dshHomePath('sessions')\n    nested: !!js ctx.dshHomePath('storages', 'cache')\n    root: !!js dshHomePath()\n"); return file;
    }
    [Fact]
    public async Task ApplicationBootProvidesHomeHelperBeforePrepareAndLazyExpressions()
    {
        var environment = new Dictionary<string, string> { ["DSH_HOME"] = At("home") };
        var paths = new DshHomePaths(environment: environment); environment["DSH_HOME"] = At("changed");
        await using var ctx = await ApplicationBoot.BootAsync(Config(), Resolver(), evaluator: new JintExpressionEvaluator(), homePaths: paths,
            prepare: host => { Assert.Equal(At("home"), host.Get<DshHomePath>("dshHomePath")!()); return Task.CompletedTask; });
        await ctx.RunAsync(host => { var captured = Assert.IsType<EntryOptions>(host.Get("captured")); Assert.Equal(Path.Combine(At("home"), "sessions"), captured["path"]); Assert.Equal(Path.Combine(At("home"), "storages", "cache"), captured["nested"]); Assert.Equal(At("home"), captured["root"]); return Task.CompletedTask; });
    }
    [Fact]
    public async Task DefaultBootProvidesCurrentHostSnapshot()
    {
        var file = At("empty.yml"); File.WriteAllText(file, "[]\n"); var expected = new DshHomePaths().Home;
        await using var ctx = await ApplicationBoot.BootAsync(file, new StaticModuleResolver(), prepare: host => { Assert.Equal(expected, host.Get<DshHomePath>("dshHomePath")!()); return Task.CompletedTask; });
    }
    [Fact]
    public async Task ProfileSessionUsesItsLaunchHomeWithoutSharingOtherRoots()
    {
        var profilePath = At("profile"); Profiles.Initialize(profilePath, []); var maps = new Dictionary<string, string>(); var profile = await Profiles.LoadAsync(profilePath, maps);
        await using var first = await ProfileSession.StartAsync(Config(), new(profile, At("one"), [], maps), Resolver(), evaluator: new JintExpressionEvaluator());
        await using var second = await ProfileSession.StartAsync(Config(), new(profile, At("two"), [], maps), Resolver(), evaluator: new JintExpressionEvaluator());
        await first.Context.RunAsync(ctx => { Assert.Equal(Path.Combine(At("one"), "sessions"), Assert.IsType<EntryOptions>(ctx.Get("captured"))["path"]); return Task.CompletedTask; });
        await second.Context.RunAsync(ctx => { Assert.Equal(Path.Combine(At("two"), "sessions"), Assert.IsType<EntryOptions>(ctx.Get("captured"))["path"]); return Task.CompletedTask; });
    }
    [Fact]
    public async Task ProfileSessionRefreshTracksCustomUserLayerSource()
    {
        var profilePath = At("profile"); Profiles.Initialize(profilePath, []); var maps = new Dictionary<string, string>();
        var profile = await Profiles.LoadAsync(profilePath, maps); var custom = Path.Combine(profilePath, "application.patch.yml");
        File.WriteAllText(custom, "- id: capture\n  config: { path: initial }\n"); profile = profile with { UserLayer = new(custom, []) };
        await using var session = await ProfileSession.StartAsync(Config(), new(profile, directory, [], maps), Resolver(), evaluator: new JintExpressionEvaluator());
        await session.Context.RunAsync(ctx => { Assert.Equal("initial", Assert.IsType<EntryOptions>(ctx.Get("captured"))["path"]); return Task.CompletedTask; });
        File.WriteAllText(custom, "- id: capture\n  config: { path: updated }\n"); await session.RefreshAsync();
        await session.Context.RunAsync(ctx => { Assert.Equal("updated", Assert.IsType<EntryOptions>(ctx.Get("captured"))["path"]); return Task.CompletedTask; });
    }
    [Fact]
    public void SnapshotResolvesExplicitEnvironmentDefaultAndTildePaths()
    {
        var environment = new Dictionary<string, string> { ["DSH_HOME"] = "~/env-dsh" };
        Assert.Equal(At("explicit"), new DshHomePaths("./explicit", environment, directory, directory).Home);
        Assert.Equal(At("env-dsh"), new DshHomePaths(environment: environment, userHome: directory).Home);
        foreach (var value in new[] { "", "   " }) { environment["DSH_HOME"] = value; Assert.Equal(At(".dsh"), new DshHomePaths(environment: environment, userHome: directory).Home); }
        Assert.Equal(directory, new DshHomePaths("~", userHome: directory).Home);
        Assert.Equal(At("other"), new DshHomePaths("~\\other", userHome: directory).Home);
        Assert.Equal(At("~other"), new DshHomePaths("~other", currentDirectory: directory).Home);
    }
}
