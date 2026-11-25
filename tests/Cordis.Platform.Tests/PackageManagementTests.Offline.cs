using System.IO.Compression;
using System.Text;
using Cordis.Clr;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed partial class PackageManagementTests
{
    [Fact]
    public async Task Offline_restore_pins_latest_stable_and_retains_independent_native_graphs()
    {
        var profile = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
        await File.WriteAllTextAsync(Path.Combine(profile, "package.json"), "{}");
        await using var resolver = new ClrModuleResolver();
        using var tools = DotnetPluginToolchain.OpenOffline(profile, resolver, [Feed]);

        // These ordinary packages require different versions of a private dependency. They have no plugin entries.
        void Pack(string id, string version, string? dependency = null)
        {
            using var package = ZipFile.Open(Path.Combine(Feed, $"{id}.{version}.nupkg"), ZipArchiveMode.Create);
            using (var writer = new StreamWriter(package.CreateEntry(id + ".nuspec").Open()))
                writer.Write(
                    $"<package><metadata><id>{id}</id><version>{version}</version><authors>fixture</authors><description>fixture</description>" +
                    (dependency is null
                        ? ""
                        : $"<dependencies><dependency id=\"Offline.Private\" version=\"[{dependency}]\" /></dependencies>") +
                    "</metadata></package>");
            package.CreateEntry("lib/net10.0/_._");
        }

        Pack("Offline.Private", "1.0.0");
        Pack("Offline.Private", "2.0.0");
        Pack("Offline.Left", "1.0.0", "1.0.0");
        Pack("Offline.Left", "2.0.0-preview.1", "2.0.0");
        Pack("Offline.Right", "2.0.0", "2.0.0");
        var left = await tools.PrepareProfilePackageAsync(
            "Offline.Left",
            null,
            null,
            false,
            inspection => inspection.Request.Version == "1.0.0",
            _ =>
            {
            });
        var right = await tools.PrepareProfilePackageAsync(
            "Offline.Right",
            "[2.0,3.0)",
            null,
            false,
            inspection => inspection.Request.Version == "2.0.0",
            _ =>
            {
            });
        Assert.Equal("1.0.0", left.Declaration);
        Assert.Equal("[2.0,3.0)", right.Declaration);
        Assert.False(left.IsBundle);
        Assert.False(right.IsBundle);
        foreach (var (package, expected) in new[] { (left, "1.0.0"), (right, "2.0.0") })
        {
            var explanation = new StringBuilder();
            await tools.WhyAsync(
                package.Name,
                package.Declaration,
                package.Graph,
                "Offline.Private",
                chunk => explanation.Append(chunk));
            Assert.Contains(package.Name, explanation.ToString());
            Assert.Contains($"Offline.Private (v{expected})", explanation.ToString());
        }

        // Repair a missing payload from its retained standard lock, even after another stable feed version appears.
        Pack("Offline.Left", "1.1.0", "2.0.0");
        Directory.Delete(left.Directory, recursive: true);
        Assert.Equal(left.Version, tools.ReadLockedVersion(left.Name, left.Declaration, left.Graph));
        var repaired = await tools.PrepareProfilePackageAsync(
            left.Name,
            left.Declaration,
            left.Graph,
            false,
            _ => true,
            _ =>
            {
            });
        Assert.Equal(left, repaired);
        using var disconnected = DotnetPluginToolchain.OpenOffline(profile, resolver, []);
        Assert.Equal(
            left,
            await disconnected.PrepareProfilePackageAsync(
                left.Name,
                left.Declaration,
                left.Graph,
                false,
                _ => throw new InvalidOperationException("No rebuild expected."),
                _ =>
                {
                }));
        Assert.Throws<InvalidDataException>(() => tools.ReadProfilePackage(left.Name, "[1.0,2.0)", left.Graph));
        await File.WriteAllTextAsync(
            Path.Combine(profile, "package.json"),
            $$"""{"dependencies":{"Offline.Left":"1.0.0"},"dsh":{"profile":{"restore":{"Offline.Left":"{{left.Graph}}" } } } }""");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DotnetPluginToolchain.DeleteRetainedArtifactAsync(profile, left.Name, left.Graph));
        await DotnetPluginToolchain.DeleteRetainedArtifactAsync(profile, right.Name, right.Graph);
        Assert.False(Directory.Exists(right.Directory));
        Assert.True(Directory.Exists(left.Directory));
    }

    [Fact]
    public async Task Explicit_archive_pins_approved_bytes_and_produces_an_importable_bundle_offline()
    {
        var profile = Directory.CreateDirectory(Path.Combine(directory, "offline")).FullName;
        await File.WriteAllTextAsync(Path.Combine(profile, "package.json"), "{}");
        await using var resolver = new ClrModuleResolver();
        using var tools = DotnetPluginToolchain.OpenOffline(profile, resolver, []);
        // The host's own central package management is outside this isolated restore project.
        await File.WriteAllTextAsync(Path.Combine(profile, "Directory.Packages.props"), "<incomplete>");
        var path = Path.Combine(Feed, "IndependentPlugin.1.0.0.nupkg");
        var refusals = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.PrepareProfilePackageAsync(
            path,
            null,
            null,
            false,
            inspection =>
            {
                refusals++;
                Assert.Equal(path, inspection.Request.Source);
                return false;
            },
            _ =>
            {
            }));
        Assert.Equal(1, refusals);
        Assert.False(Directory.Exists(Path.Combine(profile, ".cordis", "nuget")));
        await tools.AddProfilePackagesAsync(
            [path],
            inspection => inspection.Request.Version == "1.0.0",
            _ =>
            {
            });
        var manifest = PackageManifest.Read(Path.Combine(profile, "package.json"));
        Assert.Contains("IndependentPlugin", manifest.Bundles);
        var published = tools.ReadProfilePackage(
            "IndependentPlugin",
            "1.0.0",
            manifest.RestoreGraphs["IndependentPlugin"]);
        Assert.True(published.IsBundle);
        File.Delete(path);
        var retained = await tools.PrepareProfilePackageAsync(
            published.Name,
            published.Declaration,
            published.Graph,
            false,
            _ => false,
            _ =>
            {
            });
        Assert.Equal(published, retained);
        await using var reopenedResolver = new ClrModuleResolver();
        using var reopened = await DotnetPluginToolchain.OpenForRunAsync(profile, reopenedResolver, []);
        Assert.Equal(published.Directory, reopened.Bundles[published.Name]);
        Assert.NotNull((await reopenedResolver.ResolveAsync("nuget:independentplugin", new Uri("file:///"))).Plugin);
    }
}
