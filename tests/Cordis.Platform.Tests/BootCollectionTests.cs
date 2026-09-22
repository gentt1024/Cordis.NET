using System.Runtime.CompilerServices;
using Cordis.Clr;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class BootCollectionTests
{
    [Fact]
    public async Task OptionalStartupErrorsDoNotRetainCollectiblePluginForTheLiveRootLifetime()
    {
        var state = await BootAndRemoveFailedPlugin();
        try
        {
            Assert.True(state.Unload.UnloadRequested);
            for (var attempt = 0; attempt < 12 && !state.Unload.IsCollected; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Yield();
            }
            Assert.True(state.Unload.IsCollected, "A live root must not retain a removed plugin through startup collectors.");
            Assert.Contains("ProbeActivationException", state.Snapshot.Error);
            Assert.Contains("collectible startup failure", state.Snapshot.Error);
            await state.Root.RunAsync(ctx =>
            {
                Assert.NotNull(ctx.Get<Loader>("loader"));
                ctx.Provide("still-running", true);
                Assert.Equal(true, ctx.Get("still-running"));
                return Task.CompletedTask;
            });
            Assert.True(state.Unload.TryDeleteShadow());
        }
        finally
        {
            await state.Root.DisposeAsync();
            if (state.Unload.TryDeleteShadow()) Directory.Delete(state.ShadowRoot);
        }
    }

    private sealed record BootState(Context Root, EntryDiagnosticSnapshot Snapshot, ClrUnloadObservation Unload, string ShadowRoot);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<BootState> BootAndRemoveFailedPlugin()
    {
        var path = Path.Combine(Path.GetTempPath(), "cordis-boot-" + Guid.NewGuid().ToString("N") + ".yml");
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-boot-shadow-" + Guid.NewGuid().ToString("N"));
        await using var resolver = new ClrModuleResolver(shadow);
        resolver.Register("optional-failure", new ClrModuleDefinition(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "probe-v1"), "ProbePlugin.dll", "Cordis.ProbeFixture.FailingEntry"));
        Context? root = null;
        try
        {
            await File.WriteAllTextAsync(path, "- id: optional\n  name: optional-failure\n");
            root = await ApplicationBoot.BootGenericAsync(path, resolver, warn: static _ => { });
            Loader loader = null!;
            await root.RunAsync(ctx => { loader = ctx.Get<Loader>("loader")!; return Task.CompletedTask; });
            var diagnostic = Assert.Single(await ApplicationBoot.AuditAsync(loader));
            Assert.Equal("apply", diagnostic.Phase);
            Assert.Equal("Cordis.ProbeFixture.ProbeActivationException", diagnostic.Error!.GetType().FullName);
            var snapshot = diagnostic.ToSnapshot();
            await loader.RemoveAsync("root:optional");
            await loader.WaitAsync();
            await root.RunAsync(ctx =>
            {
                // The bounded raw log buffer intentionally owns exception arguments. Clear it
                // explicitly in this regression to isolate boot's separate startup collectors.
                Assert.Contains(ctx.Logger.Buffer, log => log.Arguments.Any(argument => argument is Exception));
                Assert.IsAssignableFrom<IList<LogMessage>>(ctx.Logger.Buffer).Clear();
                return Task.CompletedTask;
            });
            await resolver.DisposeAsync();
            return new(root, snapshot, Assert.Single(resolver.Unloads), shadow);
        }
        catch
        {
            if (root is not null) await root.DisposeAsync();
            throw;
        }
        finally { File.Delete(path); }
    }
}
