using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class ProfileOriginalTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "cordis-profiles-original-" + Guid.NewGuid().ToString("N"));
    public ProfileOriginalTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private string At(string name) => Path.Combine(directory, name);
    [Fact]
    public async Task OnlyShippedNamesAutoInitialize()
    {
        var maps = new Dictionary<string, string>();
        await Assert.ThrowsAsync<FileNotFoundException>(() => DshProfilePolicy.LoadNamedAsync(directory, "custom", maps));
        Assert.False(Directory.Exists(Profiles.ResolveDirectory(directory, "custom")));
        Assert.Equal(["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-acp-app"], DshProfilePolicy.Templates["acp"]);
        Assert.Equal(["@deepseek-ai/dsh-base", "@deepseek-ai/dsh-sdk-app"], DshProfilePolicy.Templates["sdk"]);
        Assert.Equal(["@deepseek-ai/dsh-sdk-minimal"], DshProfilePolicy.Templates["sdk-minimal"]);
        await Assert.ThrowsAsync<FileNotFoundException>(() => DshProfilePolicy.LoadNamedAsync(directory, "web", maps));
        Assert.Equal(DshProfilePolicy.Templates["web"], PackageManifest.Read(Path.Combine(Profiles.ResolveDirectory(directory, "web"), "package.json")).Bundles);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyExactRetiredHeadlessTupleMigrates(bool custom)
    {
        var maps = new Dictionary<string, string>();
        foreach (var name in new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app", "@deepseek-ai/dsh-headless", "custom-bundle" })
        {
            var path = At(name.Replace('/', '_')); Directory.CreateDirectory(path); maps[name] = path;
            File.WriteAllText(Path.Combine(path, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":\"cordis.patch.yml\"}}}"); File.WriteAllText(Path.Combine(path, "cordis.patch.yml"), "[]\n");
        }
        var selected = maps.Keys.Take(custom ? 4 : 3).ToArray(); var profile = Profiles.ResolveDirectory(directory, "headless"); Profiles.Initialize(profile, selected);
        var manifestPath = Path.Combine(profile, "package.json"); var manifest = PackageManifest.Read(manifestPath); manifest.Raw["custom"] = "retained"; manifest.Write(manifestPath);
        var loaded = await DshProfilePolicy.LoadNamedAsync(directory, "headless", maps);
        Assert.Equal(custom ? selected : DshProfilePolicy.Templates["headless"], loaded.Bundles.Select(bundle => bundle.Name));
        Assert.Equal("retained", PackageManifest.Read(manifestPath).Raw["custom"]);
    }
    [Fact]
    public void ProfileNameRejectsTraversal()
    {
        Assert.Equal(Path.Combine(directory, "profiles", "web"), Profiles.ResolveDirectory(directory, "web"));
        foreach (var name in new[] { "", ".", "..", "../web", "a/b", "a\\b", "node_modules" }) Assert.Throws<ArgumentException>(() => Profiles.ResolveDirectory(directory, name));
    }
    [Fact]
    public void InitializationPreservesManifestAndUserLayer()
    {
        Profiles.Initialize(directory, ["base"]);
        Assert.Equal("base", Assert.Single(PackageManifest.Read(At("package.json")).Bundles));
        Assert.Contains("[]", File.ReadAllText(At("cordis.patch.yml")));
        File.WriteAllText(At("cordis.patch.yml"), "- id: x\n  config: {}\n");
        Profiles.Initialize(directory, ["other"]);
        Assert.Equal("base", Assert.Single(PackageManifest.Read(At("package.json")).Bundles));
        Assert.Contains("- id: x", File.ReadAllText(At("cordis.patch.yml")));
    }
    [Fact]
    public void ManifestRoundTripRejectsBrokenAndAbsentFiles()
    {
        var manifest = new PackageManifest(new EntryOptions { ["name"] = "p", ["dsh"] = new EntryOptions { ["profile"] = new EntryOptions { ["bundles"] = new[] { "a" } } } });
        manifest.Write(At("package.json")); Assert.Equal("a", Assert.Single(PackageManifest.Read(At("package.json")).Bundles));
        File.WriteAllText(At("package.json"), "[]"); Assert.Throws<FormatException>(() => PackageManifest.Read(At("package.json")));
        Assert.Throws<FileNotFoundException>(() => PackageManifest.Read(At("missing.json")));
    }
    [Fact]
    public async Task OrderedBundlesAndUserLayerAlsoSupportBareProfiles()
    {
        var maps = new Dictionary<string, string>();
        foreach (var name in new[] { "a", "b" })
        {
            var bundle = At(name); Directory.CreateDirectory(bundle); maps[name] = bundle;
            File.WriteAllText(Path.Combine(bundle, "package.json"), "{\"exports\":{\".\":\"./index.js\"},\"dsh\":{\"bundle\":{\"patch\":\"cordis.patch.yml\"}}}");
            File.WriteAllText(Path.Combine(bundle, "cordis.patch.yml"), name == "a" ? "- insert:\n  - id: a\n    name: pkg-a\n" : "- id: a\n  config: { v: 2 }\n");
        }
        var profilePath = At("managed"); Profiles.Initialize(profilePath, ["a", "b"]); var user = Path.Combine(profilePath, "cordis.patch.yml");
        File.WriteAllText(user, "- id: a\n  config: { v: 3 }\n");
        var profile = await Profiles.LoadAsync(profilePath, maps);
        Assert.Equal(profilePath, profile.Directory); Assert.Equal("managed", profile.Name); Assert.Equal(["a", "b"], profile.Bundles.Select(bundle => bundle.Name));
        Assert.Equal(3L, Assert.IsType<EntryOptions>(Assert.Single(Profiles.Compose(profile.Layers)).Config)["v"]);
        File.Delete(user); Assert.Empty((await Profiles.LoadAsync(profilePath, maps)).UserLayer.Patches);
        File.WriteAllText(Path.Combine(profilePath, "package.json"), "{\"name\":\"bare\"}"); Assert.Empty((await Profiles.LoadAsync(profilePath, maps)).Bundles);
    }
    [Fact]
    public async Task MissingBundleDeclarationFails()
    {
        var bundle = At("bundle"); Directory.CreateDirectory(bundle); File.WriteAllText(Path.Combine(bundle, "package.json"), "{}");
        Profiles.Initialize(directory, ["bundle"]);
        var error = await Assert.ThrowsAsync<FormatException>(() => Profiles.LoadAsync(directory, new Dictionary<string, string> { ["bundle"] = bundle })); Assert.Contains("declares no dsh.bundle", error.Message);
    }
    private sealed class EnvironmentEvaluator : IExpressionEvaluator
    {
        public object? Evaluate(string expression, Context context) => Environment.GetEnvironmentVariable(expression);
    }
    [Theory]
    [InlineData("present")]
    [InlineData("empty")]
    [InlineData("absent")]
    public async Task UserPatchesPreserveLiteralAssertionsAndResolveInsertedPaths(string state)
    {
        var rootPath = At("cordis.yml"); var patch = At("cordis.patch.yml");
        File.WriteAllText(rootPath, "- id: noop\n  name: ./noop.mjs\n  config: { value: base }\n");
        var variable = "CORDIS_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "user-value");
        try
        {
            if (state == "present") File.WriteAllText(patch, $"- id: noop\n  name: ./noop.mjs\n  config: {{ value: !!js {variable} }}\n- insert:\n  - id: extra\n    name: ./noop.mjs\n");
            else if (state == "empty") File.WriteAllText(patch, "[]\n");
            var plugin = new Plugin<object?> { Apply = (_, _) => { } };
            var resolver = new StaticModuleResolver().Register("./noop.mjs", plugin).Register(new Uri(At("noop.mjs")).AbsoluteUri, plugin);
            await using var context = await ApplicationBoot.BootAsync(rootPath, resolver, await Profiles.ReadPatchesAsync(patch, true), evaluator: new EnvironmentEvaluator());
            await context.RunAsync(ctx =>
            {
                var loader = ctx.Get<Loader>("loader")!;
                Assert.Equal(state == "present" ? "user-value" : "base", Assert.IsType<EntryOptions>(loader.Resolve("root:noop").Fiber!.Config)["value"]);
                Assert.Equal(state == "present", loader.Entries().Any(entry => entry.Options.Id == "extra")); return Task.CompletedTask;
            });
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }
}
