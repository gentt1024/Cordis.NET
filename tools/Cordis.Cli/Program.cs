using Cordis.Composition;

if (args.Length < 2 || args[0] is not ("validate" or "preview"))
{
    Console.Error.WriteLine("Usage: cordis validate|preview <cordis.yml> [--patch <file>] [--json]");
    return 2;
}
try
{
    var data = await ConfigurationFile.ReadEntriesAsync(args[1]);
    var patches = new List<EntryOptions>();
    bool json = false;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--json") json = true;
        else if (args[i] == "--patch" && ++i < args.Length) patches.AddRange(await Profiles.ReadPatchesAsync(args[i]));
        else throw new ArgumentException("Unknown or incomplete option.");
    }
    var result = EntryPatches.Apply(data, patches, Console.Error.WriteLine);
    if (args[0] == "preview") Console.Write(ConfigurationFile.Write(result, json));
    else Console.WriteLine($"Valid entry document: {result.Count} root entries. Plugin schemas and expressions are evaluated during activation.");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
