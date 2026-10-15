using Cordis;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ConfigurationEditTests
{
    [Fact]
    public async Task Wrapped_null_and_missing_secrets_keep_presence_sidecars_without_returning_secret_fields()
    {
        var secret = ConfigDescriptor
            .String()
            .WithMetadata(
                new()
                {
                    Role = "secret"
                });
        var description = ConfigDescriptor
            .Object(
                ("section",
                    ConfigDescriptor.Object(
                        ("nullGetter", ConfigDescriptor.Getter(secret)),
                        ("missingGetter", ConfigDescriptor.Getter(secret)),
                        ("nullUnion", ConfigDescriptor.Union(ConfigDescriptor.Any(), secret)),
                        ("missingUnion", ConfigDescriptor.Union(ConfigDescriptor.Any(), secret)))))
            .Volatile();
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            description: description,
            baseConfig: new EntryOptions
            {
                ["section"] = new EntryOptions
                {
                    ["nullGetter"] = null,
                    ["nullUnion"] = null
                }
            });
        fixture.Policy = new(["section"]);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Empty(view.Value.GetProperty("section").EnumerateObject());
        Assert.Equal(
            new[] { "section/nullGetter", "section/missingGetter", "section/nullUnion", "section/missingUnion" },
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
            if (childScenario != scenario)
                return;
            ConfigDescriptor? tree = null;
            tree = ConfigDescriptor.Object(
                ("value", ConfigDescriptor.String()),
                ("password", ConfigDescriptor
                    .String()
                    .Optional()
                    .WithMetadata(
                        new()
                        {
                            Role = "secret"
                        })),
                ("next", ConfigDescriptor.Lazy(() => tree!).Optional()));
            object? section = new EntryOptions
            {
                ["value"] = "one",
                ["password"] = "root-secret"
            };
            if (scenario == "finite")
                ((EntryOptions)section)["next"] = new EntryOptions
                {
                    ["value"] = "last",
                    ["password"] = "tail-secret"
                };
            if (scenario == "null")
                ((EntryOptions)section)["next"] = null;
            if (scenario is "lazy-cycle" or "union-cycle" or "union-rebuild-cycle")
            {
                ConfigDescriptor? loop = null;
                loop = scenario == "lazy-cycle" ? ConfigDescriptor.Lazy(() => loop!) :
                    scenario == "union-cycle" ? ConfigDescriptor.Union(_ => 0, ConfigDescriptor.Lazy(() => loop!)) :
                    ConfigDescriptor.Union(
                        _ => 1,
                        ConfigDescriptor.Object(("unreadable", ConfigDescriptor.String())),
                        ConfigDescriptor.Lazy(() => loop!));
                tree = ConfigDescriptor.Object(
                    ("cycle", loop),
                    ("password", ConfigDescriptor
                        .String()
                        .WithMetadata(
                            new()
                            {
                                Role = "secret"
                            })));
                section = new EntryOptions
                {
                    ["cycle"] = scenario == "union-rebuild-cycle"
                        ? new EntryOptions
                        {
                            ["unreadable"] = "must-not-leak"
                        }
                        : "must-not-leak",
                    ["password"] = "root-secret"
                };
            }

            await using var fixture = await Fixture.StartAsync(
                whole: true,
                baseConfig: new EntryOptions
                {
                    ["section"] = section
                },
                description: ConfigDescriptor.Object(("section", tree)).Volatile());
            fixture.Policy = new(["section"]);
            var snapshot = await fixture.Forms.DescribeAsync();
            var view = Assert.Single(snapshot.Value.Namespaces);
            var value = view.Value.GetProperty("section");
            var serialized = value.GetRawText();
            Assert.DoesNotContain("root-secret", serialized);
            Assert.DoesNotContain("tail-secret", serialized);
            Assert.DoesNotContain("must-not-leak", serialized);
            if (scenario == "finite")
            {
                Assert.Equal("one", value.GetProperty("value").GetString());
                Assert.Equal("last", value.GetProperty("next").GetProperty("value").GetString());
            }

            if (scenario == "missing")
                Assert.False(value.TryGetProperty("next", out _));
            if (scenario == "null")
                Assert.Equal(JsonValueKind.Null, value.GetProperty("next").ValueKind);
            if (scenario is "lazy-cycle" or "union-cycle" or "union-rebuild-cycle")
            {
                Assert.False(value.TryGetProperty("cycle", out _));
                Assert.Contains(
                    snapshot.Diagnostics,
                    diagnostic => diagnostic.Contains("redaction", StringComparison.Ordinal));
            }

            return;
        }

        var dotnet = Path.GetFullPath(
            Path.Combine(
                RuntimeEnvironment.GetRuntimeDirectory(),
                "../../..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(typeof(ConfigurationEditTests).Assembly.Location);
        start.ArgumentList.Add(
            "--TestCaseFilter:FullyQualifiedName~Recursive_settings_redaction_terminates_in_an_isolated_testhost");
        start.Environment[marker] = scenario;
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await child.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            throw;
        }

        Assert.True(
            child.ExitCode == 0,
            $"recursive probe {scenario} exited {child.ExitCode}\n{await stdout}\n{await stderr}");
    }

    [Fact]
    public async Task Secret_redaction_is_conservative_across_union_branches_and_preserves_other_public_values()
    {
        var branch = ConfigDescriptor.Union(
            ConfigDescriptor.Object(
                ("password", ConfigDescriptor
                    .String()
                    .WithMetadata(
                        new()
                        {
                            Role = "secret"
                        })),
                ("first", ConfigDescriptor.String())),
            ConfigDescriptor.Object(("password", ConfigDescriptor.String()), ("second", ConfigDescriptor.String())));
        var description = ConfigDescriptor.Object(("choice", branch)).Volatile();
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            description: description,
            baseConfig: new EntryOptions
            {
                ["choice"] = new EntryOptions
                {
                    ["password"] = null,
                    ["first"] = "one",
                    ["second"] = "two"
                }
            });
        fixture.Policy = new(["choice"]);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        var value = view.Value.GetProperty("choice");
        Assert.Equal("one", value.GetProperty("first").GetString());
        Assert.Equal("two", value.GetProperty("second").GetString());
        Assert.False(value.TryGetProperty("password", out _));
        Assert.True(Assert.Single(view.Secrets).Set);
    }

    [Fact]
    public async Task Nested_live_settings_exclude_ordinary_siblings_and_authorize_each_exact_boundary()
    {
        var descriptor = ConfigDescriptor.Object(
            ("section", ConfigDescriptor.Object(
                ("live", ConfigDescriptor.Number().Volatile()),
                ("ordinary", ConfigDescriptor.String()))));
        var config = new EntryOptions
        {
            ["section"] = new EntryOptions
            {
                ["live"] = 1,
                ["ordinary"] = "private-ordinary"
            }
        };
        var schema = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw), descriptor)
            .WithVolatile<object?>(
                ["section", "live"],
                raw =>
                    ((IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)raw!)["section"]!)[
                        "live"]);
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: config, schema: schema);
        fixture.Policy = new(["section"]);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        var section = view.Value.GetProperty("section");
        Assert.Equal(new[] { "live" }, section.EnumerateObject().Select(field => field.Name));
        Assert.Equal(1, section.GetProperty("live").GetInt32());
        Assert.DoesNotContain("ordinary", view.Schema.GetRawText());
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set(["section", "ordinary"], "changed")],
            view.Revision));
        Assert.Contains("field-not-offered", refusal.Message);
        refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [
                Set(
                    ["section"],
                    new EntryOptions
                    {
                        ["live"] = 2
                    })
            ],
            view.Revision));
        Assert.Contains("field-not-offered", refusal.Message);
        await fixture.Forms.MutateAsync("worker", [Set(["section", "live"], 2)], view.Revision);
        var result = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(2, result.Value.GetProperty("section").GetProperty("live").GetInt32());
        Assert.Equal(1, fixture.Activations);
        var retained = (IReadOnlyDictionary<string, object?>)fixture.Loader.Resolve("root:worker").Options.Config!;
        Assert.Equal("private-ordinary", ((IReadOnlyDictionary<string, object?>)retained["section"]!)["ordinary"]);
    }

    [Fact]
    public async Task Settings_redact_nested_secret_slots_in_objects_arrays_dicts_and_reject_ancestor_replacements()
    {
        var secret = ConfigDescriptor
            .String()
            .Default("default-secret")
            .WithMetadata(
                new()
                {
                    Role = "secret"
                });
        var description = ConfigDescriptor
            .Object(
                ("section", ConfigDescriptor.Object(
                    ("public", ConfigDescriptor.String()),
                    ("password", secret),
                    ("hidden", ConfigDescriptor
                        .String()
                        .WithMetadata(
                            new()
                            {
                                Hidden = true
                            })),
                    ("items", ConfigDescriptor.Array(
                        ConfigDescriptor.Object(
                            ("token", secret),
                            ("label", ConfigDescriptor.String()),
                            ("hiddenNested", ConfigDescriptor
                                .String()
                                .WithMetadata(
                                    new()
                                    {
                                        Hidden = true
                                    }))))),
                    ("accounts", ConfigDescriptor.Dict(secret)))))
            .Volatile();
        var config = new EntryOptions
        {
            ["section"] = new EntryOptions
            {
                ["public"] = "visible",
                ["password"] = "saved-secret",
                ["hidden"] = "hidden-secret",
                ["items"] = new List<object?>
                {
                    new EntryOptions
                    {
                        ["token"] = "array-secret",
                        ["label"] = "item",
                        ["hiddenNested"] = "hidden-array-secret"
                    }
                },
                ["accounts"] = new EntryOptions
                {
                    ["alice"] = "dict-secret"
                },
            }
        };
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            baseConfig: config,
            description: description,
            source:
            "- id: worker\n  config:\n    section:\n      public: visible\n      password: saved-secret\n      hidden: hidden-secret\n      items:\n        - token: array-user-secret\n          label: item\n          hiddenNested: hidden-user-secret\n      accounts: { alice: dict-secret }\n");
        fixture.Policy = new(["section"]);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        var value = view.Value.GetProperty("section");
        Assert.False(value.TryGetProperty("password", out _));
        Assert.False(value.TryGetProperty("hidden", out _));
        var item = Assert.Single(value.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "label" }, item.EnumerateObject().Select(field => field.Name));
        Assert.Equal(
            new[] { "section/password", "section/items/0/token", "section/accounts/alice" },
            view.Secrets.Select(slot => string.Join("/", slot.Path)));
        Assert.All(view.Secrets, slot => Assert.True(slot.Set));
        Assert.DoesNotContain("default-secret", view.Schema.GetRawText());
        Assert.DoesNotContain("hidden", view.Schema.GetRawText());
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [
                Set(
                    ["section"],
                    new EntryOptions
                    {
                        ["public"] = "replacement"
                    })
            ],
            view.Revision));
        Assert.Contains("field-not-offered", rejected.Message);
        rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set(["section", "hidden"], "replacement")],
            view.Revision));
        Assert.Contains("field-not-offered", rejected.Message);
        await fixture.Forms.MutateAsync(
            "worker",
            [Set(["section", "password"], "new-secret")],
            view.Revision);
        var changed = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.True(changed.Revision > view.Revision);
        view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.All(view.Secrets, slot => Assert.True(slot.Set));
        Assert.DoesNotContain("new-secret", view.Value.GetRawText());
        Assert.Equal(1, fixture.Activations);
        var saved = await File.ReadAllTextAsync(fixture.Patch);
        foreach (var reset in new Func<Task>[]
                 {
                     () => fixture.Forms.ReplaceAsync("worker", Json("{}")),
                     () => fixture.Forms.MutateAsync("worker", [new SettingsPathOp("unset", [])])
                 })
        {
            rejected = await Assert.ThrowsAsync<InvalidOperationException>(reset);
            Assert.Contains("field-not-offered", rejected.Message);
            Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
        }
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
            return values.Item1 == values.Item2
                ? ConfigResult<object?>.Success(raw)
                : ConfigResult<object?>.Failure("pair must agree");
        }

        await using var fixture = await Fixture.StartAsync(
            whole: true,
            baseConfig: CompositeConfig(),
            description: CompositeDescription(),
            validate: Validate);
        fixture.Policy = new(["pair", "items", "credential"], ["credential"]);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(new[] { "pair", "items" }, view.Value.EnumerateObject().Select(field => field.Name));
        candidates.Clear();
        await fixture.Forms.MutateAsync(
            "worker",
            [Set(["pair", "left"], 2), Set(["pair", "right"], 2)],
            view.Revision);
        var result = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(2, result.Value.GetProperty("pair").GetProperty("left").GetInt32());
        Assert.NotEmpty(candidates);
        Assert.All(candidates, candidate => Assert.Equal(candidate.Left, candidate.Right));
        Assert.Equal(1, fixture.Activations);
        var saved = await File.ReadAllTextAsync(fixture.Patch);
        view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set(["pair", "left"], 3), Set(["pair", "right"], 4)],
            view.Revision));
        Assert.Contains("pair must agree", refused.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
        refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set(["pair", "left"], 3), Set(["credential"], "leak")],
            view.Revision));
        Assert.Contains("field-not-offered", refused.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
        refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set([], CompositeConfig())],
            view.Revision));
        Assert.Contains("field-not-offered", refused.Message);
        refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [new SettingsPathOp("set", ["pair", "left"])],
            view.Revision));
        Assert.Contains("invalid-settings-operation", refused.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
        refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.UpdateAsync(
            "worker",
            Json("""{"pair":{"left":8}}""")));
        Assert.Contains("pair must agree", refused.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
        refused = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set([], null)]));
        Assert.Contains("settings-object-required", refused.Message);
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task Dynamic_live_paths_build_objects_from_scalars_and_preserve_json_keys()
    {
        var descriptor = ConfigDescriptor.Object(
            ("dynamic", ConfigDescriptor.Any().Volatile()),
            ("choice",
                ConfigDescriptor
                    .Union(ConfigDescriptor.String(), ConfigDescriptor.Object(("declared", ConfigDescriptor.String())))
                    .Volatile()),
            ("ordinary", ConfigDescriptor.String()));
        var schema = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw), descriptor)
            .WithVolatile<object?>(["dynamic"], raw => ((IReadOnlyDictionary<string, object?>)raw!)["dynamic"])
            .WithVolatile<object?>(["choice"], raw => ((IReadOnlyDictionary<string, object?>)raw!)["choice"])
            .WithSimplify(raw => raw);
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            schema: schema,
            baseConfig: new EntryOptions
            {
                ["dynamic"] = 1,
                ["choice"] = "initial",
                ["ordinary"] = "retained"
            });
        var before = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        await fixture.Forms.MutateAsync(
            "worker",
            [Set(["dynamic", "", " "], "accepted"), Set(["choice", "newField"], true)],
            before.Revision);
        var after = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal("accepted", after.Value.GetProperty("dynamic").GetProperty("").GetProperty(" ").GetString());
        Assert.True(after.Value.GetProperty("choice").GetProperty("newField").GetBoolean());
        var raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal("retained", raw.Raw["ordinary"]);
        Assert.Equal(1, fixture.Activations);
        var saved = await File.ReadAllTextAsync(fixture.Patch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [
                new SettingsPathOp("set", ["dynamic"])
                {
                    Value = Json("""{"__jsExpr":"untrusted"}""")
                }
            ]));
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task Settings_replace_removes_unknown_live_members_and_retains_hidden_and_ordinary_fields()
    {
        var descriptor = ConfigDescriptor.Object(
            ("section", ConfigDescriptor
                .Object(
                    ("label", ConfigDescriptor.String()),
                    ("hidden", ConfigDescriptor
                        .String()
                        .WithAnnotations(
                            new Dictionary<string, object?>
                            {
                                ["hidden"] = true
                            })))
                .Volatile()),
            ("ordinary", ConfigDescriptor.String()));
        var schema = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw), descriptor)
            .WithVolatile<object?>(["section"], raw => ((IReadOnlyDictionary<string, object?>)raw!)["section"])
            .WithSimplify(raw => raw);
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            schema: schema,
            baseConfig: new EntryOptions
            {
                ["section"] = new EntryOptions
                {
                    ["label"] = "inherited",
                    ["hidden"] = "kept-hidden",
                    ["unknown"] = "remove-live"
                },
                ["ordinary"] = "kept-ordinary",
                ["unknownOutsideLive"] = "kept-outside"
            });
        var before = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.False(before.Value.GetProperty("section").TryGetProperty("hidden", out _));
        await fixture.Forms.ReplaceAsync("worker", Json("""{"section":{"label":"edited"}}"""), before.Revision);
        var after = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal("edited", after.Value.GetProperty("section").GetProperty("label").GetString());
        Assert.False(after.Value.GetProperty("section").TryGetProperty("hidden", out _));
        var raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var section = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(raw.Raw["section"]);
        Assert.False(section.ContainsKey("unknown"));
        Assert.Equal("kept-hidden", section["hidden"]);
        Assert.Equal("kept-ordinary", raw.Raw["ordinary"]);
        Assert.Equal("kept-outside", raw.Raw["unknownOutsideLive"]);
    }

    [Fact]
    public async Task Settings_base_uses_validated_live_projection_independently_of_persistence_simplification()
    {
        var descriptor = ConfigDescriptor.Object(("count", ConfigDescriptor.Number().Volatile()));
        var schema = new ConfigSchema<object?>(
                raw => ConfigResult<object?>.Success(
                    new EntryOptions
                    {
                        ["count"] = Convert.ToInt32(((IReadOnlyDictionary<string, object?>)raw!)["count"]) + 100
                    }),
                descriptor)
            .WithVolatile<int>(
                ["count"],
                value => Convert.ToInt32(((IReadOnlyDictionary<string, object?>)value!)["count"]))
            .WithSimplify(value => new EntryOptions
            {
                ["count"] = Convert.ToInt32(((IReadOnlyDictionary<string, object?>)value!)["count"]) - 100
            });
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            schema: schema,
            source: "- id: worker\n  config: { count: 2 }\n",
            baseConfig: new EntryOptions
            {
                ["count"] = 1
            });
        var saved = await File.ReadAllTextAsync(fixture.Patch);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(102, view.Value.GetProperty("count").GetInt32());
        Assert.Equal(101, view.Base!.Value.GetProperty("count").GetInt32());
        Assert.Equal(2, view.User!.Value.GetProperty("count").GetInt32());
        Assert.Equal(saved, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task Settings_update_replace_and_secret_parent_edits_preserve_ordinary_configuration()
    {
        var descriptor = ConfigDescriptor.Object(
            ("section", ConfigDescriptor
                .Object(
                    ("label", ConfigDescriptor.String()),
                    ("secret", ConfigDescriptor
                        .String()
                        .WithMetadata(
                            new()
                            {
                                Role = "secret"
                            })))
                .Volatile()),
            ("ordinary", ConfigDescriptor.String()));
        var config = new EntryOptions
        {
            ["section"] = new EntryOptions
            {
                ["label"] = "inherited",
                ["secret"] = "inherited-secret"
            },
            ["ordinary"] = "keep-ordinary"
        };
        var schema = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw), descriptor)
            .WithVolatile<object?>(["section"], raw => ((IReadOnlyDictionary<string, object?>)raw!)["section"]);
        await using var fixture = await Fixture.StartAsync(whole: true, baseConfig: config, schema: schema);
        fixture.Policy = new(["section", "ordinary"]);
        var initial = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(0, initial.Revision);
        await fixture.Forms.UpdateAsync(
            "worker",
            Json("""{"section":{"label":"merged"}}"""),
            initial.Revision);
        var changed = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal("merged", changed.Value.GetProperty("section").GetProperty("label").GetString());
        var raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal("inherited-secret", ((IReadOnlyDictionary<string, object?>)raw.Raw["section"]!)["secret"]);
        await fixture.Forms.MutateAsync(
            "worker",
            [
                Set(
                    ["section"],
                    new EntryOptions
                    {
                        ["label"] = "replaced-parent",
                        ["secret"] = "written-secret"
                    })
            ],
            changed.Revision);
        changed = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.DoesNotContain("written-secret", changed.Value.GetRawText());
        raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal("written-secret", ((IReadOnlyDictionary<string, object?>)raw.Raw["section"]!)["secret"]);
        Assert.Equal("keep-ordinary", raw.Raw["ordinary"]);
        await fixture.Forms.ReplaceAsync("worker", Json("{}"), changed.Revision);
        changed = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal("inherited", changed.Value.GetProperty("section").GetProperty("label").GetString());
        raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal("inherited-secret", ((IReadOnlyDictionary<string, object?>)raw.Raw["section"]!)["secret"]);
        Assert.Equal("keep-ordinary", raw.Raw["ordinary"]);
        Assert.Equal(1, fixture.Activations);
    }

    [Fact]
    public async Task Ordered_array_edits_and_object_resets_restore_source_inheritance()
    {
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            baseConfig: CompositeConfig(),
            description: CompositeDescription());
        var view = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.MutateConfigurationAsync(
            "root:worker",
            [
                new ConfigurationSet(["pair", "left"], 9),
                new ConfigurationSet(["items", "2"], "c"),
                new ConfigurationUnset(["items", "0"])
            ],
            view.Revision,
            liveOnly: true);
        Assert.True(result.Applied, result.Diagnostic);
        view = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal(new object?[] { "b", "c" }, Assert.IsAssignableFrom<IEnumerable<object?>>(view.Raw["items"]));
        result = await fixture.Operations.MutateConfigurationAsync(
            "root:worker",
            [new ConfigurationUnset(["pair", "left"]), new ConfigurationUnset(["items"])],
            view.Revision,
            liveOnly: true);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Empty(ConfigurationFile.ParseEntries(await File.ReadAllTextAsync(fixture.Patch)));
        var inherited = CompositeConfig();
        ((EntryOptions)inherited["pair"]!)["left"] = 5;
        await File.WriteAllTextAsync(
            fixture.Include.Filename,
            ConfigurationFile.Write(
                new[]
                {
                    new EntryOptions
                    {
                        Id = "worker",
                        Name = "worker",
                        Config = inherited
                    }
                }));
        await fixture.Loader.Context.RunAsync(async _ =>
        {
            await fixture.Include.RefreshAsync();
            await fixture.Loader.WaitAsync();
        });
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
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            baseConfig: CompositeConfig(),
            description: CompositeDescription());
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var source = await File.ReadAllTextAsync(fixture.Patch);
        ConfigurationPathOperation invalid =
            unset ? new ConfigurationUnset(["items", index]) : new ConfigurationSet(["items", index], "bad");
        var result = await fixture.Operations.MutateConfigurationAsync(
            "root:worker",
            [new ConfigurationSet(["pair", "left"], 9), invalid],
            before.Revision);
        Assert.Equal("invalid-array-index", result.Error);
        Assert.Equal(source, await File.ReadAllTextAsync(fixture.Patch));
        Assert.Equal(before.Revision, (await fixture.Operations.ReadConfigurationAsync("root:worker")).Revision);
    }

    private static EntryOptions CompositeConfig() =>
        new()
        {
            ["pair"] = new EntryOptions
            {
                ["left"] = 1,
                ["right"] = 1
            },
            ["items"] = new List<object?>
            {
                "a",
                "b"
            },
            ["credential"] = "private",
        };

    private static ConfigDescriptor CompositeDescription() =>
        ConfigDescriptor
            .Object(
                ("pair",
                    ConfigDescriptor.Object(("left", ConfigDescriptor.Number()), ("right", ConfigDescriptor.Number()))),
                ("items", ConfigDescriptor.Array(ConfigDescriptor.String())),
                ("credential", ConfigDescriptor.String()))
            .Volatile();

    [Fact]
    public async Task Secret_object_descendants_remain_editable_and_reset_to_inheritance_without_leaking_values()
    {
        var descriptor = ConfigDescriptor.Object(
            ("credentials", ConfigDescriptor
                .Object(("token", ConfigDescriptor.String()))
                .WithMetadata(
                    new()
                    {
                        Role = "secret"
                    })
                .Volatile()),
            ("label", ConfigDescriptor.String().Volatile()));
        var schema = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw), descriptor)
            .WithVolatile<object?>(["credentials"], raw => ((IReadOnlyDictionary<string, object?>)raw!)["credentials"])
            .WithVolatile<string>(["label"], raw => (string)((IReadOnlyDictionary<string, object?>)raw!)["label"]!);
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            schema: schema,
            baseConfig: new EntryOptions
            {
                ["credentials"] = new EntryOptions
                {
                    ["token"] = "inherited-token"
                },
                ["label"] = "public"
            });
        fixture.Policy = new(["credentials", "label"]);
        var initial = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.False(initial.Value.TryGetProperty("credentials", out _));
        Assert.Equal(new[] { "credentials" }, Assert.Single(initial.Secrets).Path);
        Assert.True(initial.Secrets[0].Set);
        await fixture.Forms.MutateAsync(
            "worker",
            [Set(["credentials", "token"], "written-token")],
            initial.Revision);
        var changed = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.False(changed.Value.TryGetProperty("credentials", out _));
        Assert.True(Assert.Single(changed.Secrets).Set);
        var raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal("written-token", ((IReadOnlyDictionary<string, object?>)raw.Raw["credentials"]!)["token"]);
        await fixture.Forms.MutateAsync(
            "worker",
            [new SettingsPathOp("unset", ["credentials", "token"])],
            changed.Revision);
        changed = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.False(changed.Value.TryGetProperty("credentials", out _));
        raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.Equal("inherited-token", ((IReadOnlyDictionary<string, object?>)raw.Raw["credentials"]!)["token"]);
        Assert.DoesNotContain("written-token", await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task
        Settings_schema_keeps_public_defaults_and_fresh_path_wrappers_while_mutations_use_original_defaults()
    {
        var liveDefault = new EntryOptions
        {
            ["public"] = "public-default",
            ["number"] = 123456789,
            ["secret"] = "secret-default",
            ["requiredSecret"] = "required-secret-value"
        };
        var parentDefault = new EntryOptions
        {
            ["live"] = liveDefault,
            ["ordinary"] = "ordinary-from-original-default"
        };
        var secret = ConfigDescriptor
            .String()
            .WithMetadata(
                new()
                {
                    Role = "secret"
                });
        var description = ConfigDescriptor.Object(
            ("container", ConfigDescriptor
                .Object(
                    ("live", ConfigDescriptor
                        .Object(
                            ("public", ConfigDescriptor.String().Default("public-default")),
                            ("number", ConfigDescriptor.Number().Default(123456789)),
                            ("secret", secret.Default("secret-default")),
                            ("requiredSecret", secret))
                        .Default(liveDefault)
                        .Volatile()),
                    ("ordinary", ConfigDescriptor.String()))
                .Optional()
                .Default(parentDefault)
                .WithMetadata(
                    new()
                    {
                        Role = "secret",
                        Description = "ordinary-parent-description"
                    })));
        var schema = new ConfigSchema<object?>(
            raw =>
            {
                var input = (IReadOnlyDictionary<string, object?>)raw!;
                return ConfigResult<object?>.Success(
                    new EntryOptions
                    {
                        ["container"] = input.GetValueOrDefault("container") ?? parentDefault
                    });
            },
            description).WithVolatile<object?>(
            ["container", "live"],
            value =>
                ((IReadOnlyDictionary<string, object?>)((IReadOnlyDictionary<string, object?>)value!)["container"]!)
                ["live"]);
        await using var fixture = await Fixture.StartAsync(whole: true, schema: schema, baseConfig: new EntryOptions());
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        var publicMeta = SettingsSchemaNode(view.Schema, "container", "live", "public").GetProperty("meta");
        Assert.True(
            publicMeta.TryGetProperty("default", out var publicDefault),
            "The declared public form default was omitted.");
        Assert.Equal("public-default", publicDefault.GetString());
        Assert.Equal(
            123456789,
            SettingsSchemaNode(view.Schema, "container", "live", "number")
                .GetProperty("meta")
                .GetProperty("default")
                .GetInt32());
        foreach (var field in new[] { "secret", "requiredSecret" })
        {
            var metadata = SettingsSchemaNode(view.Schema, "container", "live", field).GetProperty("meta");
            Assert.Equal("secret", metadata.GetProperty("role").GetString());
            Assert.False(metadata.TryGetProperty("default", out _));
            Assert.False(metadata.TryGetProperty("required", out _));
        }

        var wrapper = SettingsSchemaNode(view.Schema, "container");
        Assert.Equal("object", wrapper.GetProperty("type").GetString());
        var wrapperMeta = wrapper.GetProperty("meta");
        Assert.False(wrapperMeta.TryGetProperty("role", out _));
        Assert.False(wrapperMeta.TryGetProperty("description", out _));
        Assert.False(wrapperMeta.GetProperty("required").GetBoolean());
        Assert.Empty(wrapperMeta.GetProperty("default").EnumerateObject());
        Assert.False(wrapper.GetProperty("dict").TryGetProperty("ordinary", out _));
        var publicValue = view.Value.GetProperty("container").GetProperty("live");
        Assert.Equal("public-default", publicValue.GetProperty("public").GetString());
        Assert.False(publicValue.TryGetProperty("secret", out _));
        Assert.False(publicValue.TryGetProperty("requiredSecret", out _));
        Assert.All(view.Secrets, slot => Assert.True(slot.Set));
        Assert.DoesNotContain("secret-default", view.Schema.GetRawText());
        Assert.DoesNotContain("required-secret-value", view.Schema.GetRawText());
        Assert.DoesNotContain("ordinary-parent-description", view.Schema.GetRawText());
        var full = await fixture.Operations.ReadConfigurationSchemasAsync("root:worker");
        Assert.Contains("ordinary-parent-description", full.Schemastery.Document);
        Assert.Contains("secret-default", full.Schemastery.Document);
        await fixture.Forms.MutateAsync("worker", [Set(["container", "live", "public"], "edited")], view.Revision);
        var raw = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var container = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(raw.Raw["container"]);
        Assert.Equal("ordinary-from-original-default", container["ordinary"]);
        var live = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(container["live"]);
        Assert.Equal("edited", live["public"]);
        Assert.Equal(123456789, Convert.ToInt32(live["number"]));
        Assert.Equal("secret-default", live["secret"]);
        Assert.Equal(1, fixture.Activations);
    }

    [Fact]
    public async Task Whole_live_settings_root_preserves_public_metadata_and_defaults_with_field_selection()
    {
        var description = ConfigDescriptor
            .Object(
                ("public", ConfigDescriptor.String().Default("field-public-default")),
                ("secret", ConfigDescriptor
                    .String()
                    .Default("field-secret-default")
                    .WithMetadata(
                        new()
                        {
                            Role = "secret"
                        })),
                ("excluded", ConfigDescriptor.String()))
            .Volatile()
            .Optional()
            .Default(
                new EntryOptions
                {
                    ["public"] = "root-public-default",
                    ["secret"] = "root-secret-default",
                    ["excluded"] = "excluded-default-value"
                })
            .WithMetadata(
                new()
                {
                    Description = "whole-live-description"
                });
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            description: description,
            baseConfig: new EntryOptions
            {
                ["public"] = "visible",
                ["secret"] = "written-secret",
                ["excluded"] = "not-offered"
            });
        fixture.Policy = new(["public", "secret"]);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        var root = SettingsSchemaNode(view.Schema);
        var meta = root.GetProperty("meta");
        Assert.True(
            meta.TryGetProperty("description", out var descriptionValue),
            "The whole live root description was omitted.");
        Assert.Equal("whole-live-description", descriptionValue.GetString());
        Assert.False(meta.GetProperty("required").GetBoolean());
        var defaults = meta.GetProperty("default");
        Assert.Equal("root-public-default", defaults.GetProperty("public").GetString());
        Assert.False(defaults.TryGetProperty("secret", out _));
        Assert.False(defaults.TryGetProperty("excluded", out _));
        Assert.Equal(
            "field-public-default",
            SettingsSchemaNode(view.Schema, "public").GetProperty("meta").GetProperty("default").GetString());
        Assert.False(root.GetProperty("dict").TryGetProperty("excluded", out _));
        Assert.Equal("visible", view.Value.GetProperty("public").GetString());
        Assert.False(view.Value.TryGetProperty("secret", out _));
        Assert.DoesNotContain("field-secret-default", view.Schema.GetRawText());
        Assert.DoesNotContain("root-secret-default", view.Schema.GetRawText());
        Assert.DoesNotContain("excluded-default-value", view.Schema.GetRawText());
    }

    [Fact]
    public async Task A_secret_live_root_is_omitted_with_a_native_diagnostic_instead_of_leaking_or_failing_describe()
    {
        const string secretValue = "whole-root-secret-must-not-escape";
        var description = ConfigDescriptor
            .Object(("token", ConfigDescriptor.String()))
            .Volatile()
            .WithMetadata(
                new()
                {
                    Role = "secret"
                });
        await using var fixture = await Fixture.StartAsync(
            whole: true,
            description: description,
            baseConfig: new EntryOptions
            {
                ["token"] = secretValue
            });
        Assert.Equal(FiberState.Active, fixture.Loader.Resolve("root:worker").Fiber!.State);
        var snapshot = await fixture.Forms.DescribeAsync();
        Assert.Empty(snapshot.Value.Namespaces);
        Assert.True(snapshot.Value.Writable);
        Assert.Contains(snapshot.Diagnostics, diagnostic => diagnostic.Contains("worker", StringComparison.Ordinal));
        Assert.DoesNotContain(secretValue, string.Join("\n", snapshot.Diagnostics));
    }

    [Theory]
    [InlineData("# retained\n[]\n")]
    [InlineData("# retained\n- id: worker\n  config: { limit: 1, label: worker }\n")]
    [InlineData("# retained\n- id: worker\n  config:\n    limit: 1\n    label: worker\n")]
    public async Task Field_source_retains_comments_and_parses_after_edit(string source)
    {
        await using var fixture = await Fixture.StartAsync(source);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["limit"],
            2,
            snapshot.Revision);
        Assert.True(result.Saved, result.Diagnostic);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        Assert.Contains("# retained", text);
        Assert.Single(ConfigurationFile.ParseEntries(text));
    }

    [Fact]
    public async Task Flow_root_edit_preserves_unrelated_source_comments_and_expression()
    {
        const string source =
            "[\n {id: worker, config: {limit: 1, label: worker}},\n # unrelated documentation\n {id: absent, config: !!js 123}\n]\n";
        await using var fixture = await Fixture.StartAsync(source);
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var changed =
            await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, before.Revision);
        Assert.True(changed.Applied, changed.Diagnostic);
        Assert.Contains(
            "# unrelated documentation\n {id: absent, config: !!js 123}",
            await File.ReadAllTextAsync(fixture.Patch));
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
        var result = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["limit"],
            1,
            snapshot.Revision);
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
        fixture.Policy = new(["limit"]);
        var before = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(2, before.Value.GetProperty("limit").GetInt32());
        Assert.True(before.User!.Value.TryGetProperty("limit", out _));
        var fiber = fixture.Loader.Resolve("root:worker").Fiber;
        var configuration = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var result = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["limit"],
            1,
            configuration.Revision);
        Assert.True(result.Saved && result.Applied, result.Diagnostic);
        var view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(1, view.Value.GetProperty("limit").GetInt32());
        var text = await File.ReadAllTextAsync(fixture.Patch);
        Assert.False(view.User!.Value.TryGetProperty("limit", out _), text);
        var rows = ConfigurationFile.ParseEntries(text);
        var retained = Assert.Single(rows, row => row.Id == "worker");
        Assert.False(retained.ContainsKey("config"));
        Assert.Equal(ConfigurationFile.Parse(insertion), retained["insert"]);
        Assert.Equal("keep", retained["note"]);
        Assert.Equal(2, rows.Count);
        Assert.Contains("# retained", text);
        Assert.Contains("# unrelated documentation", text);
        Assert.Contains("config: !!js throw new Error('must not run')", text);

        await File.WriteAllTextAsync(
            fixture.Include.Filename,
            "- id: worker\n  name: worker\n  config: { limit: 3, label: worker }\n");
        await fixture.Loader.Context.RunAsync(async _ =>
        {
            await fixture.Include.RefreshAsync();
            await fixture.Loader.WaitAsync();
        });
        view = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(3, view.Value.GetProperty("limit").GetInt32());
        Assert.False(view.User!.Value.TryGetProperty("limit", out _));
        Assert.Same(fiber, fixture.Loader.Resolve("root:worker").Fiber);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task User_layer_insert_is_editable_and_unrelated_expression_remains_opaque()
    {
        const string source =
            "# retained\n- insert:\n  - id: worker\n    name: worker\n    config: { limit: 1, label: worker }\n- id: absent\n  insert: null\n  config: !!js throw new Error('must not run')\n";
        await using var fixture = await Fixture.StartAsync(source, inserted: true);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        fixture.Policy = new(["limit"]);
        var insertedView = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.True(insertedView.User!.Value.TryGetProperty("limit", out _));
        var result = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["limit"],
            2,
            snapshot.Revision);
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
        var result = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["limit"],
            2,
            snapshot.Revision,
            liveOnly: true);
        Assert.True(result.Applied, result.Diagnostic);
        Assert.Equal(1, fixture.Activations);
        Assert.Equal(
            2,
            Convert.ToInt32(
                fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues[""] is
                    IReadOnlyDictionary<string, object?> map
                    ? map["limit"]
                    : null));
    }

    [Fact]
    public async Task Raw_edit_distinguishes_null_from_non_finite_and_refuses_undefined()
    {
        await using var fixture = await Fixture.StartAsync(whole: true);
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var changed = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["extra"],
            double.NaN,
            before.Revision);
        Assert.True(changed.Applied, changed.Diagnostic);
        var nonFinite = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.True(double.IsNaN((double)nonFinite.Raw["extra"]!));
        changed = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["extra"],
            null,
            nonFinite.Revision);
        Assert.True(changed.Applied, changed.Diagnostic);
        var nullable = await fixture.Operations.ReadConfigurationAsync("root:worker");
        Assert.NotEqual(nonFinite.Revision, nullable.Revision);
        Assert.True(nullable.Raw.ContainsKey("extra"));
        Assert.Null(nullable.Raw["extra"]);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        changed = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["extra"],
            Undefined.Value,
            nullable.Revision);
        Assert.Equal("non-persistable-configuration", changed.Error);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
    }

    [Fact]
    public async Task Settings_presentation_notification_does_not_reenter_the_configuration_transaction()
    {
        await using var fixture = await Fixture.StartAsync();
        var before = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        var notification = new TaskCompletionSource<object?[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Operations.RunExclusiveAsync = async action =>
        {
            entered.TrySetResult();
            try
            {
                await release.Task;
                await action();
            }
            finally
            {
                exited.TrySetResult();
            }
        };
        try
        {
            await fixture.Include.Context.RunAsync(context =>
            {
                context.Events.On(
                    "settings/document-updated",
                    (_, args) =>
                    {
                        notification.TrySetResult(args);
                        return null;
                    });
                fixture.Forms.Configure(false, fixture.Loader.Resolve("root:worker").Fiber);
                return Task.CompletedTask;
            });
            var completed = await Task.WhenAny(notification.Task, entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(notification.Task, completed);
            var arguments = await notification.Task;
            Assert.Equal("worker", arguments[0]);
            Assert.Equal(before.Revision, Assert.IsType<double>(arguments[1]));
        }
        finally
        {
            release.TrySetResult();
            if (entered.Task.IsCompleted)
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Caller_owned_read_only_map_is_detached_before_queue_wait()
    {
        await using var fixture = await Fixture.StartAsync(whole: true);
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Operations.RunExclusiveAsync = async action =>
        {
            entered.SetResult();
            await release.Task;
            await action();
        };
        var backing = new Dictionary<string, object?>
        {
            ["value"] = "original"
        };
        var pending = fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["extra"],
            new ReadOnlyView(backing),
            before.Revision);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backing["value"] = "mutated";
        release.SetResult();
        var result = await pending;
        Assert.True(result.Applied, result.Diagnostic);
        var published = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
            fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues[""]);
        Assert.Equal(
            "original",
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(published["extra"])["value"]);
    }

    [Fact]
    public async Task Settings_read_and_submit_enforce_selection_redaction_and_live_only()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Policy = new(["limit", "label", "credential"], ["credential"]);
        var before = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(new[] { "limit" }, before.Value.EnumerateObject().Select(field => field.Name));
        Assert.Equal(1, before.Value.GetProperty("limit").GetInt32());
        Assert.False(before.User!.Value.TryGetProperty("limit", out _));
        var hidden = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set(["credential"], "must not save")],
            before.Revision));
        Assert.Contains("field-not-offered", hidden.Message);
        var ordinary = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set(["label"], "new")],
            before.Revision));
        Assert.Contains("field-not-offered", ordinary.Message);
        await fixture.Forms.MutateAsync("worker", [Set(["limit"], 2)], before.Revision);
        var after = Assert.Single((await fixture.Forms.DescribeAsync()).Value.Namespaces);
        Assert.Equal(2, after.Value.GetProperty("limit").GetInt32());
        Assert.True(after.User!.Value.TryGetProperty("limit", out _));
        Assert.Equal(1, fixture.Activations);
        await Assert.ThrowsAsync<SettingsConflictException>(() => fixture.Forms.MutateAsync(
            "worker",
            [Set(["limit"], 3)],
            before.Revision));
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
        var backing = new Dictionary<string, object?>
        {
            ["value"] = "original"
        };
        var config = new EntryOptions
        {
            ["limit"] = 1,
            ["label"] = "worker",
            ["extra"] = new ReadOnlyView(backing)
        };
        await using var fixture = await Fixture.StartAsync(
            overlays:
            [
                new(
                    "host",
                    [
                        new()
                        {
                            Id = "worker",
                            Config = config
                        }
                    ])
            ]);
        var snapshot = await fixture.Operations.ReadConfigurationAsync("root:worker");
        backing["value"] = "mutated";
        Assert.Equal(
            "original",
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot.Raw["extra"])["value"]);
    }

    [Fact]
    public async Task Field_edit_preserves_live_identity_validates_and_rejects_stale_revision()
    {
        await using var fixture = await Fixture.StartAsync();
        var before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var fiber = fixture.Loader.Resolve("root:worker").Fiber!;
        ConfigReference<int> reference = null!;
        await fixture.Loader.Context.RunAsync(_ =>
        {
            reference = fiber.GetConfigReference<int>("limit");
            return Task.CompletedTask;
        });
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
        var invalid = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["limit"],
            -1,
            current.Revision);
        Assert.Equal("invalid-configuration", invalid.Error);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
        Assert.Equal(2, reference.Value);

        result = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["label"],
            "batch",
            current.Revision);
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
        var failed = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["label"],
            "fail",
            before.Revision);
        Assert.False(failed.Saved);
        Assert.False(failed.Applied);
        Assert.True(failed.Error == "reconcile-failed", failed.Diagnostic);
        Assert.Contains("activation rejected", failed.Diagnostic);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));

        await File.WriteAllTextAsync(
            Path.Combine(fixture.Home, "cordis.patch.yml"),
            "- id: worker\n  config: { limit: 7, label: forced }\n");
        before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var overridden =
            await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 8, before.Revision);
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
        var changed =
            await fixture.Operations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, before.Revision);
        Assert.True(changed.Saved && changed.Applied, changed.Diagnostic);
        Assert.Equal(2, fixture.Loader.Resolve("root:worker").Fiber!.ConfigurationValues["limit"]);
        var text = await File.ReadAllTextAsync(fixture.Patch);
        before = await fixture.Operations.ReadConfigurationAsync("root:worker");
        var failed = await fixture.Operations.EditConfigurationFieldAsync(
            "root:worker",
            ["label"],
            "fail",
            before.Revision);
        Assert.Equal("reconcile-failed", failed.Error);
        Assert.Contains("activation rejected", failed.Diagnostic);
        Assert.False(failed.Saved);
        Assert.False(failed.Applied);
        Assert.Equal(text, await File.ReadAllTextAsync(fixture.Patch));
        Assert.Equal(2, notifications);
    }

    private sealed record Settings(int Limit, string Label);

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static JsonElement SettingsSchemaNode(JsonElement schema, params string[] path)
    {
        var refs = schema.GetProperty("refs");
        var node = refs.GetProperty(
            schema.GetProperty("uid").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var key in path)
            node = refs.GetProperty(
                node
                    .GetProperty("dict")
                    .GetProperty(key)
                    .GetInt32()
                    .ToString(System.Globalization.CultureInfo.InvariantCulture));
        return node;
    }

    private static SettingsPathOp Set(string[] path, object? value) => new("set", path)
    {
        Value = Json(ConfigurationFile.Write(value, true))
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(
            Path.GetTempPath(),
            "cordis-field-edit-" + Guid.NewGuid().ToString("N"));

        private readonly Context context = new();
        public string Home => Path.Combine(directory, "home");
        public string Patch => Path.Combine(directory, "cordis.patch.yml");

        public Loader Loader
        {
            get;
            private set;
        } = null!;

        public Include Include
        {
            get;
            private set;
        } = null!;

        public PluginConfigurationOperations Operations
        {
            get;
            private set;
        } = null!;

        public SettingsForms Forms
        {
            get;
            private set;
        } = null!;

        public SettingsPolicy? Policy
        {
            get;
            set;
        }

        public int Activations
        {
            get;
            private set;
        }

        public static async Task<Fixture> StartAsync(
            string source = "# retained\n[]\n",
            bool inserted = false,
            bool whole = false,
            IReadOnlyList<ConfigurationLayer>? overlays = null,
            EntryOptions? baseConfig = null,
            ConfigDescriptor? description = null,
            Func<object?, ConfigResult<object?>>? validate = null,
            ConfigSchema<object?>? schema = null)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.directory);
            Directory.CreateDirectory(fixture.Home);
            Profiles.Initialize(fixture.directory, []);
            await File.WriteAllTextAsync(fixture.Patch, source);
            var launch = new ProfileLaunch(
                await Profiles.LoadAsync(fixture.directory, new Dictionary<string, string>()),
                fixture.Home,
                overlays ?? [],
                new Dictionary<string, string>(),
                new Dictionary<string, string>());
            IPlugin plugin = new Plugin<Settings>
            {
                Configuration = ConfigObject<Settings>
                    .Create(raw =>
                        raw is IReadOnlyDictionary<string, object?> map &&
                        Convert.ToInt32(map.GetValueOrDefault("limit")) is var limit && limit > 0 &&
                        map.GetValueOrDefault("label") is string label
                            ? ConfigResult<Settings>.Success(new(limit, label))
                            : ConfigResult<Settings>.Failure("positive limit and label required"))
                    .Field("limit", ConfigDescriptor.Number().Volatile(), value => value.Limit)
                    .Field("label", ConfigDescriptor.String(), value => value.Label)
                    .Build(),
                Apply = (_, settings) =>
                {
                    if (settings.Label == "fail")
                        throw new InvalidOperationException("activation rejected");
                    fixture.Activations++;
                },
            };
            if (whole)
                plugin = new Plugin<object?>
                {
                    Configuration = schema ?? new ConfigSchema<object?>(
                        validate ?? (raw => ConfigResult<object?>.Success(raw)),
                        description ?? ConfigDescriptor
                            .Object(("limit", ConfigDescriptor.Number()), ("label", ConfigDescriptor.String()))
                            .Volatile()).WithVolatileValue(),
                    Apply = (_, _) => fixture.Activations++,
                };
            await fixture.context.RunAsync(async owner =>
            {
                fixture.Loader = new Loader(owner, new StaticModuleResolver().Register("worker", plugin));
                var basePath = Path.Combine(fixture.directory, "base.yml");
                await File.WriteAllTextAsync(
                    basePath,
                    inserted
                        ? "[]\n"
                        : ConfigurationFile.Write(
                            new[]
                            {
                                new EntryOptions
                                {
                                    Id = "worker",
                                    Name = "worker",
                                    Config = baseConfig ?? new EntryOptions
                                    {
                                        ["limit"] = 1,
                                        ["label"] = "worker"
                                    },
                                }
                            }));
                fixture.Include = await ApplicationBoot.MountAsync(fixture.Loader, basePath);
                await fixture.Loader.WaitAsync();
            });
            fixture.Operations = new(launch, fixture.Include)
            {
                RunExclusiveAsync = action => action()
            };
            await fixture.context.RunAsync(owner =>
            {
                fixture.Forms = new SettingsForms(
                    owner,
                    fixture.Operations,
                    _ => fixture.Policy ??
                        new SettingsPolicy(
                            fixture.Loader.Resolve("root:worker").Fiber!.ConfigDescription!.Properties.Keys));
                return Task.CompletedTask;
            });
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
}
