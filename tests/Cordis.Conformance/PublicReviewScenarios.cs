using Cordis.Composition;

namespace Cordis.Conformance;

internal static class PublicReviewScenarios
{
    internal static IReadOnlyList<(string Id, Func<Task<string[]>> Run)> Cases
    {
        get;
    } =
    [
        ("V03-expression-source", ExpressionSource),
        ("V04-finite-lazy-tree", FiniteTree),
        ("V05-zero-reference-default", ZeroReferenceDefault)
    ];

    private sealed class LiteralEvaluator : IExpressionEvaluator
    {
        public object? Evaluate(string expression, Context context) =>
            expression is "1" or "1 + 0" ? 1 : throw new FormatException(expression);
    }

    private static async Task<string[]> ExpressionSource()
    {
        var trace = new List<string>();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var references = new List<ConfigReference<int>>();
            var schema = new ConfigSchema<EntryOptions>(
                raw => ConfigResult<EntryOptions>.Success((EntryOptions)raw!),
                ConfigDescriptor.Object(
                    ("ordinary", ConfigDescriptor.Number()),
                    ("live", ConfigDescriptor.Number().Volatile()))).WithVolatile(
                "live",
                value => Convert.ToInt32(value["live"]));
            var plugin = new Plugin<EntryOptions>
            {
                Configuration = schema,
                Apply = (owner, _) => references.Add(owner.Fiber.GetConfigReference<int>("live"))
            };
            var loader = new Loader(
                ctx,
                new StaticModuleResolver().Register("plugin", plugin),
                expressionEvaluator: new LiteralEvaluator());

            object? Raw(int live, string source = "1") =>
                ConfigurationFile.Parse($"ordinary: !!js {source}\nlive: {live}\n");

            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = Raw(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var effective = entry.Fiber!.Config;
            await entry.UpdateAsync(
                new()
                {
                    Config = ConfigurationFile.Parse("{\"ordinary\":{\"__jsExpr\":\"1\"},\"live\":1}", true)
                });
            await loader.WaitAsync();
            if (!ReferenceEquals(effective, entry.Fiber.Config))
                throw new Exception("Expression transport remounted");
            trace.Add($"same-source:apply:{references.Count};live:{references[0].Value}");
            await entry.UpdateAsync(
                new()
                {
                    Config = Raw(2)
                });
            await loader.WaitAsync();
            if (!ReferenceEquals(effective, entry.Fiber.Config))
                throw new Exception("Ordinary expression defeated live update");
            trace.Add($"live:apply:{references.Count};value:{references[0].Value}");
            await entry.UpdateAsync(
                new()
                {
                    Config = Raw(2, "1 + 0")
                });
            await loader.WaitAsync();
            trace.Add($"changed-source:apply:{references.Count};old:{references[0].Value};new:{references[^1].Value}");
        });
        return trace.ToArray();
    }

    private static async Task<string[]> FiniteTree()
    {
        var expansions = 0;

        ConfigDescriptor Tree()
        {
            if (++expansions > 16)
                throw new Exception("Factory expanded past finite data");
            return ConfigDescriptor.Object(
                ("name", ConfigDescriptor.String()),
                ("children", ConfigDescriptor.Array(ConfigDescriptor.Lazy(Tree)).Default(Array.Empty<object?>())));
        }

        object? value = new EntryOptions
        {
            ["name"] = "leaf",
            ["children"] = Array.Empty<object?>()
        };
        for (var i = 0;i < 3;i++)
            value = new EntryOptions
            {
                ["name"] = "branch",
                ["children"] = new[] { value }
            };
        var names = new List<string>();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var fiber = ctx.Plugin(
                new Plugin<object?>
                {
                    Configuration = new(raw => ConfigResult<object?>.Success(raw), Tree()),
                    Apply = (_, _) =>
                    {
                    }
                },
                value);
            await fiber.WaitAsync();
            if (fiber.State != FiberState.Active || expansions != 4)
                throw new Exception("Finite tree did not activate");
            var node = (IReadOnlyDictionary<string, object?>)fiber.Config!;
            while (true)
            {
                names.Add((string)node["name"]!);
                var children = (object?[])node["children"]!;
                if (children.Length == 0)
                    break;
                node = (IReadOnlyDictionary<string, object?>)children[0]!;
            }
        });
        return [$"tree:{string.Join(',', names)};active:true"];
    }

    private static async Task<string[]> ZeroReferenceDefault()
    {
        var trace = new List<string>();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var applies = 0;
            var updates = 0;
            var schema = new ConfigSchema<EntryOptions>(
                raw =>
                {
                    var input = (EntryOptions)raw!;
                    var nested = input.GetValueOrDefault("nested") as IReadOnlyDictionary<string, object?>;
                    return ConfigResult<EntryOptions>.Success(
                        new EntryOptions
                        {
                            ["nested"] = new EntryOptions
                            {
                                ["fixed"] = nested?.GetValueOrDefault("fixed") ?? "default"
                            }
                        });
                },
                ConfigDescriptor.Object(
                    ("nested", ConfigDescriptor
                        .Object(("fixed", ConfigDescriptor.String()))
                        .Default(
                            new EntryOptions
                            {
                                ["fixed"] = "default"
                            }))));
            var plugin = new Plugin<EntryOptions>
            {
                Configuration = schema,
                Apply = (owner, _) =>
                {
                    applies++;
                    owner.On(
                        "internal/config",
                        (evt, _) =>
                        {
                            updates++;
                            return evt.Next();
                        });
                }
            };
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = new EntryOptions()
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var effective = entry.Fiber!.Config;

            EntryOptions Raw(string text) =>
                new()
                {
                    ["nested"] = new EntryOptions
                    {
                        ["fixed"] = text
                    }
                };

            var equivalent = Raw("default");
            await entry.UpdateAsync(
                new()
                {
                    Config = equivalent
                });
            await loader.WaitAsync();
            if (!ReferenceEquals(effective, entry.Fiber.Config) ||
                !ReferenceEquals(equivalent, entry.Fiber.RawConfig) || updates != 0)
                throw new Exception("Equivalent default revalidated or remounted");
            trace.Add($"default:apply:{applies};same:true;config-hooks:{updates}");
            await entry.UpdateAsync(
                new()
                {
                    Config = Raw("changed")
                });
            await loader.WaitAsync();
            trace.Add($"changed:apply:{applies}");
        });
        return trace.ToArray();
    }
}
