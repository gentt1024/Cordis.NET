using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void PatchesPreserveExactIdentityAndReplacementRules()
    {
        var original = new EntryOptions { Id = "old", Name = "plugin", Config = new EntryOptions { ["a"] = 1, ["b"] = 2 } };
        Assert.Same(original, EntryPatches.Apply([original], [])[0]);
        var inserted = new EntryOptions { Id = "new", Name = "plugin" };
        var replacement = new EntryOptions { ["only"] = true };
        var result = EntryPatches.Apply([original], [new() { ["insert"] = new[] { inserted } }, new() { Id = "new", Config = replacement }, new() { Id = "old", Name = "other", Config = false }]);
        Assert.NotSame(original, result[0]); Assert.Same(inserted, result[1]); Assert.Same(replacement, inserted.Config);
        Assert.Equal("plugin", result[0].Name); Assert.IsType<EntryOptions>(result[0].Config);
    }
    [Fact]
    public void PatchesTraverseOnlyMarkedGroupsAndWarnOnMisses()
    {
        var children = new[] { new EntryOptions { Id = "child", Name = "plugin" } };
        var warnings = new List<string>();
        var result = EntryPatches.Apply([new() { Id = "include", Name = "cordis:include", Config = children }, new() { Id = "group", Group = true, Config = new[] { new EntryOptions { Id = "inside" } } }], [new() { Id = "child", Disabled = true }, new() { Id = "inside", Disabled = true }, new() { Id = "missing", ["insert"] = children }], warnings.Add);
        Assert.Equal(2, warnings.Count); Assert.Null(children[0].Disabled); Assert.True((bool)Data.Entries(result[1].Config)[0].Disabled!);
    }
    [Fact]
    public void YamlRoundTripKeepsExpressionsNullAndQuotedScalars()
    {
        var data = ConfigurationFile.ParseEntries("- id: test\n  name: plugin\n  disabled: !!js service.ready\n  config: {quoted: 'true', yes: yes, value: null, number: 23}\n");
        Assert.IsType<JsExpression>(data[0].Disabled);
        var second = ConfigurationFile.ParseEntries(ConfigurationFile.Write(data));
        Assert.Equal(data[0].Disabled, second[0].Disabled); var map = Assert.IsType<EntryOptions>(second[0].Config);
        Assert.Equal("true", map["quoted"]); Assert.Equal("yes", map["yes"]); Assert.Null(map["value"]); Assert.Equal(23L, map["number"]);
        Assert.Throws<FormatException>(() => ConfigurationFile.ParseEntries(""));
    }
    [Fact]
    public void YamlJsonSchemaScalarsMatchJsonTypesAndValues()
    {
        var yaml = Assert.IsType<EntryOptions>(ConfigurationFile.Parse("bool: !!bool \"false\"\nint: !!int \"12\"\nhex: 0x10\nquotedHex: \"0x10\"\n"));
        var json = Assert.IsType<EntryOptions>(ConfigurationFile.Parse("{\"bool\":false,\"int\":12,\"hex\":16,\"quotedHex\":\"0x10\"}", true));

        foreach (var key in json.Keys)
        {
            Assert.Equal(json[key]?.GetType(), yaml[key]?.GetType());
            Assert.Equal(json[key], yaml[key]);
        }
        Assert.Throws<FormatException>(() => ConfigurationFile.Parse("value: !!bool invalid\n"));
    }

    [Fact]
    public void YamlWriterKeepsSchemaLookingStringsAsStrings()
    {
        var values = new EntryOptions
        {
            ["decimal"] = "12",
            ["hex"] = "0x10",
            ["boolean"] = "false",
            ["special"] = ".inf",
            ["null"] = "null"
        };

        var parsed = Assert.IsType<EntryOptions>(ConfigurationFile.Parse(ConfigurationFile.Write(values)));
        foreach (var pair in values)
        {
            Assert.IsType<string>(parsed[pair.Key]);
            Assert.Equal(pair.Value, parsed[pair.Key]);
        }
    }
    [Theory]
    [InlineData(".inf", double.PositiveInfinity)]
    [InlineData("-.inf", double.NegativeInfinity)]
    [InlineData(".nan", double.NaN)]
    public void YamlRoundTripKeepsNonFiniteNumbers(string scalar, double expected)
    {
        var first = Assert.IsType<EntryOptions>(ConfigurationFile.Parse($"value: {scalar}\n"));
        var original = Assert.IsType<double>(first["value"]);
        Assert.True(double.IsNaN(expected) ? double.IsNaN(original) : original.Equals(expected));

        var written = ConfigurationFile.Write(first);
        var second = Assert.IsType<EntryOptions>(ConfigurationFile.Parse(written));
        var actual = Assert.IsType<double>(second["value"]);
        Assert.True(double.IsNaN(expected) ? double.IsNaN(actual) : actual.Equals(expected));
    }
    [Fact]
    public void JsonWriterMatchesJsonStringifyForNonFiniteNumbers()
    {
        var value = new EntryOptions
        {
            ["positive"] = double.PositiveInfinity,
            ["negative"] = double.NegativeInfinity,
            ["nan"] = double.NaN,
        };

        Assert.Equal(
            """
            {
              "positive": null,
              "negative": null,
              "nan": null
            }

            """,
            ConfigurationFile.Write(value, json: true));
    }
    [Fact]
    public void JsonWriterKeepsArrayNestedAndFiniteValues()
    {
        var value = new EntryOptions
        {
            ["array"] = new List<object?> { double.PositiveInfinity, "finite", null, new EntryOptions { ["nested"] = double.NaN } },
            ["finite"] = 1.25,
            ["text"] = "unchanged",
            ["nil"] = null,
        };

        Assert.Equal(
            """
            {
              "array": [
                null,
                "finite",
                null,
                {
                  "nested": null
                }
              ],
              "finite": 1.25,
              "text": "unchanged",
              "nil": null
            }

            """,
            ConfigurationFile.Write(value, json: true));
    }
    [Fact]
    public void ProfilesComposeInOrderWithoutMutatingLayerInputs()
    {
        var row = new EntryOptions { Id = "row", Name = "p", Config = "bundle" };
        var layers = new[] { new ConfigurationLayer("bundle", [new() { ["insert"] = new[] { row } }]), new ConfigurationLayer("profile", [new() { Id = "row", Config = "profile" }]), new ConfigurationLayer("home", [new() { Id = "row", Config = "home" }]), new ConfigurationLayer("cli", [new() { Id = "row", Config = "cli" }]) };
        Assert.Equal("cli", Profiles.Compose(layers)[0].Config); Assert.Equal("bundle", row.Config);
    }
    [Theory][InlineData("")][InlineData("../x")][InlineData("node_modules")][InlineData("a/b")] public void InvalidProfileNamesFail(string name) => Assert.Throws<ArgumentException>(() => Profiles.ResolveDirectory("home", name));
}

public sealed class LoaderTests
{
    private static Plugin<object?> Noop() => new() { Name = "noop", Apply = (_, _) => { } };
    [Fact]
    public async Task ReplacementPreservesMissingAndExplicitNullConfigAcrossAllFibers()
    {
        await using var context = new Context();
        Loader loader = null!;
        var previous = ConfigAware("previous");
        var replacement = ConfigAware("replacement");
        await context.RunAsync(_ =>
        {
            loader = new Loader(context, new StaticModuleResolver().Register("plugin", previous));
            return Task.CompletedTask;
        });
        await loader.Root.UpdateAsync([new() { Id = "missing", Name = "plugin" }, new() { Id = "null", Name = "plugin", Config = null }]);
        await loader.WaitAsync();

        await loader.ReplacePluginAsync(previous, replacement);

        Assert.Same(Undefined.Value, loader.Resolve("missing").Fiber!.RawConfig);
        Assert.Equal("default", loader.Resolve("missing").Fiber!.Config);
        Assert.Null(loader.Resolve("null").Fiber!.RawConfig);
        Assert.Null(loader.Resolve("null").Fiber!.Config);
        Assert.Equal(2, context.Registry.Get(replacement)!.Fibers.Count);
    }

    [Fact]
    public async Task FailedReplacementRestoresMissingAndExplicitNullConfig()
    {
        await using var context = new Context();
        Loader loader = null!;
        var previous = ConfigAware("previous");
        var replacement = ConfigAware("replacement", failApply: true);
        await context.RunAsync(_ =>
        {
            loader = new Loader(context, new StaticModuleResolver().Register("plugin", previous));
            return Task.CompletedTask;
        });
        await loader.Root.UpdateAsync([new() { Id = "missing", Name = "plugin" }, new() { Id = "null", Name = "plugin", Config = null }]);
        await loader.WaitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.ReplacePluginAsync(previous, replacement));

        Assert.Same(Undefined.Value, loader.Resolve("missing").Fiber!.RawConfig);
        Assert.Equal("default", loader.Resolve("missing").Fiber!.Config);
        Assert.Null(loader.Resolve("null").Fiber!.RawConfig);
        Assert.Null(loader.Resolve("null").Fiber!.Config);
        Assert.Equal(2, context.Registry.Get(previous)!.Fibers.Count);
        Assert.Null(context.Registry.Get(replacement));
    }

    private static Plugin<object?> ConfigAware(string name, bool failApply = false) => new()
    {
        Name = name,
        Config = value => ReferenceEquals(value, Undefined.Value)
            ? ConfigResult<object?>.Success("default")
            : value is null
                ? ConfigResult<object?>.Success(null)
                : ConfigResult<object?>.Failure("unexpected config"),
        Apply = (_, _) => { if (failApply) throw new InvalidOperationException("candidate failed"); },
    };
    [Fact]
    public async Task EntryInjectStaysPendingAndExpressionsResolveAfterProvider()
    {
        await using var context = new Context(); Loader loader = null!; var received = new List<object?>();
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("consumer", new Plugin<object?> { Apply = (_, config) => received.Add(config) }), expressionEvaluator: new Evaluator()); return Task.CompletedTask; });
        var expression = new JsExpression("value");
        await loader.CreateAsync(new() { Id = "consumer", Name = "consumer", ["inject"] = new[] { "value" }, Config = expression }); await loader.WaitAsync();
        Assert.Equal(FiberState.Pending, loader.Resolve("consumer").Fiber!.State); Assert.Empty(received);
        await context.RunAsync(_ => { context.Provide("value", 42); return Task.CompletedTask; }); await loader.WaitAsync();
        Assert.Equal(42, Assert.Single(received)); Assert.Same(expression, loader.Resolve("consumer").Options.Config);
    }
    [Fact]
    public async Task GroupOwnsChildrenAndSharesIsolationRealm()
    {
        await using var context = new Context(); Loader loader = null!; object? seen = null;
        var modules = new StaticModuleResolver().Register("provider", new Plugin<object?> { Apply = (ctx, _) => ctx.Provide("service", "private") }).Register("consumer", new Plugin<object?> { Inject = ["service"], Apply = (ctx, _) => seen = ctx.Get("service") });
        await context.RunAsync(_ => { loader = new Loader(context, modules); return Task.CompletedTask; });
        await loader.CreateAsync(new() { Id = "group", Name = "cordis:group", Group = true, ["isolate"] = new EntryOptions { ["service"] = true }, Config = new[] { new EntryOptions { Id = "p", Name = "provider" }, new EntryOptions { Id = "c", Name = "consumer" } } }); await loader.WaitAsync();
        Assert.Equal("private", seen); await context.RunAsync(_ => { Assert.Null(context.Get("service")); return Task.CompletedTask; });
        await loader.RemoveAsync("group"); Assert.Empty(loader.Entries());
    }
    [Fact]
    public async Task ImportFailureLeavesSuccessfulSiblingRunning()
    {
        await using var context = new Context(); Loader loader = null!;
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("good", Noop())); return Task.CompletedTask; });
        await loader.Root.UpdateAsync([new() { Id = "good", Name = "good" }, new() { Id = "bad", Name = "absent" }]); await loader.WaitAsync();
        Assert.Equal(FiberState.Active, loader.Resolve("good").Fiber!.State); Assert.Null(loader.Resolve("bad").Fiber); Assert.IsType<FileNotFoundException>(loader.Resolve("bad").LastError);
    }
    [Fact]
    public async Task IncludeKeepsLastGoodParseAndRemovesOldPatches()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cordis-include-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "cordis.yml"); await File.WriteAllTextAsync(file, "- id: row\n  name: noop\n  config: base\n");
            await using var context = new Context(); Loader loader = null!;
            await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("noop", Noop())); return Task.CompletedTask; });
            var include = await ApplicationBoot.MountAsync(loader, file, [new() { Id = "row", Config = "patched" }]);
            Assert.Equal("patched", include.Resolve("row").Options.Config);
            await File.WriteAllTextAsync(file, ""); await include.RefreshAsync(); Assert.Equal("patched", include.Resolve("row").Options.Config);
            await File.WriteAllTextAsync(file, "- id: row\n  name: noop\n  config: edited\n"); await include.RefreshAsync(); await loader.WaitAsync(); Assert.Equal("patched", include.Resolve("row").Options.Config);
            await include.UpdateOptionsAsync(include.Options with { Patches = [] }); await loader.WaitAsync(); Assert.Equal("edited", include.Resolve("row").Options.Config);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task IncludeInitialIsOnlyUsedForMissingFilesAndWritesAreDurable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cordis-write-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            await using var context = new Context(); Loader loader = null!;
            await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("noop", Noop())); return Task.CompletedTask; });
            var include = new Include(context, loader, new("created.yml", [new() { Id = "row", Name = "noop" }]), new Uri(directory + Path.DirectorySeparatorChar));
            await include.StartAsync(); Assert.True(File.Exists(include.Filename));
            var attempts = 0; include.ReplaceFileAsync = (source, destination) => { if (++attempts < 3) throw new UnauthorizedAccessException("busy"); File.Move(source, destination, true); return Task.CompletedTask; };
            include.Root.Data[0].Disabled = true; include.Write(); await include.FlushWriteAsync(); Assert.Equal(3, attempts); Assert.Contains("disabled: true", await File.ReadAllTextAsync(include.Filename)); await include.StopAsync();
            await File.WriteAllTextAsync(include.Filename, "broken: ["); var second = new Include(context, loader, include.Options, new Uri(directory + Path.DirectorySeparatorChar)); await Assert.ThrowsAnyAsync<Exception>(() => second.StartAsync()); Assert.Equal("broken: [", await File.ReadAllTextAsync(include.Filename));
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Evaluator : IExpressionEvaluator { public object? Evaluate(string expression, Context context) => context.Get(expression); }
}
