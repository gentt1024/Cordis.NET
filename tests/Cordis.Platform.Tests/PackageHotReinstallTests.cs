using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed partial class PackageManagementTests
{
    [Theory]
    [InlineData("1.0.0", "v1")]
    [InlineData("2.0.0", "v2")]
    public async Task Hot_package_reinstall_replaces_runtime_identity_and_preserves_retained_code(
        string version,
        string code)
    {
        if (version == "2.0.0")
            await PackUpdatedPluginAsync(version, code);
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        var first = await owner.InstallPackageAsync(
            host.Toolchain,
            new("IndependentPlugin", "1.0.0", Feed),
            "first",
            true);
        Assert.Equal("applied", first.Application);
        var original = Assert.Single(await host.Toolchain.GetLoadedModulesAsync("IndependentPlugin"));
        var originalDirectory = host.Toolchain.Bundles["IndependentPlugin"];
        var originalCode = Assert.IsType<Func<string>>(await ReadHotInstalledCodeAsync(host));
        Assert.Equal("v1", originalCode());

        var removed = await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin");
        Assert.Equal("applied", removed.Application);
        Assert.False(removed.Installed);
        Assert.False(removed.Selected);
        Assert.Empty(host.Toolchain.Bundles);
        Assert.Null(await ReadHotInstalledCodeAsync(host));
        Assert.True(Directory.Exists(originalDirectory));
        Assert.Contains(originalDirectory, removed.Residuals!);
        Assert.Equal("IndependentPlugin", host.Toolchain.ResolvePackageName("independentplugin"));
        await host.Toolchain.RemoveAsync("INDEPENDENTPLUGIN");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => host.Toolchain.RemoveAsync("UnknownPlugin"));

        var reinstall = await owner.InstallPackageAsync(
            host.Toolchain,
            new("independentplugin", version, Feed),
            "reinstall",
            true);
        Assert.Null(reinstall.Error);
        Assert.Equal("IndependentPlugin", reinstall.Target);
        Assert.True(reinstall.Installed, reinstall.Diagnostic);
        Assert.True(reinstall.Selected);
        Assert.Equal("applied", reinstall.Application);
        Assert.Null(reinstall.RestartReason);
        var replacement = Assert.Single(await host.Toolchain.GetLoadedModulesAsync("IndependentPlugin"));
        var replacementCode = Assert.IsType<Func<string>>(await ReadHotInstalledCodeAsync(host));
        Assert.NotSame(original, replacement);
        Assert.NotSame(original.Plugin, replacement.Plugin);
        Assert.NotSame(originalCode, replacementCode);
        Assert.Equal(code, replacementCode());
        Assert.Equal("v1", originalCode());
        Assert.Equal(version, ReadHotDependencyVersion(host));
        Assert.Equal(version == "1.0.0", originalDirectory == host.Toolchain.Bundles["IndependentPlugin"]);
        Assert.Null(host.Session.LastError);
        Assert.False(host.Session.RequiresRestart);
    }

    [Fact]
    public async Task Hot_package_disabled_reinstall_remains_inactive_until_selected()
    {
        await PackUpdatedPluginAsync("2.0.0", "v2");
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        var initial = await owner.InstallPackageAsync(
            host.Toolchain,
            new("IndependentPlugin", "1.0.0", Feed),
            "disabled",
            true,
            enabled: false);
        Assert.Equal("applied", initial.Application);
        Assert.False(initial.Selected);
        Assert.Null(await ReadHotInstalledCodeAsync(host));
        Assert.Empty(await host.Toolchain.GetLoadedModulesAsync("IndependentPlugin"));
        Assert.Equal("applied", (await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin")).Application);
        Assert.Empty(host.Toolchain.Bundles);

        var reinstall = await owner.InstallPackageAsync(
            host.Toolchain,
            new("IndependentPlugin", "2.0.0", Feed),
            "disabled-reinstall",
            true,
            enabled: false);
        Assert.Equal("applied", reinstall.Application);
        Assert.True(reinstall.Installed, reinstall.Diagnostic);
        Assert.False(reinstall.Selected);
        Assert.Equal("2.0.0", ReadHotDependencyVersion(host));
        Assert.Null(await ReadHotInstalledCodeAsync(host));
        Assert.Empty(await host.Toolchain.GetLoadedModulesAsync("IndependentPlugin"));
        var enabled = await owner.SetBundleEnabledAsync("IndependentPlugin", true);
        Assert.Equal("applied", enabled.Application);
        Assert.Equal("v2", (await ReadHotInstalledCodeAsync(host))!());
        Assert.Null(host.Session.LastError);
        Assert.False(host.Session.RequiresRestart);
    }

    [Fact]
    public async Task Hot_package_failed_activation_keeps_saved_state_and_can_remove_then_retry()
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        Assert.Equal(
            "applied",
            (await owner.InstallPackageAsync(host.Toolchain, new("IndependentPlugin", "1.0.0", Feed), "first", true))
            .Application);
        Assert.Equal("applied", (await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin")).Application);
        var failure = await owner.InstallPackageAsync(
            host.Toolchain,
            new("IndependentPlugin", "2.0.0", Feed),
            "bad-reinstall",
            true);
        Assert.Equal("failed", failure.Application);
        Assert.Equal("apply", failure.Stage);
        Assert.True(failure.Installed, failure.Diagnostic);
        Assert.True(failure.Selected);
        Assert.Contains("independent apply failure", failure.Diagnostic);
        Assert.Equal("2.0.0", ReadHotDependencyVersion(host));
        Assert.Contains("IndependentPlugin", PackageManifest.Read(Path.Combine(host.Profile, "package.json")).Bundles);
        Assert.True(Directory.Exists(host.Toolchain.Bundles["IndependentPlugin"]));
        Assert.Null(await ReadHotInstalledCodeAsync(host));

        var removed = await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin");
        Assert.Equal("applied", removed.Application);
        Assert.False(removed.Installed);
        var retry = await owner.InstallPackageAsync(
            host.Toolchain,
            new("IndependentPlugin", "1.0.0", Feed),
            "retry",
            true);
        Assert.Equal("applied", retry.Application);
        Assert.True(retry.Installed, retry.Diagnostic);
        Assert.Equal("v1", (await ReadHotInstalledCodeAsync(host))!());
        Assert.Equal("1.0.0", ReadHotDependencyVersion(host));
        Assert.Null(host.Session.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hot_package_removal_refuses_direct_or_foreign_fibers_before_saving(bool foreignEntry)
    {
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        Assert.Equal(
            "applied",
            (await owner.InstallPackageAsync(host.Toolchain, new("IndependentPlugin", "1.0.0", Feed), "first", true))
            .Application);
        var module = Assert.Single(await host.Toolchain.GetLoadedModulesAsync("IndependentPlugin"));
        Fiber? direct = null;
        var configuration = Path.Combine(Path.GetDirectoryName(host.Profile)!, "cordis.yml");
        if (foreignEntry)
        {
            await File.WriteAllTextAsync(
                configuration,
                "- id: foreign\n  name: nuget:independentplugin\n  isolate:\n    installed-code: true\n    installed-limit: true\n  config:\n    limit: 9\n");
            await host.Session.RefreshAsync();
            Assert.Null(host.Session.LastError);
            var entry = Assert.Single(host.Session.Loader.Entries(), entry => entry.Options.Id == "foreign");
            Assert.Same(module, entry.Module);
            Assert.Equal(FiberState.Active, entry.Fiber!.State);
        }
        else
            await host.Session.Context.RunAsync(async context =>
            {
                direct = context
                    .Isolate("installed-code")
                    .Isolate("installed-limit")
                    .Plugin(
                        module.Plugin,
                        new EntryOptions
                        {
                            ["limit"] = 9
                        });
                await direct.WaitAsync();
            });

        var manifest = await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json"));
        var retained = host.Toolchain.Bundles["IndependentPlugin"];
        try
        {
            var refused = await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin");
            Assert.Equal("restart-required", refused.Application);
            Assert.Equal("unmanaged-plugin", refused.RestartReason);
            Assert.True(refused.Installed);
            Assert.True(refused.Selected);
            Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json")));
            Assert.Equal(retained, host.Toolchain.Bundles["IndependentPlugin"]);
            Assert.Equal("v1", (await ReadHotInstalledCodeAsync(host))!());
        }
        finally
        {
            if (direct is not null)
                await host.Session.Context.RunAsync(async _ => await direct.DisposeAsync());
            if (foreignEntry)
            {
                await File.WriteAllTextAsync(configuration, "[]\n");
                await host.Session.RefreshAsync();
            }
        }

        Assert.Equal("applied", (await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin")).Application);
    }

    [Fact]
    public async Task Hot_package_reinstall_loads_changed_private_dependency_and_retains_old_delegate_behavior()
    {
        await PackHotDependencyVersionsAsync();
        await using var host = await StartAsync();
        var owner = host.Session.ConfigurationOperations;
        Assert.Equal(
            "applied",
            (await owner.InstallPackageAsync(
                host.Toolchain,
                new("IndependentPlugin", "1.0.0", Feed),
                "dependency-first",
                true)).Application);
        var original = Assert.IsType<Func<string>>(await ReadHotInstalledCodeAsync(host));
        var retained = host.Toolchain.Bundles["IndependentPlugin"];
        var dependencyBytes = await File.ReadAllBytesAsync(Path.Combine(retained, "IndependentDependency.dll"));
        Assert.Equal("dependency-v1", original());
        Assert.Equal("applied", (await owner.RemovePackageAsync(host.Toolchain, "IndependentPlugin")).Application);
        var reinstall = await owner.InstallPackageAsync(
            host.Toolchain,
            new("IndependentPlugin", "2.0.0", Feed),
            "dependency-reinstall",
            true);
        Assert.Equal("applied", reinstall.Application);
        Assert.Null(reinstall.Error);
        Assert.Equal("dependency-v2", (await ReadHotInstalledCodeAsync(host))!());
        Assert.Equal("dependency-v1", original());
        Assert.Equal(
            dependencyBytes,
            await File.ReadAllBytesAsync(Path.Combine(retained, "IndependentDependency.dll")));
        Assert.NotEqual(
            dependencyBytes,
            await File.ReadAllBytesAsync(
                Path.Combine(host.Toolchain.Bundles["IndependentPlugin"], "IndependentDependency.dll")));
        Assert.False(host.Session.RequiresRestart);
    }

    private async Task PackHotDependencyVersionsAsync()
    {
        var dependency = Directory.CreateDirectory(Path.Combine(directory, "dependency-author")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(dependency, "IndependentDependency.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>IndependentDependency</PackageId></PropertyGroup></Project>""");
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            await File.WriteAllTextAsync(
                Path.Combine(dependency, "Value.cs"),
                "public static class DependencyValue { public static string Read() => \"dependency-v" + version[0] +
                "\"; }");
            await RunAsync(dependency, "pack", "-c", "Release", "-o", Feed, "-p:Version=" + version);
        }

        var author = Path.Combine(directory, "author");
        var project = Path.Combine(author, "IndependentPlugin.csproj");
        var source = Path.Combine(author, "Plugin.cs");
        var originalProject = await File.ReadAllTextAsync(project);
        await File.WriteAllTextAsync(
            source,
            (await File.ReadAllTextAsync(source)).Replace("\"v1\"", "DependencyValue.Read()"));
        await File.WriteAllTextAsync(Path.Combine(author, "Reject.targets"), "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(author, "cordis.plugin.json"),
            """{"assembly":"IndependentPlugin.dll","entryType":"Entry"}""");
        foreach (var version in new[] { "1.0.0", "2.0.0" })
        {
            await File.WriteAllTextAsync(
                project,
                originalProject.Replace(
                    "</Project>",
                    "<ItemGroup><PackageReference Include=\"IndependentDependency\" Version=\"[" + version +
                    "]\" /></ItemGroup></Project>"));
            await RunAsync(author, "pack", "-c", "Release", "-o", Feed, "--source", Feed, "-p:Version=" + version);
        }
    }

    private static async Task<Func<string>?> ReadHotInstalledCodeAsync(Host host)
    {
        Func<string>? result = null;
        await host.Session.Context.RunAsync(context =>
        {
            result = context.Get<Func<string>>("installed-code");
            return Task.CompletedTask;
        });
        return result;
    }

    private static string? ReadHotDependencyVersion(Host host) =>
        (PackageManifest.Read(Path.Combine(host.Profile, "package.json")).Raw.GetValueOrDefault("dependencies")
            as IDictionary<string, object?>)?["IndependentPlugin"] as string;
}
