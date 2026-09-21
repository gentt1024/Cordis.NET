using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class AssertionClosureTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cordis-assertions-" + Guid.NewGuid().ToString("N"));
    public AssertionClosureTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string At(string name) => Path.Combine(directory, name);
    private static Plugin<object?> Noop() => new() { Apply = (_, _) => { } };
    private static object? Value(Entry entry) => Assert.IsType<EntryOptions>(entry.Options.Config)["value"];
    [Fact]
    public async Task AuditIgnoresActiveDisabledAndAbsentRequiredRows()
    {
        await using var ctx = new Context(); Loader loader = null!;
        await ctx.RunAsync(_ => { loader = new Loader(ctx, new StaticModuleResolver().Register("noop", Noop())); return Task.CompletedTask; });
        Assert.Empty(await ApplicationBoot.AuditAsync(loader, ApplicationBoot.DshRequiredEntries));
        await loader.Root.UpdateAsync(ApplicationBoot.DshRequiredEntries.Select(id => new EntryOptions { Id = id, Name = "noop" }).ToList()); await loader.WaitAsync(); Assert.Empty(await ApplicationBoot.AuditAsync(loader, ApplicationBoot.DshRequiredEntries));
        foreach (var entry in loader.Entries().ToArray()) await entry.UpdateAsync(new() { Disabled = true });
        Assert.Empty(await ApplicationBoot.AuditAsync(loader, ApplicationBoot.DshRequiredEntries));
    }
    [Fact]
    public async Task RequiredApplyFailureDisposesSuccessfulSibling()
    {
        var file = At("cordis.yml"); File.WriteAllText(file, "- id: good\n  name: good\n- id: webserver\n  name: failing\n"); var disposed = false;
        var resolver = new StaticModuleResolver().Register("good", new Plugin<object?> { Apply = (ctx, _) => ctx.Effect(() => (Action)(() => disposed = true)) }).Register("failing", new Plugin<object?> { Apply = (_, _) => throw new InvalidOperationException("required apply failure") });
        var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(file, resolver)); Assert.True(disposed); Assert.Contains("webserver", error.Message); Assert.Contains("required apply failure", error.Message); Assert.True(Assert.Single(error.Diagnostics).Required);
    }
    [Fact]
    public async Task InitialIncludeWritesAndActivatesInitialConfig()
    {
        File.WriteAllText(At("cordis.yml"), "- id: initialized\n  name: cordis:include\n  config:\n    path: ./created.yml\n    initial:\n    - id: noop\n      name: noop\n      config: { value: initial }\n");
        await using var ctx = await ApplicationBoot.BootAsync(At("cordis.yml"), new StaticModuleResolver().Register("noop", Noop()));
        await ctx.RunAsync(_ => { var entry = ctx.Get<Loader>("loader")!.Resolve("root:initialized:noop"); Assert.Equal("initial", Value(entry)); Assert.Equal(FiberState.Active, entry.Fiber!.State); return Task.CompletedTask; });
        Assert.Contains("id: noop", File.ReadAllText(At("created.yml")));
    }
    [Fact]
    public async Task IncludeMalformedAndEmptyEditsKeepLastGoodThenRecover()
    {
        var file = At("cordis.yml"); File.WriteAllText(file, "- id: noop\n  name: noop\n  config: { value: 1 }\n");
        await using var ctx = await ApplicationBoot.BootAsync(file, new StaticModuleResolver().Register("noop", Noop())); Loader loader = null!;
        await ctx.RunAsync(_ => { loader = ctx.Get<Loader>("loader")!; return Task.CompletedTask; }); var include = (Include)loader.Resolve("root").Subtree!;
        foreach (var bad in new[] { "invalid: [unclosed", "" }) { File.WriteAllText(file, bad); await include.RefreshAsync(); Assert.Equal(1L, Value(include.Resolve("noop"))); }
        File.WriteAllText(file, "- id: noop\n  name: noop\n  config: { value: 2 }\n"); await include.RefreshAsync(); await loader.WaitAsync(); Assert.Equal(2L, Value(include.Resolve("noop")));
    }
    [Fact]
    public async Task IncludeOwnConfigUpdateRetainsNewPatchesAcrossRefreshAndRemovesOldInsertions()
    {
        var file = At("cordis.yml"); File.WriteAllText(file, "- id: noop\n  name: noop\n  config: { value: base }\n");
        List<EntryOptions> patches = [new() { Id = "noop", Config = new EntryOptions { ["value"] = "patched" } }, new() { ["insert"] = new[] { new EntryOptions { Id = "extra", Name = "noop" } } }];
        await using var ctx = await ApplicationBoot.BootAsync(file, new StaticModuleResolver().Register("noop", Noop()), patches); Loader loader = null!;
        await ctx.RunAsync(_ => { loader = ctx.Get<Loader>("loader")!; return Task.CompletedTask; }); var root = loader.Resolve("root"); var include = (Include)root.Subtree!;
        Assert.Equal("patched", Value(include.Resolve("noop"))); Assert.Null(include.Resolve("extra").Options.Config);
        File.WriteAllText(file, "- id: noop\n  name: noop\n  config: { value: edited }\n"); await include.RefreshAsync(); await loader.WaitAsync(); Assert.Equal("patched", Value(include.Resolve("noop"))); Assert.NotNull(include.Resolve("extra"));
        await root.UpdateAsync(new() { Config = new EntryOptions { ["path"] = new Uri(file).AbsoluteUri, ["patches"] = new[] { new EntryOptions { Id = "noop", Config = new EntryOptions { ["value"] = "patched-v2" } } } } }); await loader.WaitAsync();
        Assert.Equal("patched-v2", Value(include.Resolve("noop"))); Assert.DoesNotContain(loader.Entries(), entry => entry.Options.Id == "extra");
        File.WriteAllText(file, "- id: noop\n  name: noop\n  config: { value: edited-2 }\n"); await include.RefreshAsync(); await loader.WaitAsync(); Assert.Equal("patched-v2", Value(include.Resolve("noop")));
        await root.UpdateAsync(new() { Config = new EntryOptions { ["path"] = new Uri(file).AbsoluteUri, ["patches"] = Array.Empty<EntryOptions>() } }); await loader.WaitAsync(); Assert.Equal("edited-2", Value(include.Resolve("noop")));
    }
    [Fact]
    public async Task LaterPatchConfiguresAndDisablesEarlierInsertion()
    {
        var file = At("cordis.yml"); File.WriteAllText(file, "- id: shared\n  name: noop\n  config: { value: base }\n");
        var patches = ConfigurationFile.ParseEntries("- id: shared\n  config: { value: bundle }\n- insert:\n  - id: kept\n    name: noop\n    config: { value: bundle-default }\n  - id: dropped\n    name: noop\n- id: kept\n  config: { value: user }\n- id: dropped\n  disabled: true\n");
        await using var ctx = await ApplicationBoot.BootAsync(file, new StaticModuleResolver().Register("noop", Noop()), patches);
        await ctx.RunAsync(_ => { var loader = ctx.Get<Loader>("loader")!; Assert.Equal("bundle", Value(loader.Resolve("root:shared"))); Assert.Equal("user", Value(loader.Resolve("root:kept"))); Assert.Equal(true, loader.Resolve("root:dropped").Options.Disabled); Assert.Null(loader.Resolve("root:dropped").Fiber); return Task.CompletedTask; });
    }
    private sealed class ServiceEvaluator : IExpressionEvaluator
    {
        public object? Evaluate(string expression, Context context)
        {
            var value = context.Get(expression); if (value is EntryOptions map) { if (map.GetValueOrDefault("fail") is true) throw new InvalidOperationException("rejected provider"); return map.GetValueOrDefault("value"); }
            return value;
        }
    }
    [Fact]
    public async Task LazyExpressionsRecoverAfterProviderDisableReplacementAndRejection()
    {
        File.WriteAllText(At("cordis.yml"), "[]\n");
        var resolver = new StaticModuleResolver().Register("provider", new Plugin<object?> { Apply = (ctx, raw) => ctx.Provide("phaseOne", raw) }).Register("reader", new Plugin<object?> { Inject = ["phaseOne"], Apply = (ctx, raw) => ctx.Provide("readerResult", raw) });
        var expression = new JsExpression("phaseOne");
        List<EntryOptions> patches = [new() { ["insert"] = new[] { new EntryOptions { Id = "reader", Name = "reader", Config = new EntryOptions { ["value"] = expression } }, new EntryOptions { Id = "provider", Name = "provider", Config = new EntryOptions { ["value"] = "first" } } } }];
        await using var ctx = await ApplicationBoot.BootAsync(At("cordis.yml"), resolver, patches, evaluator: new ServiceEvaluator()); Loader loader = null!;
        await ctx.RunAsync(_ => { loader = ctx.Get<Loader>("loader")!; Assert.Equal("first", Assert.IsType<EntryOptions>(ctx.Get("readerResult"))["value"]); return Task.CompletedTask; });
        var provider = loader.Resolve("root:provider"); var reader = loader.Resolve("root:reader");
        foreach (var phase in new[] { "second", "rejected", "recovered" })
        {
            await provider.UpdateAsync(new() { Disabled = true }); await loader.WaitAsync(); await ctx.RunAsync(_ => { Assert.Null(ctx.Get("readerResult")); return Task.CompletedTask; });
            await provider.UpdateAsync(new() { Config = phase == "rejected" ? new EntryOptions { ["fail"] = true } : new EntryOptions { ["value"] = phase } }); await provider.UpdateAsync(new() { Disabled = false }); await loader.WaitAsync();
            if (phase == "rejected") { var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.Fiber!.WaitAsync()); Assert.Equal("rejected provider", failure.Message); }
            await ctx.RunAsync(_ => { if (phase == "rejected") Assert.Null(ctx.Get("readerResult")); else Assert.Equal(phase, Assert.IsType<EntryOptions>(ctx.Get("readerResult"))["value"]); return Task.CompletedTask; });
        }
        Assert.Same(expression, Assert.IsType<EntryOptions>(reader.Options.Config)["value"]);
    }
    [Fact]
    public async Task ProfileReaderPreservesExpressionNodeAndAnchorsOnlyInsertionNames()
    {
        var patch = At("patch.yml"); File.WriteAllText(patch, "- id: agent-loop\n  name: ./assertion.mjs\n  config: { model: !!js process.env.DSH_SPEC_MODEL }\n- insert:\n  - id: row\n    name: ./rule.mjs\n  - id: group\n    name: cordis:group\n    group: true\n    config:\n    - id: child\n      name: ../child.mjs\n");
        var patches = await Profiles.ReadPatchesAsync(patch, true); Assert.Equal(2, patches.Count); Assert.Equal("agent-loop", patches[0].Id); Assert.Equal("process.env.DSH_SPEC_MODEL", Assert.IsType<JsExpression>(Assert.IsType<EntryOptions>(patches[0].Config)["model"]).Source); Assert.Equal("./assertion.mjs", patches[0].Name);
        var inserted = Data.Entries(patches[1]["insert"]); Assert.Equal(new Uri(At("rule.mjs")).AbsoluteUri, inserted[0].Name); Assert.Equal(new Uri(Path.GetFullPath("../child.mjs", directory)).AbsoluteUri, Data.Entries(inserted[1].Config)[0].Name);
    }
    [Fact]
    public void ComposeReportsSkippedRowsAndKeepsEmptyResultSilentByDefault()
    {
        var warnings = new List<string>();
        var layers = new[] { new ConfigurationLayer("bundle", ConfigurationFile.ParseEntries("- insert:\n  - id: x\n    name: pkg-x\n    config: { a: 1 }\n")), new ConfigurationLayer("user", ConfigurationFile.ParseEntries("- id: x\n  config: { a: 2 }\n- id: missing\n  config: {}\n")) };
        var row = Assert.Single(Profiles.Compose(layers, warnings.Add)); Assert.Equal("x", row.Id); Assert.Equal("pkg-x", row.Name); Assert.Equal(2L, Assert.IsType<EntryOptions>(row.Config)["a"]); Assert.Contains("\"missing\"", Assert.Single(warnings));
        Assert.Empty(Profiles.Compose([new("user", [new() { Id = "missing", Config = new EntryOptions() }])]));
    }
}

