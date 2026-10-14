using System.Threading.Channels;
using System.Diagnostics;
using Cordis.Composition;
using Cordis.JavaScript;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class HmrReconciliationTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData("noop", false)]
    [InlineData("webserver", false)]
    [InlineData("webserver", true)]
    public async Task Best_effort_reconciliation_retains_eager_options_and_recovers(string id, bool asynchronous)
    {
        var clock = Stopwatch.StartNew();
        Action<string> trace = stage => output.WriteLine($"HMR timing: {clock.Elapsed.TotalSeconds:F3}s {stage}");
        trace("setup begin");
        var directory = Directory.CreateTempSubdirectory("cordis-hmr-reconcile-");
        try
        {
            var config = Path.Combine(directory.FullName, "cordis.yml");
            var user = Path.Combine(directory.FullName, "cordis.patch.yml");
            await File.WriteAllTextAsync(config, $"- id: {id}\n  name: noop\n  config:\n    value: base\n");
            var basePatches = new List<EntryOptions>
            {
                new()
                {
                    Id = id,
                    Config = new EntryOptions
                    {
                        ["value"] = "generated"
                    }
                }
            };
            await using var context = new Context();
            IPlugin plugin = asynchronous
                ? new Plugin<EntryOptions>
                {
                    ApplyAsync = async (_, raw) =>
                    {
                        await Task.Yield();
                        if (raw.GetValueOrDefault("fail") is true)
                            throw new InvalidOperationException("candidate config failed");
                    }
                }
                : new Plugin<EntryOptions>
                {
                    Apply = (_, raw) =>
                    {
                        if (raw.GetValueOrDefault("fail") is true)
                            throw new InvalidOperationException("candidate config failed");
                    }
                };
            Loader? loader = null;
            await context.RunAsync(ctx =>
            {
                loader = new Loader(
                    ctx,
                    new StaticModuleResolver().Register("noop", plugin),
                    expressionEvaluator: new TracedEvaluator(trace));
                return Task.CompletedTask;
            });
            var include = await ApplicationBoot.MountAsync(loader!, config, basePatches);
            ControlledWatcher? native = null;
            int watchers = 0, failures = 0;
            await using var hmr = new HmrCoordinator(path =>
            {
                watchers++;
                return native = new(path);
            });
            var outcomes = Channel.CreateUnbounded<Exception?>();
            hmr.Error += error =>
            {
                trace("error published");
                failures++;
                outcomes.Writer.TryWrite(error);
            };
            var required = new HashSet<string>(["webserver"], StringComparer.Ordinal);

            async Task Refresh(bool defaultCompose)
            {
                trace("refresh entry");
                var patches = await Profiles.ReadPatchesAsync(user, true);
                trace("patch read complete");
                await ApplicationBoot.ReconcileAsync(
                    include,
                    defaultCompose ? patches : [.. basePatches, .. patches],
                    required);
                outcomes.Writer.TryWrite(null);
                trace("refresh complete");
            }

            var watch = hmr.WatchConfig(user, () => Refresh(false));
            native!.EnableRaisingEvents = false;
            Assert.Equal(1, watchers);
            trace("watch ready");

            async Task<Exception?> Change(string text)
            {
                trace("change: " + text.Replace('\n', ' '));
                await File.WriteAllTextAsync(user, text);
                native.Change("cordis.patch.yml");
                trace("event signalled");
                return await outcomes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }

            Entry Row() => include.Store[id];
            Assert.Null(await Change($"- id: {id}\n  config:\n    value: live\n"));
            Assert.Equal("live", ((EntryOptions)Row().Options.Config!)["value"]);
            Assert.NotNull(await Change($"- id: {id}\n  config:\n    fail: true\n"));
            Assert.Equal(true, ((EntryOptions)Row().Options.Config!)["fail"]);
            var disabledError = await Change($"- id: {id}\n  disabled: !!js \"JSON.parse('invalid')\"\n");
            Assert.Contains($"{id} (noop): disabled expression failed:", disabledError!.Message);
            Assert.Contains("SyntaxError", disabledError.ToString());
            Assert.Equal(new JsExpression("JSON.parse('invalid')"), Row().Options.Disabled);
            Assert.NotNull(await Change("invalid: [unclosed\n"));
            Assert.Equal(new JsExpression("JSON.parse('invalid')"), Row().Options.Disabled);
            Assert.Null(await Change($"- id: {id}\n  config:\n    value: recovered\n"));
            Assert.Equal("recovered", ((EntryOptions)Row().Options.Config!)["value"]);
            Assert.Equal(FiberState.Active, Row().Fiber!.State);
            Assert.Equal(3, failures);
            File.Delete(user);
            native.Change("cordis.patch.yml");
            Assert.Null(await outcomes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("generated", ((EntryOptions)Row().Options.Config!)["value"]);
            await watch.DisposeAsync();
            await using var defaultWatch = hmr.WatchConfig(user, () => Refresh(true));
            native.EnableRaisingEvents = false;
            Assert.Equal(2, watchers);
            Assert.Null(await Change($"- id: {id}\n  config:\n    value: identity\n"));
            Assert.Equal("identity", ((EntryOptions)Row().Options.Config!)["value"]);
            trace("scenario complete; draining owners");
        }
        finally
        {
            trace("owners drained; deleting temporary files");
            directory.Delete(true);
            trace("cleanup complete");
        }
    }

    private sealed class ControlledWatcher(string directory) : FileSystemWatcher(directory)
    {
        public void Change(string name) => OnChanged(new(WatcherChangeTypes.Changed, Path, name));
    }

    private sealed class TracedEvaluator(Action<string> trace) : IExpressionEvaluator
    {
        private readonly JintExpressionEvaluator inner = new()
        {
            Configure = _ => trace("expression engine configured")
        };

        public object? Evaluate(string expression, Context context)
        {
            trace("expression entry");
            try
            {
                return inner.Evaluate(expression, context);
            }
            finally
            {
                trace("expression exit");
            }
        }
    }
}
