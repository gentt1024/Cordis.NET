using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis.Clr;

if (args.Length != 10)
    throw new ArgumentException(
        "Expected project, declaration, configuration, framework, runtime, package ID, version, assembly filename, input list, and operation.");
var (project, declaration, configuration, framework, runtime) = (args[0], args[1], args[2], args[3], args[4]);
var (name, version, assembly, inputs, operation) = (args[5], args[6], args[7], args[8], args[9]);
var sourceRoot = Path.GetDirectoryName(declaration)!;
var selected = LocalBuildFiles.SelectProject(sourceRoot, out _);
if (!string.Equals(
        selected,
        project,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
{
    if (operation == "query")
        throw new InvalidDataException($"Local root '{sourceRoot}' selects '{selected}', not '{project}'.");
    return;
}

if (operation == "query")
{
    Console.WriteLine(LocalBuildFiles.Resolve(sourceRoot, project, configuration, framework, runtime));
    return;
}

if (operation != "complete")
    throw new ArgumentException("Unknown local CLR output operation.");
var metadataBytes = File.ReadAllBytes(declaration);
var metadata = JsonNode.Parse(metadataBytes)!.AsObject();
if (metadata["assembly"]?.GetValue<string>() != assembly || Path.GetFileName(assembly) != assembly ||
    !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException($"The declared assembly must match the project output '{assembly}'.");
var location = LocalBuildFiles.Location(sourceRoot, project, configuration, framework, runtime);
Directory.CreateDirectory(location);
var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
var files = new Dictionary<string, string>(comparer);
foreach (var line in File.ReadLines(inputs))
{
    var pair = line.Split('|');
    if (pair.Length != 2)
        throw new InvalidDataException($"Invalid SDK runtime file pair '{line}'.");
    var source = Path.GetFullPath(pair[0]);
    var relative = LocalBuildFiles.Relative(pair[1]).Replace('\\', '/');
    var inside = Path.GetRelativePath(sourceRoot, source);
    var external = Path.IsPathRooted(inside) || inside == ".." || inside.StartsWith(
        ".." + Path.DirectorySeparatorChar,
        StringComparison.Ordinal);
    LocalBuildFiles.CheckPath(source, external ? source : sourceRoot);
    if (comparer.Equals(relative, "cordis.plugin.json") && comparer.Equals(source, declaration))
        continue;
    if (new[] { "cordis.plugin.json", "package.json", "cordis.patch.yml" }.Contains(relative, comparer))
        throw new InvalidDataException($"'{relative}' conflicts with the bundle declaration.");
    if (files.TryGetValue(relative, out var other) && !comparer.Equals(other, source))
        throw new InvalidDataException($"Two different files claim '{relative}': '{other}', '{source}'.");
    files[relative] = source;
}

foreach (var filename in new[]
         {
             assembly, Path.ChangeExtension(assembly, ".deps.json"), Path.ChangeExtension(assembly, ".pdb")
         })
    if (!files.ContainsKey(filename))
        throw new InvalidDataException($"Missing required local CLR output '{filename}' for '{project}'.");

var manifest = ClrBundleMetadata.Manifest(
    name,
    version,
    metadata["description"]?.GetValue<string>(),
    JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataBytes)!);
var generated = new Dictionary<string, byte[]>
{
    ["cordis.plugin.json"] = metadataBytes,
    ["cordis.patch.yml"] = metadata["patches"] is JsonArray patches
        ? JsonSerializer.SerializeToUtf8Bytes(patches)
        : JsonSerializer.SerializeToUtf8Bytes(ClrBundleMetadata.DefaultPatches(name)),
    ["package.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest)
};
var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
foreach (var (relative, source) in files)
{
    using var input = File.OpenRead(source);
    hashes.Add(relative, Convert.ToHexString(SHA256.HashData(input)));
}

foreach (var (relative, bytes) in generated)
    hashes.Add(relative, Convert.ToHexString(SHA256.HashData(bytes)));

var receipt = JsonSerializer.SerializeToUtf8Bytes(hashes);
var identity = Convert.ToHexString(SHA256.HashData(receipt));
var output = Path.Combine(location, identity, "bundle");
if (!Directory.Exists(output))
{
    var temporary = Path.Combine(location, ".prepare-" + Guid.NewGuid().ToString("N"));
    var bundle = Directory.CreateDirectory(Path.Combine(temporary, "bundle")).FullName;
    try
    {
        foreach (var (relative, source) in files)
        {
            var destination = Path.Combine(bundle, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }

        foreach (var (relative, bytes) in generated)
            File.WriteAllBytes(Path.Combine(bundle, relative), bytes);
        File.WriteAllBytes(BundleFiles.ReceiptPath(bundle), receipt);
        BundleFiles.Verify(bundle); // Includes changes between hashing and copying.
        try
        {
            Directory.Move(temporary, Path.GetDirectoryName(output)!);
        }
        catch (IOException) when (Directory.Exists(output))
        {
            VerifyRetained();
        }
    }
    finally
    {
        if (Directory.Exists(temporary))
            Directory.Delete(temporary, recursive: true);
    }
}
else
    VerifyRetained();

var pointer = Path.Combine(location, "current");
LocalBuildFiles.CheckPath(pointer, location);
if (!File.Exists(pointer) || File.ReadAllText(pointer) != identity)
{
    var pending = Path.Combine(location, ".current-" + Guid.NewGuid().ToString("N"));
    try
    {
        File.WriteAllText(pending, identity);
        File.Move(pending, pointer, overwrite: true);
    }
    finally
    {
        File.Delete(pending);
    }
}

Console.WriteLine($"Completed {name} {version} ({configuration}, {framework}): {output}");

void VerifyRetained()
{
    LocalBuildFiles.CheckPath(output, location);
    var retainedReceipt = BundleFiles.ReceiptPath(output);
    LocalBuildFiles.CheckPath(retainedReceipt, location);
    if (!receipt.AsSpan().SequenceEqual(File.ReadAllBytes(retainedReceipt)))
        throw new InvalidDataException("The retained local output has a different receipt.");
    BundleFiles.Verify(output);
}
