using Cordis.Composition;
using Xunit;

namespace Cordis.Extensions.Tests;

public sealed class ConfigurationSessionTests
{
    [Fact]
    public async Task Field_edit_uses_session_refresh_and_refuses_a_deployment_requiring_restart()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-edit-session-").FullName;
        try
        {
            var home = Path.Combine(directory, "home");
            var profile = Path.Combine(home, "profiles", "test");
            Profiles.Initialize(profile, []);
            var launch = new ProfileLaunch(await Profiles.LoadAsync(profile, new Dictionary<string, string>()), home, [], new Dictionary<string, string>());
            var config = Path.Combine(directory, "cordis.yml");
            await File.WriteAllTextAsync(config, "- id: worker\n  name: worker\n  config: { limit: 1 }\n");
            var package = Directory.CreateDirectory(Path.Combine(directory, "package")).FullName;
            var replacement = Directory.CreateDirectory(Path.Combine(directory, "replacement")).FullName;
            var entry = new DeploymentEntry("worker", package, "1", Path.Combine(package, "package.json"), DeploymentPackageScope.Installation);
            var original = new DeploymentGeneration(Path.Combine(home, "profiles"), profile, [entry]);
            var next = original;
            var packages = new DeploymentPackageResolver(original, native: (_, _) => package);
            var applies = 0;
            ConfigReference<int> reference = null!;
            var resolver = new StaticModuleResolver().Register("worker", new Plugin<int>
            {
                Configuration = ConfigObject<int>.Create(raw => ConfigResult<int>.Success(Convert.ToInt32(((IReadOnlyDictionary<string, object?>)raw!)["limit"])))
                    .Field("limit", ConfigDescriptor.Number().Volatile(), value => value).Build(),
                Apply = (owner, _) => { applies++; reference = owner.Fiber.GetConfigReference<int>("limit"); },
            });
            var modules = new DeploymentModuleResolver(packages, resolver).Register(package, ".", "worker");
            await using var session = await ProfileSession.StartAsync(config, launch, modules, refreshDeployment: () => Task.FromResult(next));
            var before = await session.ConfigurationOperations.ReadConfigurationAsync("root:worker");
            var changed = await session.ConfigurationOperations.EditConfigurationFieldAsync("root:worker", ["limit"], 2, before.Revision);
            Assert.True(changed.Applied, changed.Diagnostic);
            Assert.Equal(2, reference.Value);
            Assert.Equal(1, applies);
            var source = await File.ReadAllTextAsync(launch.Profile.UserLayer.Source);
            var current = await session.ConfigurationOperations.ReadConfigurationAsync("root:worker");
            next = new(original.ProfilesDirectory, original.ProfileDirectory, [entry with { Directory = replacement }]);
            // A changed deployment is refreshed with a changed profile input, as in the existing session contract.
            await File.AppendAllTextAsync(launch.Profile.UserLayer.Source, "# external source refresh\n");
            var external = await File.ReadAllTextAsync(launch.Profile.UserLayer.Source);
            var blocked = await session.ConfigurationOperations.EditConfigurationFieldAsync("root:worker", ["limit"], 3, current.Revision);
            Assert.True(session.RequiresRestart);
            Assert.Equal("restart-required", blocked.Error);
            Assert.False(blocked.Saved);
            Assert.False(blocked.Applied);
            Assert.Equal(external, await File.ReadAllTextAsync(launch.Profile.UserLayer.Source));
            Assert.Equal(2, reference.Value);
            Assert.Same(original, packages.Generation);
            await Assert.ThrowsAsync<DeploymentRestartRequiredException>(() => session.ConfigurationOperations.ReadConfigurationAsync("root:worker"));
            Assert.Contains("limit: 2", source);
        }
        finally { Directory.Delete(directory, true); }
    }
}
