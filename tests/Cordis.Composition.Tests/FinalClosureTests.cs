using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class FinalClosureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cordis-final-" + Guid.NewGuid().ToString("N"));
    public FinalClosureTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string At(string name) => Path.Combine(directory, name);
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("non-array")]
    public async Task InvalidRootConfigurationRejectsAndDisposes(string kind)
    {
        var file = At("cordis.yml"); if (kind != "missing") File.WriteAllText(file, kind == "malformed" ? "invalid: [unclosed" : "entries: []"); var disposed = false;
        var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(file, new StaticModuleResolver(), prepare: ctx => { ctx.Effect(() => (Action)(() => disposed = true)); return Task.CompletedTask; }));
        Assert.True(disposed); Assert.NotNull(Assert.Single(error.Diagnostics).Error);
    }
    [Fact]
    public async Task NestedSharedAggregateErrorsKeepOriginalCausesWithoutRepeatedMembers()
    {
        var leaf = new InvalidOperationException("unique leaf failure"); var second = new InvalidOperationException("second aggregate member");
        var aggregate = new AggregateException("activation failed", leaf, second, leaf); var wrapper = new InvalidOperationException("plugin activation failed", aggregate);
        File.WriteAllText(At("cordis.yml"), "- id: webserver\n  name: failed\n");
        var resolver = new StaticModuleResolver().Register("failed", new Plugin<object?> { Apply = (_, _) => throw wrapper });
        var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(At("cordis.yml"), resolver));
        Assert.Same(wrapper, Assert.Single(Assert.IsType<AggregateException>(error.InnerException).InnerExceptions));
        Assert.Contains("plugin activation failed", error.Message); Assert.Contains("activation failed", error.Message); Assert.Contains("second aggregate member", error.Message);
        Assert.Equal(1, error.Message.Split("unique leaf failure", StringSplitOptions.None).Length - 1);
        var prepare = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationBoot.BootAsync(At("cordis.yml"), resolver, prepare: _ => throw wrapper)); Assert.Contains("unique leaf failure", prepare.Message); Assert.Contains("second aggregate member", prepare.Message); Assert.Same(wrapper, prepare.InnerException);
    }
    [Fact]
    public void ExplicitDiagnosticMessageDoesNotExpandMetadataInNormalInspection()
    {
        EntryDiagnostic[] rows = [new("connection", "private-module", FiberState.Pending, null, ["webRuntime"], true)];
        var error = new StartupException("waiting for webRuntime", rows); Assert.Same(rows, error.Diagnostics); Assert.Null(error.InnerException); Assert.Contains("waiting for webRuntime", error.ToString()); Assert.DoesNotContain("private-module", error.ToString());
    }
    [Fact]
    public void FailureStacksAndAllMissingServicesRemainInOneDiagnostic()
    {
        var original = new InvalidOperationException("listen EADDRINUSE");
        try { throw original; } catch (InvalidOperationException) { }
        var error = new StartupException([
            new("web-runtime", "web", FiberState.Pending, null, ["webServer"], false),
            new("webserver", "server", FiberState.Failed, original, [], true),
            new("connection", "connection", FiberState.Pending, null, ["webRuntime"], true),
            new("unknown", "unknown", FiberState.Pending, null, [], false),
            new("multiple", "multiple", FiberState.Pending, null, ["first", "second"], false),
            new("loading", "loading", FiberState.Loading, null, [], false)]);
        foreach (var value in new[] { "listen EADDRINUSE", "webServer", "webRuntime", "unknown", "first, second", "fiber state Loading" }) Assert.Contains(value, error.Message);
        Assert.Contains(nameof(FailureStacksAndAllMissingServicesRemainInOneDiagnostic), error.Message);
        Assert.Same(original, Assert.Single(Assert.IsType<AggregateException>(error.InnerException).InnerExceptions));
    }
    [Theory]
    [InlineData("import")]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("dependency")]
    public async Task RequiredIdHotReloadFailureKeepsSiblingThenRecovers(string kind)
    {
        const string baseYaml = "- id: good\n  name: noop\n"; var file = At("cordis.yml"); File.WriteAllText(file, baseYaml);
        var resolver = new StaticModuleResolver().Register("noop", new Plugin<object?> { Apply = (_, _) => { } }).Register("provider", new Plugin<object?> { Apply = (ctx, _) => ctx.Provide("reloadMissing", true) });
        if (kind != "import") resolver.Register("failure", new Plugin<EntryOptions> { Inject = kind == "dependency" ? ["reloadMissing"] : [], Apply = kind == "async" ? null : (_, config) => { if (kind == "sync" && config["fail"] is true) throw new InvalidOperationException("reload sync failure"); }, ApplyAsync = kind != "async" ? null : async (_, config) => { await Task.Yield(); if (config["fail"] is true) throw new InvalidOperationException("reload async failure"); } });
        await using var ctx = await ApplicationBoot.BootAsync(file, resolver); Loader loader = null!;
        await ctx.RunAsync(_ => { loader = ctx.Get<Loader>("loader")!; return Task.CompletedTask; }); var good = loader.Resolve("root:good").Fiber; var include = (Include)loader.Resolve("root").Subtree!;
        File.WriteAllText(file, baseYaml + "- id: webserver\n  name: failure\n  config: { fail: true }\n"); await include.RefreshAsync(); await loader.WaitAsync();
        var failed = loader.Resolve("root:webserver"); Assert.Equal(kind == "import" ? (FiberState?)null : kind == "dependency" ? FiberState.Pending : FiberState.Failed, failed.Fiber?.State); Assert.Equal(FiberState.Active, good!.State); Assert.Equal(FiberState.Active, ctx.Fiber.State);
        File.WriteAllText(file, baseYaml + $"- id: webserver\n  name: {(kind == "import" ? "noop" : "failure")}\n  config: {{ fail: false }}\n" + (kind == "dependency" ? "- id: provider\n  name: provider\n" : ""));
        await include.RefreshAsync(); await loader.WaitAsync(); Assert.Equal(FiberState.Active, loader.Resolve("root:webserver").Fiber!.State); Assert.Same(good, loader.Resolve("root:good").Fiber);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("1")]
    public async Task CurrentProfileFilesRetainOverlayAndTelemetryPrecedence(string? disabled)
    {
        var bundle = At("bundle"); Directory.CreateDirectory(bundle); File.WriteAllText(Path.Combine(bundle, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":\"cordis.patch.yml\"}}}");
        var bundlePatch = Path.Combine(bundle, "cordis.patch.yml"); File.WriteAllText(bundlePatch, "- insert:\n  - id: session-telemetry-otel\n    name: telemetry\n");
        var profilePath = At("profile"); Profiles.Initialize(profilePath, ["base"]); var maps = new Dictionary<string, string> { ["base"] = bundle }; var loaded = await Profiles.LoadAsync(profilePath, maps);
        var applicationPatch = Path.Combine(profilePath, "application.patch.yml"); File.WriteAllText(applicationPatch, "- id: session-telemetry-otel\n  disabled: true\n"); File.WriteAllText(At("cordis.patch.yml"), "- id: session-telemetry-otel\n  disabled: false\n");
        var overlay = new EntryOptions { Id = "session-telemetry-otel", Disabled = false }; var launch = new ProfileLaunch(loaded with { UserLayer = new(applicationPatch, []) }, directory, [new("cli", [overlay])], maps, TelemetryDisabledEnv: disabled);
        var refresh = await ProfileComposition.RefreshAsync(launch); Assert.Equal(!string.IsNullOrEmpty(disabled), Assert.Single(Profiles.Compose(refresh.Layers)).Disabled);
        var detached = ProfileComposition.Flatten(refresh.Layers); detached[^1].Disabled = true; Assert.Equal(false, overlay.Disabled);
        var without = launch with { TelemetryDisabledEnv = null, Overlays = [] }; File.WriteAllText(At("cordis.patch.yml"), "- id: session-telemetry-otel\n  disabled: true\n"); Assert.Equal(true, Assert.Single(Profiles.Compose((await ProfileComposition.RefreshAsync(without)).Layers)).Disabled);
        File.WriteAllText(At("cordis.patch.yml"), "[]\n"); Assert.Equal(true, Assert.Single(Profiles.Compose((await ProfileComposition.RefreshAsync(without)).Layers)).Disabled);
        File.WriteAllText(applicationPatch, "- id: session-telemetry-otel\n  disabled: false\n"); Assert.Equal(false, Assert.Single(Profiles.Compose((await ProfileComposition.RefreshAsync(without)).Layers)).Disabled);
        File.WriteAllText(bundlePatch, "[]\n"); Assert.DoesNotContain((await ProfileComposition.RefreshAsync(launch)).Layers, layer => layer.Source == "DSH_TELEMETRY_DISABLED");
    }
}

