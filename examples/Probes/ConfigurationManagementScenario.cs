using Cordis.Composition;
using Cordis.Extensions;

namespace Cordis.Example.Probes;

internal static class ConfigurationManagementScenario
{
    internal static async Task RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-management-consumer-").FullName;
        try
        {
            var home = Path.Combine(directory, "home");
            var profile = Path.Combine(home, "profiles", "example");
            Profiles.Initialize(profile, []);
            var launch = new ProfileLaunch(
                await Profiles.LoadAsync(profile, new Dictionary<string, string>()),
                home,
                [],
                new Dictionary<string, string>());
            var source = Path.Combine(directory, "cordis.yml");
            await File.WriteAllTextAsync(source, "- id: worker\n  name: worker\n  config: { limit: 1 }\n");
            var activations = 0;
            ConfigReference<int> reference = null!;
            var plugin = new Plugin<int>
            {
                Configuration = ConfigObject<int>
                    .Create(raw =>
                        raw is IReadOnlyDictionary<string, object?> map &&
                        Convert.ToInt32(map.GetValueOrDefault("limit")) is var limit && limit > 0
                            ? ConfigResult<int>.Success(limit)
                            : ConfigResult<int>.Failure("positive limit required"))
                    .Field("limit", ConfigDescriptor.Number().Volatile(), value => value)
                    .Build(),
                Apply = (owner, _) =>
                {
                    activations++;
                    reference = owner.Fiber.GetConfigReference<int>("limit");
                },
            };
            await using var session = await ProfileSession.StartAsync(
                source,
                launch,
                new StaticModuleResolver().Register("worker", plugin));
            var before = await session.ConfigurationOperations.ReadConfigurationAsync("root:worker");
            var edited = await session.ConfigurationOperations.EditConfigurationFieldAsync(
                "root:worker",
                ["limit"],
                2,
                before.Revision);
            Require(
                edited.Saved && edited.Applied && reference.Value == 2 && activations == 1,
                "profile field edit applies live");
            var saved = await File.ReadAllTextAsync(launch.Profile.UserLayer.Source);
            var stale = await session.ConfigurationOperations.EditConfigurationFieldAsync(
                "root:worker",
                ["limit"],
                3,
                before.Revision);
            Require(
                stale.Error == "conflict" && !stale.Saved && reference.Value == 2,
                "profile field edit fences stale revisions");
            before = await session.ConfigurationOperations.ReadConfigurationAsync("root:worker");
            var invalid = await session.ConfigurationOperations.EditConfigurationFieldAsync(
                "root:worker",
                ["limit"],
                -1,
                before.Revision);
            Require(
                invalid.Error == "invalid-configuration" && !invalid.Saved && reference.Value == 2 &&
                saved == await File.ReadAllTextAsync(launch.Profile.UserLayer.Source),
                "profile field edit validates before persistence");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void Require(bool condition, string contract)
    {
        if (!condition)
            throw new InvalidOperationException("configuration consumer: " + contract);
    }
}
