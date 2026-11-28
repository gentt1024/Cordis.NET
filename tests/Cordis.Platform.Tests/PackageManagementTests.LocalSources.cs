using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security;
using System.Text.Json;
using Cordis.AspNetCore;
using Cordis.Clr;
using Cordis.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed partial class PackageManagementTests
{
    private static async Task PrepareLocalProjectAsync(string author)
    {
        var tools = Path.Combine(AppContext.BaseDirectory, "fixtures", "local-build");
        await File.WriteAllTextAsync(Path.Combine(author, "web.mjs"), "export const version = 1;");
        await File.WriteAllTextAsync(
            Path.Combine(author, "Directory.Build.targets"),
            $$"""
              <Project>
                <PropertyGroup><_CordisClrBuildTool>{{SecurityElement.Escape(Path.Combine(tools, "Cordis.Clr.Build.dll"))}}</_CordisClrBuildTool></PropertyGroup>
                <Import Project="{{SecurityElement.Escape(Path.Combine(tools, "Cordis.NET.Clr.targets"))}}" />
                <ItemGroup><CordisLocalFile Include="web.mjs" TargetPath="web/module.mjs" /></ItemGroup>
              </Project>
              """);
        await File.WriteAllTextAsync(
            Path.Combine(author, "cordis.plugin.json"),
            """{"assembly":"IndependentPlugin.dll","entryType":"Entry"}""");
    }

    private static async Task<PluginManager> MountLocalManagerAsync(Host host, Func<Context, string?>? subject = null)
    {
        PluginManager manager = null!;
        await host.Session.Context.RunAsync(context =>
        {
            manager = new PluginManager(
                context,
                host.Session.ConfigurationOperations,
                (_, _) => Task.FromResult(true),
                host.Toolchain)
            {
                LocalSourceSubject = subject
            };
            return Task.CompletedTask;
        });
        return manager;
    }

    private static Task AssertInstalledCodeAsync(Host host, string? expected) =>
        host.Session.Context.RunAsync(context =>
        {
            Assert.Equal(expected, context.Get<Func<string>>("installed-code")?.Invoke());
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Mounted_manager_binds_local_registration_to_the_http_subject_and_current_generation()
    {
        var author = Path.Combine(directory, "author");
        await PrepareLocalProjectAsync(author);
        var policy = new LocalSourcePolicy(
            directory,
            (access, _) =>
                Task.FromResult(access.SourceRoot == author && access.Subject is "alice" or "bob"));
        await using var host = await StartAsync(localSourcePolicy: policy);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        await using var server = builder.Build();
        var management = new CordisManagement(host.Session, (_, _) => Task.FromResult(true), host.Toolchain)
        {
            LocalSourceSubject = http => http.Request.Headers.Authorization.ToString() switch
            {
                "Bearer test-token-1" => "alice",
                "Bearer test-token-2" => "bob",
                _ => null
            }
        };
        await ManagementTestHost.MountAsync(server, host.Session, management);
        await server.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(server.Urls.Single())
        };
        using var state = JsonDocument.Parse(await client.GetStringAsync("/cordis/state"));
        var generation = state.RootElement.GetProperty("generation").GetString();

        async Task<JsonElement> CallAsync(string method, object args)
        {
            using var response = await PostRemoteAsync(
                client,
                "/remote/pluginManager/" + method,
                new
                {
                    args
                });
            response.EnsureSuccessStatusCode();
            using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return reply.RootElement.GetProperty("result").Clone();
        }

        var request = new LocalSourceRequest("author", "Debug");
        var anonymous = await CallAsync(
            "inspectLocalSource",
            new
            {
                request
            });
        Assert.Equal("plugin-manager/forbidden", anonymous.GetProperty("error").GetProperty("code").GetString());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-1");
        var inspection = (await CallAsync(
            "inspectLocalSource",
            new
            {
                request
            })).GetProperty("value");
        var inspectionId = inspection.GetProperty("inspectionId").GetString()!;

        Task<JsonElement> RegisterAsync(string id) => CallAsync(
            "registerLocalSource",
            new
            {
                request,
                options = new LocalSourceRegistration(id, inspectionId)
                {
                    Enabled = false
                }
            });

        var manifest = Path.Combine(host.Profile, "package.json");
        var before = await File.ReadAllBytesAsync(manifest);
        client.DefaultRequestHeaders.Add("If-Cordis-Generation", "retired-host");
        var stale = await RegisterAsync("stale");
        Assert.Equal("plugin-manager/generation-changed", stale.GetProperty("error").GetProperty("code").GetString());
        client.DefaultRequestHeaders.Remove("If-Cordis-Generation");
        client.DefaultRequestHeaders.Add("If-Cordis-Generation", generation);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-2");
        var other = await RegisterAsync("other");
        Assert.Equal("inspection-changed", other.GetProperty("value").GetProperty("error").GetString());
        Assert.Equal(before, await File.ReadAllBytesAsync(manifest));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token-1");
        var registered = (await RegisterAsync("own")).GetProperty("value");
        Assert.Equal("saved", registered.GetProperty("application").GetString());
        Assert.False(registered.GetProperty("selected").GetBoolean());
        Assert.Equal(
            Path.Combine(author, "IndependentPlugin.csproj"),
            PackageManifest.Read(manifest).ManagedLocalSources["IndependentPlugin"].ProjectFile);
    }

    [Fact]
    public async Task Online_source_inspection_requires_a_caller_and_host_approval_before_evaluation()
    {
        var request = new LocalSourceRequest("author", "Debug");
        await using (var host = await StartAsync())
        {
            var manager = await MountLocalManagerAsync(host);
            Assert.Equal(
                "plugin-manager/local-source-disabled",
                (await Assert.ThrowsAsync<RemoteError>(() => manager.InspectLocalSourceAsync(request))).Code);
            var archive = await manager.InspectAsync(new("IndependentPlugin", "1.0.0", Feed));
            Assert.NotEmpty(archive.ContentHash);
        }

        var observed = new List<LocalSourceStage>();
        var policy = new LocalSourcePolicy(
            directory,
            (access, _) =>
            {
                observed.Add(access.Stage);
                return Task.FromResult(
                    access.Stage == LocalSourceStage.Read && Path.GetFileName(access.SourceRoot) == "author");
            });
        await using var restricted = await StartAsync(
            localSourcePolicy: policy,
            dotnetCommand: Path.Combine(directory, "absent-dotnet"));
        var restrictedManager = await MountLocalManagerAsync(restricted, _ => "author");
        var manifest = Path.Combine(restricted.Profile, "package.json");
        var before = await File.ReadAllBytesAsync(manifest);
        var otherProfile = Path.Combine(directory, "other-profile");
        Profiles.Initialize(otherProfile, []);
        await using var independentResolver = new ClrModuleResolver();
        foreach (var profile in new[] { restricted.Profile, otherProfile })
        {
            using var other = await DotnetPluginToolchain.OpenForRunAsync(profile, independentResolver, [Feed], policy);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                restricted.Session.ConfigurationOperations.InspectLocalSourceAsync(other, "author", request));
        }

        Assert.Empty(observed);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            restrictedManager.InspectLocalSourceAsync(new("missing", "Debug")));
        Assert.Equal([LocalSourceStage.Read], observed);
        observed.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restrictedManager.InspectLocalSourceAsync(request));
        Assert.Equal([LocalSourceStage.Read, LocalSourceStage.Evaluate], observed);
        Assert.Equal(before, await File.ReadAllBytesAsync(manifest));
        Assert.Empty(restricted.Toolchain.Bundles);
    }

    [Fact]
    public async Task Local_source_inspection_rejects_a_link_before_project_evaluation()
    {
        var author = Path.Combine(directory, "author");
        var junction = Path.Combine(directory, "author-junction");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                ArgumentList =
                {
                    "/d",
                    "/c",
                    "mklink",
                    "/J",
                    junction,
                    author
                },
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var create = Process.Start(start)!;
            await create.WaitForExitAsync();
            Assert.Equal(0, create.ExitCode);
        }
        else
            Directory.CreateSymbolicLink(junction, author);

        Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
        Assert.Equal(
            await File.ReadAllBytesAsync(Path.Combine(author, "IndependentPlugin.csproj")),
            await File.ReadAllBytesAsync(Path.Combine(junction, "IndependentPlugin.csproj")));

        var stages = new List<LocalSourceStage>();
        var policy = new LocalSourcePolicy(
            directory,
            (access, _) =>
            {
                stages.Add(access.Stage);
                return Task.FromResult(true);
            });
        await using var host = await StartAsync(
            localSourcePolicy: policy,
            dotnetCommand: Path.Combine(directory, "absent-dotnet"));
        var manager = await MountLocalManagerAsync(host, _ => "author");
        await Assert.ThrowsAnyAsync<IOException>(() => manager.InspectLocalSourceAsync(new(junction, "Debug")));
        Assert.DoesNotContain(LocalSourceStage.Evaluate, stages);
        Assert.Empty(host.Toolchain.Bundles);
    }

    [Fact]
    public async Task Online_registration_binds_the_inspection_and_reopens_only_an_authorized_complete_output()
    {
        const string name = "IndependentPlugin";
        var author = Path.Combine(directory, "author");
        var project = Path.Combine(author, name + ".csproj");
        await PrepareLocalProjectAsync(author);
        var request = new LocalSourceRequest("author", "Debug");
        var actor = "alice";
        var observed = new List<LocalSourceAccess>();
        var registerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueRegister = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pauseRegistration = false;
        var allowActivation = true;
        var policy = new LocalSourcePolicy(
            directory,
            async (access, token) =>
            {
                observed.Add(access);
                if (pauseRegistration && access.Stage == LocalSourceStage.Register)
                {
                    registerEntered.TrySetResult();
                    await continueRegister.Task.WaitAsync(token);
                }

                return access.Stage != LocalSourceStage.Activate || allowActivation;
            });

        string profile;
        string manifest;
        byte[] selectedManifest;
        await using (var host = await StartAsync(enableHmr: true, localSourcePolicy: policy))
        {
            profile = host.Profile;
            manifest = Path.Combine(profile, "package.json");
            var manager = await MountLocalManagerAsync(host, _ => actor);
            var responseFile = Path.Combine(author, "Directory.Build.rsp");
            await File.WriteAllTextAsync(responseFile, "-property:PackageId=ResponseFileMarker");
            var plain = await RunDotnetAsync(author, "msbuild", project, "-nologo", "-getProperty:PackageId");
            Assert.Equal(0, plain.ExitCode);
            Assert.Contains("ResponseFileMarker", plain.Output.Split('\n').Select(line => line.Trim()));
            var first = await manager.InspectLocalSourceAsync(request);
            File.Delete(responseFile);
            Assert.Equal(name, first.Facts.PackageId);
            Assert.Equal(project, first.Facts.ProjectFile);
            Assert.Null(first.Facts.OutputIdentity);
            Assert.Null(first.Facts.OutputDirectory);

            var original = await File.ReadAllBytesAsync(manifest);
            Assert.Equal(
                "source-needs-build",
                (await manager.RegisterLocalSourceAsync(request, new("enable-unbuilt", first.InspectionId))).Error);
            actor = "bob";
            Assert.Equal(
                "inspection-changed",
                (await manager.RegisterLocalSourceAsync(
                    request,
                    new("wrong-owner", first.InspectionId)
                    {
                        Enabled = false
                    })).Error);
            actor = "alice";
            var changedConfiguration = await manager.RegisterLocalSourceAsync(
                new("author", "Release"),
                new("wrong-configuration", first.InspectionId)
                {
                    Enabled = false
                });
            Assert.Equal("inspection-changed", changedConfiguration.Error);
            var declaration = Path.Combine(author, "cordis.plugin.json");
            var originalDeclaration = await File.ReadAllBytesAsync(declaration);
            var alternate = Path.Combine(author, "Alternate.csproj");
            File.Copy(project, alternate);
            try
            {
                await File.WriteAllTextAsync(
                    declaration,
                    """{"assembly":"IndependentPlugin.dll","entryType":"Entry","project":"Alternate.csproj"}""");
                var alternateInspection = await manager.InspectLocalSourceAsync(request);
                Assert.Equal(
                    first.Facts with
                    {
                        ProjectFile = alternate
                    },
                    alternateInspection.Facts);
                var changedProject = await manager.RegisterLocalSourceAsync(
                    request,
                    new("wrong-project", first.InspectionId)
                    {
                        Enabled = false
                    });
                Assert.Equal("inspection-changed", changedProject.Error);
            }
            finally
            {
                File.Delete(alternate);
                await File.WriteAllBytesAsync(declaration, originalDeclaration);
            }

            Assert.Equal(original, await File.ReadAllBytesAsync(manifest));
            await File.AppendAllTextAsync(manifest, "\n");
            Assert.Equal(
                "inspection-changed",
                (await manager.RegisterLocalSourceAsync(
                    request,
                    new("old-profile", first.InspectionId)
                    {
                        Enabled = false
                    })).Error);

            var unbuilt = await manager.InspectLocalSourceAsync(request);
            pauseRegistration = true;
            var beforeCancel = await File.ReadAllBytesAsync(manifest);
            var cancelled = manager.RegisterLocalSourceAsync(
                request,
                new("cancel-before-save", unbuilt.InspectionId)
                {
                    Enabled = false
                });
            Assert.Same(registerEntered.Task, await Task.WhenAny(registerEntered.Task, cancelled));
            var cancelledWait = manager.WaitForLocalSourceAsync("cancel-before-save");
            Assert.Equal("cancelled", (await manager.CancelLocalSourceAsync("cancel-before-save")).Status);
            var stopped = await cancelled;
            Assert.Equal("cancelled", stopped.Application);
            Assert.Equal(stopped, await cancelledWait);
            Assert.False(stopped.Installed);
            Assert.Equal(beforeCancel, await File.ReadAllBytesAsync(manifest));
            registerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var saving = manager.RegisterLocalSourceAsync(
                request,
                new("register", unbuilt.InspectionId)
                {
                    Enabled = false
                });
            var firstCompleted = await Task.WhenAny(saving, registerEntered.Task);
            Assert.Same(registerEntered.Task, firstCompleted);
            try
            {
                actor = "bob";
                Assert.Null(await manager.WaitForLocalSourceAsync("register"));
                Assert.Equal("not-running", (await manager.CancelLocalSourceAsync("register")).Status);
                actor = "alice";
                Assert.Null(await manager.WaitForInstallAsync("register"));
                Assert.Equal("not-running", (await manager.CancelInstallAsync("register")).Status);
                var ownerWait = manager.WaitForLocalSourceAsync("register");
                continueRegister.SetResult();
                var saved = await saving;
                Assert.Equal(saved, await ownerWait);
                Assert.Equal("saved", saved.Application);
                Assert.True(saved.Installed);
                Assert.False(saved.Selected);
            }
            finally
            {
                continueRegister.TrySetResult();
                pauseRegistration = false;
            }

            var disabled = new RegisteredLocalSource(name, author, project, "Debug", false);
            Assert.Equal(disabled, await manager.ReadRegisteredLocalSourceAsync(name));
            Assert.Null(await manager.WaitForLocalSourceAsync("register"));
            Assert.Empty(host.Toolchain.Bundles);
            Assert.Equal("source-needs-build", (await manager.SetBundleEnabledAsync(name, true)).Error);

            var beforeBuild = await manager.InspectLocalSourceAsync(request);
            await RunAsync(author, "build", "-c", "Debug");
            Assert.Equal(
                "inspection-changed",
                (await manager.RegisterLocalSourceAsync(request, new("stale-output", beforeBuild.InspectionId))).Error);
            var built = await manager.InspectLocalSourceAsync(request);
            Assert.NotNull(built.Facts.OutputIdentity);
            Assert.True(File.Exists(Path.Combine(built.Facts.OutputDirectory!, name + ".dll")));
            Assert.Null((await manager.InspectLocalSourceAsync(new("author", "Release"))).Facts.OutputDirectory);
            var disabledManifest = await File.ReadAllBytesAsync(manifest);
            await File.WriteAllTextAsync(
                Path.Combine(built.Facts.OutputDirectory!, "web", "module.mjs"),
                "// changed after build");
            var damaged = await manager.RegisterLocalSourceAsync(request, new("damaged-output", built.InspectionId));
            Assert.Equal("operation-error", damaged.Error);
            Assert.Contains("deployment files differ from their published receipt", damaged.Diagnostic);
            await File.WriteAllTextAsync(Path.Combine(author, "web.mjs"), "export const version = 2;");
            await RunAsync(author, "build", "-c", "Debug", "--no-restore");
            var ready = await manager.InspectLocalSourceAsync(request);
            Assert.NotNull(ready.Facts.OutputIdentity);
            Assert.NotEqual(built.Facts.OutputIdentity, ready.Facts.OutputIdentity);
            var replaced = await manager.RegisterLocalSourceAsync(request, new("replaced-output", built.InspectionId));
            Assert.Equal("inspection-changed", replaced.Error);
            Assert.Equal(disabledManifest, await File.ReadAllBytesAsync(manifest));
            Assert.Empty(host.Toolchain.Bundles);
            // A later filesystem refresh can apply the saved selection. Observe the request in its own queue turn.
            var codeAtRestart = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Session.Hmr!.RestartHost = () => host.Session.Context.RunAsync(context =>
            {
                codeAtRestart.TrySetResult(context.Get<Func<string>>("installed-code")?.Invoke());
                return Task.CompletedTask;
            });
            var enabled = await manager.SetBundleEnabledAsync(name, true);
            Assert.True(enabled.Changed, enabled.Diagnostic);
            Assert.Equal("restart-required", enabled.Application);
            Assert.Equal("package-moved", enabled.RestartReason);
            Assert.Equal(
                disabled with
                {
                    Selected = true
                },
                await manager.ReadRegisteredLocalSourceAsync(name));
            Assert.Equal(ready.Facts.OutputDirectory, host.Toolchain.Bundles[name]);
            Assert.True(codeAtRestart.Task.IsCompletedSuccessfully);
            Assert.Null(await codeAtRestart.Task);
            selectedManifest = await File.ReadAllBytesAsync(manifest);
        }

        observed.Clear();
        allowActivation = false;
        var refuseEvaluation = new LocalSourcePolicy(
            directory,
            (access, _) =>
            {
                observed.Add(access);
                return Task.FromResult(access.Stage == LocalSourceStage.Read);
            });
        await using (var deniedResolver = new ClrModuleResolver())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                DotnetPluginToolchain.OpenForRunAsync(
                    profile,
                    deniedResolver,
                    [Feed],
                    refuseEvaluation,
                    dotnetCommand: Path.Combine(directory, "absent-dotnet")));
            Assert.Equal([LocalSourceStage.Read, LocalSourceStage.Evaluate], observed.Select(access => access.Stage));
            Assert.All(observed, access => Assert.Null(access.Subject));
            Assert.Equal(selectedManifest, await File.ReadAllBytesAsync(manifest));
        }

        observed.Clear();
        await using (var deniedResolver = new ClrModuleResolver())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                DotnetPluginToolchain.OpenForRunAsync(profile, deniedResolver, [Feed], policy));
            Assert.Contains(observed, x => x.Subject is null && x.Stage == LocalSourceStage.Activate);
            await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
                await deniedResolver.LocateAsync("nuget:independentplugin", new Uri(profile + "/")));
            Assert.Equal(selectedManifest, await File.ReadAllBytesAsync(manifest));
        }

        allowActivation = true;
        var projectBytes = await File.ReadAllBytesAsync(project);
        await using (var reopened = await StartAsync(profileDirectory: profile, localSourcePolicy: policy))
        {
            var manager = await MountLocalManagerAsync(reopened, _ => actor);
            var persisted = await manager.ReadRegisteredLocalSourceAsync(name);
            Assert.Equal(new RegisteredLocalSource(name, author, project, "Debug", true), persisted);
            await AssertInstalledCodeAsync(reopened, "v1");
            var disabled = await manager.SetBundleEnabledAsync(name, false);
            Assert.True(disabled.Changed, disabled.Diagnostic);
            Assert.Equal("restart-required", disabled.Application);
            Assert.Equal("package-moved", disabled.RestartReason);
            Assert.False((await manager.ReadRegisteredLocalSourceAsync(name))!.Selected);
        }

        await using (var disabledHost = await StartAsync(profileDirectory: profile, localSourcePolicy: policy))
        {
            await AssertInstalledCodeAsync(disabledHost, null);
            var manager = await MountLocalManagerAsync(disabledHost, _ => actor);
            var removed = await manager.RemoveBundleAsync(name);
            Assert.Null(removed.Error);
            Assert.False(removed.Installed);
            Assert.Null(await manager.ReadRegisteredLocalSourceAsync(name));
        }

        await using var cleaned = await StartAsync(profileDirectory: profile, localSourcePolicy: policy);
        var cleanedManager = await MountLocalManagerAsync(cleaned, _ => actor);
        Assert.Null(await cleanedManager.ReadRegisteredLocalSourceAsync(name));
        await AssertInstalledCodeAsync(cleaned, null);
        Assert.Empty(cleaned.Toolchain.Bundles);
        Assert.True(Directory.Exists(author));
        Assert.Equal(projectBytes, await File.ReadAllBytesAsync(project));
    }
}
