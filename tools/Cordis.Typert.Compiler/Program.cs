using System.Reflection;
using System.Runtime.Loader;

namespace Cordis.Typert.Compiler;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception error)
        {
            for (var index = 0;index < args.Length - 1;index++)
                if (args[index] == "--output")
                {
                    try
                    {
                        File.Delete(Path.GetFullPath(args[index + 1]));
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

    private static int Run(string[] args)
    {
        if (args.Length < 3 || args[0] != "--roslyn-directory")
            throw new InvalidDataException(
                "--roslyn-directory <selected SDK Roslyn/bincore> must precede the command.");
        var directory = Path.GetFullPath(args[1]);
        foreach (var name in new[] { "Microsoft.CodeAnalysis", "Microsoft.CodeAnalysis.CSharp" })
        {
            if (!File.Exists(Path.Combine(directory, name + ".dll")))
                throw new FileNotFoundException("Selected SDK Roslyn assembly is missing: " + name);
        }

        Assembly? Resolve(AssemblyLoadContext context, AssemblyName requested)
        {
            if (requested.Name is not ("Microsoft.CodeAnalysis" or "Microsoft.CodeAnalysis.CSharp"))
                return null;
            var path = Path.Combine(directory, requested.Name + ".dll");
            var identity = AssemblyName.GetAssemblyName(path);
            if (requested.Version != identity.Version ||
                !Enumerable.SequenceEqual(requested.GetPublicKeyToken() ?? [], identity.GetPublicKeyToken() ?? []))
                throw new FileLoadException(
                    "Selected SDK Roslyn identity does not match the compiler tool reference: " + requested.Name);
            return context.LoadFromAssemblyPath(path);
        }

        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            return CompilerRunner.Run(args[2..]);
        }
        finally
        {
            AssemblyLoadContext.Default.Resolving -= Resolve;
        }
    }
}
