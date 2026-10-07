using Cordis.AspNetCore;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

internal static class ApplicationCommands
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        using var shutdownRequest = new ApplicationShutdown();
        try
        {
            if (arguments.Length == 0)
                throw new ArgumentException(
                    "Usage: cordis run <profile> [--source <feed>] [--url http://127.0.0.1:port --authorization-env NAME] [--allow-build Name@Version] [-- application arguments]");
            var profile = Path.GetFullPath(arguments[0]);
            var sources = new List<string>();
            var applicationArguments = new List<string>();
            var builds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string? address = null;
            string? authorization = null;
            for (var index = 1;index < arguments.Length;index++)
            {
                var option = arguments[index];
                if (option == "--")
                {
                    applicationArguments.AddRange(arguments[(index + 1)..]);
                    break;
                }

                if (option is not ("--source" or "--url" or "--authorization-env" or "--allow-build" or "--settings"))
                {
                    // The first application token starts an opaque remainder, just as
                    // the fixed launcher does; later flags belong to the application.
                    applicationArguments.AddRange(arguments[index..]);
                    break;
                }

                if (++index >= arguments.Length)
                    throw new ArgumentException("An option value is missing: " + option);
                switch (option)
                {
                    case "--source":
                        sources.Add(arguments[index]);
                        break;
                    case "--url":
                        address = arguments[index];
                        break;
                    case "--authorization-env":
                        authorization = Environment.GetEnvironmentVariable(arguments[index]);
                        break;
                    case "--allow-build":
                        builds.Add(arguments[index]);
                        break;
                    case "--settings":
                        var selection = arguments[index].Split('=', 2);
                        if (selection.Length != 2)
                            throw new ArgumentException("--settings expects entryId=field,field.");
                        settings[selection[0]] =
                            selection[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                        break;
                }
            }

            if (address is not null && string.IsNullOrWhiteSpace(authorization))
                throw new ArgumentException(
                    "Management requires a nonempty authorization value from the named environment variable.");
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(Path.Combine(profile, ".cordis"));
            // A second CLI host must fail before it activates plugins. Online commands use this host's API.
            await using var ownership = new FileStream(
                Path.Combine(profile, ".cordis", "host.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            if (!File.Exists(Path.Combine(profile, "package.json")))
                Profiles.Initialize(profile, []);
            var configuration = Path.Combine(profile, "cordis.yml");
            if (!File.Exists(configuration))
                await File.WriteAllTextAsync(configuration, "[]\n");
            await using var resolver = new ClrModuleResolver(
                Path.Combine(profile, ".cordis", "shadow"),
                [typeof(ConfigObject<>).Assembly]);
            using var toolchain = new DotnetPluginToolchain(profile, resolver, sources);
            var launch = new ProfileLaunch(
                await Profiles.LoadAsync(profile, new Dictionary<string, string>(), toolchain.Bundles),
                Path.Combine(profile, ".cordis", "home"),
                [],
                new Dictionary<string, string>(),
                toolchain.Bundles)
            {
                CompatibilityPackageName = DotnetPluginToolchain.NormalizeCompatibilityPackageName,
                ManifestLocator = toolchain.LocateManifest
            };
            // The ordinary .NET host supplies process lifetime; only an explicitly requested
            // management endpoint creates a web server. Neither host parses application arguments.
            using var host = CreateHost(address);
            var exit = shutdownRequest.Requested;
            var ready = new ApplicationReadiness();
            var readiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = await ProfileSession.StartAsync(
                configuration,
                launch,
                resolver,
                enableHmr: true,
                prepare: context =>
                {
                    CommandLineArguments.Provide(context, applicationArguments);
                    context.Provide("appExit", (ApplicationExit)shutdownRequest.Request);
                    context.Provide("appReady", (IApplicationReady)ready);
                    return Task.CompletedTask;
                },
                diagnostic: error => Console.Error.WriteLine(error),
                applicationReady: readiness.Task);
            try
            {
                if (address is not null)
                    new CordisManagement(
                        session,
                        (http, permission) =>
                            Task.FromResult(
                                http.Request.Headers.Authorization == authorization &&
                                (permission.Operation != "build" || permission.Package is { } package &&
                                    builds.Contains(package.Request.Name + "@" + package.Request.Version))),
                        entryId => new SettingsPolicy(settings.GetValueOrDefault(entryId) ?? []),
                        toolchain).Map((WebApplication)host);
                // Help and one-shot commands may request exit during activation. Still await
                // startup above so a real boot failure wins, then dispose the tree normally.
                if (exit.IsCompleted)
                    return await exit;
                await host.StartAsync();
                Console.WriteLine("Profile running: " + profile);
                if (host is WebApplication web)
                    Console.WriteLine("Management: " + web.Urls.Single() + "/cordis");
                var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
                if (!exit.IsCompleted && !lifetime.ApplicationStopping.IsCancellationRequested)
                {
                    ready.Commit();
                    readiness.TrySetResult(true);
                }

                var shutdown = host.WaitForShutdownAsync();
                if (await Task.WhenAny(shutdown, exit) == exit)
                    host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                await shutdown;
                return exit.IsCompletedSuccessfully ? await exit : 0;
            }
            catch
            {
                shutdownRequest.Fail();
                throw;
            }
            finally
            {
                // HMR's existing readiness gate also settles when startup exits or fails.
                readiness.TrySetResult(false);
            }
        }
        catch (Exception error)
        {
            shutdownRequest.Fail();
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static IHost CreateHost(string? address)
    {
        if (address is null)
            return Host.CreateApplicationBuilder().Build();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(address);
        return builder.Build();
    }
}
