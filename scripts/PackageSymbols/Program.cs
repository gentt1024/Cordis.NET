using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

// Inspects actual release artifacts; it never fetches source or trusts the checkout's working bytes.
if (args.Length is not (2 or 3)) throw new ArgumentException("Usage: PackageSymbols <package-directory> <git-source-root> [consumer-frame.json]");
using var frameReport = args.Length == 3 ? JsonDocument.Parse(File.ReadAllBytes(args[2])) : null;
object? debugSource = null;
var sourceLinkKind = new Guid("CC110556-A091-4D38-9FEC-25AB9A351A6A");
var embeddedSourceKind = new Guid("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
var sha256Kind = new Guid("8829D00F-11B8-4213-878B-770E8597AC16");
var sha1Kind = new Guid("FF1816EC-AA5E-4D10-87F7-6F4963833460");
var results = new List<object>();
foreach (string package in Directory.GetFiles(args[0], "*.nupkg").Order(StringComparer.Ordinal))
{
    using var archive = ZipFile.OpenRead(package);
    using var manifestStream = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
    var metadata = XDocument.Load(manifestStream).Descendants().Single(element => element.Name.LocalName == "metadata");
    string commit = metadata.Elements().Single(element => element.Name.LocalName == "repository").Attribute("commit")!.Value;
    using var symbols = ZipFile.OpenRead(Path.ChangeExtension(package, ".snupkg"));
    using var symbolManifestStream = symbols.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
    var symbolMetadata = XDocument.Load(symbolManifestStream).Descendants().Single(element => element.Name.LocalName == "metadata");
    foreach (string field in new[] { "id", "version" })
        Require(metadata.Elements().Single(element => element.Name.LocalName == field).Value == symbolMetadata.Elements().Single(element => element.Name.LocalName == field).Value, $"nupkg/snupkg {field} mismatch");
    Require(symbolMetadata.Elements().Single(element => element.Name.LocalName == "repository").Attribute("commit")?.Value == commit, "nupkg/snupkg commit mismatch");
    int sourceDocuments = 0, embeddedDocuments = 0, assemblyCount = 0;
    foreach (var pdbEntry in symbols.Entries.Where(entry => entry.FullName.EndsWith(".pdb", StringComparison.Ordinal)))
    {
        byte[] pdbBytes = Read(pdbEntry);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdbBytes));
        var reader = provider.GetMetadataReader();
        var dllEntry = archive.GetEntry(Path.ChangeExtension(pdbEntry.FullName, ".dll"))
            ?? throw new InvalidDataException($"No assembly for {pdbEntry.FullName}");
        using var pe = new PEReader(new MemoryStream(Read(dllEntry)));
        var codeViewEntry = pe.ReadDebugDirectory().Single(entry => entry.Type == DebugDirectoryEntryType.CodeView);
        var codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);
        var pdbId = reader.DebugMetadataHeader!.Id;
        Require(codeView.Guid == new Guid(pdbId.AsSpan(0, 16)) && codeViewEntry.Stamp == BinaryPrimitives.ReadUInt32LittleEndian(pdbId.AsSpan(16, 4)), "DLL/PDB identity mismatch");
        byte[] checksumBytes = (byte[])pdbBytes.Clone();
        checksumBytes.AsSpan(reader.DebugMetadataHeader.IdStartOffset, 20).Clear();
        foreach (var checksumEntry in pe.ReadDebugDirectory().Where(entry => entry.Type == DebugDirectoryEntryType.PdbChecksum))
        {
            var checksum = pe.ReadPdbChecksumDebugDirectoryData(checksumEntry);
            Require(checksum.AlgorithmName == "SHA256" && SHA256.HashData(checksumBytes).AsSpan().SequenceEqual(checksum.Checksum.AsSpan()), "DLL/PDB checksum mismatch");
        }
        var sourceLinkRecords = reader.CustomDebugInformation.Select(reader.GetCustomDebugInformation)
            .Where(record => reader.GetGuid(record.Kind) == sourceLinkKind).ToArray();
        Require(sourceLinkRecords.Length == 1, "Exactly one SourceLink record is required");
        using var sourceLink = JsonDocument.Parse(reader.GetBlobBytes(sourceLinkRecords[0].Value));
        var mappings = sourceLink.RootElement.GetProperty("documents").EnumerateObject().ToArray();
        Require(mappings.Length > 0, "Empty SourceLink mapping");
        foreach (var handle in reader.Documents)
        {
            var document = reader.GetDocument(handle);
            string name = reader.GetString(document.Name);
            var embedded = reader.GetCustomDebugInformation(handle).Select(reader.GetCustomDebugInformation)
                .Where(record => reader.GetGuid(record.Kind) == embeddedSourceKind).ToArray();
            bool generated = name.Replace('\\', '/').Split('/').Contains("obj");
            byte[]? source = null;
            foreach (var mapping in mappings.Where(_ => !generated))
            {
                string pattern = mapping.Name;
                int star = pattern.IndexOf('*');
                string? suffix = star < 0 ? (name == pattern ? "" : null)
                    : name.StartsWith(pattern[..star], StringComparison.Ordinal) && name.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal)
                        ? name.Substring(star, name.Length - pattern.Length + 1) : null;
                if (suffix is null) continue;
                string url = mapping.Value.GetString()!.Replace("*", suffix, StringComparison.Ordinal);
                string prefix = $"https://raw.githubusercontent.com/gentt1024/Cordis.NET/{commit}/";
                Require(url.StartsWith(prefix, StringComparison.Ordinal), $"SourceLink commit/repository mismatch: {url}");
                string relative = Uri.UnescapeDataString(url[prefix.Length..]).Replace('\\', '/');
                Require(relative.Length > 0 && !relative.Split('/').Contains("..") && !relative.StartsWith('/'), "Invalid source path");
                source = GitBlob(commit, relative);
                sourceDocuments++;
                break;
            }
            if (source is null)
            {
                Require(generated && embedded.Length == 1, $"Document has no committed SourceLink source or embedded generated source: {name}");
                source = EmbeddedSource(reader.GetBlobBytes(embedded[0].Value));
                embeddedDocuments++;
            }
            var algorithm = reader.GetGuid(document.HashAlgorithm);
            byte[] hash = algorithm == sha256Kind ? SHA256.HashData(source) : algorithm == sha1Kind ? SHA1.HashData(source)
                : throw new InvalidDataException("Unsupported document checksum algorithm");
            Require(hash.AsSpan().SequenceEqual(reader.GetBlobBytes(document.Hash)), $"Source checksum mismatch: {name}; source={Convert.ToHexStringLower(hash)}, PDB={Convert.ToHexStringLower(reader.GetBlobBytes(document.Hash))}");
            if (frameReport is not null && dllEntry.Name == "Cordis.Core.dll" && name == frameReport.RootElement.GetProperty("file").GetString())
            {
                Require(embedded.Length == 1, "Consumer source requires embedded source for offline debugging");
                byte[] offlineSource = EmbeddedSource(reader.GetBlobBytes(embedded[0].Value));
                Require(offlineSource.AsSpan().SequenceEqual(source), "Consumer embedded source differs from bound source");
                int line = frameReport.RootElement.GetProperty("line").GetInt32();
                string[] lines = System.Text.Encoding.UTF8.GetString(offlineSource).Split('\n');
                Require(line > 0 && line <= lines.Length, "Consumer source line is outside document");
                debugSource = new { file = name, line, sourceLine = lines[line - 1].TrimEnd('\r'), commit, checksum = Convert.ToHexStringLower(hash) };
            }
        }
        assemblyCount++;
    }
    Require(assemblyCount > 0 && sourceDocuments > 0, "Package has no source-bound portable PDB");
    results.Add(new { package = Path.GetFileName(package), commit, assemblyCount, sourceDocuments, embeddedDocuments });
}
Require(results.Count > 0, "No packages found");
Require(frameReport is null || debugSource is not null, "Consumer source frame was not found in the package symbols");
Console.WriteLine(JsonSerializer.Serialize(new { packages = results, debugSource }));

byte[] GitBlob(string commit, string path)
{
    if (!Directory.Exists(Path.Combine(args[1], ".git")) && !File.Exists(Path.Combine(args[1], ".git")))
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(args[1], "SOURCE_SHA256.json")));
        Require(manifest.RootElement.GetProperty("commit").GetString() == commit, "Source archive commit differs from package");
        string fullPath = Path.GetFullPath(Path.Combine(args[1], path));
        Require(fullPath.StartsWith(Path.GetFullPath(args[1]) + Path.DirectorySeparatorChar, StringComparison.Ordinal), "Source path escapes archive");
        byte[] source = File.ReadAllBytes(fullPath);
        string expected = manifest.RootElement.GetProperty("files").GetProperty(path).GetString()!;
        Require(Convert.ToHexStringLower(SHA256.HashData(source)) == expected, $"Source archive hash mismatch: {path}");
        return source;
    }
    var start = new ProcessStartInfo("git") { WorkingDirectory = args[1], RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("show");
    start.ArgumentList.Add($"{commit}:{path}");
    using var process = Process.Start(start)!;
    using var output = new MemoryStream();
    process.StandardOutput.BaseStream.CopyTo(output);
    string error = process.StandardError.ReadToEnd();
    process.WaitForExit();
    Require(process.ExitCode == 0, $"Cannot read committed source {path}: {error}");
    return output.ToArray();
}

static byte[] Read(ZipArchiveEntry entry)
{
    using var stream = entry.Open();
    using var output = new MemoryStream();
    stream.CopyTo(output);
    return output.ToArray();
}

static byte[] EmbeddedSource(byte[] blob)
{
    int length = BinaryPrimitives.ReadInt32LittleEndian(blob);
    if (length == 0) return blob[4..];
    using var inflater = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
    using var output = new MemoryStream();
    inflater.CopyTo(output);
    byte[] source = output.ToArray();
    Require(source.Length == length, "Invalid embedded source length");
    return source;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidDataException(message);
}
