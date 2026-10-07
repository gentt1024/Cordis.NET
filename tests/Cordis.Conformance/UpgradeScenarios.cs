using Cordis.Composition;

namespace Cordis.Conformance;

internal static class UpgradeScenarios
{
    internal static IReadOnlyList<(string Id, Func<Task<string[]>> Run)> Cases
    {
        get;
    } = [("V01-volatile-entry-generations", VolatileEntry), ("V02-unchanged-snapshot-identity", SnapshotIdentity)];

    private static EntryOptions Raw(int value, string ordinary = "same") =>
        new()
        {
            ["limit"] = value,
            ["ordinary"] = ordinary
        };

    private static async Task<string[]> VolatileEntry()
    {
        var trace = new List<string>();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var references = new List<ConfigReference<int>>();
            int applies = 0, notices = 0;
            var schema = new ConfigSchema<EntryOptions>(
                raw => raw is EntryOptions value && Convert.ToInt32(value["limit"]) > 0
                    ? ConfigResult<EntryOptions>.Success(value)
                    : ConfigResult<EntryOptions>.Failure("positive limit required"),
                ConfigDescriptor.Object(
                    ("limit", ConfigDescriptor.Number().Volatile()),
                    ("ordinary", ConfigDescriptor.String()))).WithVolatile(
                "limit",
                value => Convert.ToInt32(value["limit"]));
            var plugin = new Plugin<EntryOptions>
            {
                Configuration = schema,
                Apply = (owner, _) =>
                {
                    applies++;
                    references.Add(owner.Fiber.GetConfigReference<int>("limit"));
                    owner.On(
                        "loader/volatile-update",
                        (_, _) =>
                        {
                            notices++;
                            return null;
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
                    Config = Raw(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var first = entry.Fiber!;
            var effective = first.Config;
            trace.Add($"initial:{references[0].Value};apply:{applies}");
            await entry.UpdateAsync(
                new()
                {
                    Config = Raw(2)
                });
            await loader.WaitAsync();
            if (!ReferenceEquals(first, entry.Fiber) || !ReferenceEquals(effective, first.Config))
                throw new Exception("Volatile update remounted");
            trace.Add($"live:{references[0].Value};apply:{applies};notice:{notices}");
            await entry.UpdateAsync(
                new()
                {
                    Config = Raw(-1)
                });
            await loader.WaitAsync();
            trace.Add(
                $"invalid-raw:{((EntryOptions)entry.Fiber!.RawConfig!)["limit"]};live:{references[0].Value};apply:{applies}");
            await entry.UpdateAsync(
                new()
                {
                    Config = Raw(3, "changed")
                });
            await loader.WaitAsync();
            trace.Add($"mixed:old:{references[0].Value};new:{references[1].Value};apply:{applies}");
            entry.Fiber!.Update(Raw(4, "changed"), true);
            await loader.WaitAsync();
            trace.Add(
                $"no-save:saved:{((EntryOptions)entry.Options.Config!)["limit"]};old:{references[1].Value};new:{references[2].Value};apply:{applies}");
            await entry.UpdateAsync(
                new()
                {
                    Config = Raw(5, "changed")
                });
            await loader.WaitAsync();
            trace.Add(
                $"after-no-save:old:{references[0].Value},{references[1].Value};new:{references[2].Value};apply:{applies};notice:{notices}");
        });
        return trace.ToArray();
    }

    private static async Task<string[]> SnapshotIdentity()
    {
        var trace = new List<string>();
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var schema = new ConfigSchema<EntryOptions>(
                    value => ConfigResult<EntryOptions>.Success((EntryOptions)value!),
                    ConfigDescriptor.Object(
                        ("map", ConfigDescriptor.Object(("n", ConfigDescriptor.Number())).Volatile()),
                        ("b", ConfigDescriptor.Number().Volatile())))
                .WithVolatile("map", value => (IReadOnlyDictionary<string, object?>)value["map"]!)
                .WithVolatile("b", value => (int)value["b"]!);
            ConfigReference<IReadOnlyDictionary<string, object?>>? map = null;
            ConfigReference<int>? b = null;
            string[] changes = [];
            var plugin = new Plugin<EntryOptions>
            {
                Configuration = schema,
                Apply = (owner, _) =>
                {
                    map = owner.Fiber.GetConfigReference<IReadOnlyDictionary<string, object?>>("map");
                    b = owner.Fiber.GetConfigReference<int>("b");
                    owner.On(
                        "loader/volatile-update",
                        (_, args) =>
                        {
                            changes = ((IEnumerable<string>)args[0]!).ToArray();
                            return null;
                        });
                }
            };
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));

            EntryOptions RawMap(int number) =>
                new()
                {
                    ["map"] = new EntryOptions
                    {
                        ["n"] = 1
                    },
                    ["b"] = number
                };

            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = RawMap(1)
                }
            ]);
            await loader.WaitAsync();
            var oldValue = map!.Value;
            await loader
                .Resolve("p")
                .UpdateAsync(
                    new()
                    {
                        Config = RawMap(2)
                    });
            await loader.WaitAsync();
            if (!ReferenceEquals(oldValue, map.Value))
                throw new Exception("Equal snapshot identity changed");
            trace.Add($"live:map:{map.Value["n"]},b:{b!.Value};same-snapshot:true;changed:{string.Join(',', changes)}");
        });
        return trace.ToArray();
    }
}
