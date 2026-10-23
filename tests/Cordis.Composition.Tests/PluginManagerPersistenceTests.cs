using Cordis;
using Cordis.Composition;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed partial class PluginManagerOriginalTests
{
    [Theory]
    [InlineData("# header\n- id: managed\n  disabled: false\n# tail\n", "# header\n[]\n# tail\n")]
    [InlineData(
        "# header\n- id: managed\n  disabled: true\n# other\n- id: other\n  disabled: true\n",
        "# header\n# other\n- id: other\n  disabled: true\n")]
    [InlineData(
        "- id: other\n  disabled: true\n# about managed\n- id: managed\n  disabled: true\n# tail\n",
        "- id: other\n  disabled: true\n# about managed\n# tail\n")]
    [InlineData("- id: managed\n  # between fields\n  disabled: true # inline\n", "  # between fields\n[]\n")]
    [InlineData("[{id: managed, disabled: true}]", "[]")]
    [InlineData("[{id: managed, disabled: true}, {id: other, disabled: true}]", "[ {id: other, disabled: true}]")]
    [InlineData("[{id: other, disabled: true}, {id: managed, disabled: true}]", "[{id: other, disabled: true} ]")]
    [InlineData("[{id: other}, {id: managed, disabled: true}, {id: last}]", "[{id: other},  {id: last}]")]
    [InlineData(
        "[\n# header\n{id: managed, disabled: true}, # comma, comment\n{id: other}\n]",
        "[\n# header\n # comma, comment\n{id: other}\n]")]
    public async Task PruningPreservesSurroundingCommentsAndSequenceSyntax(string patch, string expected)
    {
        await using var f = new Fixture();
        await f.Start();
        await File.WriteAllTextAsync(f.Patch, patch);
        var result = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.Null(result.Error);
        Assert.True(result.Changed);
        Assert.Equal(expected, await File.ReadAllTextAsync(f.Patch));
        Assert.False((await f.Manager.SetPluginEnabledAsync("root:managed", true)).Changed);
    }

    [Theory]
    [InlineData("- id: managed\n  name: managed\n  disabled: true\n")]
    [InlineData("- id: managed\n  config: {value: !!js process.platform}\n  disabled: true\n")]
    [InlineData("- id: &plugin managed\n  disabled: true\n- id: other\n  config: {target: *plugin}\n")]
    [InlineData("- &plugin {id: managed, disabled: true}\n- id: other\n  config: {target: *plugin}\n")]
    [InlineData("- id: managed\n  &key disabled: true\n- id: other\n  config: {*key : 1}\n")]
    [InlineData("- id: other\n  config: {flag: &flag true}\n- id: managed\n  disabled: *flag\n")]
    [InlineData("- id: managed\n  disabled: &flag true\n- id: other\n  config: {flag: *flag}\n")]
    public async Task EnablementPreservesAssertionsSettingsAnchorsAndAliases(string patch)
    {
        await using var f = new Fixture();
        await f.Start();
        await File.WriteAllTextAsync(f.Patch, patch);
        var result = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.Null(result.Error);
        var text = await File.ReadAllTextAsync(f.Patch);
        var expected = patch
            .Replace("disabled: true", "disabled: false", StringComparison.Ordinal)
            .Replace("disabled: *flag", "disabled: false", StringComparison.Ordinal)
            .Replace("disabled: &flag true", "disabled: &flag false", StringComparison.Ordinal);
        Assert.Equal(expected, text);
        EqualRows(expected, await Profiles.ReadPatchesAsync(f.Patch));
        var repeated = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        if (patch.Contains("disabled: *flag", StringComparison.Ordinal))
        {
            Assert.True(repeated.Changed);
            Assert.DoesNotContain(await Profiles.ReadPatchesAsync(f.Patch), row => row.Id == "managed");
        }
        else
            Assert.False(repeated.Changed);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("!!js true")]
    [InlineData("\"false\"")]
    public async Task NonmatchingOrExpressionInheritanceKeepsLiteralOverride(string inherited)
    {
        await using var f = new Fixture();
        await f.Start();
        await File.WriteAllTextAsync(
            Path.Combine(f.Packages["extra"], "patch.yml"),
            "- insert: [{id: managed, name: managed, disabled: " + inherited + "}]\n");
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", true)).Error);
        Assert.Equal(false, Assert.Single(await Profiles.ReadPatchesAsync(f.Patch)).Disabled);
    }

    [Fact]
    public async Task ProfileInsertionAndEarlierOverridesContributeToInheritance()
    {
        await using var f = new Fixture();
        await f.Start();
        await File.WriteAllTextAsync(Path.Combine(f.Packages["extra"], "patch.yml"), "[]\n");
        const string patch =
            "- insert: [{id: managed, name: managed}]\n- id: managed\n  name: managed\n  disabled: true\n";
        await File.WriteAllTextAsync(f.Patch, patch + "- id: managed\n  disabled: false\n");
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", false)).Error);
        Assert.Equal(patch, await File.ReadAllTextAsync(f.Patch));
        await File.WriteAllTextAsync(f.Patch, "- insert: [{id: managed, name: managed}]\n");
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", true)).Error);
        Assert.Single(await Profiles.ReadPatchesAsync(f.Patch));
    }

    [Fact]
    public async Task MissingPatchAndHomeOverlayDoNotCreateRedundantSwitch()
    {
        await using var f = new Fixture();
        await f.Start();
        File.Delete(f.Patch);
        await File.WriteAllTextAsync(
            Path.Combine(f.Launch.Home, "cordis.patch.yml"),
            "- id: managed\n  disabled: true\n");
        var result = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.Null(result.Error);
        Assert.Equal("overridden", result.Application);
        Assert.False(result.Changed);
        Assert.False(File.Exists(f.Patch));
    }

    [Fact]
    public async Task PruningAdmissionFailureLeavesPatchAndBaseUntouched()
    {
        await using var f = new Fixture();
        await f.Start();
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", false)).Error);
        var patch = await File.ReadAllTextAsync(f.Patch);
        var basis = await File.ReadAllTextAsync(f.Include.Filename);
        f.Manager.AdmitProfileAsync = _ => throw new InvalidOperationException("candidate denied");
        var result = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.Equal("failed", result.Application);
        Assert.False(result.Changed);
        Assert.Equal(patch, await File.ReadAllTextAsync(f.Patch));
        Assert.Equal(basis, await File.ReadAllTextAsync(f.Include.Filename));
        Assert.Equal(
            true,
            ConfigurationFile
                .ParseEntries(await f.Manager.PreviewAsync())
                .Single(row => row.Id == "managed")
                .Disabled);
    }

    [Fact]
    public async Task PrunedPatchStaysSavedWhenActivationFails()
    {
        await using var f = new Fixture();
        var fail = false;
        f.Modules.Register(
            "managed",
            new Plugin<object?>
            {
                Apply = (_, _) =>
                {
                    if (fail)
                        throw new InvalidOperationException("activation failed");
                }
            });
        await f.Start();
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", false)).Error);
        fail = true;
        var result = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.Equal("failed", result.Application);
        Assert.True(result.Changed);
        Assert.Empty(await Profiles.ReadPatchesAsync(f.Patch));
    }

    [Fact]
    public async Task PruningDoesNotOverwriteAnInputChangedDuringAdmission()
    {
        await using var f = new Fixture();
        await f.Start();
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", false)).Error);
        const string external = "# external edit\n- id: managed\n  disabled: true\n";
        f.Manager.AdmitProfileAsync = _ => File.WriteAllTextAsync(f.Patch, external);
        var result = await f.Manager.SetPluginEnabledAsync("root:managed", true);
        Assert.Equal("profile-conflict", result.Error);
        Assert.Equal(external, await File.ReadAllTextAsync(f.Patch));
    }

    [Fact]
    public async Task ReturningToBundleDefaultRemovesSwitchAndInheritsLaterChanges()
    {
        await using var f = new Fixture();
        await f.Start();
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", false)).Error);
        Assert.Single(await Profiles.ReadPatchesAsync(f.Patch));
        Assert.Null((await f.Manager.SetPluginEnabledAsync("root:managed", true)).Error);
        Assert.Empty(await Profiles.ReadPatchesAsync(f.Patch));
        var text = await File.ReadAllTextAsync(f.Patch);
        Assert.False((await f.Manager.SetPluginEnabledAsync("root:managed", true)).Changed);
        Assert.Equal(text, await File.ReadAllTextAsync(f.Patch));
        await File.WriteAllTextAsync(
            Path.Combine(f.Packages["extra"], "patch.yml"),
            "- insert: [{ id: managed, name: managed, disabled: true }]\n");
        Assert.Equal(
            true,
            ConfigurationFile
                .ParseEntries(await f.Manager.PreviewAsync())
                .Single(row => row.Id == "managed")
                .Disabled);
    }

    [Fact]
    public async Task SelectedLocalBundleCanBeRemovedWithoutUninstallingAndSelectedAgain()
    {
        await using var f = new Fixture();
        await f.Start();
        await f.Bundle("local", [], dependency: false);
        Assert.Null((await f.Manager.SetBundleEnabledAsync("local", true)).Error);
        var info = (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "local");
        Assert.False(info.Installed);
        Assert.True(info.Removable);
        var toolchain = new RetainingToolchain();
        var removed = await f.Manager.RemovePackageAsync(toolchain, "local");
        Assert.Null(removed.Error);
        Assert.Equal("applied", removed.Application);
        Assert.False(removed.Installed);
        Assert.False(removed.Selected);
        Assert.DoesNotContain("local", f.Manifest.Bundles);
        Assert.Equal(0, toolchain.RemoveCalls);
        Assert.True(Directory.Exists(f.Packages["local"]));
        Assert.Null((await f.Manager.SetBundleEnabledAsync("local", true)).Error);
    }

    [Fact]
    public async Task MissingSelectedBundleCanBeDeselectedWithoutToolchainRemoval()
    {
        await using var f = new Fixture();
        await f.Start();
        ProfileMaintenance.WriteBundles(f.DirectoryPath, f.Manifest, ["core", "extra", "retired"]);
        var info = (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "retired");
        Assert.True(info.Removable);
        Assert.False(info.Installed);
        Assert.NotNull(info.Error);
        var toolchain = new RetainingToolchain();
        var result = await f.Manager.RemovePackageAsync(toolchain, "retired");
        Assert.Null(result.Error);
        Assert.Equal("applied", result.Application);
        Assert.Equal(0, toolchain.RemoveCalls);
        Assert.DoesNotContain("retired", f.Manifest.Bundles);
        Assert.DoesNotContain(await f.Manager.ListBundlesAsync(), row => row.Name == "retired");
    }

    [Fact]
    public async Task LocalDeselectionRejectionPreservesSelectionAndNeverCallsToolchain()
    {
        await using var f = new Fixture();
        await f.Start();
        await f.Bundle("local", [], dependency: false);
        Assert.Null((await f.Manager.SetBundleEnabledAsync("local", true)).Error);
        var manifest = await File.ReadAllTextAsync(f.ManifestPath);
        f.Manager.AdmitProfileAsync = _ => throw new InvalidOperationException("candidate denied");
        var toolchain = new RetainingToolchain();
        var result = await f.Manager.RemovePackageAsync(toolchain, "local");
        Assert.Equal("failed", result.Application);
        Assert.True(result.Selected);
        Assert.Equal(manifest, await File.ReadAllTextAsync(f.ManifestPath));
        Assert.Equal(0, toolchain.RemoveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninstalledManagementBundleStaysProtectedAfterDeclarationDisappears(bool missingManifest)
    {
        await using var f = new Fixture();
        await f.Start(module: "cordis:manager");
        var manifest = f.Manifest;
        Assert.IsType<EntryOptions>(manifest.Raw["dependencies"]).Remove("extra");
        manifest.Write(f.ManifestPath);
        Assert.Equal(
            "management-required",
            (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "extra").ReadOnlyReason);
        var path = Path.Combine(f.Packages["extra"], "package.json");
        if (missingManifest)
            File.Delete(path);
        else
            await File.WriteAllTextAsync(path, "{}");
        var info = (await f.Manager.ListBundlesAsync()).Single(row => row.Name == "extra");
        Assert.False(info.Removable);
        Assert.Equal("management-required", info.ReadOnlyReason);
        Assert.Equal("management-required", (await f.Manager.SetBundleEnabledAsync("extra", false)).Error);
        var toolchain = new RetainingToolchain();
        Assert.Equal("management-required", (await f.Manager.RemovePackageAsync(toolchain, "extra")).Error);
        Assert.Equal(0, toolchain.RemoveCalls);
        Assert.Contains("extra", f.Manifest.Bundles);
    }

    [Theory]
    [InlineData(false, "stop-profile")]
    [InlineData(true, "bundle-in-use")]
    public async Task UninstalledBundleCannotBeRemovedWhileContributionsRemain(bool live, string error)
    {
        await using var f = new Fixture();
        await f.Start();
        var manifest = f.Manifest;
        Assert.IsType<EntryOptions>(manifest.Raw["dependencies"]).Remove("extra");
        manifest.Write(f.ManifestPath);
        if (!live)
            f.Manager.RunExclusiveAsync = null;
        else
            await File.WriteAllTextAsync(
                Path.Combine(f.Launch.Home, "cordis.patch.yml"),
                "- insert: [{id: managed, name: managed}]\n");
        var toolchain = new RetainingToolchain();
        var result = await f.Manager.RemovePackageAsync(toolchain, "extra");
        Assert.Equal(error, result.Error);
        Assert.Equal(0, toolchain.RemoveCalls);
        Assert.Equal(!live, f.Manifest.Bundles.Contains("extra"));
    }
}
