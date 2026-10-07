using Cordis;
using Cordis.Composition;

namespace Cordis.Example.Probes;

// This consumer is also copied outside the repository and run using only built NuGet packages.
internal static class ConfigurationScenario
{
    private const int DefaultLimit = 1;
    private const string DefaultLabel = "worker";

    private sealed record Settings(int Limit, string Label, string Category = "general");

    internal static async Task RunAsync()
    {
        var descriptor = ConfigDescriptor.Object(
            ("limit", ConfigDescriptor.Number().Default(DefaultLimit).Volatile()),
            ("label", ConfigDescriptor.String().Default(DefaultLabel)),
            ("category", ConfigDescriptor.String().Default("general")));
        var manual = new ConfigSchema<Settings>(Validate, descriptor)
            .WithVolatile("limit", value => value.Limit)
            .WithOrdinaryEquality((left, right) => left.Label == right.Label && left.Category == right.Category)
            .WithSimplify(Project);
        var composed = ConfigObject<Settings>
            .Create(Validate)
            .Field("limit", ConfigDescriptor.Number().Default(DefaultLimit).Volatile(), value => value.Limit)
            .Field("label", ConfigDescriptor.String().Default(DefaultLabel), value => value.Label)
            .Field("category", ConfigDescriptor.String().Default("general"), value => value.Category)
            .Build();

        await Exercise(manual);
        await Exercise(composed);
        Console.WriteLine("typed configuration manual/composed scenario passed");
    }

    private static ConfigResult<Settings> Validate(object? raw)
    {
        if (raw is not IReadOnlyDictionary<string, object?> map)
            return ConfigResult<Settings>.Failure("configuration map required");
        var limit = map.GetValueOrDefault("limit", DefaultLimit);
        var label = map.GetValueOrDefault("label", DefaultLabel);
        var category = map.GetValueOrDefault("category", "general");
        if (limit is not int number || number <= 0 || label is not string text || category is not string group)
            return ConfigResult<Settings>.Failure("positive integer limit and string label required");
        // A validator may derive ordinary output from a live raw field. The effective
        // comparison must still refuse an in-place update when that output changes.
        return ConfigResult<Settings>.Success(
            new(
                number,
                text == "derived" ? $"band-{number / 10}" : text,
                group == "derived" ? $"group-{number / 10}" : group));
    }

    private static Dictionary<string, object?> Project(Settings value) =>
        new()
        {
            ["limit"] = value.Limit,
            ["label"] = value.Label,
            ["category"] = value.Category,
        };

    private static async Task Exercise(ConfigSchema<Settings> schema)
    {
        await using var root = new Context();
        await root.RunAsync(async context =>
        {
            var references = new List<ConfigReference<int>>();
            var plugin = new Plugin<Settings>
            {
                Configuration = schema,
                Apply = (owner, _) => references.Add(owner.Fiber.GetConfigReference<int>("limit")),
            };
            // Transparent adapters must explicitly forward the optional configuration declaration.
            var diagnostics = new List<Exception>();
            var loader = new Loader(
                context,
                new StaticModuleResolver().Register("worker", new Adapter(plugin)),
                diagnostic: diagnostics.Add);
            Check(
                Equals(((IPlugin)plugin).ResolveConfig(new EntryOptions()), new Settings(1, "worker")),
                "validator omitted defaults");
            // Fixed upstream raw comparison does not normalize scalar defaults. Keep ordinary
            // raw keys identical when asserting that only the live field changes.
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "worker",
                    Name = "worker",
                    Config = Project(new(1, "worker"))
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("worker");
            var fiber = entry.Fiber ?? throw new InvalidOperationException(
                "configuration consumer: activation failed",
                entry.LastError ?? diagnostics.LastOrDefault());
            Check(fiber.State == FiberState.Active, "default activation");
            var initial = (Settings)fiber.Config!;
            Check(initial == new Settings(1, "worker"), "validator defaults");
            var reference = references[0];

            await entry.UpdateAsync(
                new()
                {
                    Config = Project(new(2, "worker"))
                });
            Check(
                ReferenceEquals(entry.Fiber, fiber) && ReferenceEquals(fiber.Config, initial),
                "live activation identity");
            Check(
                reference.Value == 2 && initial.Limit == 1 && references.Count == 1,
                "live value and effective identity");

            var rejected = Project(new(-1, "worker"));
            await entry.UpdateAsync(
                new()
                {
                    Config = rejected
                });
            Check(
                ReferenceEquals(fiber.RawConfig, rejected) && reference.Value == 2,
                "rejected raw retained, live value unchanged");

            await entry.UpdateAsync(
                new()
                {
                    Config = Project(new(3, "batch"))
                });
            await loader.WaitAsync();
            Check(
                references.Count == 2 && reference.Value == 2 && references[1].Value == 3,
                "ordinary restart freezes old reference");
            Check(((Settings)entry.Fiber!.Config!).Label == "batch", "ordinary field becomes effective");

            await entry.UpdateAsync(
                new()
                {
                    Config = Project(new(3, "batch", "secondary"))
                });
            await loader.WaitAsync();
            Check(
                references.Count == 3 && references[2].Value == 3 &&
                ((Settings)entry.Fiber!.Config!).Category == "secondary",
                "second ordinary field restarts");

            var saved = (IReadOnlyDictionary<string, object?>)entry.Fiber.SimplifyConfiguration(
                new Settings(4, "saved"))!;
            Check(
                saved.Count == 3 && Equals(saved["limit"], 4) && Equals(saved["label"], "saved") &&
                Equals(saved["category"], "general"),
                "complete persistence");
            Check(Equals(((IPlugin)plugin).ResolveConfig(saved), new Settings(4, "saved")), "saved round trip");

            var beforeNoSave = entry.Options.Config;
            entry.Fiber.Update(Project(new(5, "transient")), noSave: true);
            await loader.WaitAsync();
            Check(ReferenceEquals(entry.Options.Config, beforeNoSave), "noSave keeps saved raw");
            Check(
                references.Count == 4 && references[2].Value == 3 && references[3].Value == 5,
                "noSave still restarts");

            var retired = references[3];
            await loader.ReplacePluginAsync(
                plugin,
                new Adapter(
                    new Plugin<Settings>
                    {
                        Configuration = schema,
                        Apply = (owner, _) => references.Add(owner.Fiber.GetConfigReference<int>("limit")),
                    }));
            Check(
                references.Count == 5 && retired.Value == 5 && !ReferenceEquals(retired, references[4]),
                "replacement creates new references");

            foreach (var ordinary in new[] { new Settings(9, "derived"), new Settings(9, "worker", "derived") })
            {
                await entry.UpdateAsync(
                    new()
                    {
                        Config = Project(ordinary)
                    });
                await loader.WaitAsync();
                var derivedEffective = entry.Fiber!.Config;
                var derivedReference = references[^1];
                var count = references.Count;
                await entry.UpdateAsync(
                    new()
                    {
                        Config = Project(
                            ordinary with
                            {
                                Limit = 10
                            })
                    });
                await loader.WaitAsync();
                var effective = (Settings)entry.Fiber!.Config!;
                Check(
                    references.Count == count + 1 && (ordinary.Label == "derived"
                        ? effective.Label == "band-1"
                        : effective.Category == "group-1"),
                    "ordinary effective change refuses live commit");
                Check(
                    derivedReference.Value == 9 && !ReferenceEquals(derivedEffective, entry.Fiber.Config),
                    "derived restart freezes old value");
            }
        });
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("configuration consumer: " + message);
    }

    private sealed class Adapter(IPlugin inner) : IPlugin, IConfigurationPlugin
    {
        public object Identity => inner.Identity;
        public string? Name => inner.Name;
        public IReadOnlyDictionary<string, object?> Dependencies => inner.Dependencies;
        public object? ResolveConfig(object? raw) => inner.ResolveConfig(raw);
        public Task ApplyAsync(Context context, object? config) => inner.ApplyAsync(context, config);
        public PluginConfiguration? CaptureConfiguration() => (inner as IConfigurationPlugin)?.CaptureConfiguration();
    }
}
