using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class ProfilePluginOriginalTests
{
    [Fact]
    public void InventoryUsesAliasKeysVersionsAndInstallationPrecedence()
    {
        using var f = new Fixture(); f.Manifest(new() { ["dependencies"] = new EntryOptions { ["alias"] = "file:../local-package", ["shared"] = "^2", ["library"] = "^1", ["missing"] = "git+example", ["unversioned"] = "file:../unversioned" }, ["dsh"] = Profile("shared", "alias") });
        f.Install("alias", true, "3.0.0"); f.Install("shared", false); f.Install("library", false); f.Install("unversioned", false, null); f.Install("shared", true, "9.0.0", true);
        Assert.Equal(new[] { new ProfileDependency("alias", "3.0.0", true, true), new("shared", "1.0.0", true, true), new("library", "1.0.0", false, false), new("missing", "git+example", false, false), new("unversioned", "file:../unversioned", false, false) }, f.Inventory().Dependencies);
    }
    [Fact]
    public void InventoryRetainsMalformedInstalledMetadataAndEmptyProfiles()
    {
        using var f = new Fixture(); Assert.Empty(f.Inventory().Dependencies); f.Manifest(new() { ["dependencies"] = new EntryOptions { ["broken"] = "^1", ["invalid"] = "^2" } }); f.Install("broken"); f.Install("invalid"); File.WriteAllText(Path.Combine(f.Packages["broken"], "package.json"), "{"); File.WriteAllText(Path.Combine(f.Packages["invalid"], "package.json"), "[]");
        Assert.Equal(new[] { new ProfileDependency("broken", "^1", false, false), new("invalid", "^2", false, false) }, f.Inventory().Dependencies);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReconcileDeclarationsAndRemoval(bool preserveDisabled)
    {
        using var f = new Fixture(); var manifest = new EntryOptions { ["name"] = "custom-profile", ["private"] = false, ["custom"] = new EntryOptions { ["keep"] = true }, ["dependencies"] = new EntryOptions { ["active"] = "^1", ["disabled"] = "^1", ["gaining"] = "^1", ["losing"] = "^1", ["removed"] = "^1" }, ["dsh"] = Profile("template", "template", "active", "losing", "removed") }; f.Manifest(manifest);
        foreach (var name in new[] { "active", "disabled", "gaining", "losing", "removed" }) f.Install(name, name != "gaining"); var before = f.Inventory();
        manifest["dependencies"] = new EntryOptions { ["active"] = "^2", ["disabled"] = "^2", ["gaining"] = "^2", ["losing"] = "^2", ["added"] = "^1", ["new-library"] = "^1" }; f.Manifest(manifest); f.Install("gaining"); f.Install("losing", false); f.Install("added"); f.Install("new-library", false);
        var result = ProfileMaintenance.Reconcile(f.Path, before, preserveDisabled, f.Packages, f.Installation); var expected = new[] { "template", "template", "active" }.Concat(preserveDisabled ? [] : new[] { "disabled" }).Concat(["gaining", "added"]).ToArray();
        Assert.Equal(expected, result.Inventory.Manifest.Bundles); Assert.Equal(expected.Skip(2), result.Inventory.Dependencies.Where(d => d.Enabled).Select(d => d.Name)); Assert.Equal("new-library", Assert.Single(result.AddedPlainDependencies)); Assert.Equal(false, result.Inventory.Manifest.Raw["private"]); Assert.Equal(true, Assert.IsType<EntryOptions>(result.Inventory.Manifest.Raw["custom"])["keep"]);
        var bytes = File.ReadAllText(System.IO.Path.Combine(f.Path, "package.json")); Assert.Empty(ProfileMaintenance.Reconcile(f.Path, result.Inventory, preserveDisabled, f.Packages, f.Installation).AddedPlainDependencies); Assert.Equal(bytes, File.ReadAllText(System.IO.Path.Combine(f.Path, "package.json")));
    }
    [Fact]
    public void ReconcileCreatesAbsentProfileMetadata()
    {
        using var f = new Fixture(); var before = f.Inventory(); f.Manifest(new() { ["dependencies"] = new EntryOptions { ["added"] = "^1" } }); f.Install("added"); Assert.Equal("added", Assert.Single(ProfileMaintenance.Reconcile(f.Path, before, false, f.Packages, f.Installation).Inventory.Manifest.Bundles));
    }
    [Fact]
    public void ReconcileRemovesUnresolvedDependencyLayerAndPreservesTemplate()
    {
        using var f = new Fixture(); f.Manifest(new() { ["dependencies"] = new EntryOptions { ["missing"] = "^1" }, ["dsh"] = Profile("missing", "template") }); var result = ProfileMaintenance.Reconcile(f.Path, f.Inventory(), true, f.Packages, f.Installation); Assert.Equal("template", Assert.Single(result.Inventory.Manifest.Bundles)); Assert.Equal(new[] { "template", "manual" }, ProfileMaintenance.WriteBundles(f.Path, result.Inventory.Manifest, ["template", "manual"]).Bundles);
    }
    private static EntryOptions Profile(params string[] bundles) => new() { ["profile"] = new EntryOptions { ["bundles"] = bundles } };
    private sealed class Fixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cordis-profile-inventory-" + Guid.NewGuid().ToString("N")); public Dictionary<string, string> Packages { get; } = new(StringComparer.Ordinal); public Dictionary<string, string> Installation { get; } = new(StringComparer.Ordinal);
        public Fixture() { Directory.CreateDirectory(Path); Manifest(new()); }
        public void Manifest(EntryOptions data) => File.WriteAllText(System.IO.Path.Combine(Path, "package.json"), ConfigurationFile.Write(data, true));
        public void Install(string name, bool bundle = true, string? version = "1.0.0", bool installation = false)
        {
            var path = System.IO.Path.Combine(Path, installation ? "installation" : "packages", name); Directory.CreateDirectory(path); (installation ? Installation : Packages)[name] = path; var data = new EntryOptions { ["name"] = name };
            if (version is not null) data["version"] = version; if (bundle) data["dsh"] = new EntryOptions { ["bundle"] = new EntryOptions { ["patch"] = "missing.yml" } }; File.WriteAllText(System.IO.Path.Combine(path, "package.json"), ConfigurationFile.Write(data, true));
        }
        public ProfileInventory Inventory() => ProfileMaintenance.Inventory(Path, Packages, Installation); public void Dispose() => Directory.Delete(Path, true);
    }
}
