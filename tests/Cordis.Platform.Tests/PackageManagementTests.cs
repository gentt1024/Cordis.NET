using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cordis.AspNetCore;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Cordis.Platform.Tests;

[Collection("Collectible CLR")]
public sealed class PackageManagementTests : IAsyncLifetime
{
    private readonly string directory = Directory.CreateTempSubdirectory("cordis-package-consumer-").FullName;
    private string Feed => Path.Combine(directory, "feed");

    public async Task InitializeAsync()
    {
        var author = Directory.CreateDirectory(Path.Combine(directory, "author")).FullName;
        Directory.CreateDirectory(Feed);
        string Reference(Type type) => $"<Reference Include=\"{type.Assembly.GetName().Name}\"><HintPath>{SecurityElement.Escape(type.Assembly.Location)}</HintPath><Private>false</Private></Reference>";
        await File.WriteAllTextAsync(Path.Combine(author, "IndependentPlugin.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><PackageId>IndependentPlugin</PackageId><Version>1.0.0</Version></PropertyGroup>
              <ItemGroup>{{Reference(typeof(IPlugin))}}{{Reference(typeof(IClrPluginModule))}}{{Reference(typeof(ConfigObject<>))}}
                <None Include="cordis.plugin.json" Pack="true" PackagePath="/" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(author, "Plugin.cs"), """
            using Cordis;
            using Cordis.Clr;
            using Cordis.Composition;
            public sealed class Entry : IClrPluginModule
            {
                public IPlugin CreatePlugin() => new Plugin<int>
                {
                    Configuration = ConfigObject<int>.Create(raw => ConfigResult<int>.Success(
                        raw is IReadOnlyDictionary<string, object?> map && map.TryGetValue("limit", out var limit) ? Convert.ToInt32(limit) : 1))
                        .Field("limit", ConfigDescriptor.Number().Volatile(), value => value).Build(),
                    Apply = (ctx, limit) =>
                    {
                        if (limit < 0) throw new InvalidOperationException("independent apply failure");
                        ctx.Provide("installed-limit", ctx.Fiber.GetConfigReference<int>("limit"));
                    },
                };
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(author, "cordis.plugin.json"), """{"assembly":"IndependentPlugin.dll","entryType":"Entry"}""");
        await RunAsync(author, "pack", "-c", "Release", "-o", Feed);
        await File.WriteAllTextAsync(Path.Combine(author, "cordis.plugin.json"), """
            {"assembly":"IndependentPlugin.dll","entryType":"Entry","patches":[{"insert":[{"id":"independentplugin","name":"nuget:independentplugin","config":{"limit":-1}}]}]}
            """);
        await RunAsync(author, "pack", "-c", "Release", "--no-build", "-o", Feed, "-p:Version=2.0.0");
        await File.WriteAllTextAsync(Path.Combine(author, "Reject.targets"), """
            <Project><Target Name="RejectPackageBuild" BeforeTargets="Build"><Error Text="package-build-refused" /></Target></Project>
            """);
        var projectPath = Path.Combine(author, "IndependentPlugin.csproj");
        await File.WriteAllTextAsync(projectPath, (await File.ReadAllTextAsync(projectPath)).Replace("</ItemGroup>",
            "<None Include=\"Reject.targets\" Pack=\"true\" PackagePath=\"buildTransitive/IndependentPlugin.targets\" /></ItemGroup>"));
        await RunAsync(author, "pack", "-c", "Release", "--no-build", "-o", Feed, "-p:Version=3.0.0");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Known_incompatible_peers_are_refused_before_build_even_when_not_selected(bool enabled)
    {
        var marker = await PackIncompatibleAsync();
        await using var host = await StartAsync(runtime: new DshRuntimeIdentity("0.2.0-rc.2"));
        var manifest = await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json"));
        var phases = new List<string>();
        host.Session.ConfigurationOperations.PackageProgressed += progress => phases.Add(progress.Phase);
        var result = await host.Session.ConfigurationOperations.InstallPackageAsync(host.Toolchain,
            new("IndependentPlugin", "5.0.0", Feed), "incompatible", buildApproved: true, enabled: enabled);
        Assert.False(result.Installed, result.Diagnostic);
        Assert.Contains("incompatible DSH peers", result.Diagnostic);
        Assert.False(File.Exists(marker), "An incompatible package executed its MSBuild target before admission.");
        Assert.DoesNotContain("prepare", phases);
        Assert.False(Directory.Exists(Path.Combine(host.Profile, ".cordis", "work")));
        Assert.Equal(manifest, await File.ReadAllTextAsync(Path.Combine(host.Profile, "package.json")));
        Assert.Empty(host.Session.SelectedBundles);
    }

    [Theory]
    [InlineData("IndependentPlugin")]
    [InlineData("independentplugin")]
    public async Task Nuget_compatibility_grants_use_one_exact_identity_across_http_install_and_restart(string spelling)
    {
        var marker = await PackIncompatibleAsync();
        var runtime = new DshRuntimeIdentity("0.2.0-rc.2");
        string profile;
        string deployedManifest;
        string manifestText;
        var cli = Path.Combine(AppContext.BaseDirectory, "fixtures", "cli", "Cordis.Cli.dll");
        await using (var host = await StartAsync(runtime: runtime))
        {
            profile = host.Profile;
            var operations = host.Session.ConfigurationOperations;
            var request = new PackageRequest("IndependentPlugin", "5.0.0", Feed);
            var neither = await operations.InstallPackageAsync(host.Toolchain, request, "neither", buildApproved: false);
            Assert.Contains("incompatible DSH peers", neither.Diagnostic);
            Assert.False(File.Exists(marker));
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var server = builder.Build();
            var permissions = new List<ManagementPermission>();
            new CordisManagement(host.Session, (_, permission) =>
            {
                permissions.Add(permission);
                return Task.FromResult(true);
            }, _ => new SettingsPolicy([]), host.Toolchain).Map(server);
            await server.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(server.Urls.Single()) };
            using var state = JsonDocument.Parse(await client.GetStringAsync("/cordis/state"));
            client.DefaultRequestHeaders.Add("If-Cordis-Generation", state.RootElement.GetProperty("generation").GetString());
            using var grant = await client.PostAsync("/cordis/compatibility", new StringContent(JsonSerializer.Serialize(new
            {
                packageVersion = spelling + "@5.0.0", runtimeVersion = runtime.Version, enabled = true, acceptRisk = true,
            }), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, grant.StatusCode);
            using var saved = JsonDocument.Parse(await grant.Content.ReadAsStringAsync());
            Assert.True(saved.RootElement.GetProperty("error").ValueKind == JsonValueKind.Null, saved.RootElement.ToString());
            Assert.Equal("independentplugin@5.0.0", permissions.Last().Target);
            Assert.Contains("independentplugin@5.0.0", await File.ReadAllTextAsync(Path.Combine(profile, DshProfilePolicy.CompatibilityFilename)));
            var otherVersion = await operations.InstallPackageAsync(host.Toolchain, request with { Version = "5.0.1" }, "other-version", buildApproved: true);
            Assert.Contains("incompatible DSH peers", otherVersion.Diagnostic);
            Assert.False(File.Exists(marker));
            var notApproved = await operations.InstallPackageAsync(host.Toolchain, request, "no-build", buildApproved: false);
            Assert.Contains("build-execution approval", notApproved.Diagnostic);
            Assert.False(File.Exists(marker));
            var endpoint = server.Urls.Single() + "/cordis";
            await RunAsync(directory, cli, "install", endpoint, request.Name, request.Version, "--source", Feed, "--approve-build");
            Assert.True(File.Exists(marker));
            await host.Session.Context.RunAsync(ctx =>
            {
                Assert.Equal(1, ctx.Get<ConfigReference<int>>("installed-limit")!.Value);
                return Task.CompletedTask;
            });
            var alias = await operations.InstallPackageAsync(host.Toolchain, request with { Name = "INDEPENDENTPLUGIN" }, "alias", true);
            Assert.Equal("already-installed", alias.Error);
            Assert.Single(PackageManifest.Read(Path.Combine(profile, "package.json")).Bundles);
            deployedManifest = Path.Combine(host.Toolchain.Bundles[request.Name], "package.json");
            manifestText = await File.ReadAllTextAsync(deployedManifest);
            Assert.Equal("IndependentPlugin", PackageManifest.Read(deployedManifest).Raw["name"]);
            // Grant aliases converge to one exact key through the real CLI as well.
            await RunAsync(directory, cli, "grant", endpoint, "INDEPENDENTPLUGIN@5.0.0", runtime.Version, "true", "true");
            Assert.Single(operations.ReadVersionCompatibility().Exemptions);
        }
        // Reopening the actual mixed-spelling deployment must not rewrite its manifest.
        await using (var restarted = await StartAsync(runtime: runtime, profileDirectory: profile))
        {
            Assert.Equal(["IndependentPlugin"], restarted.Session.LoadedBundles);
            Assert.Equal(manifestText, await File.ReadAllTextAsync(deployedManifest));
            var manifest = PackageManifest.Read(deployedManifest);
            var grants = restarted.Session.ConfigurationOperations.ReadVersionCompatibility().Exemptions;
            Assert.False(DshProfilePolicy.EvaluateCompatibility(manifest, runtime, grants)!.Exempted);
            var changed = new EntryOptions(manifest.Raw) { ["version"] = "5.0.0-RC" };
            var prereleaseGrants = new Dictionary<string, IReadOnlyList<string>> { ["independentplugin@5.0.0-rc"] = [runtime.Version] };
            Assert.False(DshProfilePolicy.EvaluateCompatibility(new(changed), runtime, prereleaseGrants,
                DotnetPluginToolchain.NormalizeCompatibilityPackageName)!.Exempted);
        }
        var directConfiguration = Path.Combine(Path.GetDirectoryName(profile)!, "cordis.yml");
        await File.WriteAllTextAsync(directConfiguration, "- id: direct\n  name: nuget:independentplugin\n");
        await using (var changedRuntime = await StartAsync(runtime: new("0.2.0-rc.3"), profileDirectory: profile))
        {
            Assert.Empty(changedRuntime.Session.LoadedBundles);
            Assert.Contains(changedRuntime.Session.SkippedBundles, item => item.Name == "IndependentPlugin");
            Assert.True(changedRuntime.Session.Loader.Resolve("root:direct").Disabled);
            Assert.Contains("nuget:independentplugin", await File.ReadAllTextAsync(directConfiguration));
        }
        await using (var restarted = await StartAsync(runtime: runtime, profileDirectory: profile))
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var server = builder.Build();
            new CordisManagement(restarted.Session, (_, _) => Task.FromResult(true), _ => new SettingsPolicy([]), restarted.Toolchain).Map(server);
            await server.StartAsync();
            await RunAsync(directory, cli, "grant", server.Urls.Single() + "/cordis", spelling + "@5.0.0", runtime.Version, "false", "false");
            Assert.Empty(restarted.Session.ConfigurationOperations.ReadVersionCompatibility().Exemptions);
            Assert.Empty(restarted.Session.LoadedBundles);
            Assert.True(restarted.Session.Loader.Resolve("root:direct").Disabled);
        }
        await using var revoked = await StartAsync(runtime: runtime, profileDirectory: profile);
        Assert.Empty(revoked.Session.LoadedBundles);
        Assert.Equal(manifestText, await File.ReadAllTextAsync(deployedManifest));
    }

    [Fact]
    public async Task Generic_nuget_profile_does_not_apply_dsh_peer_policy()
    {
        var marker = await PackIncompatibleAsync();
        await using var host = await StartAsync();
        var result = await host.Session.ConfigurationOperations.InstallPackageAsync(host.Toolchain,
            new("IndependentPlugin", "5.0.0", Feed), "generic", buildApproved: true);
        Assert.Equal("applied", result.Application);
        Assert.True(result.Installed, result.Diagnostic);
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(host.Profile, DshProfilePolicy.CompatibilityFilename)));
    }

    private async Task<string> PackIncompatibleAsync()
    {
        var author = Path.Combine(directory, "author");
        var marker = Path.Combine(directory, "incompatible-build-marker.txt");
        await File.WriteAllTextAsync(Path.Combine(author, "cordis.plugin.json"), """
            {"assembly":"IndependentPlugin.dll","entryType":"Entry","peerDependencies":{"@deepseek-ai/dsh":"^9.0.0"}}
            """);
        await File.WriteAllTextAsync(Path.Combine(author, "Reject.targets"), $$"""
            <Project><Target Name="ObservePackageBuild" BeforeTargets="Build"><WriteLinesToFile File="{{SecurityElement.Escape(marker)}}" Lines="SDK executed" Overwrite="true" /></Target></Project>
            """);
        await RunAsync(author, "pack", "-c", "Release", "--no-build", "-o", Feed, "-p:Version=5.0.0");
        await RunAsync(author, "pack", "-c", "Release", "--no-build", "-o", Feed, "-p:Version=5.0.1");
        Assert.False(File.Exists(marker));
        return marker;
    }

    [Fact]
    public async Task Independent_nuget_plugin_installs_updates_configuration_and_removes_through_session()
    {
        await using var host = await StartAsync();
        var unload = await RunLifecycleAsync(host);
        for (var attempt = 0; attempt < 12 && !unload.IsCollected; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
        Assert.True(unload.IsCollected);
        Assert.True(unload.TryDeleteShadow());
    }

    private static async Task<ClrUnloadObservation> RunLifecycleAsync(Host host)
    {
        var operations = host.Session.ConfigurationOperations;
        var request = new PackageRequest("IndependentPlugin", "1.0.0", host.Toolchain.Sources[0]);
        var inspected = await host.Toolchain.InspectAsync(request);
        Assert.True(inspected.RequiresBuildApproval);
        Assert.Contains("1.0.0", await host.Toolchain.VersionsAsync(request.Name, request.Source));
        var denied = await operations.InstallPackageAsync(host.Toolchain, request, "denied", false);
        Assert.False(denied.Installed);
        Assert.Empty(PackageManifest.Read(Path.Combine(host.Profile, "package.json")).Bundles);
        var installed = await operations.InstallPackageAsync(host.Toolchain, request, "install", true);
        Assert.True(installed.Installed, installed.Diagnostic);
        Assert.True(installed.Selected);
        Assert.Equal("applied", installed.Application);
        var phases = new List<string>();
        void Phase(PackageProgress progress) => phases.Add(progress.Phase);
        operations.PackageProgressed += Phase;
        var alias = await operations.InstallPackageAsync(host.Toolchain, request with { Name = request.Name.ToLowerInvariant() }, "alias", true);
        operations.PackageProgressed -= Phase;
        Assert.Equal("already-installed", alias.Error);
        Assert.True(alias.Installed);
        Assert.True(alias.Selected);
        Assert.DoesNotContain("prepare", phases);
        await host.Session.Context.RunAsync(ctx =>
        {
            Assert.Equal(1, ctx.Get<ConfigReference<int>>("installed-limit")!.Value);
            return Task.CompletedTask;
        });
        var snapshot = await operations.ReadConfigurationAsync("root:independentplugin");
        var edit = await operations.EditConfigurationFieldAsync("root:independentplugin", ["limit"], 3, snapshot.Revision);
        Assert.True(edit.Applied, edit.Diagnostic);
        await host.Session.Context.RunAsync(ctx =>
        {
            Assert.Equal(3, ctx.Get<ConfigReference<int>>("installed-limit")!.Value);
            return Task.CompletedTask;
        });
        using var interrupt = new CancellationTokenSource();
        void InterruptRemoval(PackageProgress progress)
        {
            if (progress.Phase == "remove") interrupt.Cancel();
        }
        operations.PackageProgressed += InterruptRemoval;
        var interrupted = await operations.RemovePackageAsync(host.Toolchain, request.Name, interrupt.Token);
        operations.PackageProgressed -= InterruptRemoval;
        Assert.Equal("cancelled", interrupted.Application);
        Assert.True(interrupted.Installed);
        Assert.False(interrupted.Selected);
        Assert.True(Directory.Exists(host.Toolchain.Bundles[request.Name]));
        var removed = await operations.RemovePackageAsync(host.Toolchain, request.Name.ToLowerInvariant());
        Assert.False(removed.Installed, removed.Diagnostic);
        Assert.False(removed.Selected);
        Assert.Equal("applied", removed.Application);
        await host.Session.Context.RunAsync(ctx =>
        {
            Assert.Null(ctx.Get("installed-limit"));
            return Task.CompletedTask;
        });
        Assert.False(Directory.Exists(host.Toolchain.Bundles[request.Name]));
        var unload = Assert.Single(host.Resolver.Unloads);
        Assert.True(unload.UnloadRequested);
        Assert.Null(await operations.WaitForInstallAsync("install"));
        Assert.Equal("not-running", await operations.CancelInstallAsync("install"));
        return unload;
    }

    [Fact]
    public async Task Unselected_valid_source_is_refused_before_sdk_preparation()
    {
        var unselected = Directory.CreateDirectory(Path.Combine(directory, "unselected-feed")).FullName;
        File.Copy(Path.Combine(Feed, "IndependentPlugin.1.0.0.nupkg"), Path.Combine(unselected, "IndependentPlugin.1.0.0.nupkg"));
        await using var host = await StartAsync();
        var manifest = await File.ReadAllBytesAsync(Path.Combine(host.Profile, "package.json"));
        var progress = new List<PackageProgress>();
        host.Session.ConfigurationOperations.PackageProgressed += progress.Add;

        var result = await host.Session.ConfigurationOperations.InstallPackageAsync(host.Toolchain,
            new("IndependentPlugin", "1.0.0", unselected), "unselected-source", buildApproved: true);

        Assert.Equal("failed", result.Application);
        Assert.Equal("inspect", result.Stage);
        Assert.Contains("not in the host-selected source list", result.Diagnostic);
        Assert.False(result.Installed);
        Assert.False(result.Selected);
        Assert.DoesNotContain(progress, row => row.Phase == "prepare" || row.Output is not null);
        await AssertUnpreparedAsync(host, manifest);
    }

    [Fact]
    public async Task Replaced_inspected_archive_is_refused_before_sdk_preparation()
    {
        var selected = Directory.CreateDirectory(Path.Combine(directory, "mutable-feed")).FullName;
        var package = Path.Combine(selected, "IndependentPlugin.1.0.0.nupkg");
        File.Copy(Path.Combine(Feed, "IndependentPlugin.1.0.0.nupkg"), package);
        await using var host = await StartAsync([selected]);
        var operations = host.Session.ConfigurationOperations;
        var manifest = await File.ReadAllBytesAsync(Path.Combine(host.Profile, "package.json"));
        var request = new PackageRequest("IndependentPlugin", "1.0.0", selected);
        var inspected = await host.Toolchain.InspectAsync(request);
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Update))
        {
            archive.GetEntry("cordis.plugin.json")!.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("cordis.plugin.json").Open());
            writer.Write("""{"assembly":"IndependentPlugin.dll","entryType":"Entry","description":"replaced after inspection"}""");
        }
        var replacement = await host.Toolchain.InspectAsync(request);
        Assert.Equal("replaced after inspection", replacement.Description);
        Assert.NotEqual(inspected.ContentHash, replacement.ContentHash);
        var progress = new List<PackageProgress>();
        operations.PackageProgressed += progress.Add;

        var result = await operations.InstallPackageAsync(host.Toolchain,
            request with { ExpectedHash = inspected.ContentHash }, "changed-root", buildApproved: true);

        Assert.Equal("failed", result.Application);
        Assert.Equal("inspection-changed", result.Error);
        Assert.Equal("inspect", result.Stage);
        Assert.False(result.Installed);
        Assert.False(result.Selected);
        Assert.DoesNotContain(progress, row => row.Phase == "prepare" || row.Output is not null);
        // Direct library consumers carry the same inspection into preparation, which rechecks acquisition.
        var output = new List<string>();
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Toolchain.PrepareAsync(inspected, true, output.Add));
        Assert.Contains("changed after inspection", refused.Message);
        Assert.Empty(output);
        await AssertUnpreparedAsync(host, manifest);
    }

    [Fact]
    public async Task Actual_management_host_build_policy_refuses_before_sdk_preparation()
    {
        await using var host = await StartAsync();
        var operations = host.Session.ConfigurationOperations;
        var manifest = await File.ReadAllBytesAsync(Path.Combine(host.Profile, "package.json"));
        var permissions = new List<ManagementPermission>();
        var progress = new List<PackageProgress>();
        operations.PackageProgressed += progress.Add;
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        new CordisManagement(host.Session, (_, permission) =>
        {
            permissions.Add(permission);
            return Task.FromResult(permission.Operation != "build");
        }, _ => new SettingsPolicy([]), host.Toolchain).Map(server);
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(server.Urls.Single()) };
        using var state = JsonDocument.Parse(await client.GetStringAsync("/cordis/state"));
        client.DefaultRequestHeaders.Add("If-Cordis-Generation", state.RootElement.GetProperty("generation").GetString());
        var request = new EntryOptions { ["name"] = "IndependentPlugin", ["version"] = "1.0.0", ["source"] = Feed };
        using var inspectResponse = await client.PostAsync("/cordis/inspect", JsonContent(request));
        inspectResponse.EnsureSuccessStatusCode();
        using var inspection = JsonDocument.Parse(await inspectResponse.Content.ReadAsStringAsync());
        var hash = inspection.RootElement.GetProperty("hash").GetString();
        request["inspectedHash"] = hash;
        request["requestId"] = "denied-by-host";

        using var response = await client.PostAsync("/cordis/install", JsonContent(request));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(permissions, permission => permission.Operation == "install");
        var build = Assert.Single(permissions, permission => permission.Operation == "build");
        Assert.Equal("IndependentPlugin", build.Target);
        Assert.Equal(hash, build.Package!.ContentHash);
        Assert.True(build.Package.RequiresBuildApproval);
        Assert.Empty(progress);
        Assert.Null(await operations.WaitForInstallAsync("denied-by-host"));
        await AssertUnpreparedAsync(host, manifest);
    }

    private static StringContent JsonContent(EntryOptions value)
        => new(ConfigurationFile.Write(value, true), Encoding.UTF8, "application/json");

    private static async Task AssertUnpreparedAsync(Host host, byte[] originalManifest)
    {
        Assert.Equal(originalManifest, await File.ReadAllBytesAsync(Path.Combine(host.Profile, "package.json")));
        Assert.Empty(host.Toolchain.Bundles);
        Assert.Empty(host.Session.SelectedBundles);
        // The real adapter creates staging before starting the SDK; rejection leaves neither staging nor an owner record.
        Assert.False(Directory.Exists(Path.Combine(host.Profile, ".cordis", "work")));
        Assert.False(Directory.Exists(Path.Combine(host.Profile, ".cordis", "packages")));
        Assert.False(File.Exists(Path.Combine(host.Profile, ".cordis", "package-run.json")));
    }

    [Fact]
    public async Task Failed_sdk_build_cannot_select_or_publish_the_package()
    {
        await using var host = await StartAsync();
        using var missingSdk = new DotnetPluginToolchain(host.Profile, host.Resolver, [Feed], "missing-sdk-" + Guid.NewGuid().ToString("N"));
        var startFailure = await host.Session.ConfigurationOperations.InstallPackageAsync(missingSdk,
            new("IndependentPlugin", "3.0.0", Feed), "missing-sdk", true);
        Assert.Equal("failed", startFailure.Application);
        Assert.False(startFailure.Installed);
        // A confirmed failure to start must not poison the next real SDK invocation.
        var result = await host.Session.ConfigurationOperations.InstallPackageAsync(host.Toolchain,
            new("IndependentPlugin", "3.0.0", Feed), "build-failure", true);
        Assert.Equal("failed", result.Application);
        Assert.Equal("prepare", result.Stage);
        Assert.Contains("package-build-refused", result.Diagnostic);
        Assert.NotNull(result.ToolExitCode);
        Assert.NotEqual(0, result.ToolExitCode);
        Assert.True(Directory.Exists(Assert.Single(result.Residuals!)));
        Assert.False(result.Installed);
        Assert.False(result.Selected);
        Assert.Empty(PackageManifest.Read(Path.Combine(host.Profile, "package.json")).Bundles);
        Assert.Empty(host.Toolchain.Bundles);
    }

    [Fact]
    public async Task Corrupt_patch_does_not_remove_a_previously_identified_management_bundle()
    {
        await using var host = await StartAsync();
        var operations = host.Session.ConfigurationOperations;
        var installed = await operations.InstallPackageAsync(host.Toolchain, new("IndependentPlugin", "1.0.0", Feed), "protected", true);
        Assert.True(installed.Installed, installed.Diagnostic);
        operations.ProtectedModules.Add("nuget:independentplugin");
        Assert.False(Assert.Single(await operations.ListBundlesAsync()).Removable);
        await File.WriteAllTextAsync(Path.Combine(host.Toolchain.Bundles["IndependentPlugin"], "cordis.patch.yml"), "[");
        var refused = await operations.RemovePackageAsync(host.Toolchain, "IndependentPlugin");
        Assert.Equal("management-required", refused.Error);
        Assert.True(refused.Installed);
        Assert.True(refused.Selected);
        Assert.True(Directory.Exists(host.Toolchain.Bundles["IndependentPlugin"]));
    }

    [Fact]
    public async Task Session_closed_inside_queue_rejects_installation_before_inspection()
    {
        await using var host = await StartAsync();
        await host.Session.ConfigurationOperations.RunExclusiveAsync!(() => host.Session.DisposeAsync().AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => host.Session.ConfigurationOperations.InstallPackageAsync(host.Toolchain,
            new("IndependentPlugin", "1.0.0", "source-that-must-not-be-read"), "closed", true));
        Assert.Empty(host.Toolchain.Bundles);
    }

    [Fact]
    public async Task Apply_failure_retains_installed_and_selected_state_for_diagnosis_and_removal()
    {
        await using var host = await StartAsync();
        var result = await host.Session.ConfigurationOperations.InstallPackageAsync(host.Toolchain,
            new("IndependentPlugin", "2.0.0", Feed), "bad", true);
        Assert.True(result.Installed, result.Diagnostic);
        Assert.True(result.Selected);
        Assert.Equal("failed", result.Application);
        Assert.Equal("apply", result.Stage);
        Assert.Contains("independent apply failure", result.Diagnostic);
        Assert.True(Directory.Exists(host.Toolchain.Bundles["IndependentPlugin"]));
        var removed = await host.Session.ConfigurationOperations.RemovePackageAsync(host.Toolchain, "IndependentPlugin");
        Assert.False(removed.Installed, removed.Diagnostic);
        Assert.False(removed.Selected);
    }

    [Fact]
    public async Task Cancelling_running_sdk_preparation_drains_before_wait_completes_and_does_not_select()
    {
        await PrepareLongBuildAsync();
        await using var host = await StartAsync();
        var operations = host.Session.ConfigurationOperations;
        var started = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transcript = new StringBuilder();
        operations.PackageProgressed += progress =>
        {
            if (progress.Output is not null)
            {
                lock (transcript)
                {
                    transcript.Append(progress.Output);
                    var match = Regex.Match(transcript.ToString(), @"package-helper:(\d+)\r?\n");
                    if (match.Success) started.TrySetResult(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        };
        var install = operations.InstallPackageAsync(host.Toolchain, new("IndependentPlugin", "4.0.0", Feed), "cancel", true);
        var waiting = operations.WaitForInstallAsync("cancel");
        var childId = await started.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("cancelled", await operations.CancelInstallAsync("cancel"));
        var result = await install;
        Assert.Equal("cancelled", result.Application);
        Assert.False(result.Installed);
        Assert.False(result.Selected);
        Assert.Same(result, await waiting);
        Assert.Empty(PackageManifest.Read(Path.Combine(host.Profile, "package.json")).Bundles);
        Assert.Empty(host.Toolchain.Bundles);
        try
        {
            using var child = Process.GetProcessById(childId);
            Assert.True(child.HasExited, "The package child must exit before cancellation completes.");
        }
        catch (ArgumentException) { }
        var pulse = Path.Combine(Assert.Single(result.Residuals!), "tool-pulse.txt");
        var stopped = await File.ReadAllTextAsync(pulse);
        await Task.Delay(650);
        Assert.Equal(stopped, await File.ReadAllTextAsync(pulse));
    }

    private async Task PrepareLongBuildAsync()
    {
        var helper = Directory.CreateDirectory(Path.Combine(directory, "helper")).FullName;
        await File.WriteAllTextAsync(Path.Combine(helper, "BuildHelper.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(helper, "Program.cs"), """
            File.WriteAllText(args[0], "started");
            Console.WriteLine("package-helper:" + Environment.ProcessId);
            for (var index = 0; index < 60; index++)
            {
                Thread.Sleep(500);
                File.AppendAllText(args[0], ".");
            }
            """);
        await RunAsync(helper, "build", "-c", "Release");
        var author = Path.Combine(directory, "author");
        await File.WriteAllTextAsync(Path.Combine(author, "Reject.targets"), """
            <Project><Target Name="RunPackageBuild" BeforeTargets="Build"><Exec Command="dotnet &amp;quot;$(MSBuildThisFileDirectory)tools/BuildHelper.dll&amp;quot; &amp;quot;$(MSBuildProjectDirectory)/tool-pulse.txt&amp;quot;" /></Target></Project>
            """.Replace("&amp;quot;", "&quot;", StringComparison.Ordinal));
        var project = Path.Combine(author, "IndependentPlugin.csproj");
        var tools = SecurityElement.Escape(Path.Combine(helper, "bin", "Release", "net10.0"));
        await File.WriteAllTextAsync(project, (await File.ReadAllTextAsync(project)).Replace("</ItemGroup>",
            $"<None Include=\"{tools}/BuildHelper.*\" Pack=\"true\" PackagePath=\"buildTransitive/tools/\" /></ItemGroup>"));
        await RunAsync(author, "pack", "-c", "Release", "--no-build", "-o", Feed, "-p:Version=4.0.0");
    }

    [Fact]
    public async Task Cli_host_installs_and_removes_the_independent_author_package_through_management_transport()
    {
        var profile = Path.Combine(directory, "cli-profile");
        var cli = Path.Combine(AppContext.BaseDirectory, "fixtures", "cli", "Cordis.Cli.dll");
        var authorization = "Bearer " + Guid.NewGuid().ToString("N");
        Process Start(params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true };
            start.Environment["CORDIS_TEST_AUTHORIZATION"] = authorization;
            start.ArgumentList.Add(cli);
            foreach (var value in arguments) start.ArgumentList.Add(value);
            return Process.Start(start)!;
        }
        async Task<(int ExitCode, string Output)> Command(params string[] arguments)
        {
            using var process = Start(arguments);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90));
            return (process.ExitCode, await output + await error);
        }
        using var host = Start("run", profile, "--source", Feed, "--url", "http://127.0.0.1:0",
            "--authorization-env", "CORDIS_TEST_AUTHORIZATION", "--allow-build", "IndependentPlugin@1.0.0",
            "--settings", "root:independentplugin=limit");
        var stderr = host.StandardError.ReadToEndAsync();
        try
        {
            string? endpoint = null;
            while (await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)) is { } line)
                if (line.StartsWith("Management: ", StringComparison.Ordinal)) { endpoint = line[12..]; break; }
            Assert.NotNull(endpoint);
            var remaining = host.StandardOutput.ReadToEndAsync();
            var secondHost = await Command("run", profile, "--source", Feed);
            Assert.NotEqual(0, secondHost.ExitCode);
            var denied = await Command("install", endpoint, "IndependentPlugin", "1.0.0", "--source", Feed,
                "--authorization-env", "CORDIS_TEST_AUTHORIZATION");
            Assert.NotEqual(0, denied.ExitCode);
            var installed = await Command("install", endpoint, "IndependentPlugin", "1.0.0", "--source", Feed,
                "--approve-build", "--authorization-env", "CORDIS_TEST_AUTHORIZATION");
            Assert.True(installed.ExitCode == 0, installed.Output);
            var settings = await Command("settings", endpoint, "root:independentplugin", "--authorization-env", "CORDIS_TEST_AUTHORIZATION");
            Assert.True(settings.ExitCode == 0, settings.Output);
            Assert.Contains("limit", settings.Output);
            var removed = await Command("remove", endpoint, "IndependentPlugin", "--authorization-env", "CORDIS_TEST_AUTHORIZATION");
            Assert.True(removed.ExitCode == 0, removed.Output);
            Assert.Empty(PackageManifest.Read(Path.Combine(profile, "package.json")).Bundles);
            Assert.False(Directory.Exists(Path.Combine(profile, ".cordis", "packages", "independentplugin", "1.0.0")));
            var unknown = await Command("wait", endpoint, "missing-request", "--authorization-env", "CORDIS_TEST_AUTHORIZATION");
            Assert.True(unknown.ExitCode == 3, "Expected unknown installation exit 3; actual " + unknown.ExitCode + ": " + unknown.Output);
            host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
            await remaining;
        }
        finally
        {
            if (!host.HasExited) host.Kill(entireProcessTree: true);
            await host.WaitForExitAsync();
            await stderr;
        }
    }

    private async Task<Host> StartAsync(IEnumerable<string>? sources = null, DshRuntimeIdentity? runtime = null, string? profileDirectory = null)
    {
        var root = profileDirectory is null ? Directory.CreateDirectory(Path.Combine(directory, Guid.NewGuid().ToString("N"))).FullName
            : Path.GetDirectoryName(profileDirectory)!;
        var profile = profileDirectory ?? Path.Combine(root, "profile");
        if (!File.Exists(Path.Combine(profile, "package.json"))) Profiles.Initialize(profile, []);
        var config = Path.Combine(root, "cordis.yml");
        if (!File.Exists(config)) await File.WriteAllTextAsync(config, "[]\n");
        var resolver = new ClrModuleResolver(Path.Combine(root, "shadow"), [typeof(ConfigObject<>).Assembly]);
        var toolchain = new DotnetPluginToolchain(profile, resolver, sources ?? [Feed]);
        var launch = new ProfileLaunch(await Profiles.LoadAsync(profile, new Dictionary<string, string>(), toolchain.Bundles),
            root, [], new Dictionary<string, string>(), toolchain.Bundles) {
            RuntimeIdentity = runtime,
            CompatibilityPackageName = DotnetPluginToolchain.NormalizeCompatibilityPackageName,
            ManifestLocator = toolchain.LocateManifest,
        };
        var session = await ProfileSession.StartAsync(config, launch, resolver);
        return new(profile, resolver, toolchain, session);
    }

    private sealed record Host(string Profile, ClrModuleResolver Resolver, DotnetPluginToolchain Toolchain, ProfileSession Session) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            Toolchain.Dispose();
            await Resolver.DisposeAsync();
        }
    }

    private static async Task RunAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await output + await error);
    }

    public Task DisposeAsync()
    {
        // CLR source files are separate from collectible shadow copies, whose release may follow a later GC.
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }
}
