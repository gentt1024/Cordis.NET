using Cordis;
using Cordis.Composition;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class BootOriginalTests
{
    private sealed class Cleanup(Func<Task> action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(action());
    }
    private sealed class ThrowingEvaluator : IExpressionEvaluator
    {
        public object? Evaluate(string expression, Context context) => throw new FormatException("expression failed");
    }
    [Theory]
    [InlineData("agent-loop")]
    [InlineData("webserver")]
    [InlineData("modules")]
    [InlineData("connection")]
    [InlineData("headless-runner")]
    [InlineData("acp")]
    [InlineData("sdk-jsonrpc-server")]
    public async Task RequiredAndOptionalFailuresShareOriginalAggregateWithoutWarning(string id)
    {
        var path = Path.GetTempFileName() + ".yml"; var warnings = new List<string>();
        var required = new InvalidOperationException("address already in use"); var optional = new InvalidOperationException("todo unavailable");
        try
        {
            await File.WriteAllTextAsync(path, $"- id: {id}\n  name: required\n- id: tool-todo\n  name: optional\n");
            var resolver = new StaticModuleResolver().Register("required", new Plugin<object?> { Apply = (_, _) => throw required }).Register("optional", new Plugin<object?> { Apply = (_, _) => throw optional });
            var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(path, resolver, warn: warnings.Add));
            Assert.Equal([required, optional], Assert.IsType<AggregateException>(error.InnerException).InnerExceptions);
            Assert.Equal(2, error.Diagnostics.Count); Assert.Empty(warnings);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task OptionalFailuresKeepGoodEntriesAndReportEveryFailureKindOnce()
    {
        var path = Path.GetTempFileName() + ".yml"; var warnings = new List<string>();
        var content = "- id: good\n  name: good\n- id: import\n  name: missing\n- id: config\n  name: noop\n  config: { value: !!js fail }\n- id: disabled\n  name: noop\n  disabled: !!js fail\n- id: sync\n  name: sync\n- id: async\n  name: async\n- id: waiting\n  name: waiting\n";
        try
        {
            await File.WriteAllTextAsync(path, content);
            var resolver = new StaticModuleResolver().Register("good", new Plugin<object?> { Apply = (ctx, _) => ctx.Provide("goodStarted", true) }).Register("noop", new Plugin<object?> { Apply = (_, _) => { } })
                .Register("sync", new Plugin<object?> { Apply = (_, _) => throw new InvalidOperationException("sync apply failure") })
                .Register("async", new Plugin<object?> { ApplyAsync = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("async apply failure"); } })
                .Register("waiting", new Plugin<object?> { Inject = ["neverProvided"], Apply = (_, _) => { } });
            await using var context = await ApplicationBoot.BootAsync(path, resolver, evaluator: new ThrowingEvaluator(), warn: warnings.Add);
            await context.RunAsync(ctx => { Assert.Equal(true, ctx.Get("goodStarted")); return Task.CompletedTask; });
            var warning = Assert.Single(warnings);
            foreach (var message in new[] { "6 entries did not activate", "disabled expression failed", "expression failed", "sync apply failure", "async apply failure", "waiting for service: neverProvided" }) Assert.Contains(message, warning);
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task FailedStartupRetainsAsynchronousCleanupWarningsAndErrors()
    {
        var path = Path.GetTempFileName() + ".yml";
        try
        {
            await File.WriteAllTextAsync(path, "- id: cleanup\n  name: cleanup\n- id: webserver\n  name: missing\n");
            var cleaned = false;
            var resolver = new StaticModuleResolver().Register("cleanup", new Plugin<object?> { Apply = (ctx, _) => ctx.Effect(() => new Cleanup(async () => { await Task.Yield(); cleaned = true; ctx.Logger.Warn("plugin cleanup warning"); throw new InvalidOperationException("plugin cleanup error"); })) });
            var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(path, resolver, prepare: ctx => { ctx.Effect(() => new Cleanup(async () => { await Task.Yield(); ctx.Logger.Warn("root cleanup warning"); })); return Task.CompletedTask; }));
            Assert.True(cleaned);
            Assert.Equal(path, error.ConfigurationPath);
            var arguments = error.StartupLogs.SelectMany(log => log.Arguments).ToArray();
            Assert.Contains("plugin cleanup warning", arguments);
            Assert.Contains("root cleanup warning", arguments);
            Assert.Contains(arguments, value => value is Exception failure && failure.Message.Contains("plugin cleanup error", StringComparison.Ordinal));
            Assert.Contains(error.Diagnostics, diagnostic => diagnostic.Module == "missing" && diagnostic.Required);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task SuccessfulBootKeepsHostExporterAndStopsStartupCollector()
    {
        var path = Path.GetTempFileName() + ".yml"; var messages = new List<string>();
        try
        {
            await File.WriteAllTextAsync(path, "[]\n");
            await using var context = await ApplicationBoot.BootAsync(path, new StaticModuleResolver(), prepare: ctx => { ctx.Logger.Exporter(new DelegateLogExporter(log => messages.Add(Logger.Format(log)), 2)); ctx.Logger.Info("startup information"); ctx.Logger.Warn("startup warning"); return Task.CompletedTask; });
            await context.RunAsync(ctx => { ctx.Logger.Warn("warning after startup"); return Task.CompletedTask; });
            Assert.Equal(["startup information", "startup warning", "warning after startup"], messages);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData("import")]
    [InlineData("schema")]
    [InlineData("config")]
    [InlineData("disabled")]
    [InlineData("sync")]
    [InlineData("async")]
    [InlineData("dependency")]
    public async Task RequiredFailureDisposesStartup(string kind)
    {
        var path = Path.GetTempFileName() + ".yml"; var disposed = false;
        try
        {
            var suffix = kind == "config" ? "  config: { value: !!js fail }\n" : kind == "disabled" ? "  disabled: !!js fail\n" : "";
            await File.WriteAllTextAsync(path, "- id: webserver\n  name: required\n" + suffix);
            var plugin = new Plugin<object?> { Inject = kind == "dependency" ? ["missingRequiredService"] : [], Apply = kind == "async" ? null : (_, _) => { if (kind == "sync") throw new InvalidOperationException("sync failure"); }, ApplyAsync = kind != "async" ? null : async (_, _) => { await Task.Yield(); throw new InvalidOperationException("async failure"); }, Config = value => kind == "schema" ? ConfigResult<object?>.Failure("schema failure") : ConfigResult<object?>.Success(value) };
            var resolver = new StaticModuleResolver(); if (kind != "import") resolver.Register("required", plugin);
            var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(path, resolver, evaluator: new ThrowingEvaluator(), prepare: ctx => { ctx.Effect(() => (Action)(() => disposed = true)); return Task.CompletedTask; }));
            Assert.True(disposed);
            Assert.Single(error.Diagnostics);
            Assert.True(error.Diagnostics[0].Required);
            if (kind == "dependency") { Assert.Null(error.InnerException); Assert.Contains("missingRequiredService", error.Message); }
            else Assert.NotNull(error.InnerException);
            if (kind == "disabled") Assert.Contains("disabled expression failed", error.Message);
            if (kind == "sync" || kind == "async") Assert.Contains(kind + " failure", error.Message);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootReturnsWhenSurfaceDisposesRoot(bool beforeMount)
    {
        var path = Path.GetTempFileName() + ".yml"; Context root = null!;
        try
        {
            await File.WriteAllTextAsync(path, beforeMount ? "[]\n" : "- id: exiting\n  name: exiting\n");
            var resolver = new StaticModuleResolver().Register("exiting", new Plugin<object?> { Apply = (_, _) => _ = root.DisposeAsync() });
            var context = await ApplicationBoot.BootAsync(path, resolver, prepare: async ctx => { root = ctx; if (beforeMount) await ctx.DisposeAsync(); });
            Assert.Same(root, context); await Assert.ThrowsAsync<ObjectDisposedException>(() => context.RunAsync(_ => Task.CompletedTask));
        }
        finally { File.Delete(path); }
    }
}



