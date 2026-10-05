using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ClientModuleCatalogTests
{
    [Fact]
    public async Task Graph_orders_external_modules_and_metadata_changes_select_a_new_roster()
    {
        var root = Directory.CreateTempSubdirectory("cordis-client-graph-").FullName;
        try
        {
            await PackageAsync(root, "provider", "[]", "[]");
            await PackageAsync(root, "consumer", "[\"provider\"]", "[\"provider/client\",\"@deepseek-ai/cordis\"]");
            await PackageAsync(root, "independent", "[]", "[]");
            var packages = DeploymentPackageResolver.Native((name, _) => Path.Combine(root, name));
            var first = await ClientModuleCatalog.CaptureAsync(packages, ["consumer", "provider"], new Uri(root + Path.DirectorySeparatorChar));
            Assert.Equal(new[] { "provider", "consumer" }, first.Graph.Entries.Select(row => row.Id));
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(first.Graph));
            Assert.Equal(first.Graph.Revision, wire.RootElement.GetProperty("rev").GetString());
            Assert.Equal("provider", wire.RootElement.GetProperty("batches")[0].GetProperty("entries")[0].GetString());
            var provider = first.Graph.Entries[0];
            Assert.Contains("?rev=" + provider.Revision, provider.Url);
            Assert.NotNull(first.FindArtifact(provider.Id, provider.Revision));

            var second = await ClientModuleCatalog.CaptureAsync(packages, ["provider"], new Uri(root + Path.DirectorySeparatorChar));
            Assert.NotEqual(first.Graph.Revision, second.Graph.Revision);
            Assert.Null(second.FindArtifact("consumer", first.Graph.Entries[1].Revision));
            Assert.NotNull(first.FindArtifact("consumer", first.Graph.Entries[1].Revision));
            await Assert.ThrowsAsync<FormatException>(() => ClientModuleCatalog.CaptureAsync(packages,
                ["consumer", "independent"], new Uri(root + Path.DirectorySeparatorChar)));
            var closed = await ClientModuleCatalog.CaptureAsync(packages, ["consumer", "independent"],
                new Uri(root + Path.DirectorySeparatorChar), withdrawUnavailableDependencies: true);
            Assert.Equal(new[] { "independent" }, closed.Graph.Entries.Select(row => row.Id));
            Assert.Null(closed.FindArtifact("consumer", first.Graph.Entries[1].Revision));
            var independent = Assert.Single(closed.Graph.Entries);
            Assert.NotNull(closed.FindArtifact(independent.Id, independent.Revision));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("[\"missing/client\"]", "[]", "unavailable")]
    [InlineData("[\"consumer/client\"]", "[\"provider/client\"]", "cycle")]
    [InlineData("[42]", "[]", "string array")]
    public async Task Invalid_graph_cannot_be_published(string providerExternal, string consumerExternal, string diagnostic)
    {
        var root = Directory.CreateTempSubdirectory("cordis-client-graph-refusal-").FullName;
        try
        {
            await PackageAsync(root, "provider", "[]", providerExternal);
            await PackageAsync(root, "consumer", "[]", consumerExternal);
            var packages = DeploymentPackageResolver.Native((name, _) => Path.Combine(root, name));
            var error = await Assert.ThrowsAsync<FormatException>(() => ClientModuleCatalog.CaptureAsync(packages,
                ["provider", "consumer"], new Uri(root + Path.DirectorySeparatorChar)));
            Assert.Contains(diagnostic, error.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task PackageAsync(string root, string name, string inject, string external)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "package.json"),
            "{\"name\":\"" + name + "\",\"dsh\":{\"client\":{\"platform\":\"web\",\"inject\":" + inject
            + ",\"external\":" + external + "}},\"exports\":{\"./client\":\"./client.js\"}}");
        await File.WriteAllTextAsync(Path.Combine(directory, "client.js"), "globalThis.__ModuleLoader__.load({id:" + JsonSerializer.Serialize(name)
            + ",factory(){return {apply(){}}}});\n");
    }
}
