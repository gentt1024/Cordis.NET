using Cordis.AspNetCore;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

internal static class ApplicationCommands
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments.Length == 0) throw new ArgumentException("Usage: cordis run <profile> --source <feed> [--url http://127.0.0.1:port --authorization-env NAME] [--allow-build Name@Version]");
            var profile = Path.GetFullPath(arguments[0]);
            var sources = new List<string>();
            var builds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var settings = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string? address = null;
            string? authorization = null;
            for (var index = 1; index < arguments.Length; index++)
            {
                if (++index >= arguments.Length) throw new ArgumentException("An option value is missing.");
                switch (arguments[index - 1])
                {
                    case "--source": sources.Add(arguments[index]); break;
                    case "--url": address = arguments[index]; break;
                    case "--authorization-env": authorization = Environment.GetEnvironmentVariable(arguments[index]); break;
                    case "--allow-build": builds.Add(arguments[index]); break;
                    case "--settings":
                        var selection = arguments[index].Split('=', 2);
                        if (selection.Length != 2) throw new ArgumentException("--settings expects entryId=field,field.");
                        settings[selection[0]] = selection[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
                        break;
                    default: throw new ArgumentException("Unknown run option: " + arguments[index - 1]);
                }
            }
            if (sources.Count == 0) throw new ArgumentException("At least one explicit --source is required.");
            if (address is not null && string.IsNullOrWhiteSpace(authorization))
                throw new ArgumentException("Management requires a nonempty authorization value from the named environment variable.");
            Directory.CreateDirectory(profile);
            Directory.CreateDirectory(Path.Combine(profile, ".cordis"));
            // A second CLI host must fail before it activates plugins. Online commands use this host's API.
            await using var ownership = new FileStream(Path.Combine(profile, ".cordis", "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (!File.Exists(Path.Combine(profile, "package.json"))) Profiles.Initialize(profile, []);
            var configuration = Path.Combine(profile, "cordis.yml");
            if (!File.Exists(configuration)) await File.WriteAllTextAsync(configuration, "[]\n");
            await using var resolver = new ClrModuleResolver(Path.Combine(profile, ".cordis", "shadow"), [typeof(ConfigObject<>).Assembly]);
            using var toolchain = new DotnetPluginToolchain(profile, resolver, sources);
            var launch = new ProfileLaunch(await Profiles.LoadAsync(profile, new Dictionary<string, string>(), toolchain.Bundles),
                Path.Combine(profile, ".cordis", "home"), [], new Dictionary<string, string>(), toolchain.Bundles)
            { CompatibilityPackageName = DotnetPluginToolchain.NormalizeCompatibilityPackageName, ManifestLocator = toolchain.LocateManifest };
            await using var session = await ProfileSession.StartAsync(configuration, launch, resolver, enableHmr: true);
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls(address ?? "http://127.0.0.1:0");
            await using var host = builder.Build();
            if (address is not null)
                new CordisManagement(session, (http, permission) => Task.FromResult(
                    http.Request.Headers.Authorization == authorization
                    && (permission.Operation != "build" || permission.Package is { } package
                        && builds.Contains(package.Request.Name + "@" + package.Request.Version))),
                    entryId => new SettingsPolicy(settings.GetValueOrDefault(entryId) ?? []), toolchain).Map(host);
            await host.StartAsync();
            Console.WriteLine("Profile running: " + profile);
            if (address is not null) Console.WriteLine("Management: " + host.Urls.Single() + "/cordis");
            await host.WaitForShutdownAsync();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
