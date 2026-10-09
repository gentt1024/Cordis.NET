using System.Text.Json.Nodes;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed partial class PackageManagementTests
{
    [Fact]
    public async Task Consecutive_package_updates_preserve_live_code_and_new_resolver_uses_last_saved_version()
    {
        await PackUpdatedPluginAsync("2.0.0", "v2");
        await PackUpdatedPluginAsync("2.1.0", "v3");

        string profile;
        string replacement;
        await using (var host = await StartAsync())
        {
            profile = host.Profile;
            var owner = host.Session.ConfigurationOperations;
            Assert.Null(
                (await owner.InstallPackageAsync(host.Toolchain, new("IndependentPlugin", "1.0.0", Feed), "v1", true))
                .Error);
            var original = host.Toolchain.Bundles["IndependentPlugin"];
            var originalBytes = await File.ReadAllBytesAsync(Path.Combine(original, "IndependentPlugin.dll"));
            Func<string> callV1 = null!;
            await host.Session.Context.RunAsync(ctx =>
            {
                callV1 = ctx.Get<Func<string>>("installed-code")!;
                return Task.CompletedTask;
            });
            Assert.Equal("v1", callV1());
            var tools = new ObservedPackageToolchain(host.Toolchain);
            var update = await owner.InstallPackageAsync(tools, new("INDEPENDENTPLUGIN", "2.0.0", Feed), "v2", true);
            Assert.Null(update.Error);
            Assert.True(update.Installed, update.Diagnostic);
            Assert.True(update.Selected);
            Assert.Equal("restart-required", update.Application);
            Assert.Equal("IndependentPlugin", update.Target);
            replacement = tools.Prepared!.PublicationDirectory!;
            Assert.True(File.Exists(Path.Combine(replacement, "IndependentPlugin.dll")));
            Assert.Equal(original, host.Toolchain.Bundles["IndependentPlugin"]);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(Path.Combine(original, "IndependentPlugin.dll")));
            Assert.Equal(
                "2.0.0",
                JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "package.json")))!["dependencies"]![
                    "IndependentPlugin"]!.GetValue<string>());
            var repeated = await owner.InstallPackageAsync(
                tools,
                new("independentplugin", "2.0.0", Feed),
                "same-saved-version",
                true);
            Assert.Equal("already-installed", repeated.Error);
            Assert.Equal(1, tools.Publications);
            var next = await owner.InstallPackageAsync(tools, new("IndependentPlugin", "2.1.0", Feed), "v3", true);
            Assert.Null(next.Error);
            Assert.Equal("restart-required", next.Application);
            Assert.True(next.Selected);
            replacement = tools.Prepared!.PublicationDirectory!;
            Assert.Equal(
                "2.1.0",
                JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "package.json")))!["dependencies"]![
                    "IndependentPlugin"]!.GetValue<string>());
            Assert.Equal(original, host.Toolchain.Bundles["IndependentPlugin"]);
            Assert.Equal("v1", callV1());
            await host.Session.Context.RunAsync(ctx =>
            {
                Assert.Same(callV1, ctx.Get<Func<string>>("installed-code"));
                Assert.Equal("v1", ctx.Get<Func<string>>("installed-code")!());
                return Task.CompletedTask;
            });
        }

        await using var restarted = await StartAsync(profileDirectory: profile);
        Assert.Equal(replacement, restarted.Toolchain.Bundles["IndependentPlugin"]);
        await restarted.Session.Context.RunAsync(ctx =>
        {
            Assert.Equal("v3", ctx.Get<Func<string>>("installed-code")!());
            return Task.CompletedTask;
        });
        var removed = await restarted.Session.ConfigurationOperations.RemovePackageAsync(
            restarted.Toolchain,
            "IndependentPlugin");
        Assert.Null(removed.Error);
        Assert.False(removed.Installed);
        Assert.False(removed.Selected);
        foreach (var version in new[] { "1.0.0", "2.0.0", "2.1.0" })
        {
            var retained = Path.Combine(profile, ".cordis", "packages", "independentplugin", version);
            Assert.Contains(retained, removed.Residuals!);
            Assert.True(Directory.Exists(retained));
        }
    }

    [Fact]
    public async Task Failed_later_package_update_preserves_pending_version_and_live_code()
    {
        await PackUpdatedPluginAsync("2.0.0", "v2");
        await PackUpdatedPluginAsync("2.1.0", "v3");
        foreach (var boundary in new[]
                 {
                     "sdk", "admission", "prepared-content", "published-content", "existing-destination"
                 })
        {
            string profile;
            await using (var host = await StartAsync())
            {
                profile = host.Profile;
                var owner = host.Session.ConfigurationOperations;
                Assert.Null(
                    (await owner.InstallPackageAsync(
                        host.Toolchain,
                        new("IndependentPlugin", "1.0.0", Feed),
                        "v1",
                        true)).Error);
                Assert.Equal(
                    "restart-required",
                    (await owner.InstallPackageAsync(
                        host.Toolchain,
                        new("IndependentPlugin", "2.0.0", Feed),
                        "pending-v2",
                        true)).Application);
                var manifest = await File.ReadAllTextAsync(Path.Combine(profile, "package.json"));
                var original = host.Toolchain.Bundles["IndependentPlugin"];
                var tools = new ObservedPackageToolchain(host.Toolchain);
                string? residual = null;
                owner.AdmitProfileAsync = async candidate =>
                {
                    if (boundary == "admission")
                        throw new InvalidOperationException("replacement admission refused");
                    if (boundary == "prepared-content")
                        await File.WriteAllTextAsync(
                            Path.Combine(candidate.Package!.Directory, "partial.dll"),
                            "partial");
                };
                if (boundary == "published-content")
                    tools.AfterPublish = async package =>
                    {
                        residual = package.PublicationDirectory!;
                        await File.WriteAllTextAsync(Path.Combine(residual, "partial.dll"), "partial");
                    };
                if (boundary == "existing-destination")
                    tools.AfterPrepare = async package =>
                    {
                        residual = Directory.CreateDirectory(package.PublicationDirectory!).FullName;
                        await File.WriteAllTextAsync(
                            Path.Combine(residual, "partial.dll"),
                            "interrupted earlier attempt");
                    };
                var result = await owner.InstallPackageAsync(
                    tools,
                    new("IndependentPlugin", boundary == "sdk" ? "3.0.0" : "2.1.0", Feed),
                    "failed-update",
                    true);
                Assert.Equal("failed", result.Application);
                Assert.True(result.Installed);
                Assert.True(result.Selected);
                Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(profile, "package.json")));
                Assert.Equal(original, host.Toolchain.Bundles["IndependentPlugin"]);
                switch (boundary)
                {
                    case "sdk":
                        Assert.Contains("package-build-refused", result.Diagnostic);
                        break;
                    case "admission":
                        Assert.Contains("replacement admission refused", result.Diagnostic);
                        break;
                    case "existing-destination":
                        Assert.Contains("already exists", result.Diagnostic);
                        Assert.Equal(
                            "interrupted earlier attempt",
                            await File.ReadAllTextAsync(Path.Combine(residual!, "partial.dll")));
                        Assert.False(File.Exists(Path.Combine(residual!, "IndependentPlugin.dll")));
                        break;
                    default:
                        Assert.Equal("profile-conflict", result.Error);
                        break;
                }

                if (residual is not null)
                    Assert.Contains(residual, result.Residuals!);
                await host.Session.Context.RunAsync(ctx =>
                {
                    Assert.Equal("v1", ctx.Get<Func<string>>("installed-code")!());
                    return Task.CompletedTask;
                });
            }

            await using var restarted = await StartAsync(profileDirectory: profile);
            await restarted.Session.Context.RunAsync(ctx =>
            {
                Assert.Equal("v2", ctx.Get<Func<string>>("installed-code")!());
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task Package_updates_preserve_saved_selection_without_changing_running_selection()
    {
        await PackUpdatedPluginAsync("2.0.0", "v2");
        foreach (var (initialEnabled, updateEnabled) in new[] { (true, false), (false, false), (false, true) })
        {
            string profile;
            var selected = initialEnabled || updateEnabled;
            await using (var host = await StartAsync())
            {
                profile = host.Profile;
                var owner = host.Session.ConfigurationOperations;
                Assert.Null(
                    (await owner.InstallPackageAsync(
                        host.Toolchain,
                        new("IndependentPlugin", "1.0.0", Feed),
                        "initial",
                        true,
                        enabled: initialEnabled)).Error);
                var update = await owner.InstallPackageAsync(
                    host.Toolchain,
                    new("IndependentPlugin", "2.0.0", Feed),
                    "update",
                    true,
                    enabled: updateEnabled);
                Assert.Null(update.Error);
                Assert.Equal("restart-required", update.Application);
                Assert.Equal(selected, update.Selected);
                Assert.Equal(
                    selected,
                    PackageManifest.Read(Path.Combine(profile, "package.json")).Bundles.Contains("IndependentPlugin"));
                await host.Session.Context.RunAsync(ctx =>
                {
                    var code = ctx.Get<Func<string>>("installed-code");
                    Assert.Equal(initialEnabled ? "v1" : null, code?.Invoke());
                    return Task.CompletedTask;
                });
            }

            await using var restarted = await StartAsync(profileDirectory: profile);
            await restarted.Session.Context.RunAsync(ctx =>
            {
                var code = ctx.Get<Func<string>>("installed-code");
                Assert.Equal(selected ? "v2" : null, code?.Invoke());
                return Task.CompletedTask;
            });
        }
    }

    private async Task PackUpdatedPluginAsync(string version, string codeVersion)
    {
        var author = Path.Combine(directory, "author");
        var source = Path.Combine(author, "Plugin.cs");
        var original = await File.ReadAllTextAsync(source);
        await File.WriteAllTextAsync(source, original.Replace("\"v1\"", "\"" + codeVersion + "\""));
        await File.WriteAllTextAsync(Path.Combine(author, "Reject.targets"), "<Project />");
        await File.WriteAllTextAsync(
            Path.Combine(author, "cordis.plugin.json"),
            """{"assembly":"IndependentPlugin.dll","entryType":"Entry"}""");
        try
        {
            await RunAsync(author, "pack", "-c", "Release", "-o", Feed, "-p:Version=" + version);
        }
        finally
        {
            await File.WriteAllTextAsync(source, original);
        }
    }
}
