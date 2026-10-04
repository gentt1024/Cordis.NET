using Cordis;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ConfigurationEditTests
{
    [Theory]
    [InlineData("# retained\n[]\n")]
    [InlineData("# retained\n- id: worker\n  config: { limit: 1, label: worker }\n")]
    [InlineData("# retained\n- id: worker\n  config:\n    limit: 1\n    label: worker\n")]
    public async Task Field_source_retains_comments_and_parses_after_edit(string source)
    {
        await using var fixture = await Fixture.StartAsync(source);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, snapshot.Revision);
        Assert.True(result.Saved, result.Diagnostic);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        Assert.Contains("# retained", text);
        Assert.Single(ConfigurationFile.ParseEntries(text));
    }

    [Fact]
    public async Task Flow_root_edit_preserves_unrelated_source_comments_and_expression()
    {
        const string source = "[\n {id: worker, config: {limit: 1, label: worker}},\n # unrelated documentation\n {id: absent, config: !!js 123}\n]\n";
        await using var fixture = await Fixture.StartAsync(source);
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var changed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, before.Revision);
        Assert.True(changed.Applied, changed.Diagnostic);
        Assert.Contains("# unrelated documentation\n {id: absent, config: !!js 123}", await File.ReadAllTextAsync(fixture.Patch));
        before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        changed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 1, before.Revision);
        Assert.True(changed.Applied, changed.Diagnostic);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        Assert.Contains("# unrelated documentation\n {id: absent, config: !!js 123}", text);
        Assert.Single(ConfigurationFile.ParseEntries(text));
    }

    [Theory]
    [InlineData("# retained\n- { id: worker, config: { limit: 2, label: worker } }\n")]
    [InlineData("# retained\n- id: worker\n  config: { limit: 2, label: worker }\n")]
    public async Task Reset_to_inherited_removes_the_override(string source)
    {
        await using var fixture = await Fixture.StartAsync(source);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 1, snapshot.Revision);
        Assert.True(result.Applied, result.Diagnostic);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        Assert.Contains("# retained", text);
        Assert.Empty(ConfigurationFile.ParseEntries(text));
        Assert.Equal(1, fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues["limit"]);
    }

    [Fact]
    public async Task User_layer_insert_is_editable_and_unrelated_expression_remains_opaque()
    {
        const string source = "# retained\n- insert:\n  - id: worker\n    name: worker\n    config: { limit: 1, label: worker }\n- id: absent\n  config: !!js throw new Error('must not run')\n";
        await using var fixture = await Fixture.StartAsync(source, inserted: true);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, snapshot.Revision);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Equal(2, fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues["limit"]);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        Assert.Contains("!!js throw new Error('must not run')", text);
        Assert.True(ConfigurationFile.ParseEntries(text)[0].ContainsKey("insert"));
        snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 1, snapshot.Revision);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Equal(2, ConfigurationFile.ParseEntries(await File.ReadAllTextAsync(fixture.Patch)).Count);
    }

    [Fact]
    public async Task Live_only_child_edit_accepts_a_whole_object_binding()
    {
        await using var fixture = await Fixture.StartAsync(whole: true);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, snapshot.Revision, liveOnly: true);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Equal(1, fixture.Activations);
        Assert.Equal(2, Convert.ToInt32(fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues[""] is IReadOnlyDictionary<string, object?> map ? map["limit"] : null));
    }

    [Fact]
    public async Task Raw_edit_distinguishes_null_from_non_finite_and_refuses_undefined()
    {
        await using var fixture = await Fixture.StartAsync(whole: true);
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var changed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["extra"], double.NaN, before.Revision);
        Assert.True(changed.Applied, changed.Diagnostic);
        var nonFinite = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.True(double.IsNaN((double)nonFinite.Raw["extra"]!));
        changed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["extra"], null, nonFinite.Revision);
        Assert.True(changed.Applied, changed.Diagnostic);
        var nullable = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.NotEqual(nonFinite.Revision, nullable.Revision);
        Assert.True(nullable.Raw.ContainsKey("extra"));
        Assert.Null(nullable.Raw["extra"]);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        changed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["extra"], Undefined.Value, nullable.Revision);
        Assert.Equal("non-persistable-configuration", changed.Error);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task Caller_owned_read_only_map_is_detached_before_queue_wait()
    {
        await using var fixture = await Fixture.StartAsync(whole: true);
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Operations.RunExclusiveAsync = async action => { entered.SetResult(); await release.Task; await action(); };
        var backing = new Dictionary<string, object?> { ["value"] = "original" };
        var pending = fixture.Operations.EditConfigurationFieldAsync("root:worker", ["extra"], new ReadOnlyView(backing), before.Revision);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backing["value"] = "mutated";
        release.SetResult();
        var result = await pending;
        Assert.True(result.Applied, result.Diagnostic);
        var published = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues[""]);
        Assert.Equal("original", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(published["extra"])["value"]);
    }

    private sealed class ReadOnlyView(Dictionary<string, object?> values) : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => values[key];
        public IEnumerable<string> Keys => values.Keys;
        public IEnumerable<object?> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(string key) => values.ContainsKey(key);
        public bool TryGetValue(string key, out object? value) => values.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Fact]
    public async Task Field_edit_preserves_live_identity_validates_and_rejects_stale_revision()
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var fiber = fixture.Loader.Resolve("root:worker").Fiber!;
        ConfigReference<int> reference = null!;
        await fixture.Loader.Context.RunAsync(_ => { reference = fiber.GetConfigReference<int>("limit"); return Task.CompletedTask; });
        var result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, before.Revision);
        Assert.True(result.Saved, result.Diagnostic);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Equal(2, reference.Value);
        Assert.Equal(1, fixture.Activations);
        Assert.Contains("# retained", await File.ReadAllTextAsync(fixture.Patch));

        var stale = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 3, before.Revision);
        Assert.Equal("conflict", stale.Error);
        Assert.False(stale.Saved);
        var current = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var text = await File.ReadAllTextAsync(fixture.Patch);
        var invalid = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], -1, current.Revision);
        Assert.Equal("invalid-configuration", invalid.Error);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
        Assert.Equal(2, reference.Value);

        result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["label"], "batch", current.Revision);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Equal(2, fixture.Activations);
        Assert.Equal(2, reference.Value);
        Assert.Equal("batch", ((Settings)fixture.Loader.Resolve("root:worker").Fiber!.Config!).Label);
    }

    [Fact]
    public async Task Field_edit_rejects_home_override_and_restores_source_after_activation_failure()
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var text = await File.ReadAllTextAsync(fixture.Patch);
        var failed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["label"], "fail", before.Revision);
        Assert.False(failed.Saved);
        Assert.False(failed.Applied);
        Assert.True(failed.Error == "reconcile-failed", failed.Diagnostic);
        Assert.Contains("activation rejected", failed.Diagnostic);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));

        await File.WriteAllTextAsync(Path.Combine(fixture.Home, "cordis.patch.yml"), "- id: worker\n  config: { limit: 7, label: forced }\n");
        before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var overridden = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 8, before.Revision);
        Assert.Equal("overridden", overridden.Error);
        Assert.False(overridden.Saved);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
    }

    private sealed record Settings(int Limit, string Label);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "cordis-field-edit-" + Guid.NewGuid().ToString("N"));
        private readonly Context context = new();
        public string Home => Path.Combine(directory, "home");
        public string Patch => Path.Combine(directory, "cordis.patch.yml");
        public Loader Loader { get; private set; } = null!;
        public Include Include { get; private set; } = null!;
        public PluginConfigurationOperations Operations { get; private set; } = null!;
        public int Activations { get; private set; }

        public static async Task<Fixture> StartAsync(string source = "# retained\n[]\n", bool inserted = false, bool whole = false)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.directory);
            Directory.CreateDirectory(fixture.Home);
            Profiles.Initialize(fixture.directory, []);
            await File.WriteAllTextAsync(fixture.Patch, source);
            var launch = new ProfileLaunch(await Profiles.LoadAsync(fixture.directory, new Dictionary<string, string>()), fixture.Home, [], new Dictionary<string, string>(), new Dictionary<string, string>());
            IPlugin plugin = new Plugin<Settings>
            {
                Configuration = ConfigObject<Settings>.Create(raw => raw is IReadOnlyDictionary<string, object?> map
                    && Convert.ToInt32(map.GetValueOrDefault("limit")) is var limit && limit > 0
                    && map.GetValueOrDefault("label") is string label
                        ? ConfigResult<Settings>.Success(new(limit, label)) : ConfigResult<Settings>.Failure("positive limit and label required"))
                    .Field("limit", ConfigDescriptor.Number().Volatile(), value => value.Limit)
                    .Field("label", ConfigDescriptor.String(), value => value.Label).Build(),
                Apply = (_, settings) =>
                {
                    if (settings.Label == "fail") throw new InvalidOperationException("activation rejected");
                    fixture.Activations++;
                },
            };
            if (whole)
                plugin = new Plugin<object?>
                {
                    Configuration = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw),
                        ConfigDescriptor.Object(("limit", ConfigDescriptor.Number()), ("label", ConfigDescriptor.String())).Volatile()).WithVolatileValue(),
                    Apply = (_, _) => fixture.Activations++,
                };
            await fixture.context.RunAsync(async owner =>
            {
                fixture.Loader = new Loader(owner, new StaticModuleResolver().Register("worker", plugin));
                var basePath = Path.Combine(fixture.directory, "base.yml");
                await File.WriteAllTextAsync(basePath, inserted ? "[]\n" : ConfigurationFile.Write(new[] { new EntryOptions
                {
                    Id = "worker", Name = "worker", Config = new EntryOptions { ["limit"] = 1, ["label"] = "worker" },
                } }));
                fixture.Include = await ApplicationBoot.MountAsync(fixture.Loader, basePath);
                await fixture.Loader.WaitAsync();
            });
            fixture.Operations = new(launch, fixture.Include) { RunExclusiveAsync = action => action() };
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
}
