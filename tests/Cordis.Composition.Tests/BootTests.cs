using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class BootTests
{
    [Fact]
    public async Task HostPreparationPrecedesMountAndOptionalFailuresKeepSiblings()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml"); var warnings = new List<string>();
        try
        {
            await File.WriteAllTextAsync(file, "- id: good\n  name: good\n- id: missing\n  name: absent\n");
            var resolver = new StaticModuleResolver().Register("good", new Plugin<object?> { Inject = ["prepared"], Apply = (ctx, _) => ctx.Provide("started", true) });
            Context? prepared = null;
            await using var context = await ApplicationBoot.BootAsync(file, resolver, prepare: ctx => { Assert.Empty(ctx.Get<Loader>("loader")!.Entries()); prepared = ctx; ctx.Provide("prepared", true); return Task.CompletedTask; }, warn: warnings.Add);
            Assert.Same(context, prepared);
            await context.RunAsync(_ => { Assert.Equal(true, context.Get("started")); return Task.CompletedTask; }); Assert.Single(warnings);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task RequiredFailureCleansSuccessfulPluginsAndPreservesDiagnostics()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml"); var cleaned = false;
        try
        {
            await File.WriteAllTextAsync(file, "- id: good\n  name: good\n- id: required\n  name: absent\n");
            var resolver = new StaticModuleResolver().Register("good", new Plugin<object?> { Apply = (ctx, _) => ctx.Effect(() => (Action)(() => cleaned = true)) });
            var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(file, resolver, required: new HashSet<string> { "required" })); Assert.True(cleaned); Assert.Equal(file, error.ConfigurationPath); Assert.NotEmpty(error.StartupMessages); Assert.IsType<AggregateException>(error.InnerException);
            var diagnostic = Assert.Single(error.Diagnostics); Assert.Equal("root:required", diagnostic.Id); Assert.Equal("absent", diagnostic.Module); Assert.Null(diagnostic.State); Assert.True(diagnostic.Required); Assert.IsType<FileNotFoundException>(diagnostic.Error);
            Assert.Contains(error.StartupMessages, failure => failure.Message.Contains("absent", StringComparison.Ordinal));
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task HostPreparationFailureHasDistinctStageAndCleansEffects()
    {
        var cleaned = false; var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ApplicationBoot.BootAsync("missing.yml", new StaticModuleResolver(), prepare: ctx => { ctx.Effect(() => (Action)(() => cleaned = true)); throw new InvalidOperationException("prepare rejected"); }));
        Assert.Contains("host preparation failed", error.Message); Assert.True(cleaned); Assert.Equal("prepare rejected", error.InnerException!.Message);
    }
    [Fact]
    public async Task ReconciliationPreservesOwningEntryAndRawFiberPatchGeneration()
    {
        var file = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            await File.WriteAllTextAsync(file, "[]\n"); await using var context = new Context(); Loader loader = null!;
            await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("noop", new Plugin<object?> { Apply = (_, _) => { } })); return Task.CompletedTask; }); var include = await ApplicationBoot.MountAsync(loader, file);
            var patches = new List<EntryOptions> { new() { ["insert"] = new[] { new EntryOptions { Id = "added", Name = "noop" } } } }; await ApplicationBoot.ReconcileAsync(include, patches);
            Assert.Same(patches, Assert.IsType<EntryOptions>(include.Owner!.Options.Config)["patches"]); Assert.Same(patches, Assert.IsType<EntryOptions>(include.Owner.Fiber!.RawConfig)["patches"]);
            await include.Owner.Fiber.RestartAsync(); await loader.WaitAsync(); Assert.Equal(FiberState.Active, loader.Resolve("root:added").Fiber!.State);
        }
        finally { File.Delete(file); }
    }
}
