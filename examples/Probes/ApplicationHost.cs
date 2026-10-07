using System.Net;
using System.Text;
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
            using var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            Console.WriteLine("application host ready " + prefix);
            Console.Out.Flush();
            var stopped = false;
            while (!stopped)
            {
                var request = await listener.GetContextAsync();
                try
                {
                    var route = request.Request.Url!.AbsolutePath;
                    if (route == "/settings" && request.Request.HttpMethod == "GET")
                    {
                        var view = await session.ConfigurationOperations.ReadSettingsAsync("root:worker", policy);
                        await JsonAsync(
                            request,
                            new()
                            {
                                ["version"] = 1,
                                ["mode"] = "primitive-live-set",
                                ["revision"] = view.Revision,
                                ["fields"] = view
                                    .Fields.Select(field => new EntryOptions
                                    {
                                        ["name"] = field.Name,
                                        ["kind"] = field.Kind,
                                        ["value"] = field.Value,
                                        ["overridden"] = field.Overridden
                                    })
                                    .ToList(),
                                ["diagnostics"] = view.Diagnostics.ToList(),
                                ["activations"] = activations
                            });
                    }
                    else if (route == "/settings" && request.Request.HttpMethod == "POST")
                    {
                        using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                        var body = (EntryOptions)ConfigurationFile.Parse(await reader.ReadToEndAsync(), true)!;
                        var result = await session.ConfigurationOperations.EditSettingsFieldAsync(
                            "root:worker",
                            (string)body["field"]!,
                            body["value"],
                            (string)body["revision"]!,
                            policy);
                        await JsonAsync(
                            request,
                            new()
                            {
                                ["saved"] = result.Saved,
                                ["applied"] = result.Applied,
                                ["revision"] = result.Revision,
                                ["error"] = result.Error
                            });
                    }
                    else if (route == "/client" && request.Request.HttpMethod == "GET")
                    {
                        // Polling a content identity is the example's generation notification adaptation.
                        // Replacing this one captured object never changes what an old version URL serves.
                        artifact = await ClientArtifact.CaptureAsync(packages, "demo-client", parent);
                        await JsonAsync(
                            request,
                            new()
                            {
                                ["revision"] = artifact.Revision,
                                ["entry"] = "/client/" + artifact.Revision + ".mjs"
                            });
                    }
                    else if (route == "/client/" + artifact.Revision + ".mjs" &&
                             request.Request.HttpMethod is "GET" or "HEAD")
                    {
                        request.Response.Headers["ETag"] = artifact.ETag;
                        request.Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
                        if (request.Request.Headers["If-None-Match"] == artifact.ETag)
                            request.Response.StatusCode = 304;
                        else
                        {
                            request.Response.ContentType = "text/javascript; charset=utf-8";
                            request.Response.ContentLength64 = artifact.Length;
                            if (request.Request.HttpMethod != "HEAD")
                            {
                                await using var content = artifact.OpenRead();
                                await content.CopyToAsync(request.Response.OutputStream);
                            }
                        }

                        request.Response.Close();
                    }
                    else if (route == "/stop" && request.Request.HttpMethod == "POST")
                    {
                        stopped = true;
                        await JsonAsync(
                            request,
                            new()
                            {
                                ["stopped"] = true
                            });
                    }
                    else
                    {
                        request.Response.StatusCode = 404;
                        request.Response.Close();
                    }
                }
                catch (Exception error)
                {
                    request.Response.StatusCode = 400;
                    await JsonAsync(
                        request,
                        new()
                        {
                            ["error"] = error.Message
                        });
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static async Task JsonAsync(HttpListenerContext request, EntryOptions body)
    {
        var content = Encoding.UTF8.GetBytes(ConfigurationFile.Write(body, true));
        request.Response.ContentType = "application/json; charset=utf-8";
        request.Response.ContentLength64 = content.Length;
        await request.Response.OutputStream.WriteAsync(content);
        request.Response.Close();
    }
}
