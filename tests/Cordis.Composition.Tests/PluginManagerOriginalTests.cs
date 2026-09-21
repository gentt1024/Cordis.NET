using Cordis;
using Cordis.Composition;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class PluginManagerOriginalTests
{
    [Fact]
    public async Task PatchPreservesCommentsExpressionsAndLastOverride()
    {
        await using var f = new Fixture();
        await File.WriteAllTextAsync(f.Patch, "# personal configuration\n- id: tool\n  config:\n    value: !!js process.platform\n- id: tool\n  disabled: false # availability\n");
        Assert.True(await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", false));
        var text = await File.ReadAllTextAsync(f.Patch);
        Assert.Contains("# personal configuration", text);
        Assert.Contains("!!js process.platform", text);
        Assert.Contains("# availability", text);
        EqualRows("- id: tool\n  config:\n    value: !!js process.platform\n- id: tool\n  disabled: true\n", await Profiles.ReadPatchesAsync(f.Patch));
        Assert.False(await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", false));
        Assert.Equal(text, await File.ReadAllTextAsync(f.Patch));
        Assert.True(await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", true));
    }

    [Fact]
    public async Task PatchCreatesMissingFileAndAppendsAfterInsertions()
    {
        await using var f = new Fixture();
        File.Delete(f.Patch);
        await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", false);
        EqualRows("- id: tool\n  disabled: true\n", await Profiles.ReadPatchesAsync(f.Patch));
        await File.WriteAllTextAsync(f.Patch, "- insert:\n    - id: tool\n      name: package\n");
        await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", true);
        EqualRows("- insert:\n    - id: tool\n      name: package\n- id: tool\n  disabled: false\n", await Profiles.ReadPatchesAsync(f.Patch));
    }

    [Theory]
    [InlineData("- id: [broken")]
    [InlineData("mapping: true\n")]
    public async Task PatchRejectsMalformedWithoutOverwriting(string text)
    {
        await using var f = new Fixture();
        await File.WriteAllTextAsync(f.Patch, text);
        await Assert.ThrowsAnyAsync<Exception>(() => ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", true));
        Assert.Equal(text, await File.ReadAllTextAsync(f.Patch));
    }

    [Fact]
    public async Task PatchRetainsNameAssertions()
    {
        await using var f = new Fixture();
        await File.WriteAllTextAsync(f.Patch, "- id: tool\n  name: another-package\n  disabled: false\n");
        await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", false);
        EqualRows("- id: tool\n  name: another-package\n  disabled: false\n- id: tool\n  disabled: true\n", await Profiles.ReadPatchesAsync(f.Patch));
        Assert.False(await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", false));
    }

    [Fact]
    public async Task PatchRejectsDirectory()
    {
        await using var f = new Fixture();
        File.Delete(f.Patch);
        Directory.CreateDirectory(f.Patch);
        await Assert.ThrowsAnyAsync<Exception>(() => ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", true));
        Assert.True(Directory.Exists(f.Patch));
    }

    [Fact]
    public async Task PatchUpdatesLastMatchingNamedOverride()
    {
        await using var f = new Fixture();
        const string input = "- id: tool\n  disabled: true\n- id: tool\n  name: package\n  config:\n    value: !!js process.platform\n  disabled: true # availability\n- id: tool\n  name: another-package\n  disabled: true\n";
        await File.WriteAllTextAsync(f.Patch, input);
        Assert.True(await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", true));
        var text = await File.ReadAllTextAsync(f.Patch);
        Assert.Contains("!!js process.platform", text);
        Assert.Contains("# availability", text);
        var patches = await Profiles.ReadPatchesAsync(f.Patch);
        EqualRows(input.Replace("disabled: true # availability", "disabled: false # availability", StringComparison.Ordinal), patches);
        var result = Assert.Single(EntryPatches.Apply([new() { Id = "tool", Name = "package" }], patches));
        Assert.Equal("tool", result.Id);
        Assert.Equal("package", result.Name);
        Assert.Equal(false, result.Disabled);
        Assert.False(await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", true));
        Assert.Equal(text, await File.ReadAllTextAsync(f.Patch));
        Assert.True(await ProfileMaintenance.WriteEnabledAsync(f.Patch, "tool", "package", false));
        Assert.Equal(3, (await Profiles.ReadPatchesAsync(f.Patch)).Count);
    }

    [Fact]
    public async Task PluginTogglesPersistOnceAndApply()
    {
        await using var f = new Fixture();
        await f.Start();
        AssertChange(await f.Manager.SetPluginEnabledAsync("root:managed", false), true, "applied");
        Assert.False((await f.Manager.ListPluginsAsync()).Single(p => p.PatchId == "managed").Enabled);
        AssertChange(await f.Manager.SetPluginEnabledAsync("root:managed", false), false, "applied");
        AssertChange(await f.Manager.SetPluginEnabledAsync("root:managed", true), true, "applied");
        Assert.Single((await Profiles.ReadPatchesAsync(f.Patch)), row => row.Id == "managed");
    }

    [Fact]
    public async Task BundleTogglesRetainDependenciesAndAppendWhenReenabled()
    {
        await using var f = new Fixture();
        await f.Start();
        AssertChange(await f.Manager.SetBundleEnabledAsync("extra", false), true, "applied");
        Assert.Equal("1.0.0", Assert.IsType<EntryOptions>(f.Manifest.Raw["dependencies"])["extra"]);
        Assert.DoesNotContain(await f.Manager.ListPluginsAsync(), row => row.PatchId == "managed");
        await f.Bundle("third", []);
        AssertChange(await f.Manager.SetBundleEnabledAsync("third", true), true, "applied");
        AssertChange(await f.Manager.SetBundleEnabledAsync("extra", true), true, "applied");
        Assert.Equal(new[] { "core", "third", "extra" }, f.Manifest.Bundles);
    }

    [Fact]
    public async Task OverlayReportsSavedToggleAsOverridden()
    {
        await using var f = new Fixture();
        await f.Start(overlays: [new("cli", [new() { Id = "managed", Disabled = true }])]);
        AssertChange(await f.Manager.SetPluginEnabledAsync("root:managed", true), true, "overridden");
        Assert.Equal(false, Assert.Single(await Profiles.ReadPatchesAsync(f.Patch)).Disabled);
    }

    [Fact]
    public async Task StartupOnlyTogglePersistsWithoutChangingLiveEntry()
    {
        await using var f = new Fixture();
        await f.Start(live: false);
        AssertChange(await f.Manager.SetPluginEnabledAsync("root:managed", false), true, "restart-required");
        Assert.True((await f.Manager.ListPluginsAsync()).Single(row => row.EntryId == "root:managed").Enabled);
    }

    [Fact]
    public async Task ProtectsOwnerUnknownEntriesAndManagementBundles()
    {
        await using var f = new Fixture();
        await f.Start();
        var before = File.ReadAllText(f.Patch);
        var owner = await f.Manager.SetPluginEnabledAsync("root:manager", false);
        Assert.Equal("management-required", owner.Error);
        AssertChange(owner, false, "failed");
        var unknown = await f.Manager.SetPluginEnabledAsync("missing", true);
        Assert.Equal("unknown-plugin", unknown.Error);
        AssertChange(unknown, false, "failed");
        Assert.Equal("management-required", (await f.Manager.SetBundleEnabledAsync("core", false)).Error);
        Assert.Equal("operation-error", (await f.Manager.SetBundleEnabledAsync("unknown", true)).Error);
        AssertChange(await f.Manager.SetBundleEnabledAsync("extra", true), false, "applied");
        Assert.Equal(before, File.ReadAllText(f.Patch));
    }

    [Theory]
    [InlineData("@deepseek-ai/dsh-host-plugin-inventory")]
    [InlineData("@deepseek-ai/dsh-typert-registry")]
    [InlineData("@deepseek-ai/dsh-api-remotes")]
    public async Task ProtectsManagementDependencyAndContainingBundle(string module)
    {
        await using var f = new Fixture();
        f.Modules.Register(module, new Plugin<object?>
        {
            Apply = (_, _) =>
        {
        }
        });
        await f.Start(module: module);
        var manifest = File.ReadAllText(f.ManifestPath);
        var patch = File.ReadAllText(f.Patch);
        Assert.Equal("management-required", (await f.Manager.ListPluginsAsync()).Single(row => row.EntryId == "root:managed").ReadOnlyReason);
        Assert.Equal("management-required", (await f.Manager.SetPluginEnabledAsync("root:managed", false)).Error);
        var bundle = (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "extra");
        Assert.False(bundle.Removable);
        Assert.Equal("management-required", bundle.ReadOnlyReason);
        AssertChange(await f.Manager.SetBundleEnabledAsync("extra", false), false, "failed");
        Assert.Equal(manifest, File.ReadAllText(f.ManifestPath));
        Assert.Equal(patch, File.ReadAllText(f.Patch));
    }

    [Fact]
    public async Task ActivationFailureKeepsSavedChangeAndCorrectedConfigRetries()
    {
        await using var f = new Fixture();
        f.Modules.Register("managed", new Plugin<object?>
        {
            Apply = (_, config) =>
        {
            if (config is not true)
                throw new InvalidOperationException("invalid settings");
        }
        });
        await f.Start(initiallyDisabled: true);
        await File.WriteAllTextAsync(f.Patch, "- id: managed\n  disabled: true\n");
        AssertChange(await f.Manager.SetPluginEnabledAsync("root:managed", true), true, "failed");
        Assert.Contains("disabled: false", File.ReadAllText(f.Patch));
        await File.WriteAllTextAsync(f.Patch, "- id: managed\n  disabled: true\n  config: true\n");
        var retry = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.True(retry.Application == "applied", retry.Diagnostic);
    }

    [Fact]
    public async Task InventoryDescribesBundleRowsOverridesAndLiveTransitions()
    {
        await using var f = new Fixture();
        await f.Start();
        await f.Bundle("described", [new() { ["insert"] = new[] { new EntryOptions { Id = "described-row", Name = "managed" } } }, new() { Id = "managed", Disabled = true }], "2.0.0", "Describes itself.");
        var bundle = (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "described");
        Assert.Equal("2.0.0", bundle.Version);
        Assert.Equal("Describes itself.", bundle.Description);
        Assert.False(bundle.Enabled);
        Assert.True(bundle.Installed);
        Assert.False(bundle.Optional);
        Assert.True(bundle.Removable);
        Assert.Equal(new[] { "managed" }, bundle.Overrides);
        Assert.Equal(new BundleConfigurationRow("described-row", "managed", null), Assert.Single(bundle.Rows));
        Assert.Equal("applied", (await f.Manager.SetBundleEnabledAsync("described", true)).Application);
        Assert.Equal("root:described-row", Assert.Single((await f.Manager.ListBundlesAsync()).Single(row => row.Name == "described").Rows).EntryId);
        await f.Manager.SetBundleEnabledAsync("described", false);
        Assert.Null(Assert.Single((await f.Manager.ListBundlesAsync()).Single(row => row.Name == "described").Rows).EntryId);
    }

    [Fact]
    public async Task InventoryOffersOptionalBundlesAndOmitsPlainInstallationPackages()
    {
        await using var f = new Fixture();
        await f.Start();
        var offered = await f.Bundle("offered", [new() { ["insert"] = new[] { new EntryOptions { Id = "offered-row", Name = "managed" } } }], "3.0.0", "Package one-liner.", dependency: false);
        f.Installation["offered"] = offered;
        f.Manager.OptionalBundles.Add("offered");
        var plain = await f.Bundle("plain", [], dependency: false);
        File.WriteAllText(Path.Combine(plain, "package.json"), "{}");
        f.Installation["plain"] = plain;
        var row = (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "offered");
        Assert.False(row.Enabled);
        Assert.False(row.Installed);
        Assert.True(row.Optional);
        Assert.False(row.Removable);
        Assert.Equal("3.0.0", row.Version);
        Assert.Equal("Package one-liner.", row.Description);
        Assert.Equal(new BundleConfigurationRow("offered-row", "managed", null), Assert.Single(row.Rows));
        Assert.Empty(row.Overrides);
        Assert.Equal("applied", (await f.Manager.SetBundleEnabledAsync("offered", true)).Application);
        Assert.True((await f.Manager.ListBundlesAsync()).Single(row => row.Name == "offered").Enabled);
        Assert.DoesNotContain(await f.Manager.ListBundlesAsync(), row => row.Name == "plain");
    }

    [Fact]
    public async Task ChangeNotificationsIncludeNoopsAndFailuresButNotExternalRefresh()
    {
        await using var f = new Fixture();
        await f.Start();
        var reasons = new List<string>();
        f.Manager.Changed += reasons.Add;
        await ApplicationBoot.ReconcileAsync(f.Include, ProfileComposition.Flatten((await ProfileComposition.RefreshAsync(f.Launch)).Layers));
        Assert.Empty(reasons);
        await f.Manager.SetPluginEnabledAsync("root:managed", false);
        Assert.Equal(new[] { "plugin" }, reasons);
        await f.Manager.SetBundleEnabledAsync("extra", true);
        Assert.Equal(new[] { "plugin", "bundle" }, reasons);
        reasons.Clear();
        await f.Manager.SetPluginEnabledAsync("root:managed", false);
        await f.Manager.SetPluginEnabledAsync("root:managed", false);
        await f.Manager.SetBundleEnabledAsync("unknown", true);
        Assert.Equal(new[] { "plugin", "plugin", "bundle" }, reasons);
    }

    [Fact]
    public async Task ConfigurationMutationSharesHmrQueueAndSerializesConcurrentCalls()
    {
        await using var f = new Fixture();
        await f.Start();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        f.Manager.RunExclusiveAsync = async action =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult();
                await release.Task;
            }

            await action();
        };
        var first = f.Manager.SetPluginEnabledAsync("root:managed", false);
        await entered.Task;
        var second = f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.Equal(1, calls);
        Assert.False(second.IsCompleted);
        Assert.Equal("[]\n", File.ReadAllText(f.Patch));
        release.SetResult();
        Assert.Equal("applied", (await first).Application);
        Assert.Equal("applied", (await second).Application);
        Assert.Equal(2, calls);
        Assert.Equal(false, Assert.Single(await Profiles.ReadPatchesAsync(f.Patch)).Disabled);
    }

    [Fact]
    public async Task InventoryListsVersionsAndCurrentPluginTargets()
    {
        await using var f = new Fixture();
        await f.Start();
        var plugins = await f.Manager.ListPluginsAsync();
        var managed = plugins.Single(row => row.EntryId == "root:managed");
        Assert.Equal("managed", managed.PatchId);
        Assert.True(managed.Enabled);
        Assert.Equal("management-required", plugins.Single(row => row.EntryId == "root:manager").ReadOnlyReason);
        var bundles = await f.Manager.ListBundlesAsync();
        Assert.Equal(new[] { "core", "extra" }, bundles.Select(row => row.Name));
        var core = bundles[0];
        Assert.Equal("1.0.0", core.Version);
        Assert.True(core.Enabled);
        Assert.False(core.Installed);
        Assert.False(core.Optional);
        Assert.False(core.Removable);
        Assert.Equal("management-required", core.ReadOnlyReason);
        Assert.Equal(new BundleConfigurationRow("manager", "cordis:manager", "root:manager"), Assert.Single(core.Rows));
        Assert.Empty(core.Overrides);
        var extra = bundles[1];
        Assert.Equal("1.0.0", extra.Version);
        Assert.True(extra.Enabled);
        Assert.True(extra.Installed);
        Assert.False(extra.Optional);
        Assert.True(extra.Removable);
        Assert.Null(extra.ReadOnlyReason);
        Assert.Equal(new BundleConfigurationRow("managed", "managed", "root:managed"), Assert.Single(extra.Rows));
        Assert.Empty(extra.Overrides);
    }

    [Fact]
    public async Task GroupChildrenAreAddressableAndDuplicateIdsAreReadOnly()
    {
        await using var f = new Fixture();
        await f.Start();
        await f.Bundle("grouped", [new() { ["insert"] = new[] { new EntryOptions { Id = "group", Name = "cordis:group", Group = true, Config = new[] { new EntryOptions { Id = "child", Name = "managed" } } } } }]);
        var grouped = await f.Manager.SetBundleEnabledAsync("grouped", true);
        Assert.True(grouped.Application == "applied", grouped.Diagnostic);
        Assert.Contains(await f.Manager.ListPluginsAsync(), row => row.PatchId == "child");
        await File.WriteAllTextAsync(f.Patch, "- insert:\n    - id: duplicate-group\n      name: group\n      group: true\n      config:\n        - id: managed\n          name: managed\n");
        Assert.Equal("unaddressable", (await f.Manager.ListPluginsAsync()).Single(row => row.EntryId == "root:managed").ReadOnlyReason);
    }

    [Fact]
    public async Task PlainSelectedPackagesAndMissingVersionsRemainDistinct()
    {
        await using var f = new Fixture();
        await f.Start();
        File.WriteAllText(Path.Combine(f.Packages["extra"], "package.json"), "{}");
        var extra = (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "extra");
        Assert.True(extra.Enabled);
        Assert.Equal("not-bundle", extra.Error);
        Assert.Equal("applied", (await f.Manager.SetBundleEnabledAsync("extra", false)).Application);
        Assert.DoesNotContain(await f.Manager.ListBundlesAsync(), row => row.Name == "extra");
        AssertChange(await f.Manager.SetBundleEnabledAsync("extra", true), false, "failed");
        var coreManifest = PackageManifest.Read(Path.Combine(f.Installation["core"], "package.json"));
        coreManifest.Raw.Remove("version");
        coreManifest.Write(Path.Combine(f.Installation["core"], "package.json"));
        Assert.Null((await f.Manager.ListBundlesAsync())[0].Version);
        f.Installation.Clear();
        File.WriteAllText(f.ManifestPath, "{}");
        Assert.Empty(await f.Manager.ListBundlesAsync());
        Assert.Equal("failed", (await f.Manager.SetBundleEnabledAsync("unknown", false)).Application);
        Assert.Empty(await f.Manager.ListBundlesAsync());
    }

    [Fact]
    public async Task MissingPatchIsCreatedAndUnreadableDiskStateRejects()
    {
        await using var f = new Fixture();
        await f.Start();
        File.Delete(f.Patch);
        AssertChange(await f.Manager.SetPluginEnabledAsync("root:managed", false), true, "applied");
        File.Delete(f.Patch);
        Directory.CreateDirectory(f.Patch);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Manager.SetPluginEnabledAsync("root:managed", true));
    }

    [Fact]
    public async Task UnchangedFailuresRemainWarningsForUnrelatedChanges()
    {
        await using var f = new Fixture();
        await f.Start();
        f.Modules.Register("broken", new Plugin<object?> { Apply = (_, _) => throw new InvalidOperationException("test activation failed") });
        f.Modules.Register("pending", new Plugin<object?>
        {
            Inject = ["unavailable"],
            Apply = (_, _) =>
        {
        }
        });
        await f.Bundle("broken", [new() { ["insert"] = new[] { new EntryOptions { Id = "broken", Name = "broken" }, new EntryOptions { Id = "missing", Name = "absent" }, new EntryOptions { Id = "pending", Name = "pending" } } }]);
        Assert.Equal("failed", (await f.Manager.SetBundleEnabledAsync("broken", true)).Application);
        Assert.Equal("failed", (await f.Manager.SetPluginEnabledAsync("root:broken", true)).Application);
        Assert.Equal("failed", (await f.Manager.SetBundleEnabledAsync("broken", true)).Application);
        var changed = await f.Manager.SetPluginEnabledAsync("root:managed", false);
        Assert.Equal("applied", changed.Application);
        Assert.True(changed.Warnings!.Count == 3, string.Join("; ", changed.Warnings.Select(w => w.Id + ":" + w.Error?.Message)));
        Assert.Equal("applied", (await f.Manager.SetPluginEnabledAsync("root:managed", true)).Application);
    }

    [Fact]
    public async Task ReconcileActivatesNewBundlesAndPreservesRetainedDisabledOnes()
    {
        await using var f = new Fixture();
        await f.Bundle("disabled", []);
        var before = ProfileMaintenance.Inventory(f.DirectoryPath, f.Packages, f.Installation);
        await f.Bundle("new-bundle", []);
        var result = ProfileMaintenance.Reconcile(f.DirectoryPath, before, true, f.Packages, f.Installation);
        Assert.Equal(new[] { "new-bundle" }, result.Inventory.Manifest.Bundles);
        Assert.Equal(new[] { "new-bundle" }, ProfileMaintenance.Reconcile(f.DirectoryPath, result.Inventory, true, f.Packages, f.Installation).Inventory.Manifest.Bundles);
    }

    [Fact]
    public async Task ReconcileRetainsBuiltinsRemovesDeletedDependenciesAndReportsPlainPackages()
    {
        await using var f = new Fixture();
        await f.Bundle("removed", []);
        ProfileMaintenance.WriteBundles(f.DirectoryPath, f.Manifest, ["builtin", "removed"]);
        var before = ProfileMaintenance.Inventory(f.DirectoryPath, f.Packages, f.Installation);
        await f.Bundle("plain", []);
        File.WriteAllText(Path.Combine(f.Packages["plain"], "package.json"), "{\"name\":\"plain\"}");
        var manifest = f.Manifest;
        Assert.IsType<EntryOptions>(manifest.Raw["dependencies"]).Remove("removed");
        manifest.Write(f.ManifestPath);
        var result = ProfileMaintenance.Reconcile(f.DirectoryPath, before, true, f.Packages, f.Installation);
        Assert.Equal(new[] { "builtin" }, result.Inventory.Manifest.Bundles);
        Assert.Equal(new[] { "plain" }, result.AddedPlainDependencies);
    }

    [Fact]
    public async Task ReconcileKeepsExplicitSelectionSingleAndHandlesEmptyManifest()
    {
        await using var f = new Fixture();
        File.WriteAllText(f.ManifestPath, "{}");
        var before = ProfileMaintenance.Inventory(f.DirectoryPath, f.Packages, f.Installation);
        Assert.Empty(ProfileMaintenance.Reconcile(f.DirectoryPath, before, true, f.Packages, f.Installation).Inventory.Manifest.Bundles);
        await f.Bundle("new", []);
        ProfileMaintenance.WriteBundles(f.DirectoryPath, f.Manifest, ["new"]);
        Assert.Equal(new[] { "new" }, ProfileMaintenance.Reconcile(f.DirectoryPath, before, true, f.Packages, f.Installation).Inventory.Manifest.Bundles);
    }

    [Fact]
    public async Task PreviewRetainsExpressionsAndDoesNotMutateFiles()
    {
        await using var f = new Fixture();
        await f.Start();
        await File.WriteAllTextAsync(f.Patch, "- id: managed\n  config: !!js process.platform\n");
        var before = File.ReadAllText(f.Patch);
        var manifest = File.ReadAllText(f.ManifestPath);
        var preview = await f.Manager.PreviewAsync();
        Assert.Contains("!!js", preview);
        Assert.Contains("process.platform", preview);
        Assert.Equal(before, File.ReadAllText(f.Patch));
        Assert.Equal(manifest, File.ReadAllText(f.ManifestPath));
    }

    [Fact]
    public async Task CustomUserLayerPathOwnsPersistenceAndPreview()
    {
        await using var f = new Fixture();
        await f.Start();
        var custom = Path.Combine(f.DirectoryPath, "custom-user.yml");
        await File.WriteAllTextAsync(custom, "[]\n");
        var launch = f.Launch with
        {
            Profile = f.Launch.Profile with
            {
                UserLayer = new(custom, [])
            }
        };
        var manager = new PluginConfigurationOperations(launch, f.Include, "root:manager")
        {
            RunExclusiveAsync = action => action()
        };
        AssertChange(await manager.SetPluginEnabledAsync("root:managed", false), true, "applied");
        Assert.Equal(true, Assert.Single(await Profiles.ReadPatchesAsync(custom)).Disabled);
        Assert.Equal("[]\n", File.ReadAllText(f.Patch));
        var preview = ConfigurationFile.ParseEntries(await manager.PreviewAsync());
        Assert.Equal(true, preview.Single(row => row.Id == "managed").Disabled);
    }

    private static void EqualRows(string expected, object actual) => Assert.True(Data.DeepEquals(ConfigurationFile.ParseEntries(expected), actual));
    private static void AssertChange(ConfigurationChange change, bool changed, string application)
    {
        Assert.Equal(changed, change.Changed);
        Assert.Equal(application, change.Application);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "cordis-manager-" + Guid.NewGuid().ToString("N"));
        public string Patch => Path.Combine(DirectoryPath, "cordis.patch.yml");
        public string ManifestPath => Path.Combine(DirectoryPath, "package.json");
        public PackageManifest Manifest => PackageManifest.Read(ManifestPath);
        public Context Context { get; } = new();
        public StaticModuleResolver Modules { get; } = new();
        public Dictionary<string, string> Installation { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Packages { get; } = new(StringComparer.Ordinal);
        public Include Include { get; private set; } = null!;
        public ProfileLaunch Launch { get; private set; } = null!;
        public PluginConfigurationOperations Manager { get; private set; } = null!;

        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Profiles.Initialize(DirectoryPath, []);
            Modules.Register("cordis:manager", new Plugin<object?>
            {
                Apply = (_, _) =>
            {
            }
            });
            Modules.Register("managed", new Plugin<object?>
            {
                Apply = (_, _) =>
            {
            }
            });
        }

        public async Task<string> Bundle(string name, List<EntryOptions> patches, string version = "1.0.0", string? description = null, bool dependency = true)
        {
            var directory = Path.Combine(DirectoryPath, name);
            Directory.CreateDirectory(directory);
            var raw = new EntryOptions
            {
                ["name"] = name,
                ["version"] = version,
                ["dsh"] = new EntryOptions
                {
                    ["bundle"] = new EntryOptions
                    {
                        ["patch"] = "patch.yml"
                    }
                }
            };
            if (description is not null)
                raw["description"] = description;
            new PackageManifest(raw).Write(Path.Combine(directory, "package.json"));
            await File.WriteAllTextAsync(Path.Combine(directory, "patch.yml"), ConfigurationFile.Write(patches));
            Packages[name] = directory;
            if (dependency)
            {
                var manifest = Manifest;
                if (manifest.Raw.GetValueOrDefault("dependencies") is not EntryOptions dependencies)
                    manifest.Raw["dependencies"] = dependencies = new();
                dependencies[name] = version;
                manifest.Write(ManifestPath);
            }

            return directory;
        }

        public async Task Start(bool live = true, IReadOnlyList<ConfigurationLayer>? overlays = null, string module = "managed", bool initiallyDisabled = false)
        {
            Installation["core"] = await Bundle("core", [new() { ["insert"] = new[] { new EntryOptions { Id = "manager", Name = "cordis:manager" } } }], dependency: false);
            await Bundle("extra", [new() { ["insert"] = new[] { new EntryOptions { Id = "managed", Name = module, Disabled = initiallyDisabled } } }]);
            ProfileMaintenance.WriteBundles(DirectoryPath, Manifest, ["core", "extra"]);
            var profile = await Profiles.LoadAsync(DirectoryPath, Installation, Packages);
            var home = Path.Combine(DirectoryPath, "home");
            Directory.CreateDirectory(home);
            Launch = new(profile, home, overlays ?? [], Installation, Packages);
            var path = Path.Combine(DirectoryPath, "cordis.yml");
            await File.WriteAllTextAsync(path, "[]\n");
            Loader loader = null!;
            await Context.RunAsync(_ =>
            {
                loader = new Loader(Context, Modules);
                loader.Builtins["manager"] = new Plugin<object?>
                {
                    Apply = (_, _) =>
                    {
                    }
                };
                return Task.CompletedTask;
            });
            Include = await ApplicationBoot.MountAsync(loader, path, ProfileComposition.Flatten((await ProfileComposition.RefreshAsync(Launch)).Layers));
            Manager = new(Launch, Include, "root:manager")
            {
                RunExclusiveAsync = live ? action => action() : null
            };
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            Directory.Delete(DirectoryPath, true);
        }
    }
}
