using System.Text.Json;
using Cordis;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ConfigurationDeclarationTests
{
    [Fact]
    public async Task Discovery_keeps_expression_backed_include_parent_opaque_even_with_literal_sibling_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cordis-schema-expression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "root.yml");
            await File.WriteAllTextAsync(path, """
                - id: inert
                  name: cordis:include
                  config: { __jsExpr: 'throw new Error("must stay inert")', path: child.yml }
                - id: sibling
                  name: worker
                """);
            await File.WriteAllTextAsync(Path.Combine(directory, "child.yml"), "- id: forbidden-child\n  name: worker\n");
            var calls = 0;
            var plugin = new Plugin<object?> { Configuration = new(_ => { calls++; throw new InvalidOperationException("must stay inert"); }, ConfigDescriptor.Any()) };
            var catalog = await ConfigurationSchemaDiscovery.DiscoverAsync(path, new StaticModuleResolver().Register("worker", plugin));
            Assert.Equal(new[] { "inert", "sibling" }, catalog.Entries.Select(entry => entry.EntryId));
            Assert.Contains("Expression", catalog.Entries[0].Diagnostic);
            Assert.Null(catalog.Entries[0].Schemastery);
            Assert.NotNull(catalog.Entries[1].Schemastery);
            Assert.Equal(0, calls);
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void Redacted_exports_do_not_allow_annotations_to_override_core_modes_or_restore_secret_defaults()
    {
        var field = ConfigDescriptor.String().WithAnnotations(new Dictionary<string, object?>
        {
            ["role"] = "secret", ["default"] = "injected-secret", ["required"] = true, ["volatile"] = true,
        });
        var descriptor = ConfigDescriptor.Object(("password", field));
        var export = ConfigurationSchemaExporter.ToSchemastery(descriptor, plainValues: true, omitDefaults: true);
        Assert.DoesNotContain("injected-secret", export.Document);
        using var envelope = JsonDocument.Parse(export.Document);
        var secret = envelope.RootElement.GetProperty("refs").GetProperty("1").GetProperty("meta");
        Assert.False(secret.TryGetProperty("required", out _));
        Assert.False(secret.TryGetProperty("volatile", out _));
        using var json = JsonDocument.Parse(ConfigurationSchemaExporter.ToJsonSchema(descriptor, omitDefaults: true).Document);
        Assert.False(json.RootElement.GetProperty("$defs").GetProperty("node0").TryGetProperty("required", out _));
        Assert.True(json.RootElement.GetProperty("$defs").GetProperty("node1").GetProperty("writeOnly").GetBoolean());
    }
    [Fact]
    public void Metadata_is_detached_captured_serialized_and_exported_without_installing_a_validator()
    {
        var labels = new Dictionary<string, string> { ["en"] = "Limit" };
        var extra = new Dictionary<string, object?> { ["choices"] = new List<object?> { "small" } };
        var declaration = ConfigDescriptor.Number().WithMetadata(new()
        {
            Descriptions = labels, Extra = extra, Min = 2, Max = 10, Step = 2, Role = "slider", Hidden = false,
            Disabled = true, Collapse = true, Badges = [new("preview", "warning")], Link = "https://example.test/help",
            Comment = "explicit validator contract",
        }).Optional().Default(4).Volatile();
        labels["en"] = "changed";
        ((List<object?>)extra["choices"]!).Add("changed");
        var validations = 0;
        var schema = new ConfigSchema<object?>(raw => { validations++; return ConfigResult<object?>.Success(raw); }, declaration).WithVolatileValue();
        var captured = schema.CaptureConfiguration().Descriptor;
        var restored = ConfigDescriptor.Deserialize(captured.Serialize());
        using var envelope = JsonDocument.Parse(ConfigurationSchemaExporter.ToSchemastery(restored).Document);
        var meta = envelope.RootElement.GetProperty("refs").GetProperty("0").GetProperty("meta");
        Assert.Equal("Limit", meta.GetProperty("description").GetProperty("en").GetString());
        Assert.Single(meta.GetProperty("extra").GetProperty("choices").EnumerateArray());
        Assert.Equal(2, meta.GetProperty("min").GetDouble());
        Assert.Equal("slider", meta.GetProperty("role").GetString());
        Assert.True(meta.GetProperty("disabled").GetBoolean());
        using var json = JsonDocument.Parse(ConfigurationSchemaExporter.ToJsonSchema(restored).Document);
        var number = json.RootElement.GetProperty("$defs").GetProperty("node0");
        Assert.Equal(2, number.GetProperty("minimum").GetDouble());
        Assert.Equal(10, number.GetProperty("maximum").GetDouble());
        Assert.False(number.TryGetProperty("multipleOf", out _));
        Assert.Equal(0, validations);
        Assert.Equal(-100, ((IPlugin)new Plugin<object?> { Configuration = schema }).ResolveConfig(-100));
        Assert.Throws<ArgumentException>(() => ConfigDescriptor.Any().WithAnnotations(new Dictionary<string, object?> { ["opaque"] = new object() }));
    }

    [Fact]
    public async Task Discovery_reads_native_carriers_and_disabled_declarations_without_booting_or_creating_sources()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cordis-schema-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var calls = 0;
            var lazyCalls = 0;
            var plugin = new Plugin<object?>
            {
                Configuration = new(raw => { calls++; throw new InvalidOperationException("validation must remain inert"); },
                    ConfigDescriptor.Object(("lazy", ConfigDescriptor.Lazy(() => { lazyCalls++; throw new InvalidOperationException("builder must remain inert"); })))),
                Apply = (_, _) => { calls++; throw new InvalidOperationException("activation must remain inert"); },
            };
            var path = Path.Combine(directory, "root.yml");
            await File.WriteAllTextAsync(path, """
                - id: section
                  name: cordis:group
                  config:
                    - id: disabled
                      name: worker
                      disabled: true
                      config: !!js 'throw new Error("inert")'
                    - id: broken
                      name: absent
                - id: fallback
                  name: cordis:include
                  config:
                    path: missing.yml
                    initial:
                      - id: nested
                        name: worker
                - id: recursive
                  name: cordis:include
                  config: { path: root.yml }
                - id: good
                  name: worker
                """);
            var result = await ConfigurationSchemaDiscovery.DiscoverAsync(path, new StaticModuleResolver().Register("worker", plugin));
            Assert.Equal(new[] { "section:disabled", "section:broken", "fallback:nested", "good" }, result.Entries.Select(entry => entry.EntryId));
            Assert.True(result.Entries[0].Disabled);
            Assert.NotNull(result.Entries[0].Schemastery);
            Assert.Contains("Cannot resolve", result.Entries[1].Diagnostic);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("include cycle", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(directory, "missing.yml")));
            Assert.Equal(0, calls);
            Assert.Equal(0, lazyCalls);
        }
        finally { Directory.Delete(directory, true); }
    }
}
