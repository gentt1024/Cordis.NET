using Cordis;

namespace Cordis.Conformance;

// Static authoring checks run under both JIT and actual Native AOT in the upgrade mode.
// They leave the established paired default trace unchanged.
internal static class ConfigurationScenarios
{
    private sealed record Settings(int Limit, string Label);

    internal static async Task CheckAsync()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int validates = 0, applies = 0;
            var schema = new ConfigSchema<Settings>(raw =>
            {
                validates++;
                return raw is Settings value && value.Limit > 0
                    ? ConfigResult<Settings>.Success(value) : ConfigResult<Settings>.Failure("positive limit required");
            }, ConfigDescriptor.Object(("limit", ConfigDescriptor.Number().Volatile()), ("label", ConfigDescriptor.String())))
                .WithVolatile("limit", value => value.Limit)
                .WithOrdinaryEquality((left, right) => left.Label == right.Label)
                .WithSimplify(value => new Dictionary<string, object?> { ["limit"] = value.Limit, ["label"] = value.Label });
            var fiber = ctx.Plugin(new Plugin<Settings> { Configuration = schema, Apply = (_, _) => applies++ }, new Settings(1, "same"));
            await fiber.WaitAsync();
            var reference = fiber.GetConfigReference<int>("limit"); var effective = fiber.Config;
            if (!fiber.TryPrepareConfigurationUpdate(new Settings(2, "same"), out var candidate) || candidate is null || reference.Value != 1)
                throw new InvalidOperationException("Configuration preparation published state or failed.");
            if (!candidate.Commit() || reference.Value != 2 || !ReferenceEquals(effective, fiber.Config) || applies != 1 || validates != 2)
                throw new InvalidOperationException("Configuration commit broke captured validation, identity, or lifecycle.");
            if (fiber.TryPrepareConfigurationUpdate(new Settings(3, "changed"), out _)) throw new InvalidOperationException("Ordinary typed changes must fall back.");
            var oldReference = reference;
            fiber.Update(new Settings(4, "changed")); await fiber.WaitAsync();
            if (oldReference.Value != 2 || ReferenceEquals(oldReference, fiber.GetConfigReference<int>("limit")) || fiber.GetConfigReference<int>("limit").Value != 4)
                throw new InvalidOperationException("Activation references did not retain their independent final state.");
            var raw = (IReadOnlyDictionary<string, object?>)fiber.SimplifyConfiguration(fiber.Config)!;
            if ((int)raw["limit"]! != 4) throw new InvalidOperationException("Typed static simplification failed.");

            ConfigDescriptor? recursive = null;
            var shared = ConfigDescriptor.Number().Optional().Default(double.NaN);
            recursive = ConfigDescriptor.Lazy(() => ConfigDescriptor.Object(("a", shared), ("b", shared.Alias()), ("child", recursive!)));
            var recursiveFiber = ctx.Plugin(new Plugin<object?> { Configuration = new(value => ConfigResult<object?>.Success(value), recursive), Apply = (_, _) => { } }, new Dictionary<string, object?>());
            await recursiveFiber.WaitAsync();
            var restored = ConfigDescriptor.Deserialize(recursiveFiber.ConfigDescription!.Serialize());
            if (!ReferenceEquals(restored.Inner!.Properties["a"], restored.Inner.Properties["b"]) || !ReferenceEquals(restored, restored.Inner.Properties["child"]) ||
                !double.IsNaN((double)restored.Inner.Properties["a"].DefaultValue!)) throw new InvalidOperationException("Static graph reconstruction lost sharing, recursion, or numeric metadata.");
            object nan = double.NaN;
            if (ConfigDescriptor.StrictEquals(nan, nan) || !ConfigDescriptor.StrictEquals(new Dictionary<string, object?>(), new Dictionary<string, object?> { ["missing"] = Undefined.Value }))
                throw new InvalidOperationException("Strict fixed-source configuration comparison failed.");

            int graphValidations = 0;
            var graphSchema = new ConfigSchema<IReadOnlyDictionary<string, object?>>(value =>
            {
                graphValidations++; return ConfigResult<IReadOnlyDictionary<string, object?>>.Success((IReadOnlyDictionary<string, object?>)value!);
            }, ConfigDescriptor.Object(("map", ConfigDescriptor.Any().Volatile()), ("number", ConfigDescriptor.Number().Volatile())))
                .WithVolatile("map", value => value["map"]).WithVolatile("number", value => (int)value["number"]!);
            var graphFiber = ctx.Plugin(new Plugin<IReadOnlyDictionary<string, object?>> { Configuration = graphSchema, Apply = (_, _) => { } },
                new Dictionary<string, object?> { ["map"] = new Dictionary<string, object?> { ["n"] = 1 }, ["number"] = 1 });
            await graphFiber.WaitAsync();
            var map = graphFiber.GetConfigReference<object?>("map"); var number = graphFiber.GetConfigReference<int>("number"); var oldSnapshot = map.Value;
            if (!graphFiber.TryPrepareConfigurationUpdate(new Dictionary<string, object?>
            {
                ["map"] = new Dictionary<string, object?> { ["n"] = 1 }, ["number"] = 2
            }, out var graphCandidate) || graphCandidate is null || graphValidations != 2 || number.Value != 1 || !ReferenceEquals(oldSnapshot, map.Value))
                throw new InvalidOperationException("All candidate fields must validate before publication.");
            if (!graphCandidate.ChangedPaths.SequenceEqual(new[] { "number" }) || !graphCandidate.Commit() || number.Value != 2 || !ReferenceEquals(oldSnapshot, map.Value))
                throw new InvalidOperationException("Equal snapshot identity was replaced while committing another field.");
            var simplifiedObject = ConfigDescriptor.Object(("count", ConfigDescriptor.Number().Default(1))).Default(new Dictionary<string, object?>());
            var simplifiedDictionary = ConfigDescriptor.Dict(ConfigDescriptor.Number().Default(1)).Default(new Dictionary<string, object?> { ["count"] = null });
            if (simplifiedObject.Simplify(new Dictionary<string, object?> { ["count"] = 1 }) is not null ||
                simplifiedDictionary.Simplify(new Dictionary<string, object?> { ["count"] = 1 }) is not null)
                throw new InvalidOperationException("Object/dictionary defaults were not rechecked after child simplification.");

            var lazyShape = new ConfigSchema<Settings>(raw => ConfigResult<Settings>.Success((Settings)raw!),
                ConfigDescriptor.Object(("limit", ConfigDescriptor.Lazy(() => ConfigDescriptor.Number())), ("label", ConfigDescriptor.String())))
                .WithDescriptionData(value => new Dictionary<string, object?> { ["limit"] = value.Limit, ["label"] = value.Label });
            var typedLazy = ctx.Plugin(new Plugin<Settings> { Configuration = lazyShape, Apply = (_, _) => { } }, new Settings(7, "typed"));
            await typedLazy.WaitAsync();
            var selects = 0;
            var union = ConfigDescriptor.Union(_ => { selects++; return 0; }, ConfigDescriptor.Number(),
                ConfigDescriptor.Lazy(() => throw new InvalidOperationException("Unused union branch expanded.")));
            var unionFiber = ctx.Plugin(new Plugin<int> { Configuration = new(raw => ConfigResult<int>.Success((int)raw!), union), Apply = (_, _) => { } }, 1);
            await unionFiber.WaitAsync();
            if (typedLazy.State != FiberState.Active || unionFiber.State != FiberState.Active || selects != 1)
                throw new InvalidOperationException("Static lazy authoring signals failed.");
        });
        Console.Error.WriteLine("PASS U-config-static-authoring-refs-graph");
    }
}
