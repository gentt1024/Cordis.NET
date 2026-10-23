using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ClientModuleCatalogTests
{
    [Fact]
    public async Task Release_identity_is_host_selected_and_does_not_change_the_artifact_graph_revision()
    {
        var root = Directory.CreateTempSubdirectory("cordis-client-release-").FullName;
        try
        {
            await PackageAsync(root, "consumer", "[]", "[]");
            var packages = DeploymentPackageResolver.Native((name, _) => Path.Combine(root, name));
            var parent = new Uri(root + Path.DirectorySeparatorChar);
            var absent = await ClientModuleCatalog.CaptureAsync(packages, ["consumer"], parent);
            var identity = new ClientModuleRelease("1.2.3", "fixed-commit");
            var released = await ClientModuleCatalog.CaptureAsync(packages, ["consumer"], parent, release: identity);
            var other = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer"],
                parent,
                Array.Empty<string>(),
                release: new("1.2.4"));
            Assert.Same(identity, released.Graph.Release);
            Assert.Equal(absent.Graph.Revision, released.Graph.Revision);
            Assert.Equal(released.Graph.Revision, other.Graph.Revision);
            Assert.Equal(
                JsonSerializer.Serialize(absent.Graph.Entries),
                JsonSerializer.Serialize(released.Graph.Entries));
            Assert.Equal(absent.Graph.Batches.Select(row => row.Url), released.Graph.Batches.Select(row => row.Url));
            using var absentWire = JsonDocument.Parse(JsonSerializer.Serialize(absent.Graph));
            Assert.False(absentWire.RootElement.TryGetProperty("release", out _));
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(released.Graph));
            Assert.Equal("1.2.3", wire.RootElement.GetProperty("release").GetProperty("version").GetString());
            Assert.Equal("fixed-commit", wire.RootElement.GetProperty("release").GetProperty("commit").GetString());
            using var noCommit = JsonDocument.Parse(JsonSerializer.Serialize(other.Graph));
            Assert.False(noCommit.RootElement.GetProperty("release").TryGetProperty("commit", out _));
            // Keep the original positional construction and deconstruction contract.
            var (revision, entries, batches) = new ClientModuleGraph("manual", [], []);
            Assert.Equal("manual", revision);
            Assert.Empty(entries);
            Assert.Empty(batches);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Shell_requests_are_exact_and_do_not_require_dynamic_provider_rows()
    {
        var root = Directory.CreateTempSubdirectory("cordis-client-shell-").FullName;
        try
        {
            await PackageAsync(root, "consumer", "[]", "[\"@example/shell\",\"@example/shell/tools\"]");
            var packages = DeploymentPackageResolver.Native((name, _) => Path.Combine(root, name));
            var parent = new Uri(root + Path.DirectorySeparatorChar);
            await Assert.ThrowsAsync<FormatException>(() =>
                ClientModuleCatalog.CaptureAsync(packages, ["consumer"], parent));
            // Supplying the root does not imply supplying the subpath.
            await Assert.ThrowsAsync<FormatException>(() =>
                ClientModuleCatalog.CaptureAsync(packages, ["consumer"], parent, ["@example/shell"]));
            await PackageAsync(root, "independent", "[]", "[]");
            var missing = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer", "independent"],
                parent,
                ["@example/shell"],
                withdrawUnavailableDependencies: true);
            Assert.Equal(new[] { "independent" }, missing.Graph.Entries.Select(row => row.Id));
            var catalog = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer"],
                parent,
                ["@example/shell", "@example/shell/tools"]);
            var consumer = Assert.Single(catalog.Graph.Entries);
            Assert.Equal(new[] { "@example/shell", "@example/shell/tools" }, consumer.External);
            Assert.NotNull(catalog.FindArtifact(consumer.Id, consumer.Revision));
            var closed = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer"],
                parent,
                ["@example/shell", "@example/shell/tools"],
                withdrawUnavailableDependencies: true);
            Assert.Equal(catalog.Graph.Revision, closed.Graph.Revision);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Access_is_an_exact_default_platform_request()
    {
        var root = Directory.CreateTempSubdirectory("cordis-client-access-").FullName;
        try
        {
            await PackageAsync(root, "consumer", "[]", "[\"@cordis-net/client-modules/access\"]");
            await PackageAsync(root, "unknown-subpath", "[]", "[\"@cordis-net/client-modules/access/internal\"]");
            var packages = DeploymentPackageResolver.Native((name, _) => Path.Combine(root, name));
            var parent = new Uri(root + Path.DirectorySeparatorChar);
            var catalog = await ClientModuleCatalog.CaptureAsync(packages, ["consumer"], parent);
            var consumer = Assert.Single(catalog.Graph.Entries);
            Assert.Equal(new[] { "@cordis-net/client-modules/access" }, consumer.External);
            Assert.NotNull(catalog.FindArtifact(consumer.Id, consumer.Revision));
            var closed = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer"],
                parent,
                withdrawUnavailableDependencies: true);
            Assert.Equal(catalog.Graph.Revision, closed.Graph.Revision);
            await Assert.ThrowsAsync<FormatException>(() =>
                ClientModuleCatalog.CaptureAsync(packages, ["unknown-subpath"], parent));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

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
            var first = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer", "provider"],
                new Uri(root + Path.DirectorySeparatorChar));
            Assert.Equal(new[] { "provider", "consumer" }, first.Graph.Entries.Select(row => row.Id));
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(first.Graph));
            Assert.Equal(first.Graph.Revision, wire.RootElement.GetProperty("rev").GetString());
            Assert.Equal("provider", wire.RootElement.GetProperty("batches")[0].GetProperty("entries")[0].GetString());
            var provider = first.Graph.Entries[0];
            Assert.Contains("?rev=" + provider.Revision, provider.Url);
            Assert.NotNull(first.FindArtifact(provider.Id, provider.Revision));

            var second = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["provider"],
                new Uri(root + Path.DirectorySeparatorChar));
            Assert.NotEqual(first.Graph.Revision, second.Graph.Revision);
            Assert.Null(second.FindArtifact("consumer", first.Graph.Entries[1].Revision));
            Assert.NotNull(first.FindArtifact("consumer", first.Graph.Entries[1].Revision));
            await Assert.ThrowsAsync<FormatException>(() => ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer", "independent"],
                new Uri(root + Path.DirectorySeparatorChar)));
            var closed = await ClientModuleCatalog.CaptureAsync(
                packages,
                ["consumer", "independent"],
                new Uri(root + Path.DirectorySeparatorChar),
                withdrawUnavailableDependencies: true);
            Assert.Equal(new[] { "independent" }, closed.Graph.Entries.Select(row => row.Id));
            Assert.Null(closed.FindArtifact("consumer", first.Graph.Entries[1].Revision));
            var independent = Assert.Single(closed.Graph.Entries);
            Assert.NotNull(closed.FindArtifact(independent.Id, independent.Revision));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("[\"missing/client\"]", "[]", "unavailable")]
    [InlineData("[\"consumer/client\"]", "[\"provider/client\"]", "cycle")]
    [InlineData("[42]", "[]", "string array")]
    public async Task Invalid_graph_cannot_be_published(
        string providerExternal,
        string consumerExternal,
        string diagnostic)
    {
        var root = Directory.CreateTempSubdirectory("cordis-client-graph-refusal-").FullName;
        try
        {
            await PackageAsync(root, "provider", "[]", providerExternal);
            await PackageAsync(root, "consumer", "[]", consumerExternal);
            var packages = DeploymentPackageResolver.Native((name, _) => Path.Combine(root, name));
            var error = await Assert.ThrowsAsync<FormatException>(() => ClientModuleCatalog.CaptureAsync(
                packages,
                ["provider", "consumer"],
                new Uri(root + Path.DirectorySeparatorChar)));
            Assert.Contains(diagnostic, error.Message);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static async Task PackageAsync(string root, string name, string inject, string external)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(directory, "package.json"),
            "{\"name\":\"" + name + "\",\"dsh\":{\"client\":{\"platform\":\"web\",\"inject\":" + inject +
            ",\"external\":" + external + "}},\"exports\":{\"./client\":\"./client.js\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(directory, "client.js"),
            "globalThis.__ModuleLoader__.load({id:" + JsonSerializer.Serialize(name) +
            ",factory(){return {apply(){}}}});\n");
    }
}
