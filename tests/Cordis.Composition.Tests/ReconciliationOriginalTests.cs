using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class ReconciliationOriginalTests
{
    [Fact]
    public async Task InFlightFailureDuringRemovalIsReported()
    {
        await using var fixture = await Fixture.Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Modules.Register("candidate", new Plugin<object?> { ApplyAsync = async (_, value) => { if (value is not true) return; entered.SetResult(); await release.Task; throw new InvalidOperationException("in-flight failure"); } });
        await fixture.Mount([new() { ["insert"] = new[] { new EntryOptions { Id = "candidate", Name = "candidate", Config = false } } }]); await fixture.Include.Resolve("candidate").UpdateAsync(new() { Config = true }); await entered.Task;
        var operation = ApplicationBoot.ReconcileAsync(fixture.Include, []); release.SetResult(); var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation); Assert.Contains("in-flight failure", error.Message);
    }
    [Fact]
    public async Task RemovedPluginResourcesAreJoinedAfterStoreRemoval()
    {
        await using var fixture = await Fixture.Create(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Modules.Register("held", new Plugin<object?> { Apply = (ctx, _) => ctx.Effect(() => new Cleanup(async () => { entered.SetResult(); await release.Task; })) }); await fixture.Mount([new() { ["insert"] = new[] { new EntryOptions { Id = "held", Name = "held" } } }]);
        var operation = ApplicationBoot.ReconcileAsync(fixture.Include, []); await entered.Task; Assert.Empty(fixture.Include.Entries()); Assert.False(operation.IsCompleted); release.SetResult(); await operation;
    }
    [Fact]
    public async Task UnchangedImportDiagnosticsAreRetained()
    {
        await using var fixture = await Fixture.Create(); var patches = new List<EntryOptions> { new() { ["insert"] = new[] { new EntryOptions { Id = "missing", Name = "absent" } } } }; await fixture.Mount(patches);
        var diagnostics = await ApplicationBoot.ReconcileAsync(fixture.Include, patches); Assert.Equal("root:missing", Assert.Single(diagnostics).Id); Assert.IsType<FileNotFoundException>(diagnostics[0].Error);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PatchFilesystemPathsPreserveReservedCharacters(bool optional)
    {
        await using var fixture = await Fixture.Create(); var path = Path.Combine(fixture.Directory, "patch.yml"); var absolute = Path.Combine(fixture.Directory, "absolute #100%.dll");
        var patches = new[] { new EntryOptions { Id = "assertion", Name = absolute }, new EntryOptions { ["insert"] = new[] { new EntryOptions { Id = "absolute", Name = absolute }, new EntryOptions { Id = "relative", Name = "./absolute #100%.dll" }, new EntryOptions { Id = "url", Name = new Uri(absolute).AbsoluteUri } } } };
        await File.WriteAllTextAsync(path, ConfigurationFile.Write(patches)); var result = await Profiles.ReadPatchesAsync(path, optional); Assert.Equal(absolute, result[0].Name); foreach (var row in Data.Entries(result[1]["insert"])) Assert.Equal(new Uri(absolute).AbsoluteUri, row.Name);
    }
    private sealed class Cleanup(Func<Task> callback) : IAsyncDisposable { public ValueTask DisposeAsync() => new(callback()); }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "cordis-reconcile-" + Guid.NewGuid().ToString("N")); public Context Context { get; } = new(); public StaticModuleResolver Modules { get; } = new(); public Loader Loader { get; private set; } = null!; public Include Include { get; private set; } = null!;
        public static async Task<Fixture> Create() { var f = new Fixture(); System.IO.Directory.CreateDirectory(f.Directory); await File.WriteAllTextAsync(Path.Combine(f.Directory, "cordis.yml"), "[]\n"); await f.Context.RunAsync(_ => { f.Loader = new Loader(f.Context, f.Modules); return Task.CompletedTask; }); return f; }
        public async Task Mount(List<EntryOptions> patches) => Include = await ApplicationBoot.MountAsync(Loader, Path.Combine(Directory, "cordis.yml"), patches);
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
