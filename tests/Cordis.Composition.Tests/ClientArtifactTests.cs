using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ClientArtifactTests
{
    [Fact]
    public async Task Captured_web_export_has_immutable_content_identity()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-client-artifact-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "package.json"),
                """
                {"name":"client","dsh":{"client":{"platform":"web"}},"exports":{"./client":{"default":"./client.mjs"}}}
                """);
            var entry = Path.Combine(directory, "client.mjs");
            await File.WriteAllTextAsync(entry, "export const value = 1;\n");
            var packages = DeploymentPackageResolver.Native((_, _) => directory);
            var first = await ClientArtifact.CaptureAsync(packages, "client", new Uri(entry));
            await File.WriteAllTextAsync(entry, "export const value = 2;\n");
            var second = await ClientArtifact.CaptureAsync(packages, "client", new Uri(entry));
            Assert.NotEqual(first.Revision, second.Revision);
            await using var stream = first.OpenRead();
            using var reader = new StreamReader(stream);
            Assert.Equal("export const value = 1;\n", await reader.ReadToEndAsync());
            Assert.False(stream.CanWrite);
            Assert.Equal("\"" + first.Revision + "\"", first.ETag);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("./../outside.mjs", false)]
    [InlineData("./link/outside.mjs", true)]
    public async Task Declared_artifact_cannot_escape_package_lexically_or_through_links(string target, bool link)
    {
        var directory = Directory.CreateTempSubdirectory("cordis-client-boundary-").FullName;
        try
        {
            var package = Directory.CreateDirectory(Path.Combine(directory, "package")).FullName;
            await File.WriteAllTextAsync(Path.Combine(directory, "outside.mjs"), "export const value = 'outside';\n");
            if (link)
                DeploymentResolutionTests.CreateDirectoryAlias(Path.Combine(package, "link"), directory);
            await File.WriteAllTextAsync(
                Path.Combine(package, "package.json"),
                ConfigurationFile.Write(
                    new EntryOptions
                    {
                        ["name"] = "client",
                        ["dsh"] = new EntryOptions
                        {
                            ["client"] = new EntryOptions
                            {
                                ["platform"] = "web"
                            }
                        },
                        ["exports"] = new EntryOptions
                        {
                            ["./client"] = target
                        },
                    },
                    true));
            var packages = DeploymentPackageResolver.Native((_, _) => package);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                ClientArtifact.CaptureAsync(packages, "client", new Uri(Path.Combine(package, "host.yml"))));
        }
        finally
        {
            var alias = Path.Combine(directory, "package", "link");
            if (Directory.Exists(alias))
                Directory.Delete(alias);
            Directory.Delete(directory, true);
        }
    }
}
