using Cordis;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ConfigurationEditTests
{
    [Fact]
    public async Task Wrapped_null_and_missing_secrets_keep_presence_sidecars_without_returning_secret_fields()
    {
        var secret = ConfigDescriptor.String().WithMetadata(new() { Role = "secret" });
        var description = ConfigDescriptor.Object(("section", ConfigDescriptor.Object(
            ("nullGetter", ConfigDescriptor.Getter(secret)), ("missingGetter", ConfigDescriptor.Getter(secret)),
            ("nullUnion", ConfigDescriptor.Union(ConfigDescriptor.Any(), secret)),
            ("missingUnion", ConfigDescriptor.Union(ConfigDescriptor.Any(), secret))))).Volatile();
        await using var fixture = await Fixture.StartAsync(whole: true, description: description,
            baseConfig: new EntryOptions { ["section"] = new EntryOptions { ["nullGetter"] = null, ["nullUnion"] = null } });
        var view = await fixture.Operations.ReadSettingsAsync("root:worker", new SettingsPolicy(["section"]));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(view.Fields).Value));
        Assert.Equal(new[] { "section/nullGetter", "section/missingGetter", "section/nullUnion", "section/missingUnion" },
            view.Secrets.Select(secret => string.Join("/", secret.Path)));
        Assert.Equal(new[] { true, false, true, false }, view.Secrets.Select(secret => secret.Set));
    }
    [Theory]
    [InlineData("finite")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("lazy-cycle")]
    [InlineData("union-cycle")]
    [InlineData("union-rebuild-cycle")]
    public async Task Recursive_settings_redaction_terminates_in_an_isolated_testhost(string scenario)
    {
        const string marker = "CORDIS_RECURSIVE_SETTINGS_PROBE";
        if (Environment.GetEnvironmentVariable(marker) is { } childScenario)
        {
            if (childScenario != scenario) return;
            ConfigDescriptor? tree = null;
            tree = ConfigDescriptor.Object(("value", ConfigDescriptor.String()),
                ("password", ConfigDescriptor.String().Optional().WithMetadata(new() { Role = "secret" })),
                ("next", ConfigDescriptor.Lazy(() => tree!).Optional()));
            object? section = new EntryOptions { ["value"] = "one", ["password"] = "root-secret" };
            if (scenario == "finite") ((EntryOptions)section)["next"] = new EntryOptions { ["value"] = "last", ["password"] = "tail-secret" };
            if (scenario == "null") ((EntryOptions)section)["next"] = null;
            if (scenario is "lazy-cycle" or "union-cycle" or "union-rebuild-cycle")
            {
                ConfigDescriptor? loop = null;
                loop = scenario == "lazy-cycle" ? ConfigDescriptor.Lazy(() => loop!)
                    : scenario == "union-cycle" ? ConfigDescriptor.Union(_ => 0, ConfigDescriptor.Lazy(() => loop!))
                    : ConfigDescriptor.Union(_ => 1, ConfigDescriptor.Object(("unreadable", ConfigDescriptor.String())), ConfigDescriptor.Lazy(() => loop!));
                tree = ConfigDescriptor.Object(("cycle", loop), ("password", ConfigDescriptor.String().WithMetadata(new() { Role = "secret" })));
                section = new EntryOptions { ["cycle"] = scenario == "union-rebuild-cycle"
                    ? new EntryOptions { ["unreadable"] = "must-not-leak" } : "must-not-leak", ["password"] = "root-secret" };
            }
            await using var fixture = await Fixture.StartAsync(whole: true,
                baseConfig: new EntryOptions { ["section"] = section }, description: ConfigDescriptor.Object(("section", tree)).Volatile());
            var view = await fixture.Operations.ReadSettingsAsync("root:worker", new SettingsPolicy(["section"]));
            var value = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(view.Fields).Value);
            var serialized = ConfigurationFile.Write(value, true);
            Assert.DoesNotContain("root-secret", serialized);
            Assert.DoesNotContain("tail-secret", serialized);
            Assert.DoesNotContain("must-not-leak", serialized);
            if (scenario == "finite")
            {
                Assert.Equal("one", value["value"]);
                Assert.Equal("last", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(value["next"])["value"]);
            }
            if (scenario == "missing") Assert.False(value.ContainsKey("next"));
            if (scenario == "null") Assert.Null(value["next"]);
            if (scenario is "lazy-cycle" or "union-cycle" or "union-rebuild-cycle")
            {
                Assert.False(value.ContainsKey("cycle"));
                Assert.Contains(view.Diagnostics, diagnostic => diagnostic.Contains("redaction", StringComparison.Ordinal));
            }
            return;
        }
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ConfigurationEditTests).Assembly.Location);
        start.ArgumentList.Add("--TestCaseFilter:FullyQualifiedName~Recursive_settings_redaction_terminates_in_an_isolated_testhost");
        start.Environment[marker] = scenario;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
        Assert.True(child.ExitCode == 0, $"recursive probe {scenario} exited {child.ExitCode}\n{await stdout}\n{await stderr}");
    }
    [Fact]
    public async Task Secret_redaction_is_conservative_across_union_branches_and_preserves_other_public_values()
    {
        var branch = ConfigDescriptor.Union(
            ConfigDescriptor.Object(("password", ConfigDescriptor.String().WithMetadata(new() { Role = "secret" })), ("first", ConfigDescriptor.String())),
            ConfigDescriptor.Object(("password", ConfigDescriptor.String()), ("second", ConfigDescriptor.String())));
        var description = ConfigDescriptor.Object(("choice", branch)).Volatile();
        await using var fixture = await Fixture.StartAsync(whole: true, description: description,
            baseConfig: new EntryOptions { ["choice"] = new EntryOptions { ["password"] = null, ["first"] = "one", ["second"] = "two" } });
        var view = await fixture.Operations.ReadSettingsAsync("root:worker", new SettingsPolicy(["choice"]));
        var value = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(view.Fields).Value);
        Assert.Equal("one", value["first"]); Assert.Equal("two", value["second"]);
        Assert.False(value.ContainsKey("password"));
        Assert.True(Assert.Single(view.Secrets).Set);
    }
    [Fact]
    public async Task Nested_live_settings_exclude_ordinary_siblings_and_authorize_each_exact_boundary()
    {
        var descriptor = ConfigDescriptor.Object(("section", ConfigDescriptor.Object(
            ("live", ConfigDescriptor.Number().Volatile()), ("ordinary", ConfigDescriptor.String()))));
        var config = new EntryOptions { ["section"] = new EntryOptions { ["live"] = 1, ["ordinary"] = "private-ordinary" } };
        var schema = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw), descriptor)
            .WithVolatile<object?>(["section", "live"], raw => ((IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)raw!)["section"]!)["live"]);
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: config, schema: schema);
        var policy = new SettingsPolicy(["section"]);
        var view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        var section = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(view.Fields).Value);
        Assert.Equal(new[] { "live" }, section.Keys);
        Assert.Equal(1, Convert.ToInt32(section["live"]));
        var exports = await fixture.Operations.ReadSettingsSchemasAsync("root:worker", policy);
        Assert.DoesNotContain("ordinary", exports.Schemastery.Document);
        var refusal = await fixture.Operations.MutateSettingsAsync("root:worker", [new ConfigurationSet(["section", "ordinary"], "changed")], view.Revision, policy);
        Assert.Equal("field-not-offered", refusal.Error);
        refusal = await fixture.Operations.MutateSettingsAsync("root:worker", [new ConfigurationSet(["section"], new EntryOptions { ["live"] = 2 })], view.Revision, policy);
        Assert.Equal("field-not-offered", refusal.Error);
        var result = await fixture.Operations.MutateSettingsAsync("root:worker", [new ConfigurationSet(["section", "live"], 2)], view.Revision, policy);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Equal(1, fixture.Activations);
    }

    [Fact]
    public async Task Settings_redact_nested_secret_slots_in_objects_arrays_dicts_and_reject_ancestor_replacements()
    {
        var secret = ConfigDescriptor.String().Default("default-secret").WithMetadata(new() { Role = "secret" });
        var description = ConfigDescriptor.Object(("section", ConfigDescriptor.Object(
            ("public", ConfigDescriptor.String()), ("password", secret),
            ("hidden", ConfigDescriptor.String().WithMetadata(new() { Hidden = true })),
            ("items", ConfigDescriptor.Array(ConfigDescriptor.Object(("token", secret), ("label", ConfigDescriptor.String()),
                ("hiddenNested", ConfigDescriptor.String().WithMetadata(new() { Hidden = true }))))),
            ("accounts", ConfigDescriptor.Dict(secret))))).Volatile();
        var config = new EntryOptions { ["section"] = new EntryOptions
        {
            ["public"] = "visible", ["password"] = "saved-secret", ["hidden"] = "hidden-secret",
            ["items"] = new List<object?> { new EntryOptions { ["token"] = "array-secret", ["label"] = "item", ["hiddenNested"] = "hidden-array-secret" } },
            ["accounts"] = new EntryOptions { ["alice"] = "dict-secret" },
        } };
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: config, description: description);
        var policy = new SettingsPolicy(["section"]);
        var view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        var value = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(view.Fields).Value);
        Assert.False(value.ContainsKey("password")); Assert.False(value.ContainsKey("hidden"));
        var item = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(Assert.Single(Assert.IsAssignableFrom<IEnumerable<object?>>(value["items"])));
        Assert.Equal(new[] { "label" }, item.Keys);
        Assert.Equal(new[] { "section/password", "section/items/0/token", "section/accounts/alice" }, view.Secrets.Select(slot => string.Join("/", slot.Path)));
        Assert.All(view.Secrets, slot => Assert.True(slot.Set));
        var exports = await fixture.Operations.ReadSettingsSchemasAsync("root:worker", policy);
        Assert.DoesNotContain("default-secret", exports.Schemastery.Document);
        Assert.DoesNotContain("hidden", exports.Schemastery.Document);
        var rejected = await fixture.Operations.MutateSettingsAsync("root:worker", [new ConfigurationSet(["section"], new EntryOptions { ["public"] = "replacement" })], view.Revision, policy);
        Assert.Equal("field-not-offered", rejected.Error);
        rejected = await fixture.Operations.MutateSettingsAsync("root:worker", [new ConfigurationSet(["section", "hidden"], "replacement")], view.Revision, policy);
        Assert.Equal("field-not-offered", rejected.Error);
        var changed = await fixture.Operations.MutateSettingsAsync("root:worker", [new ConfigurationSet(["section", "password"], "new-secret")], view.Revision, policy);
        Assert.True(changed.Applied, changed.Diagnostic);
        view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        Assert.All(view.Secrets, slot => Assert.True(slot.Set));
        Assert.DoesNotContain("new-secret", ConfigurationFile.Write(Assert.Single(view.Fields).Value, true));
        Assert.Equal(1, fixture.Activations);
    }
    [Fact]
    public async Task Batch_settings_validate_only_the_complete_candidate_and_refuse_mixed_hidden_edits()
    {
        var candidates = new List<(int Left, int Right)>();
        ConfigResult<object?> Validate(object? raw)
        {
            var map = (IReadOnlyDictionary<string, object?>)raw!;
            var pair = (IReadOnlyDictionary<string, object?>)map["pair"]!;
            var values = (Convert.ToInt32(pair["left"]), Convert.ToInt32(pair["right"]));
            candidates.Add(values);
            return values.Item1 == values.Item2 ? ConfigResult<object?>.Success(raw) : ConfigResult<object?>.Failure("pair must agree");
        }
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: CompositeConfig(),
            description: CompositeDescription(), validate: Validate);
        var policy = new SettingsPolicy(["pair", "items", "credential"], ["credential"]);
        var view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        Assert.Equal(new[] { "pair", "items" }, view.Fields.Select(field => field.Name));
        candidates.Clear();
        var result = await fixture.Operations.MutateSettingsAsync("root:worker",
            [new ConfigurationSet(["pair", "left"], 2), new ConfigurationSet(["pair", "right"], 2)], view.Revision, policy);
        Assert.True(result.Saved && result.Applied, result.Diagnostic);
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.Equal(candidate.Left, candidate.Right));
        Assert.Equal(1, fixture.Activations);
        var saved = await File.ReadAllTextAsync(fixture.Patch);
        view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        result = await fixture.Operations.MutateSettingsAsync("root:worker",
            [new ConfigurationSet(["pair", "left"], 3), new ConfigurationSet(["pair", "right"], 4)], view.Revision, policy);
        Assert.Equal("invalid-configuration", result.Error);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
        result = await fixture.Operations.MutateSettingsAsync("root:worker",
            [new ConfigurationSet(["pair", "left"], 3), new ConfigurationSet(["credential"], "leak")], view.Revision, policy);
        Assert.Equal("field-not-offered", result.Error);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
        result = await fixture.Operations.MutateSettingsAsync("root:worker",
            [new ConfigurationSet([], CompositeConfig())], view.Revision, policy);
        Assert.Equal("field-not-offered", result.Error);
        result = await fixture.Operations.MutateSettingsAsync("root:worker",
            [new ConfigurationSet(["pair", "left"], double.NaN)], view.Revision, policy);
        Assert.Equal("non-json-settings-data", result.Error);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task Ordered_array_edits_and_object_resets_restore_source_inheritance()
    {
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: CompositeConfig(), description: CompositeDescription());
        var view = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.MutateConfigurationAsync("root:worker",
            [new ConfigurationSet(["pair", "left"], 9), new ConfigurationSet(["items", "2"], "c"),
                new ConfigurationUnset(["items", "0"])], view.Revision, liveOnly: true);
        Assert.True(result.Applied, result.Diagnostic);
        view = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal(new object?[] { "b", "c" }, Assert.IsAssignableFrom<IEnumerable<object?>>(view.Raw["items"]));
        result = await fixture.Operations.MutateConfigurationAsync("root:worker",
            [new ConfigurationUnset(["pair", "left"]), new ConfigurationUnset(["items"])], view.Revision, liveOnly: true);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Empty(ConfigurationFile.ParseEntries(await File.ReadAllTextAsync(fixture.Patch)));
        var inherited = CompositeConfig();
        ((EntryOptions)inherited["pair"]!)["left"] = 5;
        await File.WriteAllTextAsync(fixture.Include.Filename, ConfigurationFile.Write(new[] { new EntryOptions
            { Id = "worker", Name = "worker", Config = inherited } }));
        await fixture.Loader.Context.RunAsync(async _ => { await fixture.Include.RefreshAsync(); await fixture.Loader.WaitAsync(); });
        view = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal(5, Convert.ToInt32(Assert.IsType<EntryOptions>(view.Raw["pair"])["left"]));
        Assert.Equal(1, fixture.Activations);
    }

    [Theory]
    [InlineData("01", false)]
    [InlineData("-1", false)]
    [InlineData("3", false)]
    [InlineData("2", true)]
    public async Task Invalid_array_path_refuses_the_entire_batch(string index, bool unset)
    {
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: CompositeConfig(), description: CompositeDescription());
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var source = await File.ReadAllTextAsync(fixture.Patch);
        ConfigurationPathOperation invalid = unset ? new ConfigurationUnset(["items", index]) : new ConfigurationSet(["items", index], "bad");
        var result = await fixture.Operations.MutateConfigurationAsync("root:worker",
            [new ConfigurationSet(["pair", "left"], 9), invalid], before.Revision);
        Assert.Equal("invalid-array-index", result.Error);
        Assert.Equal(source, await File.ReadAllTextAsync(fixture.Patch));
        Assert.Equal(before.Revision, (await fixture.Operations.ReadConfigurationAsync("root:worker")).Revision);
    }

    private static EntryOptions CompositeConfig() => new()
    {
        ["pair"] = new EntryOptions { ["left"] = 1, ["right"] = 1 },
        ["items"] = new List<object?> { "a", "b" }, ["credential"] = "private",
    };

    private static ConfigDescriptor CompositeDescription() => ConfigDescriptor.Object(
        ("pair", ConfigDescriptor.Object(("left", ConfigDescriptor.Number()), ("right", ConfigDescriptor.Number()))),
        ("items", ConfigDescriptor.Array(ConfigDescriptor.String())), ("credential", ConfigDescriptor.String())).Volatile();

    [Fact]
    public async Task Settings_exports_obey_selection_and_remove_sensitive_defaults_at_every_depth()
    {
        var description = ConfigDescriptor.Object(
            ("pair", ConfigDescriptor.Object(("left", ConfigDescriptor.Number().Default(123456789)),
                ("right", ConfigDescriptor.Number())).Default(new EntryOptions { ["secret"] = "nested-default-secret" })),
            ("credential", ConfigDescriptor.String().Default("credential-default-secret"))).Volatile();
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: CompositeConfig(), description: description);
        var policy = new SettingsPolicy(["pair", "credential"], ["credential"]);
        var exports = await fixture.Operations.ReadSettingsSchemasAsync("root:worker", policy);
        var view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        Assert.Equal(view.Revision, exports.Revision);
        foreach (var exported in new[] { exports.Schemastery, exports.JsonSchema })
        {
            Assert.DoesNotContain("credential", exported.Document);
            Assert.DoesNotContain("nested-default-secret", exported.Document);
            Assert.DoesNotContain("123456789", exported.Document);
            Assert.Contains("pair", exported.Document);
        }
        var full = await fixture.Operations.ReadConfigurationSchemasAsync("root:worker");
        Assert.Contains("credential-default-secret", full.Schemastery.Document);
    }
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

    [Theory]
    [InlineData("null", false)]
    [InlineData("false", false)]
    [InlineData("null", true)]
    [InlineData("false", true)]
    public async Task Falsey_insert_override_can_return_to_inheritance(string insertion, bool flow)
    {
        var source = flow
            ? $"# retained\n[{{ id: worker, insert: {insertion}, note: keep, config: {{ limit: 2, label: worker }} }},\n # unrelated documentation\n {{ id: absent, config: !!js throw new Error('must not run') }}]\n"
            : $"# retained\n- id: worker\n  insert: {insertion}\n  note: keep\n  config: {{ limit: 2, label: worker }}\n# unrelated documentation\n- id: absent\n  config: !!js throw new Error('must not run')\n";
        await using var fixture = await Fixture.StartAsync(source);
        var policy = new SettingsPolicy(["limit"]);
        var before = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        Assert.Equal(2, Convert.ToInt32(Assert.Single(before.Fields).Value));
        Assert.True(Assert.Single(before.Fields).Overridden);
        var fiber = fixture.Loader.Resolve("root:worker").Fiber;

        var result = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 1, before.Revision);
        Assert.True(result.Saved && result.Applied, result.Diagnostic);
        var view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        Assert.Equal(1, Convert.ToInt32(Assert.Single(view.Fields).Value));
        var text = await File.ReadAllTextAsync(fixture.Patch);
        Assert.False(Assert.Single(view.Fields).Overridden, text);
        var rows = ConfigurationFile.ParseEntries(text);
        var retained = Assert.Single(rows, row => row.Id == "worker");
        Assert.False(retained.ContainsKey("config"));
        Assert.Equal(ConfigurationFile.Parse(insertion), retained["insert"]);
        Assert.Equal("keep", retained["note"]);
        Assert.Equal(2, rows.Count);
        Assert.Contains("# retained", text);
        Assert.Contains("# unrelated documentation", text);
        Assert.Contains("config: !!js throw new Error('must not run')", text);

        await File.WriteAllTextAsync(fixture.Include.Filename,
            "- id: worker\n  name: worker\n  config: { limit: 3, label: worker }\n");
        await fixture.Loader.Context.RunAsync(async _ =>
        {
            await fixture.Include.RefreshAsync();
            await fixture.Loader.WaitAsync();
        });
        view = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        Assert.Equal(3, Convert.ToInt32(Assert.Single(view.Fields).Value));
        Assert.False(Assert.Single(view.Fields).Overridden);
        Assert.Same(fiber, fixture.Loader.Resolve("root:worker").Fiber);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task User_layer_insert_is_editable_and_unrelated_expression_remains_opaque()
    {
        const string source = "# retained\n- insert:\n  - id: worker\n    name: worker\n    config: { limit: 1, label: worker }\n- id: absent\n  insert: null\n  config: !!js throw new Error('must not run')\n";
        await using var fixture = await Fixture.StartAsync(source, inserted: true);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var insertedView = await fixture.Operations.ReadSettingsAsync("root:worker", new SettingsPolicy(["limit"]));
        Assert.True(Assert.Single(insertedView.Fields).Overridden);
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

    [Fact]
    public async Task Settings_read_and_submit_enforce_selection_redaction_and_live_only()
    {
        await using var fixture = await Fixture.StartAsync();
        var policy = new SettingsPolicy(["limit", "label", "credential"], ["credential"]);
        var before = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        var field = Assert.Single(before.Fields);
        Assert.Equal("limit", field.Name);
        Assert.Equal(1, field.Value);
        Assert.False(field.Overridden);
        Assert.Contains("credential: hidden", before.Diagnostics);
        var hidden = await fixture.Operations.EditSettingsFieldAsync("root:worker", "credential", "must not save", before.Revision, policy);
        Assert.Equal("field-not-offered", hidden.Error);
        var ordinary = await fixture.Operations.EditSettingsFieldAsync("root:worker", "label", "new", before.Revision, policy);
        Assert.Equal("field-not-offered", ordinary.Error);
        var changed = await fixture.Operations.EditSettingsFieldAsync("root:worker", "limit", 2, before.Revision, policy);
        Assert.True(changed.Applied, changed.Diagnostic);
        var after = await fixture.Operations.ReadSettingsAsync("root:worker", policy);
        Assert.Equal(2, Assert.Single(after.Fields).Value);
        Assert.True(after.Fields[0].Overridden);
        Assert.Equal(1, fixture.Activations);
        changed = await fixture.Operations.EditSettingsFieldAsync("root:worker", "limit", 3, before.Revision, policy);
        Assert.Equal("conflict", changed.Error);
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
    public async Task Raw_snapshot_detaches_read_only_maps_from_host_authored_layers()
    {
        var backing = new Dictionary<string, object?> { ["value"] = "original" };
        var config = new EntryOptions { ["limit"] = 1, ["label"] = "worker", ["extra"] = new ReadOnlyView(backing) };
        await using var fixture = await Fixture.StartAsync(overlays: [new("host", [new() { Id = "worker", Config = config }])]);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        backing["value"] = "mutated";
        Assert.Equal("original", Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot.Raw["extra"])["value"]);
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

    [Fact]
    public async Task Observer_failure_cannot_replace_success_or_primary_recovery_result()
    {
        await using var fixture = await Fixture.StartAsync();
        var notifications = 0;
        fixture.Operations.Changed += _ => throw new InvalidOperationException("observer rejected");
        fixture.Operations.Changed += _ => notifications++;
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var changed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, before.Revision);
        Assert.True(changed.Saved && changed.Applied, changed.Diagnostic);
        Assert.Equal(2, fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues["limit"]);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var failed = await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["label"], "fail", before.Revision);
        Assert.Equal("reconcile-failed", failed.Error);
        Assert.Contains("activation rejected", failed.Diagnostic);
        Assert.False(failed.Saved);
        Assert.False(failed.Applied);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
        Assert.Equal(2, notifications);
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

        public static async Task<Fixture> StartAsync(string source = "# retained\n[]\n", bool inserted = false, bool whole = false,
            IReadOnlyList<ConfigurationLayer>? overlays = null, EntryOptions? baseConfig = null,
            ConfigDescriptor? description = null, Func<object?, ConfigResult<object?>>? validate = null, ConfigSchema<object?>? schema = null)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.directory);
            Directory.CreateDirectory(fixture.Home);
            Profiles.Initialize(fixture.directory, []);
            await File.WriteAllTextAsync(fixture.Patch, source);
            var launch = new ProfileLaunch(await Profiles.LoadAsync(fixture.directory, new Dictionary<string, string>()), fixture.Home, overlays ?? [], new Dictionary<string, string>(), new Dictionary<string, string>());
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
                    Configuration = schema ?? new ConfigSchema<object?>(validate ?? (raw => ConfigResult<object?>.Success(raw)),
                        description ?? ConfigDescriptor.Object(("limit", ConfigDescriptor.Number()), ("label", ConfigDescriptor.String())).Volatile()).WithVolatileValue(),
                    Apply = (_, _) => fixture.Activations++,
                };
            await fixture.context.RunAsync(async owner =>
            {
                fixture.Loader = new Loader(owner, new StaticModuleResolver().Register("worker", plugin));
                var basePath = Path.Combine(fixture.directory, "base.yml");
                await File.WriteAllTextAsync(basePath, inserted ? "[]\n" : ConfigurationFile.Write(new[] { new EntryOptions
                {
                    Id = "worker", Name = "worker", Config = baseConfig ?? new EntryOptions { ["limit"] = 1, ["label"] = "worker" },
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
