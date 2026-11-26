using Cordis.Clr;
using Cordis.Composition;

internal static class ProfilePackageCommands
{
    private const string Usage =
        "cordis plugin --profile <directory> [--source <feed>] [--allow-build ID@version] [--configuration Debug|Release] add <spec>... | install | update [ID] | remove <ID> | list | why <ID>";

    internal static async Task<int> RunAsync(string[] arguments)
    {
        if (arguments is ["--help"] or ["-h"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            string? profile = null;
            var configuration = "Debug";
            var sources = new List<string>();
            var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var words = new List<string>();
            for (var index = 0;index < arguments.Length;index++)
            {
                var word = arguments[index];
                if (!word.StartsWith('-'))
                {
                    words.Add(word);
                    continue;
                }

                if (word is not ("--profile" or "--source" or "--allow-build" or "--configuration"))
                    throw new ArgumentException("Unsupported profile package option: " + word);
                if (++index >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index]) ||
                    arguments[index].StartsWith('-'))
                    throw new ArgumentException("Missing value for " + word);
                var value = arguments[index];
                switch (word)
                {
                    case "--profile" when profile is null:
                        profile = Path.GetFullPath(value);
                        break;
                    case "--source":
                        sources.Add(value);
                        break;
                    case "--allow-build":
                        approved.Add(value);
                        break;
                    case "--configuration":
                        configuration = value;
                        break;
                    default:
                        throw new ArgumentException("Specify --profile once.");
                }
            }

            if (profile is null || words.ToArray() is not (["add", _, ..] or ["install"] or ["update"] or
                    ["update", _] or ["remove", _] or ["list"] or ["why", _]))
                throw new ArgumentException(Usage);

            await using var resolver = new ClrModuleResolver();
            using var packages = DotnetPluginToolchain.OpenOffline(profile, resolver, sources, "dotnet");

            bool Approve(PackageInspection package) =>
                approved.Contains(package.Request.Name + "@" + package.Request.Version);

            if (words[0] is "list" or "why")
            {
                var manifest = PackageManifest.Read(Path.Combine(profile, "package.json"));
                var dependencies = manifest.Raw.GetValueOrDefault("dependencies") as IDictionary<string, object?> ??
                    new EntryOptions();
                if (words[0] == "why")
                {
                    var dependency = packages.ResolvePackageName(words[1]);
                    if (dependencies.Keys.Any(name => name.Equals(dependency, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine("Direct profile dependency: " + dependency);
                    foreach (var (name, graph) in manifest.RestoreGraphs)
                    {
                        Console.WriteLine("NuGet graph for " + name);
                        await packages.WhyAsync(
                            name,
                            (string)dependencies[name]!,
                            graph,
                            dependency,
                            Console.WriteLine);
                    }

                    if (manifest.RestoreGraphs.Count == 0)
                        Console.WriteLine(
                            "No saved NuGet restore graphs. Source project references belong to MSBuild.");
                }
                else
                {
                    foreach (var (name, declaration) in dependencies)
                    {
                        var saved = declaration as string ??
                            throw new FormatException("Expected a dependency string for " + name);
                        var resolved = saved.StartsWith("link:", StringComparison.Ordinal)
                            ? "source"
                            : "no saved graph";
                        if (manifest.RestoreGraphs.TryGetValue(name, out var graph))
                        {
                            try
                            {
                                resolved = packages.ReadLockedVersion(name, saved, graph);
                            }
                            catch (IOException error) when
                                (error is FileNotFoundException or DirectoryNotFoundException)
                            {
                                resolved = "saved graph missing";
                            }
                        }

                        Console.WriteLine($"{name}\t{saved}\t{resolved}\tselected={manifest.Bundles.Contains(name)}");
                    }

                    Console.WriteLine("Runtime activation: unobserved");
                }

                return 0;
            }

            var result = words[0] switch
            {
                "add" => await packages.AddProfilePackagesAsync(
                    words.Skip(1).ToArray(),
                    Approve,
                    Console.Error.WriteLine,
                    configuration),
                "remove" => await packages.RemoveProfilePackageAsync(words[1]),
                _ => await packages.RestoreProfilePackagesAsync(
                    words[0] == "update",
                    words.Count == 2 ? words[1] : null,
                    Approve,
                    Console.Error.WriteLine,
                    configuration)
            };
            foreach (var plain in result.AddedPlainDependencies)
                Console.WriteLine("Installed ordinary dependency: " + plain);
            Console.WriteLine($"Saved=true Changed={result.Changed} Application=unobserved Notification=unobserved");
            return 0;
        }
        catch (ArgumentException error)
        {
            Console.Error.WriteLine(error.Message);
            return 2;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
