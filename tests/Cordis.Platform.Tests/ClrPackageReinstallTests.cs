using System.Runtime.CompilerServices;
using Cordis.Clr;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class ClrPackageReinstallTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cordis-clr-reinstall-").FullName;
    private static readonly Uri Basis = new("file:///");

    public ClrPackageReinstallTests() => File.WriteAllText(Path.Combine(directory, "package.json"), "{}");

    [Fact]
    public async Task Loaded_module_snapshot_preserves_import_identity_without_importing_inactive_exports()
    {
        await using var resolver = new ClrModuleResolver();
        var definition = new ClrModuleDefinition(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "v1"),
            "VersionedPlugin.dll",
            "VersionedPlugin.IdentityOne");
        resolver.Register("one", definition);
        resolver.Register("alias", definition);
        resolver.Register(
            "inactive",
            definition with
            {
                EntryType = "VersionedPlugin.InvalidEntry"
            });
        Assert.Empty(await resolver.GetLoadedModulesAsync(["one", "alias", "inactive", "missing"]));
        var one = await resolver.ResolveAsync("one", Basis);
        var alias = await resolver.ResolveAsync("alias", Basis);
        var loaded = await resolver.GetLoadedModulesAsync(["one", "alias", "one", "inactive", "missing"]);
        Assert.Equal(2, loaded.Count);
        Assert.Same(one, loaded[0]);
        Assert.Same(alias, loaded[1]);
        Assert.Same(one.Plugin, alias.Plugin);
        Assert.NotSame(one, alias);
        await resolver.RemoveAsync("one");
        Assert.Same(alias, Assert.Single(await resolver.GetLoadedModulesAsync(["one", "alias"])));
    }

    [Fact]
    public async Task Removal_releases_owned_routes_and_identical_reinstall_reuses_retained_files()
    {
        var observations = await ExerciseRemovalAndReinstallAsync();
        Assert.Equal(2, observations.Length);
        Assert.All(observations, observation => Assert.True(observation.UnloadRequested));
        for (var attempt = 0;attempt < 12 && observations.Any(observation => !observation.IsCollected);attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.All(observations, observation => Assert.True(observation.IsCollected));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<ClrUnloadObservation[]> ExerciseRemovalAndReinstallAsync()
    {
        await using var resolver = new ClrModuleResolver();
        using var tools = new DotnetPluginToolchain(directory, resolver, []);
        var first = Prepare();
        await tools.PublishAsync(first);
        var retained = tools.Bundles["Example"];
        Assert.Empty(await tools.GetLoadedModulesAsync("Example"));
        var original = await resolver.ResolveAsync("nuget:example", Basis);
        Assert.Same(original, Assert.Single(await tools.GetLoadedModulesAsync("EXAMPLE")));
        await tools.RemoveAsync("example");
        Assert.Empty(tools.Bundles);
        Assert.Empty(await tools.GetLoadedModulesAsync("Example"));
        Assert.True(Directory.Exists(retained));
        Assert.True(Assert.Single(resolver.Unloads).UnloadRequested);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => resolver.ResolveAsync("nuget:example", Basis).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            resolver.ResolveAsync("nuget:example/inactive", Basis).AsTask());

        var reinstall = Prepare();
        await tools.PublishAsync(reinstall);
        Assert.Equal(retained, tools.Bundles["Example"]);
        Assert.False(Directory.Exists(reinstall.Directory));
        var current = await resolver.ResolveAsync("nuget:example", Basis);
        Assert.NotSame(original, current);
        Assert.NotSame(original.Plugin, current.Plugin);
        Assert.Same(current, Assert.Single(await tools.GetLoadedModulesAsync("Example")));
        // The test deliberately retains both imports until every identity assertion completes.
        // Return only weak observations so xUnit's retained test task cannot keep their DLLs loaded.
        await resolver.DisposeAsync();
        return resolver.Unloads.ToArray();
    }

    [Theory]
    [InlineData("dependency.bin")]
    [InlineData("PluginDeployment.dll")]
    [InlineData("PluginDeployment.deps.json")]
    [InlineData("added.bin")]
    [InlineData("missing.bin")]
    public async Task Different_same_version_contents_preserve_both_artifacts_and_leave_routes_removed(
        string difference)
    {
        await using var resolver = new ClrModuleResolver();
        using var tools = new DotnetPluginToolchain(directory, resolver, []);
        await tools.PublishAsync(Prepare());
        var retained = tools.Bundles["Example"];
        await tools.RemoveAsync("Example");
        var reinstall = Prepare();
        if (difference == "missing.bin")
            File.Delete(Path.Combine(reinstall.Directory, "dependency.bin"));
        else
            await File.WriteAllTextAsync(Path.Combine(reinstall.Directory, difference), "changed content");
        var refusal = await Assert.ThrowsAsync<DeploymentRestartRequiredException>(() => tools.PublishAsync(reinstall));
        Assert.Equal("package-moved", refusal.Reason);
        Assert.Empty(tools.Bundles);
        Assert.True(Directory.Exists(reinstall.Directory));
        Assert.True(Directory.Exists(retained));
        Assert.Equal("original dependency", await File.ReadAllTextAsync(Path.Combine(retained, "dependency.bin")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => resolver.ResolveAsync("nuget:example", Basis).AsTask());
    }

    [Fact]
    public async Task Tampered_retained_artifact_is_an_integrity_failure_before_reinstall_registration()
    {
        await using var resolver = new ClrModuleResolver();
        using var tools = new DotnetPluginToolchain(directory, resolver, []);
        await tools.PublishAsync(Prepare());
        var retained = tools.Bundles["Example"];
        await tools.RemoveAsync("Example");
        await File.WriteAllTextAsync(Path.Combine(retained, "dependency.bin"), "tampered");
        var reinstall = Prepare();
        await Assert.ThrowsAsync<InvalidDataException>(() => tools.PublishAsync(reinstall));
        Assert.Empty(tools.Bundles);
        Assert.True(Directory.Exists(reinstall.Directory));
        Assert.Equal("tampered", await File.ReadAllTextAsync(Path.Combine(retained, "dependency.bin")));
    }

    private PreparedPackage Prepare()
    {
        var prepared = Directory.CreateDirectory(
                Path.Combine(directory, ".cordis", "work", Guid.NewGuid().ToString("N"), "bundle"))
            .FullName;
        var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "v1");
        foreach (var file in Directory.GetFiles(fixture, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(prepared, Path.GetRelativePath(fixture, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        File.WriteAllText(Path.Combine(prepared, "package.json"), """{"name":"Example","version":"1.0.0"}""");
        File.WriteAllText(
            Path.Combine(prepared, "cordis.plugin.json"),
            """{"assembly":"VersionedPlugin.dll","entryType":"VersionedPlugin.IdentityOne","exports":{"./inactive":"VersionedPlugin.InvalidEntry"}}""");
        File.WriteAllText(Path.Combine(prepared, "dependency.bin"), "original dependency");
        File.WriteAllText(Path.Combine(prepared, "PluginDeployment.dll"), "original scaffold");
        File.WriteAllText(Path.Combine(prepared, "PluginDeployment.deps.json"), "original dependencies");
        return new("Example", "1.0.0", prepared);
    }

    public void Dispose()
    {
        for (var attempt = 0;;attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception error) when (attempt < 11 && error is IOException or UnauthorizedAccessException)
            {
                // Managed ALC collection can precede release of Windows' native DLL handles.
                // Retry finalization within the same bound as the other collectible CLR tests;
                // a persistent lock still fails cleanup with its original filesystem exception.
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
        }
    }
}
