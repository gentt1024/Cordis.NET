using Cordis;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class PublicConfigurationReviewTests
{
    private sealed class LiteralEvaluator : IExpressionEvaluator
    {
        public int Calls { get; private set; }
        public object? Evaluate(string expression, Context context)
        {
            Calls++;
            return expression.Trim() is "1" or "1 + 0"
            ? 1
            : throw new InvalidOperationException("The review fixture accepts only the literal 1.");
        }
    }

    private static EntryOptions ParsedExpressionConfig(int live) =>
        Assert.IsType<EntryOptions>(ConfigurationFile.Parse($"ordinary: !!js 1\nlive: {live}\n"));

    // R1: raw expression transport nodes must compare by source, not CLR allocation.
    [Fact]
    public async Task ReparsedIdenticalYamlExpressionDoesNotRestartLegacyPlugin()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var applies = 0;
            var plugin = new Plugin<object?> { Apply = (_, _) => applies++ };
            var loader = new Loader(ctx, new StaticModuleResolver().Register("review", plugin),
                expressionEvaluator: new LiteralEvaluator());
            await loader.Root.UpdateAsync([new() { Id = "p", Name = "review", Config = ParsedExpressionConfig(1) }]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var effective = entry.Fiber!.Config;
            await entry.UpdateAsync(new() { Config = ParsedExpressionConfig(1) });
            await loader.WaitAsync();
            Assert.Equal(1, applies);
            Assert.Same(effective, entry.Fiber!.Config);
        });
    }

    [Fact]
    public async Task ReparsedSameOrdinaryExpressionDoesNotDefeatVolatileUpdate()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var applies = 0;
            var schema = new ConfigSchema<EntryOptions>(
                raw => raw is EntryOptions config
                    ? ConfigResult<EntryOptions>.Success(config)
                    : ConfigResult<EntryOptions>.Failure("Expected an object."),
                ConfigDescriptor.Object(("ordinary", ConfigDescriptor.Number()),
                    ("live", ConfigDescriptor.Number().Volatile())))
                .WithVolatile("live", config => Convert.ToInt32(config["live"]));
            var plugin = new Plugin<EntryOptions> { Configuration = schema, Apply = (_, _) => applies++ };
            var loader = new Loader(ctx, new StaticModuleResolver().Register("review", plugin),
                expressionEvaluator: new LiteralEvaluator());
            await loader.Root.UpdateAsync([new() { Id = "p", Name = "review", Config = ParsedExpressionConfig(1) }]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var before = entry.Fiber!.GetConfigReference<int>("live");
            await entry.UpdateAsync(new() { Config = ParsedExpressionConfig(2) });
            await loader.WaitAsync();
            Assert.Equal(1, applies);
            Assert.Same(before, entry.Fiber!.GetConfigReference<int>("live"));
            Assert.Equal(2, before.Value);
        });
    }

    [Fact]
    public async Task ExpressionTransportEqualityUsesSourceWithoutEvaluation()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var applies = 0;
            var evaluator = new LiteralEvaluator();
            var loader = new Loader(ctx, new StaticModuleResolver().Register("review",
                new Plugin<object?> { Apply = (_, _) => applies++ }), expressionEvaluator: evaluator);
            await loader.Root.UpdateAsync([new() { Id = "p", Name = "review", Config = ParsedExpressionConfig(1) }]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var calls = evaluator.Calls;
            var json = ConfigurationFile.Parse("{\"ordinary\":{\"__jsExpr\":\"1\"},\"live\":1}", true);
            await entry.UpdateAsync(new() { Config = json });
            await loader.WaitAsync();
            Assert.Equal(1, applies);
            Assert.Equal(calls, evaluator.Calls);
            await entry.UpdateAsync(new() { Config = ConfigurationFile.Parse("ordinary: !!js 1 + 0\nlive: 1\n") });
            await loader.WaitAsync();
            Assert.Equal(2, applies);
            Assert.True(evaluator.Calls > calls);
        });
    }

    // R2: a finite input must not require expansion of an infinite factory-built schema.
    // The guard prevents a failing implementation from exhausting the process stack.
    // Increasing this cap is NOT a fix. Follow with depth/unused-branch upstream cases.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task FactoryRecursiveLazyDescriptionAcceptsFiniteTree(int depth)
    {
        var expansions = 0;
        ConfigDescriptor BuildTree()
        {
            if (++expansions > 32)
                throw new InvalidOperationException("Review guard: recursive factory eagerly expanded past finite input.");
            return ConfigDescriptor.Object(
                ("name", ConfigDescriptor.String()),
                ("children", ConfigDescriptor.Array(ConfigDescriptor.Lazy(BuildTree)).Default(Array.Empty<object?>())));
        }

        var descriptor = BuildTree();
        var applies = 0;
        var plugin = new Plugin<object?>
        {
            Configuration = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw), descriptor),
            Apply = (_, _) => applies++,
        };
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            object? tree = new EntryOptions { ["name"] = "leaf" };
            for (var level = 0; level < depth; level++)
                tree = new EntryOptions { ["name"] = "branch", ["children"] = new[] { tree } };
            var fiber = ctx.Plugin(plugin, tree);
            await fiber.WaitAsync();
            Assert.Equal(FiberState.Active, fiber.State);
            Assert.Equal(1, applies);
            Assert.Equal(depth + 1, expansions);
        });
    }

    [Fact]
    public async Task UnusedOptionalLazyDoesNotRunItsBuilder()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var schema = new ConfigSchema<object?>(raw => ConfigResult<object?>.Success(raw),
                ConfigDescriptor.Object(("unused", ConfigDescriptor.Lazy(
                    () => throw new InvalidOperationException("Unused branch was expanded.")).Optional())));
            var fiber = ctx.Plugin(new Plugin<object?> { Configuration = schema, Apply = (_, _) => { } }, new EntryOptions());
            await fiber.WaitAsync();
            Assert.Equal(FiberState.Active, fiber.State);
        });
    }

    // R4: pinned DSH explicitly tests ordinary-only equivalent object defaults.
    [Fact]
    public async Task OrdinaryOnlyDescriptionKeepsEquivalentExplicitDefaultMounted()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var applies = 0;
            var validations = 0;
            ConfigResult<EntryOptions> Validate(object? raw)
            {
                validations++;
                var input = raw as IReadOnlyDictionary<string, object?>;
                var nested = input?.GetValueOrDefault("nested") as IReadOnlyDictionary<string, object?>;
                var value = nested?.GetValueOrDefault("fixed") as string ?? "default";
                return ConfigResult<EntryOptions>.Success(new EntryOptions
                    { ["nested"] = new EntryOptions { ["fixed"] = value } });
            }
            var schema = new ConfigSchema<EntryOptions>(Validate,
                ConfigDescriptor.Object(("nested", ConfigDescriptor.Object(("fixed", ConfigDescriptor.String()))
                    .Default(new EntryOptions { ["fixed"] = "default" }))));
            var plugin = new Plugin<EntryOptions> { Configuration = schema, Apply = (_, _) => applies++ };
            var loader = new Loader(ctx, new StaticModuleResolver().Register("review", plugin));
            await loader.Root.UpdateAsync([new() { Id = "p", Name = "review", Config = new EntryOptions() }]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var before = entry.Fiber!.Config;
            var beforeValidations = validations;
            var explicitDefault = new EntryOptions { ["nested"] = new EntryOptions { ["fixed"] = "default" } };
            await entry.UpdateAsync(new() { Config = explicitDefault });
            await loader.WaitAsync();
            Assert.Equal(1, applies);
            Assert.Same(before, entry.Fiber!.Config);
            Assert.Equal(beforeValidations, validations);
            Assert.Same(explicitDefault, entry.Fiber.RawConfig);
            await entry.UpdateAsync(new() { Config = new EntryOptions { ["nested"] = new EntryOptions { ["fixed"] = "changed" } } });
            await loader.WaitAsync();
            Assert.Equal(2, applies);
        });
    }

}
