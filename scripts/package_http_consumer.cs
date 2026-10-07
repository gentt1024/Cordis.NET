// Copied outside the checkout by verify.py and built exclusively from NuGet packages.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

const string implementation = "one";
var expectedImplementation = args.Single();
var directory = Path.Combine(Path.GetTempPath(), "cordis-http-package-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var profile = Path.Combine(directory, "profile");
    var home = Path.Combine(directory, "home");
    Directory.CreateDirectory(home);
    Profiles.Initialize(profile, []);
    var config = Path.Combine(directory, "cordis.yml");
    await File.WriteAllTextAsync(
        config,
        "- id: worker\n  name: worker\n  config: { left: 1, right: 1, label: original, password: saved-secret-marker }\n");
    var activations = 0;
    EffectHandle provider = null!;
    var plugin = new Plugin<WorkerSettings>
    {
        Configuration = ConfigObject<WorkerSettings>
            .Create(raw =>
            {
                if (raw is not IReadOnlyDictionary<string, object?> map)
                    return ConfigResult<WorkerSettings>.Failure("object required");
                var left = Convert.ToInt32(map.GetValueOrDefault("left", 1));
                var right = Convert.ToInt32(map.GetValueOrDefault("right", 1));
                if (left < 0 || left != right)
                    return ConfigResult<WorkerSettings>.Failure("nonnegative matching pair required");
                return ConfigResult<WorkerSettings>.Success(
                    new(
                        left,
                        right,
                        (string)map.GetValueOrDefault("label", "original")!,
                        (string)map.GetValueOrDefault("password", "default-secret-marker")!));
            })
            .Field("left", ConfigDescriptor.Number().Default(1).Volatile(), value => value.Left)
            .Field("right", ConfigDescriptor.Number().Default(1).Volatile(), value => value.Right)
            .Field("label", ConfigDescriptor.String().Default("original"), value => value.Label)
            .Field(
                "password",
                ConfigDescriptor
                    .String()
                    .Default("default-secret-marker")
                    .Volatile()
                    .WithMetadata(
                        new()
                        {
                            Role = "secret"
                        }),
                value => value.Password)
            .Build(),
        Apply = (context, settings) =>
        {
            activations++;
            provider = context.Provide(
                "echo",
                new EchoService(
                    implementation + ":" + settings.Label,
                    context.Fiber.GetConfigReference<int>("left"),
                    context.Fiber.GetConfigReference<int>("right")));
        },
    };
    var launch = new ProfileLaunch(
        await Profiles.LoadAsync(profile, new Dictionary<string, string>()),
        home,
        [],
        new Dictionary<string, string>());
    await using var session = await ProfileSession.StartAsync(
        config,
        launch,
        new StaticModuleResolver().Register("worker", plugin));
    var builder = WebApplication.CreateSlimBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Logging.ClearProviders();
    await using var server = builder.Build();
    new CordisManagement(
        session,
        (http, permission) =>
            Task.FromResult(
                http.Request.Headers.Authorization == "consumer-policy" ||
                http.Request.Headers.Authorization == "settings-only" && permission.Operation == "settings-read"),
        _ => new SettingsPolicy(["left", "right", "password"])).Map(server);
    server.MapCordisService<EchoService, EchoRequest, EchoResponse>(
        "/echo",
        session.Context,
        "echo",
        ConsumerJson.Default.EchoRequest,
        ConsumerJson.Default.EchoResponse,
        http => Task.FromResult(http.Request.Headers.Authorization == "consumer-policy"),
        (service, request, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                new EchoResponse($"{service.Prefix}:{service.Left.Value}/{service.Right.Value}:{request.Value}"));
        });
    await server.StartAsync();
    using var client = new HttpClient
    {
        BaseAddress = new Uri(server.Urls.Single()),
        Timeout = TimeSpan.FromSeconds(20)
    };
    Require(
        (await client.GetAsync("/cordis/settings?entryId=root:worker")).StatusCode == HttpStatusCode.Forbidden,
        "unauthorized settings");
    client.DefaultRequestHeaders.Authorization = new("consumer-policy");
    using var state = await Read("/cordis/state");
    var generation = state.RootElement.GetProperty("generation").GetString()!;
    using var initial = await Read("/cordis/settings?entryId=root:worker");
    CheckRedaction(initial);
    var revision = initial.RootElement.GetProperty("revision").GetString()!;
    var batch = """[{"op":"set","path":["left"],"value":2},{"op":"set","path":["right"],"value":2}]""";
    using (var unfenced = await client.PostAsync("/cordis/settings", Body(revision, batch)))
        Require(unfenced.StatusCode == HttpStatusCode.Conflict, "missing generation fence");
    client.DefaultRequestHeaders.Add("If-Cordis-Generation", generation);
    using (var changed = await Edit("settings", revision, batch))
        CheckApplied(changed);
    using (var current = await Read("/cordis/settings?entryId=root:worker"))
    {
        CheckRedaction(current);
        CheckPair(current, 2);
        Require(activations == 1, "batch settings restarted static plugin");
    }

    using (var stale = await Edit("settings", revision, batch))
        Require(
            stale.RootElement.GetProperty("error").GetString() == "conflict" &&
            !stale.RootElement.GetProperty("saved").GetBoolean(),
            "stale revision accepted");
    using (var current = await Read("/cordis/settings?entryId=root:worker"))
    using (var reset = await Edit(
               "settings",
               current.RootElement.GetProperty("revision").GetString()!,
               """[{"op":"unset","path":["left"]},{"op":"unset","path":["right"]}]"""))
        CheckApplied(reset);
    using (var current = await Read("/cordis/settings?entryId=root:worker"))
        CheckPair(current, 1);
    using (var schema = await Read("/cordis/settings/schema?entryId=root:worker"))
    {
        Require(
            !schema.RootElement.ToString().Contains("saved-secret-marker", StringComparison.Ordinal) &&
            !schema.RootElement.ToString().Contains("default-secret-marker", StringComparison.Ordinal),
            "settings schema leaked a secret");
        Require(
            schema.RootElement.GetProperty("schemastery").GetProperty("refs").EnumerateObject().Any(),
            "missing form declaration envelope");
    }

    await Echo(expectedImplementation + ":original:1/1:value");
    client.DefaultRequestHeaders.Authorization = new("settings-only");
    Require(
        (await client.GetAsync("/cordis/configuration?entryId=root:worker")).StatusCode == HttpStatusCode.Forbidden,
        "settings permission exposed ordinary raw configuration");
    client.DefaultRequestHeaders.Authorization = new("consumer-policy");
    using (var raw = await Read("/cordis/configuration?entryId=root:worker"))
    {
        Require(raw.RootElement.GetProperty("raw").GetProperty("label").GetString() == "original", "initial raw label");
        using var changed = await Edit(
            "configuration",
            raw.RootElement.GetProperty("revision").GetString()!,
            """[{"op":"set","path":["label"],"value":"updated"}]""");
        CheckApplied(changed);
    }

    Require(activations == 2, "ordinary update did not create a new activation");
    await Echo(expectedImplementation + ":updated:1/1:value");
    using (var raw = await Read("/cordis/configuration?entryId=root:worker"))
    {
        Require(
            raw.RootElement.GetProperty("raw").GetProperty("label").GetString() == "updated",
            "ordinary raw update missing");
        using var restored = await Edit(
            "configuration",
            raw.RootElement.GetProperty("revision").GetString()!,
            """[{"op":"unset","path":["label"]}]""");
        CheckApplied(restored);
    }

    Require(activations == 3, "ordinary inheritance restoration did not reactivate");
    await Echo(expectedImplementation + ":original:1/1:value");
    using (var raw = await Read("/cordis/configuration?entryId=root:worker"))
        Require(
            raw.RootElement.GetProperty("raw").GetProperty("label").GetString() == "original",
            "ordinary inheritance restoration missing");
    using (var refused = await client.PostAsync(
               "/cordis/install",
               new StringContent(
                   """{"name":"new-code","version":"1.0.0","source":"explicit","requestId":"static-install","inspectedHash":"unused"}""",
                   Encoding.UTF8,
                   "application/json")))
    {
        Require(refused.StatusCode == HttpStatusCode.BadRequest, "static host accepted dynamic installation");
        Require(
            (await refused.Content.ReadAsStringAsync()).Contains(
                "static code changes require republishing",
                StringComparison.Ordinal),
            "static code boundary missing");
    }

    await session.Context.RunAsync(async _ => await provider.DisposeAsync());
    using (var unavailable = await client.PostAsync(
               "/echo",
               new StringContent("""{"value":"value"}""", Encoding.UTF8, "application/json")))
        Require(unavailable.StatusCode == HttpStatusCode.ServiceUnavailable, "revoked provider remained callable");
    await server.StopAsync();
    Console.WriteLine($"independent AspNetCore HTTP package consumer passed: {implementation}");

    async Task<JsonDocument> Read(string path)
    {
        using var response = await client.GetAsync(path);
        Require(response.StatusCode == HttpStatusCode.OK, path + " returned " + response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    async Task<JsonDocument> Edit(string route, string expectedRevision, string operations)
    {
        using var response = await client.PostAsync("/cordis/" + route, Body(expectedRevision, operations));
        Require(response.StatusCode == HttpStatusCode.OK, route + " returned " + response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    async Task Echo(string expected)
    {
        using var response = await client.PostAsync(
            "/echo",
            new StringContent("""{"value":"value"}""", Encoding.UTF8, "application/json"));
        Require(response.StatusCode == HttpStatusCode.OK, "exported service unavailable");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Require(
            body.RootElement.GetProperty("value").GetString() == expected,
            "exported service did not resolve the current static implementation and provider");
    }
}
finally
{
    Directory.Delete(directory, recursive: true);
}

static StringContent Body(string revision, string operations) =>
    new(
        "{\"entryId\":\"root:worker\",\"revision\":\"" + JsonEncodedText.Encode(revision) + "\",\"operations\":" +
        operations + "}",
        Encoding.UTF8,
        "application/json");

static void Require(bool condition, string failure)
{
    if (!condition)
        throw new InvalidOperationException(failure);
}

static void CheckApplied(JsonDocument result) =>
    Require(
        result.RootElement.GetProperty("saved").GetBoolean() && result.RootElement.GetProperty("applied").GetBoolean(),
        "HTTP edit failed: " + result.RootElement);

static void CheckPair(JsonDocument view, int expected)
{
    foreach (var name in new[] { "left", "right" })
        Require(
            view
                .RootElement.GetProperty("fields")
                .EnumerateArray()
                .Single(field => field.GetProperty("name").GetString() == name)
                .GetProperty("value")
                .GetInt32() == expected,
            "pair value mismatch");
}

static void CheckRedaction(JsonDocument view)
{
    Require(
        !view.RootElement.ToString().Contains("saved-secret-marker", StringComparison.Ordinal),
        "settings leaked secret value");
    var secret = view.RootElement.GetProperty("secrets").EnumerateArray().Single();
    Require(
        secret.GetProperty("path")[0].GetString() == "password" && secret.GetProperty("set").GetBoolean(),
        "secret presence sidecar missing");
    Require(
        view
            .RootElement.GetProperty("fields")
            .EnumerateArray()
            .All(field => field.GetProperty("name").GetString() != "label"),
        "ordinary field leaked into settings");
}

record WorkerSettings(int Left, int Right, string Label, string Password);

record EchoService(string Prefix, ConfigReference<int> Left, ConfigReference<int> Right);

record EchoRequest(string Value);

record EchoResponse(string Value);

[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class ConsumerJson : JsonSerializerContext;
