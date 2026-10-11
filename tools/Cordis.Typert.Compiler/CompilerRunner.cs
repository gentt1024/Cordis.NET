using System.Runtime.CompilerServices;
using System.Text;
using Cordis.Typert.Generator.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cordis.Typert.Compiler;

internal static class CompilerRunner
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static int Run(string[] args)
    {
        Dictionary<string, string>? options = null;
        try
        {
            if (args.Length == 0 || args.Length % 2 != 1)
                throw new InvalidDataException("Expected extract or emit-dotnet followed by --name value options.");
            options = new(StringComparer.Ordinal);
            for (var index = 1;index < args.Length;index += 2)
            {
                if (!args[index].StartsWith("--", StringComparison.Ordinal) ||
                    !options.TryAdd(args[index][2..], args[index + 1]))
                    throw new InvalidDataException("Invalid or duplicate option " + args[index]);
            }

            var allowed = args[0] switch
            {
                "extract" => new[]
                {
                    "project-directory",
                    "sources",
                    "references",
                    "assembly-name",
                    "language-version",
                    "nullable",
                    "defines",
                    "service",
                    "output",
                    "allow-unsafe",
                    "check-overflow",
                    "output-kind",
                    "platform",
                    "optimize"
                },
                "emit-dotnet" => new[] { "model", "namespace", "service", "client", "output" },
                _ => throw new InvalidDataException("Unknown native compiler command " + args[0])
            };
            foreach (var option in options.Keys)
                if (!allowed.Contains(option, StringComparer.Ordinal))
                    throw new InvalidDataException("Unknown --" + option + " for " + args[0]);
            var output = Required(options, "output");
            var content = args[0] switch
            {
                "extract" => Extract(options),
                "emit-dotnet" => DotnetProjectionEmitter.Emit(
                    NativeModelJson.Deserialize(File.ReadAllText(Required(options, "model"))),
                    Required(options, "namespace"),
                    Required(options, "service"),
                    Required(options, "client")),
                _ => throw new InvalidDataException("Unknown native compiler command " + args[0])
            };
            WriteAtomic(output, content);
            Console.WriteLine("CORDISTYPERT " + args[0] + " completed.");
            return 0;
        }
        catch (Exception error)
        {
            if (options?.TryGetValue("output", out var output) == true)
            {
                try
                {
                    File.Delete(Path.GetFullPath(output));
                }
                catch (Exception cleanup)
                {
                    Console.Error.WriteLine("CORDISTYPERT: cannot remove stale output: " + cleanup.Message);
                }
            }

            Console.Error.WriteLine("CORDISTYPERT: " + error.Message);
            return 1;
        }
    }

    private static string Extract(Dictionary<string, string> options)
    {
        var directory = Path.GetFullPath(Required(options, "project-directory"));
        if (!LanguageVersionFacts.TryParse(Required(options, "language-version"), out var language))
            throw new InvalidDataException("Invalid C# language version.");
        if (!Enum.TryParse<NullableContextOptions>(Required(options, "nullable"), true, out var nullable))
            throw new InvalidDataException("Invalid C# nullable context option.");
        var defines = Required(options, "defines")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parse = new CSharpParseOptions(language, DocumentationMode.Parse, preprocessorSymbols: defines);
        var sources = ResponseFile(Required(options, "sources"), directory)
            .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), parse, path, Encoding.UTF8))
            .ToArray();
        if (sources.Length == 0)
            throw new InvalidDataException("No C# sources were supplied in the source response file.");
        var references = ResponseFile(Required(options, "references"), directory)
            .Select(path =>
            {
                var xml = Path.ChangeExtension(path, ".xml");
                DocumentationProvider? documentation = File.Exists(xml) ? new NativeXmlDocumentation(xml) : null;
                return MetadataReference.CreateFromFile(path, documentation: documentation);
            })
            .ToArray();
        var outputKind = options.GetValueOrDefault("output-kind", "Library") switch
        {
            "Library" or "library" or "" => OutputKind.DynamicallyLinkedLibrary,
            "Exe" or "exe" => OutputKind.ConsoleApplication,
            "WinExe" or "winexe" => OutputKind.WindowsApplication,
            var invalid => throw new InvalidDataException("Unsupported authored output kind " + invalid)
        };
        var platformName = options.GetValueOrDefault("platform", "");
        if (!Enum.TryParse<Platform>(platformName.Length == 0 ? "AnyCpu" : platformName, true, out var platform))
            throw new InvalidDataException("Invalid authored platform target.");
        var compilation = CSharpCompilation.Create(
            Required(options, "assembly-name"),
            sources,
            references,
            new CSharpCompilationOptions(
                outputKind,
                nullableContextOptions: nullable,
                allowUnsafe: Boolean(options, "allow-unsafe"),
                checkOverflow: Boolean(options, "check-overflow"),
                platform: platform,
                optimizationLevel: Boolean(options, "optimize") ? OptimizationLevel.Release : OptimizationLevel.Debug));
        var model = SourceContractReader.Read(compilation, directory, Required(options, "service"));
        foreach (var diagnostic in model.Diagnostics)
            Console.WriteLine(
                "CORDISTYPERT " + diagnostic.Category + " " + diagnostic.Code + ": " + diagnostic.Message);
        return NativeModelJson.Serialize(model);
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) ? value : throw new InvalidDataException("Missing --" + name);

    private static bool Boolean(Dictionary<string, string> options, string name)
    {
        var value = options.GetValueOrDefault(name, "");
        return value.Length == 0 ? false :
            bool.TryParse(value, out var parsed) ? parsed :
            throw new InvalidDataException("Invalid boolean --" + name);
    }

    private static IEnumerable<string> ResponseFile(string file, string directory) => File
        .ReadAllLines(file)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .Select(line => Path.GetFullPath(line.Trim().Trim('"'), directory))
        .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static void WriteAtomic(string output, string content)
    {
        var path = Path.GetFullPath(output);
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
