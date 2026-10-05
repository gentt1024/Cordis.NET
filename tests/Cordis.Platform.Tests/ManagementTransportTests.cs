using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed class ManagementTransportTests
{
    [Fact]
    public async Task Real_http_settings_enforce_authorization_generation_and_revision_without_restarting_live_plugin()
    {
        var root = Directory.CreateTempSubdirectory("cordis-http-").FullName;
        try
        {
            var profile = Path.Combine(root, "profile");
            Profiles.Initialize(profile, []);
            var config = Path.Combine(root, "cordis.yml");
            await File.WriteAllTextAsync(config, "- id: worker\n  name: worker\n  config: { limit: 1, label: original }\n");
            var activations = 0;
            var resolver = new StaticModuleResolver().Register("worker", new Plugin<WorkerSettings>
            {
                Configuration = ConfigObject<WorkerSettings>.Create(raw =>
                {
                    var values = (IReadOnlyDictionary<string, object?>)raw!;
                    var value = Convert.ToInt32(values["limit"]);
                    return value >= 0 ? ConfigResult<WorkerSettings>.Success(new(value, (string)values["label"]!))
                        : ConfigResult<WorkerSettings>.Failure("limit must be nonnegative");
                }).Field("limit", ConfigDescriptor.Number().Volatile(), value => value.Limit)
                    .Field("label", ConfigDescriptor.String(), value => value.Label).Build(),
                Apply = (context, _) =>
                {
                    activations++;
                    context.Provide("limit", context.Fiber.GetConfigReference<int>("limit"));
                },
            });
            var launch = new ProfileLaunch(await Profiles.LoadAsync(profile, new Dictionary<string, string>()), root, [], new Dictionary<string, string>());
            await using var session = await ProfileSession.StartAsync(config, launch, resolver);
            var permissions = new List<ManagementPermission>();
            await using var server = CreateServer();
            new CordisManagement(session, (http, permission) =>
            {
                permissions.Add(permission);
                return Task.FromResult(http.Request.Headers.Authorization == "test-host-policy"
                    && (permission.Operation != "settings-write" || permission.Target == "root:worker")
                    || http.Request.Headers.Authorization == "settings-only" && permission.Operation == "settings-read");
            }, _ => new SettingsPolicy(["limit"])).Map(server);
            await server.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(server.Urls.Single()) };
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/cordis/state")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("test-host-policy");
            using var state = JsonDocument.Parse(await client.GetStringAsync("/cordis/state"));
            var generation = state.RootElement.GetProperty("generation").GetString();
            var view = await ReadSettingsAsync(client);
            var patch = Path.Combine(profile, "cordis.patch.yml");
            var original = await File.ReadAllTextAsync(patch);
            var body = new { entryId = "root:worker", revision = view.Revision, operations = new[] { new { op = "set", path = new[] { "limit" }, value = 2 } } };
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/cordis/settings", body)).StatusCode);
            Assert.Equal(original, await File.ReadAllTextAsync(patch));
            client.DefaultRequestHeaders.Add("If-Cordis-Generation", generation);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/cordis/settings",
                new StringContent("[", Encoding.UTF8, "application/json"))).StatusCode);
            using var saved = JsonDocument.Parse(await (await client.PostAsJsonAsync("/cordis/settings", body)).Content.ReadAsStringAsync());
            Assert.True(saved.RootElement.GetProperty("applied").GetBoolean());
            Assert.Equal(2, (await ReadSettingsAsync(client)).Value);
            Assert.Equal(1, activations);
            using var stale = JsonDocument.Parse(await (await client.PostAsJsonAsync("/cordis/settings", body)).Content.ReadAsStringAsync());
            Assert.Equal("conflict", stale.RootElement.GetProperty("error").GetString());
            view = await ReadSettingsAsync(client);
            using var reset = JsonDocument.Parse(await (await client.PostAsJsonAsync("/cordis/settings", new
            {
                entryId = "root:worker", revision = view.Revision,
                operations = new[] { new { op = "unset", path = new[] { "limit" } } },
            })).Content.ReadAsStringAsync());
            Assert.True(reset.RootElement.GetProperty("applied").GetBoolean());
            Assert.Equal(1, (await ReadSettingsAsync(client)).Value);
            Assert.Contains(permissions, permission => permission.Operation == "settings-write" && permission.Target == "root:worker");
            // The CLI edits through the same owner. Ordinary changes are available only
            // through full configuration authority, while Settings remains live-only.
            client.DefaultRequestHeaders.Authorization = new("settings-only");
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/cordis/settings?entryId=root:worker")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/cordis/configuration?entryId=root:worker")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/cordis/configuration/schema?entryId=root:worker")).StatusCode);
            client.DefaultRequestHeaders.Authorization = new("test-host-policy");
            var authVariable = "CORDIS_TRANSPORT_TEST_" + Guid.NewGuid().ToString("N");
            Environment.SetEnvironmentVariable(authVariable, "test-host-policy");
            try
            {
                var endpoint = server.Urls.Single() + "/cordis";
                var read = await RunCliAsync("configuration", endpoint, "root:worker", "--authorization-env", authVariable);
                Assert.Equal(0, read.ExitCode);
                using var raw = JsonDocument.Parse(read.Output);
                Assert.Equal("original", raw.RootElement.GetProperty("raw").GetProperty("label").GetString());
                var operations = Path.Combine(root, "operations.json");
                await File.WriteAllTextAsync(operations, """[{"op":"set","path":["label"],"value":"edited"}]""");
                var edited = await RunCliAsync("edit", endpoint, "configuration", "root:worker",
                    raw.RootElement.GetProperty("revision").GetString()!, operations, "--authorization-env", authVariable);
                Assert.Equal(0, edited.ExitCode);
                using var editResult = JsonDocument.Parse(edited.Output);
                Assert.True(editResult.RootElement.GetProperty("applied").GetBoolean());
                Assert.Equal(2, activations);
                var updated = await RunCliAsync("configuration", endpoint, "root:worker", "--authorization-env", authVariable);
                Assert.Equal(0, updated.ExitCode);
                using var updatedRaw = JsonDocument.Parse(updated.Output);
                Assert.Equal("edited", updatedRaw.RootElement.GetProperty("raw").GetProperty("label").GetString());
                var schema = await RunCliAsync("schema", endpoint, "configuration", "root:worker", "--authorization-env", authVariable);
                Assert.Equal(0, schema.ExitCode);
                using var declaration = JsonDocument.Parse(schema.Output);
                var jsonSchema = declaration.RootElement.GetProperty("jsonSchema");
                var objectSchema = Definition(jsonSchema, jsonSchema);
                var labelSchema = Definition(jsonSchema, objectSchema.GetProperty("properties").GetProperty("label"));
                Assert.Equal("string", labelSchema.GetProperty("type").GetString());
                await File.WriteAllTextAsync(operations, """[{"op":"unset","path":["label"]}]""");
                var restored = await RunCliAsync("edit", endpoint, "configuration", "root:worker",
                    editResult.RootElement.GetProperty("revision").GetString()!, operations, "--authorization-env", authVariable);
                Assert.Equal(0, restored.ExitCode);
                Assert.Equal(3, activations);
                var inherited = await RunCliAsync("configuration", endpoint, "root:worker", "--authorization-env", authVariable);
                Assert.Equal(0, inherited.ExitCode);
                using var inheritedRaw = JsonDocument.Parse(inherited.Output);
                Assert.Equal("original", inheritedRaw.RootElement.GetProperty("raw").GetProperty("label").GetString());
                Assert.DoesNotContain("edited", await File.ReadAllTextAsync(patch));
            }
            finally { Environment.SetEnvironmentVariable(authVariable, null); }
            client.DefaultRequestHeaders.Remove("If-Cordis-Generation");
            client.DefaultRequestHeaders.Add("If-Cordis-Generation", "retired-host");
            Assert.Equal(HttpStatusCode.Conflict, (await client.GetAsync("/cordis/install/wait?requestId=reused")).StatusCode);
            client.DefaultRequestHeaders.Remove("If-Cordis-Generation");
            using var events = await client.GetAsync("/cordis/events", HttpCompletionOption.ResponseHeadersRead);
            Assert.Equal(HttpStatusCode.OK, events.StatusCode);
            using var stream = new StreamReader(await events.Content.ReadAsStreamAsync());
            Assert.StartsWith("data: ", await stream.ReadLineAsync());
            Assert.Equal("", await stream.ReadLineAsync());
            await session.Context.DisposeAsync();
            Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync("/cordis/state")).StatusCode);
            Assert.Null(await stream.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("enable", "target", "root:blocked", false)]
    [InlineData("remove", "name", "blocked", false)]
    [InlineData("compatibility", "packageVersion", "blocked@1.0.0", false)]
    [InlineData("install/cancel", "requestId", "blocked-task", false)]
    [InlineData("install/wait", "requestId", "blocked-task", true)]
    [InlineData("versions", "name", "blocked", true)]
    [InlineData("inspect", "name", "blocked", false)]
    [InlineData("install", "name", "blocked", false)]
    public async Task Authorization_uses_the_executed_resource_despite_unrelated_fields(
        string endpoint, string identityField, string blocked, bool read)
    {
        var root = Directory.CreateTempSubdirectory("cordis-http-resource-").FullName;
        try
        {
            var profile = Path.Combine(root, "profile");
            Profiles.Initialize(profile, []);
            var config = Path.Combine(root, "cordis.yml");
            await File.WriteAllTextAsync(config, "[]\n");
            await File.WriteAllTextAsync(Path.Combine(profile, "cordis.patch.yml"),
                "- insert:\n    - id: allowed\n      name: worker\n    - id: blocked\n      name: worker\n");
            var resolver = new StaticModuleResolver().Register("worker", new Plugin<object?> { Apply = (_, _) => { } });
            var launch = new ProfileLaunch(await Profiles.LoadAsync(profile, new Dictionary<string, string>()), root, [], new Dictionary<string, string>());
            await using var session = await ProfileSession.StartAsync(config, launch, resolver);
            var plugins = (await session.ConfigurationOperations.ListPluginsAsync()).Where(plugin => plugin.ModuleName == "worker").ToArray();
            Assert.Equal(2, plugins.Length);
            Assert.All(plugins, plugin => Assert.Null(plugin.ReadOnlyReason));
            var allowed = plugins.Single(plugin => plugin.EntryId.EndsWith(":allowed", StringComparison.Ordinal)).EntryId;
            if (endpoint == "enable") blocked = plugins.Single(plugin => plugin.EntryId.EndsWith(":blocked", StringComparison.Ordinal)).EntryId;
            await using var server = CreateServer();
            var permissions = new List<ManagementPermission>();
            new CordisManagement(session, (_, permission) =>
            {
                permissions.Add(permission);
                return Task.FromResult(permission.Operation == "read" || permission.Target == allowed);
            }, _ => new SettingsPolicy([])).Map(server);
            await server.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(server.Urls.Single()) };
            using var state = JsonDocument.Parse(await client.GetStringAsync("/cordis/state"));
            client.DefaultRequestHeaders.Add("If-Cordis-Generation", state.RootElement.GetProperty("generation").GetString());
            var before = Directory.GetFiles(profile).ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
            var body = new Dictionary<string, object?>
            {
                ["entryId"] = allowed, [identityField] = blocked,
                ["kind"] = "plugin", ["enabled"] = false, ["runtimeVersion"] = "0.2.0-rc.2", ["acceptRisk"] = true,
            };
            using var response = read
                ? await client.GetAsync($"/cordis/{endpoint}?entryId={Uri.EscapeDataString(allowed)}&{identityField}={Uri.EscapeDataString(blocked)}")
                : await client.PostAsJsonAsync("/cordis/" + endpoint + "?entryId=" + Uri.EscapeDataString(allowed), body);
            // Check state as well as HTTP refusal: a successful enable request used
            // to authorize allowed while actually disabling blocked.
            Assert.All(await session.ConfigurationOperations.ListPluginsAsync(), plugin => Assert.True(plugin.Enabled));
            Assert.Equal(before.OrderBy(pair => pair.Key), Directory.GetFiles(profile)
                .ToDictionary(path => Path.GetFileName(path), File.ReadAllText).OrderBy(pair => pair.Key));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(blocked, permissions.Last().Target);
            if (endpoint == "enable")
            {
                body["target"] = allowed;
                body["entryId"] = blocked;
                using var accepted = await client.PostAsJsonAsync("/cordis/enable?name=" + Uri.EscapeDataString(blocked), body);
                Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
                var after = await session.ConfigurationOperations.ListPluginsAsync();
                Assert.False(after.Single(plugin => plugin.EntryId == allowed).Enabled);
                Assert.True(after.Single(plugin => plugin.EntryId == blocked).Enabled);
                Assert.Equal(allowed, permissions.Last().Target);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
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
        server.MapPost("/cordis/inspect", () => Results.Text("{\"hash\":\"inspected\"}", "application/json"));
        server.MapPost("/cordis/install", () => Results.StatusCode(status));
        server.MapGet("/cordis/install/wait", () =>
        {
            Interlocked.Increment(ref waits);
            return Results.Text("{\"requestId\":\"reused\",\"application\":\"applied\",\"target\":\"previous-package\"}", "application/json");
        });
        await server.StartAsync();
        var result = await RunCliAsync("install", server.Urls.Single() + "/cordis", "new-package", "1.0.0",
            "--source", "explicit-feed", "--request-id", "reused", "--approve-build");
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Management returned " + status, result.Error);
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
        server.MapGet("/cordis/state", () => Results.Text("{\"generation\":\"" + generation + "\"}", "application/json"));
        server.MapPost("/cordis/inspect", () => Results.Text("{\"hash\":\"inspected\"}", "application/json"));
        server.MapPost("/cordis/install", (HttpContext http) =>
        {
            if (replaceHost) generation = "replacement";
            http.Abort();
            return Task.CompletedTask;
        });
        server.MapGet("/cordis/install/wait", () =>
        {
            Interlocked.Increment(ref waits);
            return Results.Text("{\"requestId\":\"reused\",\"application\":\"applied\"}", "application/json");
        });
        await server.StartAsync();
        var result = await RunCliAsync("install", server.Urls.Single() + "/cordis", "package", "1.0.0",
            "--source", "explicit-feed", "--request-id", "reused", "--approve-build");
        Assert.Equal(replaceHost ? 1 : 0, result.ExitCode);
        Assert.Equal(replaceHost ? 0 : 1, waits);
        Assert.Contains(replaceHost ? "unknown-result" : "applied", result.Output);
    }

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
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        return (process.ExitCode, await output, await error);
    }

    [Fact]
    public async Task Exported_service_resolves_current_provider_and_rejects_calls_after_revocation()
    {
        await using var context = new Context();
        EffectHandle provider = null!;
        await context.RunAsync(ctx => { provider = ctx.Provide("echo", new EchoService("one")); return Task.CompletedTask; });
        await using var server = CreateServer();
        server.MapCordisService<EchoService, EchoRequest, EchoResponse>("/echo", context, "echo",
            ManagementJson.Default.EchoRequest, ManagementJson.Default.EchoResponse,
            http => Task.FromResult(http.Request.Headers.Authorization == "test-host-policy"),
            (service, request, _) => Task.FromResult(new EchoResponse(service.Prefix + ":" + request.Value)));
        await server.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(server.Urls.Single()) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("test-host-policy");
        Assert.Equal("one:value", (await (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).Content.ReadFromJsonAsync<EchoResponse>())!.Value);
        await context.RunAsync(async _ => await provider.DisposeAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).StatusCode);
        await context.RunAsync(ctx => { ctx.Provide("echo", new EchoService("two")); return Task.CompletedTask; });
        Assert.Equal("two:value", (await (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).Content.ReadFromJsonAsync<EchoResponse>())!.Value);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/echo", new StringContent("[", Encoding.UTF8, "application/json"))).StatusCode);
        await context.DisposeAsync();
        Assert.Equal(HttpStatusCode.Gone, (await client.PostAsJsonAsync("/echo", new EchoRequest("value"))).StatusCode);
    }

    private static WebApplication CreateServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        return builder.Build();
    }

    private static async Task<(string Revision, int Value)> ReadSettingsAsync(HttpClient client)
    {
        using var view = JsonDocument.Parse(await client.GetStringAsync("/cordis/settings?entryId=root:worker"));
        return (view.RootElement.GetProperty("revision").GetString()!, view.RootElement.GetProperty("fields")[0].GetProperty("value").GetInt32());
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
