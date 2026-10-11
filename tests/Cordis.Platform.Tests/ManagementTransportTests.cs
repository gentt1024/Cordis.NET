using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed class ManagementTransportTests
{
    [Fact]
    public async Task
        Real_remote_settings_enforce_authorization_and_revision_while_configuration_retains_generation_fencing()
    {
        var root = Directory.CreateTempSubdirectory("cordis-http-").FullName;
        try
        {
            var profile = Path.Combine(root, "profile");
            Profiles.Initialize(profile, []);
            var config = Path.Combine(root, "cordis.yml");
            await File.WriteAllTextAsync(
                config,
                "- id: worker\n  name: worker\n  config: { limit: 1, label: original }\n");
            var activations = 0;
            var resolver = new StaticModuleResolver().Register(
                "worker",
                new Plugin<WorkerSettings>
                {
                    Configuration = ConfigObject<WorkerSettings>
                        .Create(raw =>
                        {
                            var values = (IReadOnlyDictionary<string, object?>)raw!;
                            var value = Convert.ToInt32(values["limit"]);
                            return value >= 0
                                ? ConfigResult<WorkerSettings>.Success(new(value, (string)values["label"]!))
                                : ConfigResult<WorkerSettings>.Failure("limit must be nonnegative");
                        })
                        .Field("limit", ConfigDescriptor.Number().Volatile(), value => value.Limit)
                        .Field("label", ConfigDescriptor.String(), value => value.Label)
                        .Build(),
                    Apply = (context, _) =>
                    {
                        activations++;
                        context.Provide("limit", context.Fiber.GetConfigReference<int>("limit"));
                    },
                });
            var launch = new ProfileLaunch(
                await Profiles.LoadAsync(profile, new Dictionary<string, string>()),
                root,
                [],
                new Dictionary<string, string>());
            await using var session = await ProfileSession.StartAsync(config, launch, resolver);
            await using var server = CreateServer();
            var management = new CordisManagement(
                session,
                (http, _) => Task.FromResult(http.Request.Headers.Authorization == "test-host-policy"));
            var registry = new TypertRegistry(session.Context);
            await session.Context.RunAsync(context =>
            {
                new SettingsForms(context, session.ConfigurationOperations, _ => new SettingsPolicy(["limit"]));
                new SettingsController(context);
                registry.Register(context, SettingsControllerTypert.Contribution("Cordis.Tests.Settings"));
                return Task.CompletedTask;
            });
            var permissions = new List<string>();
            await ManagementTestHost.MountAsync(
                server,
                session,
                management,
                registry,
                (http, endpoint) =>
                {
                    permissions.Add(endpoint);
                    return Task.FromResult(
                        http.Request.Headers.Authorization == "test-host-policy" ||
                        http.Request.Headers.Authorization == "settings-only" && endpoint == "settings/describe");
                });
            await server.StartAsync();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(server.Urls.Single())
            };
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/cordis/state")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync(
                    "/remote/settings/describe",
                    new
                    {
                        args = new
                        {
                        }
                    })).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("test-host-policy");
            using var state = JsonDocument.Parse(await client.GetStringAsync("/cordis/state"));
            var generation = state.RootElement.GetProperty("generation").GetString();
            var view = await ReadSettingsAsync(client);
            Assert.Equal(0, view.Revision);
            var patch = Path.Combine(profile, "cordis.patch.yml");
            var original = await File.ReadAllTextAsync(patch);
            Assert.Equal(
                HttpStatusCode.Conflict,
                (await client.PostAsJsonAsync(
                    "/cordis/configuration",
                    new
                    {
                        entryId = "root:worker"
                    })).StatusCode);
            Assert.Equal(original, await File.ReadAllTextAsync(patch));
            client.DefaultRequestHeaders.Add("If-Cordis-Generation", generation);
            Assert.Equal(
                HttpStatusCode.BadRequest,
                (await client.PostAsync(
                    "/remote/settings/mutate",
                    new StringContent("[", Encoding.UTF8, "application/json"))).StatusCode);
            var body = new
            {
                args = new
                {
                    ns = "worker",
                    expectedRevision = view.Revision,
                    ops = new[]
                    {
                        new
                        {
                            op = "set",
                            path = new[] { "limit" },
                            value = 2
                        }
                    }
                }
            };
            using var saved = JsonDocument.Parse(
                await (await client.PostAsJsonAsync("/remote/settings/mutate", body)).Content.ReadAsStringAsync());
            Assert.True(saved.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(2, (await ReadSettingsAsync(client)).Value);
            Assert.Equal(1, activations);
            using var stale = JsonDocument.Parse(
                await (await client.PostAsJsonAsync("/remote/settings/mutate", body)).Content.ReadAsStringAsync());
            Assert.Equal("settings/conflict", stale.RootElement.GetProperty("error").GetProperty("code").GetString());
            view = await ReadSettingsAsync(client);
            using var reset = JsonDocument.Parse(
                await (await client.PostAsJsonAsync(
                    "/remote/settings/mutate",
                    new
                    {
                        args = new
                        {
                            ns = "worker",
                            expectedRevision = view.Revision,
                            ops = new[]
                            {
                                new
                                {
                                    op = "unset",
                                    path = new[] { "limit" }
                                }
                            }
                        }
                    })).Content.ReadAsStringAsync());
            Assert.True(reset.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(1, (await ReadSettingsAsync(client)).Value);
            Assert.Contains("settings/mutate", permissions);
            // The CLI edits through the same owner. Ordinary changes are available only
            // through full configuration authority, while Settings remains live-only.
            client.DefaultRequestHeaders.Authorization = new("settings-only");
            Assert.Equal(1, (await ReadSettingsAsync(client)).Value);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.PostAsJsonAsync("/remote/settings/mutate", body)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.GetAsync("/cordis/configuration?entryId=root:worker")).StatusCode);
            Assert.Equal(
                HttpStatusCode.Forbidden,
                (await client.GetAsync("/cordis/configuration/schema?entryId=root:worker")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("test-host-policy");
            var authVariable = "CORDIS_TRANSPORT_TEST_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(authVariable, "test-host-policy");
            try
            {
                var endpoint = server.Urls.Single() + "/cordis";
                Environment.SetEnvironmentVariable(authVariable, "settings-only");
                var settingsRead = await RunCliAsync(
                    "settings",
                    endpoint,
                    "worker",
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, settingsRead.ExitCode);
                using var settingsView = JsonDocument.Parse(settingsRead.Output);
                Assert.Equal(JsonValueKind.Number, settingsView.RootElement.GetProperty("revision").ValueKind);
                Environment.SetEnvironmentVariable(authVariable, "test-host-policy");
                var settingsOps = Path.Combine(root, "settings-operations.json");
                await File.WriteAllTextAsync(settingsOps, """[{"op":"set","path":["limit"],"value":1}]""");
                var withoutRevision = await RunCliAsync(
                    "edit",
                    endpoint,
                    "settings",
                    "worker",
                    "-",
                    settingsOps,
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, withoutRevision.ExitCode);
                var nullRevision = await RunCliAsync(
                    "edit",
                    endpoint,
                    "settings",
                    "worker",
                    "null",
                    settingsOps,
                    "--authorization-env",
                    authVariable);
                Assert.Equal(1, nullRevision.ExitCode);

                var read = await RunCliAsync(
                    "configuration",
                    endpoint,
                    "root:worker",
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, read.ExitCode);
                using var raw = JsonDocument.Parse(read.Output);
                Assert.Equal("original", raw.RootElement.GetProperty("raw").GetProperty("label").GetString());
                var operations = Path.Combine(root, "operations.json");
                await File.WriteAllTextAsync(operations, """[{"op":"set","path":["label"],"value":"edited"}]""");
                var edited = await RunCliAsync(
                    "edit",
                    endpoint,
                    "configuration",
                    "root:worker",
                    raw.RootElement.GetProperty("revision").GetString()!,
                    operations,
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, edited.ExitCode);
                using var editResult = JsonDocument.Parse(edited.Output);
                Assert.True(editResult.RootElement.GetProperty("applied").GetBoolean());
                Assert.Equal(2, activations);
                var updated = await RunCliAsync(
                    "configuration",
                    endpoint,
                    "root:worker",
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, updated.ExitCode);
                using var updatedRaw = JsonDocument.Parse(updated.Output);
                Assert.Equal("edited", updatedRaw.RootElement.GetProperty("raw").GetProperty("label").GetString());
                var schema = await RunCliAsync(
                    "schema",
                    endpoint,
                    "configuration",
                    "root:worker",
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, schema.ExitCode);
                using var declaration = JsonDocument.Parse(schema.Output);
                var jsonSchema = declaration.RootElement.GetProperty("jsonSchema");
                var objectSchema = Definition(jsonSchema, jsonSchema);
                var labelSchema = Definition(jsonSchema, objectSchema.GetProperty("properties").GetProperty("label"));
                Assert.Equal("string", labelSchema.GetProperty("type").GetString());
                await File.WriteAllTextAsync(operations, """[{"op":"unset","path":["label"]}]""");
                var restored = await RunCliAsync(
                    "edit",
                    endpoint,
                    "configuration",
                    "root:worker",
                    editResult.RootElement.GetProperty("revision").GetString()!,
                    operations,
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, restored.ExitCode);
                Assert.Equal(3, activations);
                var inherited = await RunCliAsync(
                    "configuration",
                    endpoint,
                    "root:worker",
                    "--authorization-env",
                    authVariable);
                Assert.Equal(0, inherited.ExitCode);
                using var inheritedRaw = JsonDocument.Parse(inherited.Output);
                Assert.Equal("original", inheritedRaw.RootElement.GetProperty("raw").GetProperty("label").GetString());
                Assert.DoesNotContain("edited", await File.ReadAllTextAsync(patch));
            }
            finally
            {
                Environment.SetEnvironmentVariable(authVariable, null);
            }

            client.DefaultRequestHeaders.Remove("If-Cordis-Generation");
            client.DefaultRequestHeaders.Add("If-Cordis-Generation", "retired-host");
            using var retired = await client.PostAsJsonAsync(
                "/remote/pluginManager/waitForInstall",
                new
                {
                    args = new
                    {
                        requestId = "reused"
                    }
                });
            retired.EnsureSuccessStatusCode();
            using var refused = JsonDocument.Parse(await retired.Content.ReadAsStringAsync());
            Assert.False(refused.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "plugin-manager/generation-changed",
                refused.RootElement.GetProperty("error").GetProperty("code").GetString());
            client.DefaultRequestHeaders.Remove("If-Cordis-Generation");
            using var subscribe = new HttpRequestMessage(HttpMethod.Post, "/remote/remoteNotifications/watch/stream")
            {
                Content = JsonContent.Create(
                    new
                    {
                        args = new
                        {
                        }
                    })
            };
            using var events = await client.SendAsync(subscribe, HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            using var stream = new StreamReader(await events.Content.ReadAsStreamAsync());
            using var ready = JsonDocument.Parse((await stream.ReadLineAsync())!);
            Assert.True(ready.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("ready", ready.RootElement.GetProperty("value").GetProperty("kind").GetString());
            await session.Context.DisposeAsync();
            Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync("/cordis/state")).StatusCode);
            using var withdrawn =
                JsonDocument.Parse((await stream.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))!);
            Assert.False(withdrawn.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "gateway/service-unavailable",
                withdrawn.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Null(await stream.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("setPluginEnabled", "id", "root:blocked")]
    [InlineData("setBundleEnabled", "name", "blocked")]
    [InlineData("removeBundle", "name", "blocked")]
    [InlineData("setVersionExemption", "packageVersion", "blocked@1.0.0")]
    [InlineData("cancelInstall", "requestId", "blocked-task")]
    [InlineData("waitForInstall", "requestId", "blocked-task")]
    [InlineData("versions", "name", "blocked")]
    [InlineData("inspect", "request", "blocked")]
    [InlineData("installBundle", "request", "blocked")]
    public async Task Authorization_uses_the_executed_resource_despite_unrelated_fields(
        string endpoint,
        string identityField,
        string blocked)
    {
        var root = Directory.CreateTempSubdirectory("cordis-http-resource-").FullName;
        try
        {
            var profile = Path.Combine(root, "profile");
            Profiles.Initialize(profile, []);
            var config = Path.Combine(root, "cordis.yml");
            await File.WriteAllTextAsync(config, "[]\n");
            await File.WriteAllTextAsync(
                Path.Combine(profile, "cordis.patch.yml"),
                "- insert:\n    - id: allowed\n      name: worker\n    - id: blocked\n      name: worker\n");
            var resolver = new StaticModuleResolver().Register(
                "worker",
                new Plugin<object?>
                {
                    Apply = (_, _) =>
                    {
                    }
                });
            var launch = new ProfileLaunch(
                await Profiles.LoadAsync(profile, new Dictionary<string, string>()),
                root,
                [],
                new Dictionary<string, string>());
            await using var session = await ProfileSession.StartAsync(config, launch, resolver);
            var plugins = (await session.ConfigurationOperations.ListPluginsAsync())
                .Where(plugin => plugin.ModuleName == "worker")
                .ToArray();
            Assert.Equal(2, plugins.Length);
            Assert.All(plugins, plugin => Assert.Null(plugin.ReadOnlyReason));
            var allowed = plugins.Single(plugin => plugin.EntryId.EndsWith(":allowed", StringComparison.Ordinal))
                .EntryId;
            if (endpoint == "setPluginEnabled")
                blocked = plugins.Single(plugin => plugin.EntryId.EndsWith(":blocked", StringComparison.Ordinal))
                    .EntryId;
            await using var server = CreateServer();
            var permissions = new List<ManagementPermission>();
            var management = new CordisManagement(
                session,
                (_, permission) =>
                {
                    permissions.Add(permission);
                    return Task.FromResult(permission.Operation == "read" || permission.Target == allowed);
                });
            await ManagementTestHost.MountAsync(server, session, management);
            await server.StartAsync();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(server.Urls.Single())
            };
            using var state = JsonDocument.Parse(await client.GetStringAsync("/cordis/state"));
            client.DefaultRequestHeaders.Add(
                "If-Cordis-Generation",
                state.RootElement.GetProperty("generation").GetString());
            var before = Directory.GetFiles(profile).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
            var arguments = new Dictionary<string, object?>
            {
                [identityField] = identityField == "request"
                    ? new PackageRequest(blocked, "1.0.0", "selected-source")
                    : blocked,
            };
            if (endpoint is "setPluginEnabled" or "setBundleEnabled" or "setVersionExemption")
                arguments["enabled"] = false;
            if (endpoint == "setVersionExemption")
            {
                arguments["runtimeVersion"] = "0.2.0-rc.2";
                arguments["acceptRisk"] = true;
            }

            if (endpoint == "versions")
                arguments["source"] = "selected-source";
            if (endpoint == "installBundle")
                arguments["options"] = new PluginInstallOptions("unrelated-task", "inspected-hash");
            using var response = await client.PostAsJsonAsync(
                "/remote/pluginManager/" + endpoint + "?entryId=" + Uri.EscapeDataString(allowed) +
                "&name=" + Uri.EscapeDataString(allowed) + "&target=" + Uri.EscapeDataString(allowed),
                new
                {
                    args = arguments
                });
            // Check state as well as HTTP refusal: a successful enable request used
            // to authorize allowed while actually disabling blocked.
            Assert.All(await session.ConfigurationOperations.ListPluginsAsync(), plugin => Assert.True(plugin.Enabled));
            Assert.Equal(
                before.OrderBy(pair => pair.Key),
                Directory
                    .GetFiles(profile)
                    .ToDictionary(path => Path.GetFileName(path), File.ReadAllText)
                    .OrderBy(pair => pair.Key));
            response.EnsureSuccessStatusCode();
            using var refused = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(refused.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(
                "plugin-manager/forbidden",
                refused.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal(blocked, permissions.Last().Target);
            if (endpoint == "setPluginEnabled")
            {
                arguments["id"] = allowed;
                using var accepted = await client.PostAsJsonAsync(
                    "/remote/pluginManager/setPluginEnabled?entryId=" + Uri.EscapeDataString(blocked),
                    new
                    {
                        args = arguments
                    });
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                using var changed = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
                Assert.True(changed.RootElement.GetProperty("ok").GetBoolean());
                Assert.True(changed.RootElement.GetProperty("value").GetProperty("changed").GetBoolean());
                var after = await session.ConfigurationOperations.ListPluginsAsync();
                Assert.False(after.Single(plugin => plugin.EntryId == allowed).Enabled);
                Assert.True(after.Single(plugin => plugin.EntryId == blocked).Enabled);
                Assert.Equal(allowed, permissions.Last().Target);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(409)]
    public async Task Cli_does_not_recover_an_explicit_rejection_as_another_requests_success(int status)
    {
        await using var server = CreateServer();
        var waits = 0;
        server.MapGet("/cordis/state", () => Results.Text("{\"generation\":\"original\"}", "application/json"));
        server.MapPost(
            "/remote/pluginManager/inspect",
            () => RemoteValue(
                new PackageInspection(new("new-package", "1.0.0", "explicit-feed"), "inspected", "test package", true),
                PluginManagerJson.Default.PackageInspection));
        server.MapPost("/remote/pluginManager/installBundle", () => Results.StatusCode(status));
        server.MapPost(
            "/remote/pluginManager/waitForInstall",
            () =>
            {
                Interlocked.Increment(ref waits);
                return RemoteValue(
                    new PackageChange("reused", "previous-package", "complete", true, true, "applied"),
                    PluginManagerJson.Default.PackageChange);
            });
        await server.StartAsync();
        var result = await RunCliAsync(
            "install",
            server.Urls.Single() + "/cordis",
            "new-package",
            "1.0.0",
            "--source",
            "explicit-feed",
            "--request-id",
            "reused",
            "--approve-build");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("HTTP " + status, result.Error);
        Assert.Equal(0, waits);
        Assert.DoesNotContain("previous-package", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_queries_lost_installation_only_within_the_original_host_generation(bool replaceHost)
    {
        await using var server = CreateServer();
        var generation = "original";
        var waits = 0;
        var installs = 0;
        server.MapGet(
            "/cordis/state",
            () => Results.Text("{\"generation\":\"" + generation + "\"}", "application/json"));
        server.MapPost(
            "/remote/pluginManager/inspect",
            () => RemoteValue(
                new PackageInspection(new("package", "1.0.0", "explicit-feed"), "inspected", "test package", true),
                PluginManagerJson.Default.PackageInspection));
        server.MapPost(
            "/remote/pluginManager/installBundle",
            (HttpContext http) =>
            {
                Interlocked.Increment(ref installs);
                if (replaceHost)
                    generation = "replacement";
                http.Abort();
                return Task.CompletedTask;
            });
        server.MapPost(
            "/remote/pluginManager/waitForInstall",
            () =>
            {
                Interlocked.Increment(ref waits);
                return RemoteValue(
                    new PackageChange("reused", "package", "complete", true, true, "applied"),
                    PluginManagerJson.Default.PackageChange);
            });
        await server.StartAsync();
        var result = await RunCliAsync(
            "install",
            server.Urls.Single() + "/cordis",
            "package",
            "1.0.0",
            "--source",
            "explicit-feed",
            "--request-id",
            "reused",
            "--approve-build");
        Assert.Equal(replaceHost ? 1 : 0, result.ExitCode);
        Assert.Equal(replaceHost ? 0 : 1, waits);
        Assert.Equal(1, installs);
        Assert.Contains(replaceHost ? "unknown-result" : "applied", result.Output);
    }

    private static IResult RemoteValue<T>(T value, JsonTypeInfo<T> metadata) => Results.Text(
        JsonSerializer.Serialize(
            new TypertRemoteResult(true, JsonSerializer.SerializeToElement(value, metadata), null),
            TypertJson.Default.TypertRemoteResult),
        "application/json");

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "fixtures", "cli", "Cordis.Cli.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }

        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Exported_service_resolves_current_provider_and_rejects_calls_after_revocation()
    {
        await using var context = new Context();
        EffectHandle provider = null!;
        await context.RunAsync(ctx =>
        {
            provider = ctx.Provide("echo", new EchoService("one"));
            return Task.CompletedTask;
        });
        await using var server = CreateServer();
        server.MapCordisService<EchoService, EchoRequest, EchoResponse>(
            "/echo",
            context,
            "echo",
            ManagementJson.Default.EchoRequest,
            ManagementJson.Default.EchoResponse,
            http => Task.FromResult(http.Request.Headers.Authorization == "test-host-policy"),
            (service, request, _) => Task.FromResult(new EchoResponse(service.Prefix + ":" + request.Value)));
        await server.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(server.Urls.Single())
        };
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("test-host-policy");
        Assert.Equal(
            "one:value",
            (await (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).Content
                .ReadFromJsonAsync<EchoResponse>())!.Value);
        await context.RunAsync(async _ => await provider.DisposeAsync());
        Assert.Equal(
            HttpStatusCode.ServiceUnavailable,
            (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).StatusCode);
        await context.RunAsync(ctx =>
        {
            ctx.Provide("echo", new EchoService("two"));
            return Task.CompletedTask;
        });
        Assert.Equal(
            "two:value",
            (await (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).Content
                .ReadFromJsonAsync<EchoResponse>())!.Value);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await client.PostAsync("/echo", new StringContent("[", Encoding.UTF8, "application/json"))).StatusCode);
        await context.DisposeAsync();
        Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).StatusCode);
    }

    private static WebApplication CreateServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddHttpContextAccessor();
        return builder.Build();
    }

    private static async Task<(double Revision, int Value)> ReadSettingsAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            "/remote/settings/describe",
            new
            {
                args = new
                {
                }
            });
        response.EnsureSuccessStatusCode();
        using var view = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(view.RootElement.GetProperty("ok").GetBoolean());
        var row = view.RootElement.GetProperty("value").GetProperty("namespaces")[0];
        return (row.GetProperty("revision").GetDouble(), row.GetProperty("value").GetProperty("limit").GetInt32());
    }

    private sealed record EchoService(string Prefix);

    private sealed record WorkerSettings(int Limit, string Label);

    private static JsonElement Definition(JsonElement document, JsonElement reference)
    {
        var pointer = reference.GetProperty("$ref").GetString()!;
        Assert.StartsWith("#/$defs/", pointer);
        return document.GetProperty("$defs").GetProperty(pointer["#/$defs/".Length..]);
    }
}

public sealed record EchoRequest(string Value);

public sealed record EchoResponse(string Value);

[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ManagementJson : JsonSerializerContext;
