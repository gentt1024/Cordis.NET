using Cordis.Composition;

namespace Cordis.Conformance;

public static class ProfileUpgradeScenarios
{
    public static IReadOnlyList<(string Id, Func<Task<string[]>> Run)> Cases { get; } =
        [("P01-ordered-bundles-atomic-failure-and-provenance", OrderedBundles)];

    private static async Task<string[]> OrderedBundles()
    {
        var root = Path.Combine(Path.GetTempPath(), "cordis-upgrade-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string At(string name) => Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(At(path))!); File.WriteAllText(At(path), text); }
            string Bundle(string name, object patches)
            {
                var relative = "install/node_modules/" + name;
                new PackageManifest(new EntryOptions { ["name"] = name, ["version"] = "1.0.0", ["dsh"] = new EntryOptions { ["bundle"] = new EntryOptions { ["patch"] = patches } } }).Write(At(relative + "/package.json"));
                return At(relative);
            }
            foreach (var name in new[] { "upgrade-multi", "upgrade-broken", "upgrade-later", "upgrade-empty" }) Directory.CreateDirectory(At("install/node_modules/" + name));
            Write("install/package.json", "{}");
            var maps = new Dictionary<string, string>
            {
                ["upgrade-multi"] = Bundle("upgrade-multi", new[] { "one/first.yml", "two/second.yml" }),
                ["upgrade-broken"] = Bundle("upgrade-broken", new[] { "prefix.yml", "missing.yml" }),
                ["upgrade-later"] = Bundle("upgrade-later", "after.yml"),
                ["upgrade-empty"] = Bundle("upgrade-empty", Array.Empty<string>())
            };
            Write("install/node_modules/upgrade-multi/one/first.yml", "- insert:\n  - id: item\n    name: ./plugin.dll\n    config: first\n");
            Write("install/node_modules/upgrade-multi/two/second.yml", "- id: item\n  config: second\n");
            Write("install/node_modules/upgrade-broken/prefix.yml", "- insert:\n  - id: leaked\n    name: ./leaked.dll\n");
            Write("install/node_modules/upgrade-later/after.yml", "- insert:\n  - id: tail\n    name: ./tail.dll\n    config: tail\n");
            var selection = new[] { "upgrade-multi", "upgrade-broken", "upgrade-later", "upgrade-empty", "upgrade-missing" };
            Profiles.Initialize(At("profile"), selection);
            Write("profile/cordis.patch.yml", "- id: item\n  config: profile-value\n- insert:\n  - id: user\n    name: ./user.dll\n    config: user\n");
            var profile = await Profiles.LoadAsync(At("profile"), maps);
            var rows = Profiles.Compose(profile.Layers);
            Scenarios.Equal(3, profile.Bundles.Count); Scenarios.Equal(2, profile.SkippedBundles.Count);
            Scenarios.Equal("item,tail,user", string.Join(',', rows.Select(row => row.Id)));
            Scenarios.Equal("profile-value", (string)rows[0].Config!);
            var trace = new List<string>
            {
                "selected:" + string.Join(',', profile.SelectedBundles),
                "loaded:" + string.Join(',', profile.Bundles.Select(bundle => bundle.Name)),
                "skipped:" + string.Join(',', profile.SkippedBundles.Select(bundle => bundle.Name)),
                "sources:" + string.Join(';', profile.Bundles.Select(bundle => bundle.Name + ":" + string.Join(',', bundle.PatchPaths.Select(path => Path.GetRelativePath(bundle.Directory, path).Replace('\\', '/'))))),
                "rows:" + string.Join('|', rows.Select(row => row.Id + ":" + row.Config + "@" + Path.GetRelativePath(root, new Uri(row.Name).LocalPath).Replace('\\', '/'))),
                "prefix-leaked:" + (rows.Any(row => row.Id == "leaked") ? "true" : "false")
            };
            Write("profile/cordis.patch.yml", "[broken");
            var failed = false;
            try { await Profiles.LoadAsync(At("profile"), maps); }
            catch (YamlDotNet.Core.YamlException) { failed = true; }
            Scenarios.Equal(true, failed);
            var bundlesOnly = await Profiles.LoadAsync(At("profile"), maps, userLayer: false);
            Scenarios.Equal(0, bundlesOnly.UserLayer.Patches.Count);
            trace.Add("user-layer:failed;false:" + bundlesOnly.UserLayer.Patches.Count);
            return [.. trace];
        }
        finally { Directory.Delete(root, true); }
    }
}
