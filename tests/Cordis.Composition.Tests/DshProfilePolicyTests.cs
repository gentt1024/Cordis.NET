using Cordis;
using Cordis.Composition;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class DshProfilePolicyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cordis-dsh-policy-" + Guid.NewGuid().ToString("N"));
    public DshProfilePolicyTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);

    [Fact]
    public void OptionalBundlesMatchTheFixedDshPolicy()
    {
        Assert.Equal(new[]
        {
            "@deepseek-ai/dsh-experimental-agent-team-profile",
            "@deepseek-ai/dsh-experimental-voice-input-bundle",
            "@deepseek-ai/dsh-experimental-auto-review",
            "@deepseek-ai/dsh-experimental-schedule-bundle",
        }, DshProfilePolicy.OptionalBundles);
    }
    private static PackageManifest Manifest(object? peers) => new(new EntryOptions
    {
        ["name"] = "@example/plugin", ["version"] = "1.0.0+plugin", ["peerDependencies"] = peers,
    });

    [Theory]
    [InlineData("0.2.0-rc.2", "^0.2.0-rc.1", true)]
    [InlineData("0.2.0-rc.2", "^0.2.0", false)]
    [InlineData("0.2.0-rc.2", "0.2.x", true)]
    [InlineData("0.2.0-rc.2", "~0.2", true)]
    [InlineData("0.2.0-rc.2", "0.2.0 - 0.3.0", true)]
    [InlineData("1.2.3+app", "=1.2.3+other", true)]
    [InlineData("1.2.3", ">=1.2 <2", true)]
    [InlineData("1.2.3", "^0.2 || ~1.2.0", true)]
    [InlineData("0.0.4", "^0.0.3", false)]
    [InlineData("1.2.3", "workspace:^", true)]
    [InlineData("1.2.3", "workspace:~", true)]
    [InlineData("1.2.3", "workspace:*", true)]
    [InlineData("1.2.3", "workspace:1.2.3", false)]
    [InlineData("1.2.3", "", false)]
    [InlineData("1.2.3", "latest", false)]
    [InlineData("1.2.3", "^1 || garbage", false)]
    [InlineData("1.2.3", "1.x.2", false)]
    [InlineData("1.2.3", "^1.x.2", true)]
    [InlineData("1.2.3", "==1.2.3", false)]
    [InlineData("1.2.3", "1.2-foo", false)]
    [InlineData("1.2.3", "1.2.x-01", false)]
    [InlineData("1.2.3-9007199254740992", ">=1.2.3-9007199254740993", true)]
    [InlineData("9007199254740991.0.0", "^9007199254740991.0.0", false)]
    public void NpmRangesUseExplicitDshRuntimeAndIncludePrereleases(string running, string range, bool accepted)
    {
        var issue = DshProfilePolicy.EvaluateCompatibility(Manifest(new EntryOptions { ["@deepseek-ai/dsh-core"] = range }), new(running));
        Assert.Equal(accepted, issue is null);
        if (!accepted) Assert.Equal(range, issue!.Peers["@deepseek-ai/dsh-core"]);
    }

    [Fact]
    public void ValidatesEveryOwnPeerBeforeFilteringAndReportsEveryDshMismatch()
    {
        Assert.Throws<FormatException>(() => DshProfilePolicy.EvaluateCompatibility(Manifest(new EntryOptions { ["unrelated"] = 12 }), new("0.2.0")));
        Assert.Throws<FormatException>(() => DshProfilePolicy.EvaluateCompatibility(Manifest(null), new("0.2.0")));
        Assert.Null(DshProfilePolicy.EvaluateCompatibility(Manifest(new EntryOptions { ["@deepseek-ai/cordis"] = "^99", ["unrelated"] = "garbage" }), new("0.2.0")));
        var issue = DshProfilePolicy.EvaluateCompatibility(Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99", ["@deepseek-ai/dsh-core"] = "~88" }), new("0.2.0"));
        Assert.Equal(2, issue!.Peers.Count);
        Assert.False(issue.Exempted);
    }

    [Fact]
    public async Task GrantsAreExactIncludingBuildMetadataAndDoNotWriteManifestOrPatch()
    {
        var runtime = new DshRuntimeIdentity("0.2.0-rc.2+buildA");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProfileMaintenance.SetVersionExemptionAsync(directory, "@example/plugin@1.0.0+plugin", runtime.Version, runtime, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProfileMaintenance.SetVersionExemptionAsync(directory, "@example/plugin@1.0.0+plugin", "0.2.0-rc.2+buildB", runtime, true, true));
        await ProfileMaintenance.SetVersionExemptionAsync(directory, "@example/plugin@1.0.0+plugin", runtime.Version, runtime, true, true);
        var grants = DshProfilePolicy.ReadCompatibility(directory);
        Assert.True(grants.Rewritable);
        Assert.True(DshProfilePolicy.EvaluateCompatibility(Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }), runtime, grants.Exemptions)!.Exempted);
        Assert.False(DshProfilePolicy.EvaluateCompatibility(Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }), new("0.2.0-rc.2+buildB"), grants.Exemptions)!.Exempted);
        Assert.False(File.Exists(Path.Combine(directory, "package.json")));
        Assert.False(File.Exists(Path.Combine(directory, "cordis.patch.yml")));
        await ProfileMaintenance.SetVersionExemptionAsync(directory, "@example/plugin@1.0.0+plugin", runtime.Version, new("0.3.0"), false);
        Assert.Empty(DshProfilePolicy.ReadCompatibility(directory).Exemptions);
    }

    [Theory]
    [InlineData("@example/plugin@^1.0", "0.2.0")]
    [InlineData("@Example/plugin@1.0.0", "0.2.0")]
    [InlineData("plugin@v1.0.0", "0.2.0")]
    [InlineData("plugin@1.0.0", " 0.2.0")]
    [InlineData("plugin@1.0.0", "0.2")]
    public async Task RejectsNonCanonicalGrantIdentities(string package, string version)
    {
        await Assert.ThrowsAsync<FormatException>(() => ProfileMaintenance.SetVersionExemptionAsync(directory, package, version, new("0.2.0"), true, true));
        Assert.False(File.Exists(Path.Combine(directory, DshProfilePolicy.CompatibilityFilename)));
    }

    [Fact]
    public async Task CorruptRecordKeepsValidSiblingsButRefusesEveryWriteWithoutDiscardingBytes()
    {
        const string text = "{\"plugin@1.0.0\":[\"0.2.0+build\"],\"bad@^1\":[\"0.2.0\"],\"other@1.0.0\":[\"0.2\"]}";
        var path = Path.Combine(directory, DshProfilePolicy.CompatibilityFilename);
        await File.WriteAllTextAsync(path, text);
        var read = DshProfilePolicy.ReadCompatibility(directory);
        Assert.Equal(["0.2.0+build"], read.Exemptions["plugin@1.0.0"]);
        Assert.Equal(2, read.Warnings.Count);
        Assert.False(read.Rewritable);
        foreach (var enabled in new[] { false, true })
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProfileMaintenance.SetVersionExemptionAsync(directory, "plugin@1.0.0", "0.2.0+build", new("0.2.0+build"), enabled, true));
        Assert.Equal(text, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ConcurrentGrantsSerializeWithoutLosingSiblings()
    {
        var runtime = new DshRuntimeIdentity("0.2.0");
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => ProfileMaintenance.SetVersionExemptionAsync(directory, $"plugin{i}@1.0.0", runtime.Version, runtime, true, true)));
        Assert.Equal(12, DshProfilePolicy.ReadCompatibility(directory).Exemptions.Count);
    }

    [Theory]
    [InlineData("{ invalid")]
    [InlineData("[]")]
    [InlineData("{\"plugin@1.0.0\": [12]}")]
    public async Task UnreadableGrantDataWarnsAndEveryMutationPreservesOriginalBytes(string text)
    {
        var path = Path.Combine(directory, DshProfilePolicy.CompatibilityFilename); File.WriteAllText(path, text);
        var warnings = new List<string>();
        var admit = DshProfilePolicy.CreateAdmission(directory, new("0.2.0"), warnings.Add);
        admit(Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^0.2.0" }));
        Assert.NotEmpty(warnings);
        Assert.Empty(DshProfilePolicy.ReadCompatibility(directory).Exemptions);
        foreach (var enabled in new[] { false, true })
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProfileMaintenance.SetVersionExemptionAsync(directory, "plugin@1.0.0", "0.2.0", new("0.2.0"), enabled, true));
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public async Task GrantLockWaitCanBeCancelledAndRecoveredWithoutTouchingManifestOrPatchLocks()
    {
        var runtime = new DshRuntimeIdentity("0.2.0");
        var path = Path.Combine(directory, DshProfilePolicy.CompatibilityFilename);
        using (var held = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        using (var cancellation = new CancellationTokenSource())
        {
            var waiting = ProfileMaintenance.SetVersionExemptionAsync(directory, "plugin@1.0.0", runtime.Version, runtime, true, true, cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(File.Exists(path));
        }
        using var unrelated = new FileStream(Path.Combine(directory, "package.json.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await ProfileMaintenance.SetVersionExemptionAsync(directory, "plugin@1.0.0", runtime.Version, runtime, true, true);
        Assert.Equal([runtime.Version], DshProfilePolicy.ReadCompatibility(directory).Exemptions["plugin@1.0.0"]);
        Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
    }

    [Fact]
    public void CorruptSiblingWarnsWhileValidExactGrantStillPermitsStartupWithoutWriting()
    {
        var path = Path.Combine(directory, DshProfilePolicy.CompatibilityFilename);
        const string original = "{\"@example/plugin@1.0.0+plugin\":[\"0.2.0+host\"],\"invalid@^1\":[\"0.2.0\"]}";
        File.WriteAllText(path, original);
        var warnings = new List<string>();
        var prepared = DshProfilePolicy.PrepareEntries(directory, new("0.2.0+host"), [new() { Id = "exempted", Name = "plugin" }], new Uri(directory + Path.DirectorySeparatorChar),
            (_, _) => Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }), warnings.Add);
        Assert.Null(prepared[0].Disabled);
        Assert.Equal(2, warnings.Count);
        Assert.Contains("exemption is active", warnings[1]);
        Assert.Equal(original, File.ReadAllText(path));
    }

    [Fact]
    public void DeniedGroupsClearCarrierFlagAndNestedRowsRemainDetached()
    {
        var child = new EntryOptions { Id = "child", Name = "bad" };
        var group = new EntryOptions { Id = "group", Name = "bad", Group = true, Config = new List<EntryOptions> { child } };
        var native = new EntryOptions { Id = "native", Name = "cordis:group", Config = new List<EntryOptions> { child } };
        var prepared = DshProfilePolicy.PrepareEntries(directory, new("0.2.0"), [group, native], new Uri(directory + Path.DirectorySeparatorChar),
            (name, _) => name == "bad" ? Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }) : null);
        Assert.True(prepared[0].Disabled is true);
        Assert.False(prepared[0].Group);
        Assert.True(Data.Entries(prepared[1].Config)[0].Disabled is true);
        Assert.True(group.Group);
        Assert.Null(group.Disabled);
        Assert.Null(child.Disabled);
    }

    [Fact]
    public void LiteralIncludesUseFileRelativeBasesStopCyclesAndNeverWriteFiles()
    {
        var childDirectory = Path.Combine(directory, "included"); Directory.CreateDirectory(childDirectory);
        var first = Path.Combine(childDirectory, "first.yml"); var second = Path.Combine(childDirectory, "second.yml");
        const string firstText = "- id: cycle\n  name: cordis:include\n  config: { path: second.yml }\n";
        const string secondText = "- id: cycle\n  name: cordis:include\n  config: { path: first.yml }\n- id: incompatible\n  name: ./bad.dll\n";
        File.WriteAllText(first, firstText); File.WriteAllText(second, secondText);
        var bases = new List<Uri>();
        var include = new EntryOptions { Id = "include", Name = "cordis:include", Config = new EntryOptions { ["path"] = new Uri(first).AbsoluteUri } };
        var prepared = DshProfilePolicy.PrepareEntries(directory, new("0.2.0"), [include], new Uri(directory + Path.DirectorySeparatorChar),
            (name, parent) => { bases.Add(parent); return name == "./bad.dll" ? Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }) : null; });
        Assert.True(prepared[0].Disabled is true);
        Assert.Null(include.Disabled);
        Assert.Contains(new Uri(second), bases);
        Assert.Equal(firstText, File.ReadAllText(first)); Assert.Equal(secondText, File.ReadAllText(second));
        var missing = Path.Combine(childDirectory, "missing.yml");
        var initial = new EntryOptions { Id = "initial", Name = "cordis:include", Config = new IncludeOptions(missing, [new() { Name = "./bad.dll" }]) };
        Assert.True(DshProfilePolicy.PrepareEntries(directory, new("0.2.0"), [initial], new Uri(first),
            (_, _) => Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }))[0].Disabled is true);
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void LocatedMalformedManifestIsDeniedButUnresolvedModuleIsLeftForLoader()
    {
        var brokenDirectory = Path.Combine(directory, "broken"); Directory.CreateDirectory(brokenDirectory);
        var module = Path.Combine(brokenDirectory, "plugin.dll"); File.WriteAllText(module, "metadata probe only");
        File.WriteAllText(Path.Combine(brokenDirectory, "package.json"), "{ invalid");
        var broken = new EntryOptions { Id = "broken", Name = new Uri(module).AbsoluteUri };
        var missing = new EntryOptions { Id = "missing", Name = "./does-not-exist.dll" };
        var prepared = DshProfilePolicy.PrepareEntries(directory, new("0.2.0"), [broken, missing], new Uri(directory + Path.DirectorySeparatorChar),
            (name, parent) => DshProfilePolicy.LocateManifest(name, parent));
        Assert.True(prepared[0].Disabled is true);
        Assert.Null(prepared[1].Disabled);
        Assert.Null(broken.Disabled);
    }

    [Fact]
    public async Task NamedDshBundleAdmissionDoesNotChangeGenericProfileLoadingOrGrantAutomatically()
    {
        var bundle = Path.Combine(directory, "bundle"); Directory.CreateDirectory(bundle);
        var manifest = Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" });
        manifest.Raw["dsh"] = new EntryOptions { ["bundle"] = new EntryOptions { ["patch"] = "bundle.yml" } };
        manifest.Write(Path.Combine(bundle, "package.json"));
        File.WriteAllText(Path.Combine(bundle, "bundle.yml"), "[]\n");
        var profileDirectory = Profiles.ResolveDirectory(directory, "custom"); Profiles.Initialize(profileDirectory, ["@example/plugin"]);
        var maps = new Dictionary<string, string> { ["@example/plugin"] = bundle };
        var generic = await Profiles.LoadAsync(profileDirectory, maps);
        Assert.Single(generic.Bundles);
        Assert.Single((await DshProfilePolicy.LoadNamedAsync(directory, "custom", maps, null)).Bundles);
        var dsh = await DshProfilePolicy.LoadNamedAsync(directory, "custom", maps, null, true, new DshRuntimeIdentity("0.2.0"));
        Assert.Empty(dsh.Bundles);
        Assert.Equal(["@example/plugin"], dsh.SelectedBundles);
        Assert.Contains("running DSH 0.2.0", Assert.Single(dsh.SkippedBundles).Reason);
        Assert.False(File.Exists(Path.Combine(profileDirectory, DshProfilePolicy.CompatibilityFilename)));
    }

    [Fact]
    public async Task DshMountPreservesNonemptyBaseAndReconcileAdmissionWithoutChangingGenericMount()
    {
        var badDirectory = Path.Combine(directory, "bad"); Directory.CreateDirectory(badDirectory);
        var module = Path.Combine(badDirectory, "plugin.dll"); File.WriteAllText(module, "metadata probe only");
        Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }).Write(Path.Combine(badDirectory, "package.json"));
        var config = Path.Combine(directory, "root.yml");
        var badName = new Uri(module).AbsoluteUri;
        var baseRows = new List<EntryOptions> { new() { Id = "bad", Name = badName, Group = true, Config = new List<EntryOptions> { new() { Id = "child", Name = "good" } } }, new() { Id = "good", Name = "good", Config = "base" }, new() { Id = "missing", Name = "unresolved" } };
        var original = ConfigurationFile.Write(baseRows); File.WriteAllText(config, original);
        var badApplications = 0; var goodConfigurations = new List<object?>();
        var resolver = new StaticModuleResolver().Register(badName, new Plugin<object?> { Apply = (_, _) => badApplications++ })
            .Register("good", new Plugin<object?> { Apply = (_, raw) => goodConfigurations.Add(raw) });
        await using (var context = new Context())
        {
            await context.RunAsync(async _ =>
            {
            var loader = new Loader(context, resolver, new Uri(config));
            var include = await DshProfilePolicy.MountAsync(loader, config, directory, new("0.2.0"), [new() { Id = "good", Config = "patched" }]);
            Assert.Equal(0, badApplications);
            var denied = include.Store["bad"];
            Assert.True(denied.Disabled); Assert.False(denied.Options.Group); Assert.Null(denied.Fiber);
            Assert.Null(denied.Subgroup);
            Assert.Equal("patched", include.Store["good"].Fiber!.Config);
            Assert.IsType<FileNotFoundException>(include.Store["missing"].LastError);
            await ApplicationBoot.ReconcileAsync(include, [new() { Id = "good", Config = "updated" }]);
            Assert.True(include.Store["bad"].Disabled);
            Assert.Equal(0, badApplications);
            Assert.Equal("updated", include.Store["good"].Fiber!.Config);
            });
        }
        Assert.Equal(original, File.ReadAllText(config));
        await using (var context = new Context())
        {
            await context.RunAsync(async _ =>
            {
            var loader = new Loader(context, resolver, new Uri(config));
            await ApplicationBoot.MountAsync(loader, config);
            Assert.Equal(1, badApplications);
            });
        }
    }

    private sealed class RecordingResolver : IModuleResolver
    {
        public List<string> Imports { get; } = [];
        public ValueTask<IPlugin> ResolveAsync(string specifier, Uri baseUri, CancellationToken cancellationToken = default)
        {
            Imports.Add(specifier);
            return ValueTask.FromResult<IPlugin>(new Plugin<object?> { Apply = (_, _) => { } });
        }
    }

    [Fact]
    public async Task HostManifestLocatorDeniesBareRegisteredNamesBeforeExecutingResolver()
    {
        var path = Path.Combine(directory, "registered.yml");
        File.WriteAllText(path, "- id: bad\n  name: registered-clr\n- id: good\n  name: registered-static\n");
        var resolver = new RecordingResolver();
        await using var context = new Context();
        await context.RunAsync(async _ =>
        {
            var loader = new Loader(context, resolver, new Uri(path));
            var include = await DshProfilePolicy.MountAsync(loader, path, directory, new("0.2.0"), null, null, null,
                (name, _) => name == "registered-clr" ? Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = "^99" }) : null);
            Assert.True(include.Store["bad"].Disabled);
            Assert.Equal(["registered-static"], resolver.Imports);
        });
    }

    [Fact]
    public async Task ReconcileDenialClearsPreviouslyMountedGroupSubtree()
    {
        var path = Path.Combine(directory, "group.yml");
        File.WriteAllText(path, "- id: group\n  name: registered-group\n  group: true\n  config:\n  - id: child\n    name: child\n");
        var denied = false;
        await using var context = new Context();
        await context.RunAsync(async _ =>
        {
            var resolver = new StaticModuleResolver();
            var loader = new Loader(context, resolver, new Uri(path));
            resolver.Register("registered-group", loader.Builtins["group"]).Register("child", new Plugin<object?> { Apply = (_, _) => { } });
            var include = await DshProfilePolicy.MountAsync(loader, path, directory, new("0.2.0"), null, null, null,
                (name, _) => name == "registered-group" ? Manifest(new EntryOptions { ["@deepseek-ai/dsh"] = denied ? "^99" : "^0.2" }) : null);
            Assert.NotNull(include.Store["group"].Subgroup);
            Assert.True(include.Store.ContainsKey("child"));
            denied = true;
            await ApplicationBoot.ReconcileAsync(include, []);
            Assert.True(include.Store["group"].Disabled);
            Assert.False(include.Store["group"].Options.Group);
            Assert.False(include.Store.ContainsKey("child"));
            Assert.True(include.Store["group"].Fiber is null or { State: FiberState.Disposed });
        });
    }
}
