using Cordis;
using Cordis.Composition;

namespace Cordis.Conformance;

// .NET-only host adapter checks. These do not pretend to be DSH conformance scenarios
// and do not add records to the semantic trace compared with Node.
internal static class PlatformScenarios
{
    internal static async Task CheckAsync()
    {
        await AmbientStateIsPerCaller();
        Console.Error.WriteLine("PASS P01-dotnet-ambient-context");
        await EntryBoundaryIsExplicit();
        Console.Error.WriteLine("PASS P02-dotnet-entry-boundary");
        await LiveStaticComposition();
        Console.Error.WriteLine("PASS P03-static-composition-file-patch-dependency-switch");
    }

    private static async Task AmbientStateIsPerCaller()
    {
        await using var root = new Context();
        var ambient = new AsyncLocal<string?>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int count = 0;

        Task Enter(string value)
        {
            ambient.Value = value;
            return root.RunAsync(async ctx =>
            {
                if (++count == 2) entered.SetResult();
                Scenarios.Equal(value, ambient.Value);
                await release.Task;
                Scenarios.Equal(value, ambient.Value);
                // A continuation must be back in the Cordis domain, not only the caller's ambient state.
                Scenarios.Equal<object?>(null, ctx.Get<object>("not-provided"));
            });
        }

        Task a = Enter("caller-a");
        Task b = Enter("caller-b");
        ambient.Value = null;
        try
        {
            await entered.Task;
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(a, b);
        Scenarios.Equal<string?>(null, ambient.Value);
    }

    private static async Task EntryBoundaryIsExplicit()
    {
        await using var root = new Context();
        Scenarios.Throws<InvalidOperationException>(() => root.Get<object>("service"));
        await root.RunAsync(ctx =>
        {
            ctx.Provide("service", new object());
            Scenarios.Equal(true, ctx.Get<object>("service") is not null);
            return Task.CompletedTask;
        });
        await root.DisposeAsync();
        await Scenarios.ThrowsAsync<ObjectDisposedException>(() => root.RunAsync(_ => Task.CompletedTask));
    }

    private static async Task LiveStaticComposition()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-static-");
        var filename = Path.Combine(directory.FullName, "cordis.yml");
        try
        {
            await File.WriteAllTextAsync(filename, """
                - id: provider
                  name: provider-a
                  config: first
                - id: consumer
                  name: consumer
                  inject: [selected]
                """);
            await using var context = new Context();
            await context.RunAsync(async ctx =>
            {
                var starts = new List<string>();
                var stops = new List<string>();
                Plugin<string> Provider(string name) => new()
                {
                    Config = raw => ConfigResult<string>.Success((string)raw!),
                    Apply = (child, value) => child.Provide("selected", name + ":" + value),
                };
                var modules = new StaticModuleResolver()
                    .Register("provider-a", Provider("a"))
                    .Register("provider-b", Provider("b"))
                    .Register("consumer", new Plugin<object?>
                    {
                        Inject = ["selected"],
                        Apply = (child, _) =>
                        {
                            var value = child.Get<string>("selected")!;
                            starts.Add(value);
                            child.Effect(() => (Action)(() => stops.Add(value)));
                        },
                    });
                var loader = new Loader(ctx, modules);
                var include = await ApplicationBoot.MountAsync(loader, filename);
                await loader.WaitAsync();
                Scenarios.Equal("a:first", starts[^1]);

                await ApplicationBoot.ReconcileAsync(include, ConfigurationFile.ParseEntries("[{id: provider, config: patched}]"));
                await loader.WaitAsync();
                Scenarios.Equal("a:patched", starts[^1]);
                await ApplicationBoot.ReconcileAsync(include, []);
                await loader.WaitAsync();
                Scenarios.Equal("a:first", starts[^1]);

                await loader.Resolve("root:provider").UpdateAsync(new EntryOptions { Disabled = true });
                await loader.WaitAsync();
                Scenarios.Equal(FiberState.Pending, loader.Resolve("root:consumer").Fiber!.State);
                await loader.Resolve("root:provider").UpdateAsync(new EntryOptions { Disabled = false });
                await loader.WaitAsync();
                Scenarios.Equal("a:first", starts[^1]);

                await File.WriteAllTextAsync(filename, "[{id: provider, name: provider-b, config: second}, {id: consumer, name: consumer, inject: [selected]}]");
                await include.RefreshAsync();
                await loader.WaitAsync();
                // A same-id name edit is metadata in the source Loader; replace the entry to select code.
                Scenarios.Equal("a:second", starts[^1]);
                await File.WriteAllTextAsync(filename, "[{id: provider2, name: provider-b, config: second}, {id: consumer, name: consumer, inject: [selected]}]");
                await include.RefreshAsync();
                await loader.WaitAsync();
                Scenarios.Equal("b:second", starts[^1]);
                Scenarios.Equal(true, stops.Contains("a:first"));
                await File.WriteAllTextAsync(filename, "not: an-entry-array");
                await include.RefreshAsync();
                Scenarios.Equal("b:second", ctx.Get<string>("selected"));
                await File.WriteAllTextAsync(filename, "[{id: provider2, name: provider-b, config: recovered}]");
                await include.RefreshAsync();
                await loader.WaitAsync();
                Scenarios.Equal("b:recovered", ctx.Get<string>("selected"));
                Scenarios.Equal("b:second", stops[^1]);
            });
        }
        finally { directory.Delete(true); }
    }
}
