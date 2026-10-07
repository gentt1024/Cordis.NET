using Cordis.Composition;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ProfileUpgradeTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "cordis-profile-upgrade-" + Guid.NewGuid().ToString("N"));

    private readonly Dictionary<string, string> bundles = new(StringComparer.Ordinal);

    public ProfileUpgradeTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    private static List<EntryOptions> Insert(string value) =>
    [
        new()
        {
            ["insert"] = new List<EntryOptions>
            {
                new()
                {
                    Id = "record",
                    Name = "plugin",
                    Config = value
                }
            }
        }
    ];

    private Profile FromBundle(Cordis.Composition.Bundle bundle) =>
        new("record", directory, [bundle], new("user.yml", []));

    [Fact]
    public void BundleRecordPatchReplacementChangesEffectiveCompositionWithoutChangingOriginal()
    {
        var original = new Cordis.Composition.Bundle("record", directory, "original.yml", Insert("before"));
        var changed = original with
        {
            Patches = Insert("after")
        };
        Assert.Equal("after", Assert.Single(Profiles.Compose(FromBundle(changed).Layers)).Config);
        Assert.Equal("before", Assert.Single(Profiles.Compose(FromBundle(original).Layers)).Config);
        changed.Patches.Add(
            new()
            {
                Id = "record",
                Config = "mutated"
            });
        Assert.Equal("mutated", Assert.Single(Profiles.Compose(FromBundle(changed).Layers)).Config);
        Assert.Equal("before", Assert.Single(Profiles.Compose(FromBundle(original).Layers)).Config);
    }

    [Fact]
    public void BundleRecordPathReplacementChangesEffectiveSourceWithoutChangingOriginal()
    {
        var original = new Cordis.Composition.Bundle("record", directory, "original.yml", Insert("before"));
        var changed = original with
        {
            PatchPath = "replacement.yml"
        };
        Assert.Equal("replacement.yml", FromBundle(changed).Layers.First().Source);
        Assert.Equal(["replacement.yml"], changed.PatchPaths);
        Assert.Equal("before", Assert.Single(Profiles.Compose(FromBundle(changed).Layers)).Config);
        Assert.Equal("original.yml", FromBundle(original).Layers.First().Source);
    }

    [Fact]
    public void BundleLayerReplacementKeepsLegacyAndMultipleFileViewsConsistent()
    {
        var original = new Cordis.Composition.Bundle("record", directory, "original.yml", Insert("before"));
        var changed = original with
        {
            PatchLayers =
            [
                new("first.yml", Insert("first")),
                new(
                    "second.yml",
                    [
                        new()
                        {
                            Id = "record",
                            Config = "second"
                        }
                    ])
            ]
        };
        Assert.Equal("first.yml", changed.PatchPath);
        Assert.Equal(["first.yml", "second.yml"], changed.PatchPaths);
        Assert.Equal("second", Assert.Single(Profiles.Compose([new("flat", changed.Patches)])).Config);
        Assert.Equal("second", Assert.Single(Profiles.Compose(FromBundle(changed).Layers)).Config);
        Assert.Equal("before", Assert.Single(Profiles.Compose(FromBundle(original).Layers)).Config);
        var relabeled = changed with
        {
            PatchPath = "renamed-first.yml"
        };
        Assert.Equal(["renamed-first.yml", "second.yml"], relabeled.PatchPaths);
        Assert.Equal(
            ["renamed-first.yml", "second.yml"],
            FromBundle(relabeled).Layers.Take(2).Select(layer => layer.Source));
        Assert.Equal(["first.yml", "second.yml"], changed.PatchPaths);
        var replaced = changed with
        {
            Patches = Insert("replacement")
        };
        Assert.Equal("replacement", Assert.Single(Profiles.Compose(FromBundle(replaced).Layers)).Config);
        Assert.Equal(["first.yml"], replaced.PatchPaths);
        var renamed = changed with
        {
            PatchPaths = ["renamed-first.yml", "renamed-second.yml"]
        };
        Assert.Equal(
            ["renamed-first.yml", "renamed-second.yml"],
            FromBundle(renamed).Layers.Take(2).Select(layer => layer.Source));
        Assert.Equal("second", Assert.Single(Profiles.Compose(FromBundle(renamed).Layers)).Config);
        Assert.Throws<ArgumentException>(() => changed with
        {
            PatchPaths = ["missing-layer.yml"]
        });
        var (name, path, primarySource, flattened) = changed;
        Assert.Equal("record", name);
        Assert.Equal(directory, path);
        Assert.Equal("first.yml", primarySource);
        Assert.Equal("second", Assert.Single(Profiles.Compose([new("deconstructed", flattened)])).Config);
        var empty = changed with
        {
            PatchLayers = []
        };
        Assert.Equal("", empty.PatchPath);
        Assert.Empty(empty.PatchPaths);
        Assert.Empty(empty.Patches);
        Assert.Empty(Profiles.Compose(FromBundle(empty).Layers));
    }

    private string Bundle(string name, string declaration)
    {
        var path = Path.Combine(directory, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":" + declaration + "}}}");
        bundles[name] = path;
        return path;
    }

    [Fact]
    public async Task MultiplePatchFilesComposeInDeclaredOrderAndAnchorEachFile()
    {
        var bundle = Bundle("ordered", "[\"first/patch.yml\",\"second/patch.yml\"]");
        Directory.CreateDirectory(Path.Combine(bundle, "first"));
        Directory.CreateDirectory(Path.Combine(bundle, "second"));
        File.WriteAllText(
            Path.Combine(bundle, "first/patch.yml"),
            "- insert:\n  - id: first\n    name: ./plugin.dll\n    config: { value: 1 }\n");
        File.WriteAllText(
            Path.Combine(bundle, "second/patch.yml"),
            "- id: first\n  config: { value: 2 }\n- insert:\n  - id: second\n    name: ./plugin.dll\n");
        Profiles.Initialize(directory, ["ordered"]);
        var profile = await Profiles.LoadAsync(directory, bundles);
        var entries = Profiles.Compose(profile.Layers);
        Assert.Equal(["first", "second"], entries.Select(entry => entry.Id));
        Assert.Equal(
            new[] { Path.Combine(bundle, "first/patch.yml"), Path.Combine(bundle, "second/patch.yml") }.Select(
                Path.GetFullPath),
            Assert.Single(profile.Bundles).PatchPaths);
        Assert.Equal(Assert.Single(profile.Bundles).PatchPaths, profile.Layers.Take(2).Select(layer => layer.Source));
        Assert.Equal(2L, Assert.IsType<EntryOptions>(entries[0].Config)["value"]);
        Assert.Equal(new Uri(Path.Combine(bundle, "first/plugin.dll")).AbsoluteUri, entries[0].Name);
        Assert.Equal(new Uri(Path.Combine(bundle, "second/plugin.dll")).AbsoluteUri, entries[1].Name);
    }

    [Fact]
    public async Task BrokenBundleIsSkippedAtomicallyAndFollowingBundlesStillLoad()
    {
        var broken = Bundle("broken", "[\"first.yml\",\"missing.yml\"]");
        File.WriteAllText(Path.Combine(broken, "first.yml"), "- insert:\n  - id: leaked\n    name: leaked\n");
        var good = Bundle("good", "\"patch.yml\"");
        File.WriteAllText(Path.Combine(good, "patch.yml"), "- insert:\n  - id: good\n    name: good\n");
        Profiles.Initialize(directory, ["unresolved", "broken", "good"]);
        var before = File.ReadAllText(Path.Combine(directory, "package.json"));
        var profile = await Profiles.LoadAsync(directory, bundles);
        Assert.Equal(["good"], profile.Bundles.Select(bundle => bundle.Name));
        Assert.Equal(["unresolved", "broken", "good"], profile.SelectedBundles);
        Assert.Equal(["unresolved", "broken"], profile.SkippedBundles.Select(bundle => bundle.Name));
        Assert.Contains("missing.yml", profile.SkippedBundles[1].Reason);
        var diagnostics = new List<string>();
        Profiles.ReportSkippedBundles(profile, diagnostics.Add);
        Assert.Equal(2, diagnostics.Count);
        Assert.Contains("\"unresolved\"", diagnostics[0]);
        Assert.Equal(["good"], Profiles.Compose(profile.Layers).Select(entry => entry.Id));
        Assert.Equal(before, File.ReadAllText(Path.Combine(directory, "package.json")));
    }

    [Fact]
    public async Task EmptyPatchDeclarationIsALoadedBundle()
    {
        Bundle("empty", "[]");
        Profiles.Initialize(directory, ["empty"]);
        var profile = await Profiles.LoadAsync(directory, bundles);
        Assert.Equal("empty", Assert.Single(profile.Bundles).Name);
        Assert.Empty(profile.Bundles[0].PatchPaths);
        Assert.Empty(profile.SkippedBundles);
        Assert.Empty(Profiles.Compose(profile.Layers));
    }

    [Theory]
    [InlineData("[\"ok.yml\",42]")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task InvalidPatchDeclarationsAreDiagnosedWithoutReadingAPrefix(string declaration)
    {
        var bundle = Bundle("invalid", declaration);
        File.WriteAllText(Path.Combine(bundle, "ok.yml"), "- insert:\n  - id: leaked\n    name: leaked\n");
        Profiles.Initialize(directory, ["invalid"]);
        var profile = await Profiles.LoadAsync(directory, bundles);
        Assert.Empty(profile.Bundles);
        Assert.Contains("file path or a list", Assert.Single(profile.SkippedBundles).Reason);
        Assert.Empty(Profiles.Compose(profile.Layers));
    }

    [Fact]
    public async Task BrokenUserLayerStillFailsAndCanBeExplicitlyOmitted()
    {
        Profiles.Initialize(directory, []);
        File.WriteAllText(Path.Combine(directory, "cordis.patch.yml"), "not-an-entry-list");
        await Assert.ThrowsAsync<FormatException>(() => Profiles.LoadAsync(directory, bundles));
        Assert.Empty((await Profiles.LoadAsync(directory, bundles, userLayer: false)).UserLayer.Patches);
    }

    [Fact]
    public async Task ManagementAndDeploymentConsumeEveryPatchAndRetainAnEmptyBundle()
    {
        var ordered = Bundle("ordered", "[\"insert.yml\",\"override.yml\"]");
        File.WriteAllText(
            Path.Combine(ordered, "insert.yml"),
            "- insert:\n  - id: managed\n    name: plugin\n    config: first\n");
        File.WriteAllText(Path.Combine(ordered, "override.yml"), "- id: managed\n  config: second\n");
        var empty = Bundle("empty", "[]");
        Profiles.Initialize(directory, ["ordered"]);
        var manifest = PackageManifest.Read(Path.Combine(directory, "package.json"));
        manifest.Raw["dependencies"] = new EntryOptions
        {
            ["empty"] = "1.0.0"
        };
        manifest.Write(Path.Combine(directory, "package.json"));
        var profile = await Profiles.LoadAsync(directory, bundles);
        var basePath = Path.Combine(directory, "base.yml");
        File.WriteAllText(basePath, "[]\n");
        await using var context = new Cordis.Context();
        Cordis.Composition.Include include = null!;
        await context.RunAsync(async ctx =>
        {
            var resolver = new StaticModuleResolver().Register(
                "plugin",
                new Cordis.Plugin<string>
                {
                    Apply = (owner, value) => owner.Provide("managed-value", value)
                });
            var loader = new Loader(ctx, resolver, new Uri(basePath));
            include = await ApplicationBoot.MountAsync(loader, basePath, ProfileComposition.Flatten(profile.Layers));
            Assert.Equal("second", ctx.Get<string>("managed-value"));
        });
        var manager = new PluginConfigurationOperations(new(profile, directory, [], bundles), include);
        Assert.Equal(
            "root:managed",
            Assert.Single((await manager.ListBundlesAsync()).Single(bundle => bundle.Name == "ordered").Rows).EntryId);
        var emptyInfo = (await manager.ListBundlesAsync()).Single(bundle => bundle.Name == "empty");
        Assert.Empty(emptyInfo.Rows);
        Assert.Null(emptyInfo.Error);
        var before = new PackageManifest(
            new EntryOptions
            {
                ["dependencies"] = new EntryOptions()
            });
        var reconciled = await PluginConfigurationOperations.ReconcileDeployedPackagesAsync(
            directory,
            before,
            new Dictionary<string, string>
            {
                ["empty"] = empty
            },
            bundles);
        Assert.Equal(["ordered", "empty"], reconciled.Inventory.Manifest.Bundles);
        Assert.True(Assert.Single(reconciled.Inventory.Dependencies).Bundle);
        Assert.Empty(reconciled.AddedPlainDependencies);
    }
}
