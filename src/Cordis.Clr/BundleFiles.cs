using System.Security.Cryptography;
using System.Text.Json;

namespace Cordis.Clr;

// Installation receipts describe complete code artifacts, never per-ALC instances.
internal static class BundleFiles
{
    public static string ReceiptPath(string directory) =>
        Path.Combine(Path.GetDirectoryName(directory)!, "." + Path.GetFileName(directory) + ".files.json");

    public static void Record(string directory)
    {
        var files = ReadTree(directory);
        var receipt = ReceiptPath(directory);
        using var output = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(output, files);
    }

    public static void Verify(string directory)
    {
        var receipt = ReceiptPath(directory);
        if (!File.Exists(receipt))
            throw new InvalidDataException(
                "The deployment has no Cordis file receipt. Reinstall this legacy or incomplete deployment before loading it.");
        RejectLink(new FileInfo(receipt));
        using var input = File.OpenRead(receipt);
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(input) ??
            throw new InvalidDataException("The deployment file receipt is invalid.");
        var actual = ReadTree(directory);
        if (expected.Count != actual.Count || actual.Any(item =>
                !expected.TryGetValue(item.Key, out var hash) || hash != item.Value))
            throw new InvalidDataException("The deployment files differ from their published receipt.");
    }

    public static void ValidateTree(string directory)
    {
        foreach (var _ in Files(directory))
        {
        }
    }

    private static Dictionary<string, string> ReadTree(string directory) => Files(directory)
        .Order(StringComparer.Ordinal)
        .ToDictionary(
            file => Path.GetRelativePath(directory, file).Replace('\\', '/'),
            file =>
            {
                using var input = File.OpenRead(file);
                return Convert.ToHexString(SHA256.HashData(input));
            },
            StringComparer.Ordinal);

    private static IEnumerable<string> Files(string directory)
    {
        RejectLink(new DirectoryInfo(directory));
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            RejectLink(entry);
            if (entry is DirectoryInfo child)
            {
                foreach (var file in Files(child.FullName))
                    yield return file;
            }
            else
                yield return entry.FullName;
        }
    }

    private static void RejectLink(FileSystemInfo entry)
    {
        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Deployment files must not traverse links: " + entry.FullName);
    }
}
