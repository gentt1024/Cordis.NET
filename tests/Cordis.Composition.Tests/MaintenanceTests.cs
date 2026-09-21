using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class MaintenanceTests
{
    [Theory]
    [InlineData("# personal configuration\n- id: tool\n  config:\n    value: !!js process.platform\n- id: tool\n  disabled: false # availability\n")]
    [InlineData("- id: tool\n  disabled: false\n- id: tool\n  name: package\n  disabled: false # availability\n- id: tool\n  name: another-package\n  disabled: false\n")]
    [InlineData("- {id: tool, disabled: false} # availability\n")]
    public async Task EnablementEditsPreserveCommentsExpressionsAndLastApplicableOverride(string original)
    {
        using var directory = new Files(); var path = directory.At("cordis.patch.yml"); await File.WriteAllTextAsync(path, original);
        Assert.True(await ProfileMaintenance.WriteEnabledAsync(path, "tool", "package", false)); var changed = await File.ReadAllTextAsync(path); Assert.Contains("# availability", changed);
        if (original.Contains("!!js", StringComparison.Ordinal)) Assert.Contains("!!js process.platform", changed);
        Assert.False(await ProfileMaintenance.WriteEnabledAsync(path, "tool", "package", false)); Assert.Equal(changed, await File.ReadAllTextAsync(path));
        var composed = EntryPatches.Apply([new() { Id = "tool", Name = "package" }], await Profiles.ReadPatchesAsync(path)); Assert.Equal(true, composed[0].Disabled);
        Assert.True(await ProfileMaintenance.WriteEnabledAsync(path, "tool", "package", true));
    }
    [Theory]
    [InlineData(null)]
    [InlineData("[]\n")]
    [InlineData("- insert:\n  - id: tool\n    name: package\n")]
    [InlineData("- id: tool\n  name: another-package\n  disabled: false\n")]
    public async Task EnablementAppendsAfterInsertionsAndNameAssertions(string? original)
    {
        using var directory = new Files(); var path = directory.At("cordis.patch.yml"); if (original is not null) await File.WriteAllTextAsync(path, original);
        Assert.True(await ProfileMaintenance.WriteEnabledAsync(path, "tool", "package", false)); var patches = await Profiles.ReadPatchesAsync(path); Assert.Equal("tool", patches[^1].Id); Assert.Equal(true, patches[^1].Disabled);
    }
    [Theory]
    [InlineData("- id: [broken")]
    [InlineData("mapping: true\n")]
    public async Task EnablementRejectsMalformedFileWithoutOverwrite(string text)
    {
        using var directory = new Files(); var path = directory.At("cordis.patch.yml"); await File.WriteAllTextAsync(path, text);
        await Assert.ThrowsAnyAsync<Exception>(() => ProfileMaintenance.WriteEnabledAsync(path, "tool", "package", true)); Assert.Equal(text, await File.ReadAllTextAsync(path));
    }
    [Fact]
    public void SanitizePreservesBrokenPatchMetadataAndBackupCollisions()
    {
        using var directory = new Files(); Profiles.Initialize(directory.Path, ["broken"]); var manifestPath = directory.At("package.json"); var manifest = PackageManifest.Read(manifestPath); manifest.Raw["custom"] = "retained"; manifest.Raw["dependencies"] = new EntryOptions { ["broken-plugin"] = "1.2.3" }; manifest.Write(manifestPath);
        var brokenPackage = directory.At("node_modules/broken-plugin"); Directory.CreateDirectory(brokenPackage); var brokenManifest = System.IO.Path.Combine(brokenPackage, "package.json"); File.WriteAllText(brokenManifest, "{broken");
        var patch = directory.At("cordis.patch.yml"); File.WriteAllText(patch, ": broken YAML"); var clock = new FixedClock(); var first = ProfileMaintenance.Sanitize(directory.Path, ["good"], clock)!;
        Assert.Equal(": broken YAML", File.ReadAllText(first)); Assert.False(File.Exists(patch)); Assert.Equal("good", Assert.Single(PackageManifest.Read(manifestPath).Bundles)); Assert.Equal("retained", PackageManifest.Read(manifestPath).Raw["custom"]);
        Assert.Equal(patch + ".bak-1789555200000", first); Assert.Equal("{broken", File.ReadAllText(brokenManifest)); Assert.Equal("1.2.3", Assert.IsType<EntryOptions>(PackageManifest.Read(manifestPath).Raw["dependencies"])["broken-plugin"]);
        Assert.Null(ProfileMaintenance.Sanitize(directory.Path, ["good"], clock)); File.WriteAllText(patch, "second"); var second = ProfileMaintenance.Sanitize(directory.Path, ["good"], clock)!; Assert.Equal(first + "-1", second);
        File.WriteAllText(patch, "third"); Assert.Equal(first + "-2", ProfileMaintenance.Sanitize(directory.Path, ["good"], clock)); Assert.Equal(": broken YAML", File.ReadAllText(first)); Assert.Equal("second", File.ReadAllText(second)); Assert.Equal("third", File.ReadAllText(first + "-2"));
        Profiles.Initialize(directory.Path, ["good"]); Assert.Contains("[]", File.ReadAllText(patch));
    }
    [Fact]
    public void SanitizeRejectsInvalidManifestAndRenameBeforeChangingActivation()
    {
        using var directory = new Files(); Profiles.Initialize(directory.Path, ["broken"]); var patch = directory.At("cordis.patch.yml"); File.WriteAllText(patch, "broken patch");
        Assert.Throws<UnauthorizedAccessException>(() => ProfileMaintenance.Sanitize(directory.Path, ["good"], move: (_, _) => throw new UnauthorizedAccessException())); Assert.Equal("broken", Assert.Single(PackageManifest.Read(directory.At("package.json")).Bundles));
        File.WriteAllText(directory.At("package.json"), "{broken"); Assert.ThrowsAny<Exception>(() => ProfileMaintenance.Sanitize(directory.Path, ["good"])); Assert.Equal("broken patch", File.ReadAllText(patch));
        Assert.DoesNotContain(Directory.GetFiles(directory.Path), path => path.Contains(".bak-", StringComparison.Ordinal));
    }
    [Fact]
    public void SanitizeDoesNotCreateMissingProfileAndPreservesOrphanPatch()
    {
        using var directory = new Files(); var absent = directory.At("absent"); Assert.Null(ProfileMaintenance.Sanitize(absent, [])); Assert.False(Directory.Exists(absent));
        File.WriteAllText(directory.At("cordis.patch.yml"), "orphan"); var backup = ProfileMaintenance.Sanitize(directory.Path, [])!; Assert.Equal("orphan", File.ReadAllText(backup)); Assert.False(File.Exists(directory.At("package.json")));
    }
    [Fact]
    public async Task LoaderAwaitInterceptWaitsForImportsAndActivation()
    {
        await using var context = new Context(); var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Loader loader = null!; var seen = false;
        var modules = new StaticModuleResolver().Register("slow", new Plugin<object?> { ApplyAsync = async (_, _) => await gate.Task }).Register("dependent", new Plugin<object?> { InjectConfig = new Dictionary<string, object?> { ["loader"] = new EntryOptions { ["await"] = true } }, Apply = (_, _) => seen = true });
        await context.RunAsync(_ => { loader = new Loader(context, modules); return Task.CompletedTask; }); await loader.Root.UpdateAsync([new() { Id = "slow", Name = "slow" }, new() { Id = "dependent", Name = "dependent" }]);
        Assert.False(seen); Assert.Equal(FiberState.Pending, loader.Resolve("dependent").Fiber!.State); gate.SetResult(); await loader.WaitAsync(); Assert.True(seen);
    }
    [Fact]
    public async Task CustomTreeCarrierMarkerKeepsRawExpressionIdentity()
    {
        await using var context = new Context(); Loader loader = null!; object? seen = null; var raw = new JsExpression("deferred");
        var plugin = new TreeCarrierPlugin(new Plugin<object?> { Apply = (_, config) => seen = config });
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("custom-carrier", plugin)); return Task.CompletedTask; }); await loader.CreateAsync(new() { Id = "custom", Name = "custom-carrier", Config = raw }); await loader.WaitAsync(); Assert.Same(raw, seen);
    }
    [Fact]
    public async Task FailedReplacementRestoresAllPreviousFibers()
    {
        await using var context = new Context(); Loader loader = null!; var version = 0;
        var old = new Plugin<object?> { Apply = (_, _) => version = 1 }; var replacement = new Plugin<object?> { ApplyAsync = async (_, _) => { await Task.Yield(); throw new InvalidOperationException("broken replacement"); } };
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("p", old)); return Task.CompletedTask; }); await loader.CreateAsync(new() { Id = "p", Name = "p" }); await loader.WaitAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.ReplacePluginAsync(old, replacement)); Assert.Equal(1, version); Assert.Equal(FiberState.Active, loader.Resolve("p").Fiber!.State); Assert.Null(loader.Resolve("p").Options.Disabled);
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(1789555200000); }
    private sealed class Files : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cordis-maintenance-" + Guid.NewGuid().ToString("N")); public Files() => Directory.CreateDirectory(Path); public string At(string name) => System.IO.Path.Combine(Path, name); public void Dispose() => Directory.Delete(Path, true);
    }
}
