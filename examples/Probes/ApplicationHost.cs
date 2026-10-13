using System.Net;
using Cordis.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Cordis.Composition;
using Cordis.Extensions;

namespace Cordis.Example.Probes;

// Loopback demonstration transport. A deployed host must supply authentication, authorization,
// request limits and its own secret policy; management ownership stays with ProfileSession.
internal static class ApplicationHost
{
    private sealed record Settings(int Limit, string Label, string Token);

    internal static async Task RunAsync(string prefix, string clientDirectory)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var address) || !address.IsLoopback ||
            address.Scheme != "http")
            throw new ArgumentException(
                "The demonstration host accepts only an explicit loopback HTTP address.",
                nameof(prefix));
        var directory = Directory.CreateTempSubdirectory("cordis-application-host-").FullName;
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
            var config = Path.Combine(directory, "cordis.yml");
            await File.WriteAllTextAsync(
                config,
                "- id: worker\n  name: worker\n  config: { limit: 1, label: worker, token: SYNTHETIC_SECRET_MUST_NOT_LEAK }\n");
            var activations = 0;
            var plugin = new Plugin<Settings>
            {
                Configuration = ConfigObject<Settings>
                    .Create(raw =>
                        raw is IReadOnlyDictionary<string, object?> map &&
                        Convert.ToInt32(map.GetValueOrDefault("limit")) is var limit && limit > 0 &&
                        map.GetValueOrDefault("label") is string label && map.GetValueOrDefault("token") is string token
                            ? ConfigResult<Settings>.Success(new(limit, label, token))
                            : ConfigResult<Settings>.Failure("positive limit required"))
                    .Field("limit", ConfigDescriptor.Number().Volatile(), value => value.Limit)
                    .Field("label", ConfigDescriptor.String(), value => value.Label)
                    .Field("token", ConfigDescriptor.String().Volatile(), value => value.Token)
                    .Build(),
                Apply = (_, _) => activations++,
            };
            await using var session = await ProfileSession.StartAsync(
                config,
                launch,
                new StaticModuleResolver().Register("worker", plugin));
            var policy = new SettingsPolicy(["limit", "label", "token"], ["token"]);
            var registry = new TypertRegistry(session.Context);
            var contribution = SettingsControllerTypert.Contribution("Cordis.Probes.Settings");
            await session.Context.RunAsync(context =>
            {
                new SettingsForms(context, session.ConfigurationOperations, _ => policy);
                new SettingsController(context);
                registry.Register(context, contribution);
                return Task.CompletedTask;
            });
            var gateway = new TypertGateway(session.Context, registry);

            var generation = new DeploymentGeneration(
                Path.Combine(home, "profiles"),
                profile,
                [
                    new(
                        "demo-client",
                        clientDirectory,
                        "1",
                        Path.Combine(clientDirectory, "package.json"),
                        DeploymentPackageScope.Installation)
                ]);
            var packages = new DeploymentPackageResolver(generation, native: (_, _) => clientDirectory);
            var parent = new Uri(config);
            var artifact = await ClientArtifact.CaptureAsync(packages, "demo-client", parent);
            TypertHostConnection connection = null!;
            await session.Context.RunAsync(owner =>
            {
                connection = new(owner, gateway);
                owner.Provide("connection", connection);
                return Task.CompletedTask;
            });
            await using var connectionOwner = connection;
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(prefix.TrimEnd('/'));
            await using var app = builder.Build();
            app.UseWebSockets();
            app.MapCordisRemote(
                "/remote",
                connection,
                (http, _) =>
                    Task.FromResult(http.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote)));
            app.MapGet(
                "/status",
                (HttpContext http) => JsonAsync(
                    http,
                    new()
                    {
                        ["activations"] = activations
                    }));
            app.MapGet(
                "/client",
                async (HttpContext http) =>
                {
                    // Polling a content identity is the example's generation notification adaptation.
                    artifact = await ClientArtifact.CaptureAsync(packages, "demo-client", parent);
                    await JsonAsync(
                        http,
                        new()
                        {
                            ["revision"] = artifact.Revision,
                            ["entry"] = "/client/" + artifact.Revision + ".mjs"
                        });
                });
            app.MapMethods(
                "/client/{revision}.mjs",
                ["GET", "HEAD"],
                async (HttpContext http) =>
                {
                    var captured = artifact;
                    if (http.Request.RouteValues["revision"]?.ToString() != captured.Revision)
                    {
                        http.Response.StatusCode = StatusCodes.Status404NotFound;
                        return;
                    }

                    http.Response.Headers.ETag = captured.ETag;
                    http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    if (http.Request.Headers.IfNoneMatch == captured.ETag)
                        http.Response.StatusCode = StatusCodes.Status304NotModified;
                    else
                    {
                        http.Response.ContentType = "text/javascript; charset=utf-8";
                        http.Response.ContentLength = captured.Length;
                        if (http.Request.Method != "HEAD")
                        {
                            await using var content = captured.OpenRead();
                            await content.CopyToAsync(http.Response.Body, http.RequestAborted);
                        }
                    }
                });
            app.MapPost(
                "/stop",
                async (HttpContext http) =>
                {
                    await JsonAsync(
                        http,
                        new()
                        {
                            ["stopped"] = true
                        });
                    app.Lifetime.StopApplication();
                });
            await app.RunAsync();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static Task JsonAsync(HttpContext request, EntryOptions body)
    {
        request.Response.ContentType = "application/json; charset=utf-8";
        return request.Response.WriteAsync(ConfigurationFile.Write(body, true));
    }
}
