using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ConfigurationDocumentTests
{
    [Fact]
    public async Task A_claimed_declaration_never_falls_back_to_another_module_resolver()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-schema-ownership-");
        try
        {
            var root = Path.Combine(directory.FullName, "entries.yml");
            await File.WriteAllTextAsync(root, "- name: absent\n- name: broken\n- name: native\n");
            var resolver = new MixedResolver();

            var result = await ConfigurationSchemaDiscovery.DiscoverDocumentAsync("default", root, resolver);

            using var document = JsonDocument.Parse(result.Document);
            var entries = document.RootElement.GetProperty("x-cordis").GetProperty("entries").EnumerateArray();
            Assert.Equal(new[] { "absent", "error", "schema" }, entries.Select(row => row.GetProperty("status").GetString()));
            Assert.False(result.Complete);
            Assert.Equal(new[] { "native" }, resolver.NativeImports);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task Prepared_document_keeps_the_selection_and_diagnostics_after_a_profile_edit()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-schema-refresh-");
        try
        {
            var profile = directory.CreateSubdirectory("profile").FullName;
            var home = directory.CreateSubdirectory("home").FullName;
            Profiles.Initialize(profile, ["missing-bundle"]);
            var installed = new Dictionary<string, string>();
            var launch = new ProfileLaunch(await Profiles.LoadAsync(profile, installed), home, [], installed);
            var patch = launch.Profile.UserLayer.Source;
            await File.WriteAllTextAsync(patch, "- insert: [{ id: selected, name: first }]");
            var root = Path.Combine(profile, "entries.yml");
            await File.WriteAllTextAsync(root, "[]");
            var refresh = await ProfileComposition.RefreshAsync(launch);
            await File.WriteAllTextAsync(patch, "- insert: [{ id: later, name: second }]");
            var resolver = new StaticModuleResolver().Register("first", new Plugin<object?>());
            var result = await ConfigurationSchemaDiscovery.DiscoverDocumentAsync(refresh, launch.Profile.Name, root, resolver);
            using var document = JsonDocument.Parse(result.Document);
            var metadata = document.RootElement.GetProperty("x-cordis");
            Assert.Equal("selected", Assert.Single(metadata.GetProperty("entries").EnumerateArray()).GetProperty("id").GetString());
            Assert.False(result.Complete);
            Assert.Contains(result.Diagnostics, message => message.Contains("missing-bundle", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private sealed class MixedResolver : IModuleResolver, IConfigurationSchemaProjectionResolver
    {
        public List<string> NativeImports { get; } = [];

        public ValueTask<ResolvedConfigurationSchema?> TryResolveSchemaAsync(
            string specifier, Uri baseUri, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ResolvedConfigurationSchema?>(specifier switch
            {
                "absent" => new("foreign/absent", null, null),
                "broken" => throw new InvalidDataException("Foreign declaration could not be read."),
                _ => null
            });

        public ValueTask<ResolvedModule> ResolveAsync(
            string specifier, Uri baseUri, CancellationToken cancellationToken = default)
        {
            NativeImports.Add(specifier);
            return ValueTask.FromResult(new ResolvedModule(new Plugin<object?>
            {
                Configuration = new(raw => ConfigResult<object?>.Success(raw), ConfigDescriptor.Number())
            }));
        }
    }

    [Theory]
    [InlineData("missing", true)]
    [InlineData("bucket", false)]
    public async Task Include_patch_validates_inserted_children_only_when_the_destination_accepts_them(
        string firstDestination,
        bool complete)
    {
        var directory = Directory.CreateTempSubdirectory("cordis-schema-patches-");
        try
        {
            var root = Path.Combine(directory.FullName, "root.yml");
            await File.WriteAllTextAsync(
                root,
                $$"""
                  - name: cordis:include
                    config:
                      path: missing.yml
                      initial:
                        - { id: leaf, name: worker }
                        - { id: bucket, name: 'cordis:group', group: true, config: [] }
                      patches:
                        - { id: {{firstDestination}}, insert: [null] }
                        - { id: leaf, insert: [null] }
                        - { id: bucket, insert: [{ id: added, name: worker }] }
                  - { id: sibling, name: worker }
                  """);

            var export = await ConfigurationSchemaDiscovery.DiscoverDocumentAsync(
                "default",
                root,
                new StaticModuleResolver().Register("worker", new Plugin<object?>()));

            Assert.Equal(complete, export.Complete);
            using var document = JsonDocument.Parse(export.Document);
            var metadata = document.RootElement.GetProperty("x-cordis");
            var ids = metadata
                .GetProperty("entries")
                .EnumerateArray()
                .Where(entry => entry.TryGetProperty("id", out _))
                .Select(entry => entry.GetProperty("id").GetString())
                .ToArray();
            var levels = metadata
                .GetProperty("diagnostics")
                .EnumerateArray()
                .Select(item => item.GetProperty("level").GetString())
                .ToArray();
            if (complete)
            {
                Assert.Equal(new[] { "leaf", "bucket", "added", "sibling" }, ids);
                Assert.Equal(new[] { "warning", "warning" }, levels);
            }
            else
            {
                Assert.Equal(new[] { "sibling" }, ids);
                Assert.Contains("error", levels);
            }
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task A_failed_include_keeps_the_overlaid_sibling_and_its_schema_in_the_document()
    {
        var directory = Directory.CreateTempSubdirectory("cordis-config-document-");
        try
        {
            var root = Path.Combine(directory.FullName, "base.yml");
            await File.WriteAllTextAsync(root, "- name: cordis:include\n  config: { path: missing.yml }\n");
            var resolver = new StaticModuleResolver().Register(
                "worker",
                new Plugin<object?>
                {
                    Configuration = new(
                        _ => throw new InvalidOperationException("Discovery invoked the plugin validator."),
                        ConfigDescriptor.String())
                });
            var overlay = new ConfigurationLayer(
                "cli",
                ConfigurationFile.ParseEntries("- insert: [{ id: added, name: worker, config: ok }]"));

            var export = await ConfigurationSchemaDiscovery.DiscoverDocumentAsync("default", root, resolver, [overlay]);

            Assert.False(export.Complete);
            using var schema = JsonDocument.Parse(export.Document);
            var metadata = schema.RootElement.GetProperty("x-cordis");
            Assert.False(metadata.GetProperty("complete").GetBoolean());
            Assert.Contains(
                metadata.GetProperty("diagnostics").EnumerateArray(),
                row => row.GetProperty("level").GetString() == "error" && row.GetProperty("path").GetString() == "/0");
            var added = Assert.Single(
                metadata.GetProperty("entries").EnumerateArray(),
                row => row.TryGetProperty("id", out var id) && id.GetString() == "added");
            Assert.Equal("/1", added.GetProperty("path").GetString());
            Assert.Equal("schema", added.GetProperty("status").GetString());
            Assert.False(File.Exists(Path.Combine(directory.FullName, "missing.yml")));

            File.Delete(root);
            await Assert.ThrowsAnyAsync<IOException>(() =>
                ConfigurationSchemaDiscovery.DiscoverDocumentAsync("default", root, resolver, [overlay]));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
