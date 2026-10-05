using System.Text.Json;
using Cordis;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ConfigurationSchemaExportTests
{
    [Theory]
    [InlineData("tuple")]
    [InlineData("union")]
    [InlineData("intersect")]
    public void Empty_combinations_keep_executable_envelope_lists_and_valid_applicator_keywords(string kind)
    {
        var descriptor = kind switch
        {
            "tuple" => ConfigDescriptor.Tuple(), "union" => ConfigDescriptor.Union(), _ => ConfigDescriptor.Intersect(),
        };
        using var envelope = JsonDocument.Parse(ConfigurationSchemaExporter.ToSchemastery(descriptor).Document);
        Assert.Empty(envelope.RootElement.GetProperty("refs").GetProperty("0").GetProperty("list").EnumerateArray());
        using var document = JsonDocument.Parse(ConfigurationSchemaExporter.ToJsonSchema(descriptor).Document);
        var schema = document.RootElement.GetProperty("$defs").GetProperty("node0");
        Assert.False(schema.TryGetProperty("prefixItems", out _));
        Assert.False(schema.TryGetProperty("anyOf", out _));
        Assert.False(schema.TryGetProperty("allOf", out _));
        Assert.Equal(kind == "union", schema.TryGetProperty("not", out _));
        Assert.False(schema.TryGetProperty("items", out _));
    }

    [Fact]
    public void Tuple_projection_leaves_tail_items_open_like_the_fixed_non_strict_consumer()
    {
        using var document = JsonDocument.Parse(ConfigurationSchemaExporter.ToJsonSchema(ConfigDescriptor.Tuple(ConfigDescriptor.Number())).Document);
        var schema = document.RootElement.GetProperty("$defs").GetProperty("node0");
        Assert.Single(schema.GetProperty("prefixItems").EnumerateArray());
        Assert.Equal(1, schema.GetProperty("minItems").GetInt32());
        Assert.False(schema.TryGetProperty("items", out _));
    }

    [Fact]
    public void Separate_exports_preserve_shared_declarations_and_state_runtime_validation_limits()
    {
        var shared = ConfigDescriptor.String().Default("fallback");
        var descriptor = ConfigDescriptor.Object(("first", shared), ("second", shared),
            ("items", ConfigDescriptor.Array(ConfigDescriptor.Number())));
        var schemastery = ConfigurationSchemaExporter.ToSchemastery(descriptor);
        using var envelope = JsonDocument.Parse(schemastery.Document);
        var refs = envelope.RootElement.GetProperty("refs");
        var root = refs.GetProperty(envelope.RootElement.GetProperty("uid").GetInt32().ToString());
        var fields = root.GetProperty("dict");
        Assert.Equal(fields.GetProperty("first").GetInt32(), fields.GetProperty("second").GetInt32());
        var sharedNode = refs.GetProperty(fields.GetProperty("first").GetInt32().ToString());
        Assert.Equal("string", sharedNode.GetProperty("type").GetString());
        Assert.Equal("fallback", sharedNode.GetProperty("meta").GetProperty("default").GetString());

        var jsonSchema = ConfigurationSchemaExporter.ToJsonSchema(descriptor);
        using var document = JsonDocument.Parse(jsonSchema.Document);
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        var objectSchema = document.RootElement.GetProperty("$defs").GetProperty("node0");
        Assert.Equal("object", objectSchema.GetProperty("type").GetString());
        Assert.Equal(objectSchema.GetProperty("properties").GetProperty("first").GetProperty("$ref").GetString(),
            objectSchema.GetProperty("properties").GetProperty("second").GetProperty("$ref").GetString());
        Assert.Equal(new[] { "items" }, objectSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.False(schemastery.Complete);
        Assert.False(jsonSchema.Complete);
        Assert.Contains(jsonSchema.Diagnostics, diagnostic => diagnostic.Contains("captured validator", StringComparison.Ordinal));
    }

    [Fact]
    public void Unresolved_lazy_and_non_finite_defaults_produce_diagnostics_without_running_code()
    {
        var builds = 0;
        var descriptor = ConfigDescriptor.Object(("lazy", ConfigDescriptor.Lazy(() =>
        {
            builds++;
            throw new InvalidOperationException("Export must not execute plugin builders.");
        })), ("nonFinite", ConfigDescriptor.Number().Default(double.NaN)));
        foreach (var exported in new[] { ConfigurationSchemaExporter.ToSchemastery(descriptor), ConfigurationSchemaExporter.ToJsonSchema(descriptor) })
        {
            using var document = JsonDocument.Parse(exported.Document);
            Assert.Contains(exported.Diagnostics, diagnostic => diagnostic.Contains("lazy", StringComparison.Ordinal));
            Assert.Contains(exported.Diagnostics, diagnostic => diagnostic.Contains("default", StringComparison.Ordinal) && diagnostic.Contains("omitted", StringComparison.Ordinal));
            Assert.False(exported.Complete);
        }
        Assert.Equal(0, builds);
    }

    [Fact]
    public async Task Captured_recursive_description_exports_finite_reference_graphs()
    {
        var calls = 0;
        ConfigDescriptor? tree = null;
        tree = ConfigDescriptor.Object(("value", ConfigDescriptor.String()),
            ("next", ConfigDescriptor.Lazy(() => { calls++; return tree!; }).Optional()));
        await using var context = new Context();
        await context.RunAsync(async owner =>
        {
            var fiber = owner.Plugin(new Plugin<object?>
            {
                Configuration = new(raw => ConfigResult<object?>.Success(raw), tree), Apply = (_, _) => { },
            }, new Dictionary<string, object?> { ["value"] = "first", ["next"] = new Dictionary<string, object?> { ["value"] = "last" } });
            await fiber.WaitAsync();
            var before = calls;
            var schemastery = ConfigurationSchemaExporter.ToSchemastery(fiber.ConfigDescription!);
            var jsonSchema = ConfigurationSchemaExporter.ToJsonSchema(fiber.ConfigDescription!);
            using var envelope = JsonDocument.Parse(schemastery.Document);
            Assert.Equal(3, envelope.RootElement.GetProperty("refs").EnumerateObject().Count());
            using var schema = JsonDocument.Parse(jsonSchema.Document);
            Assert.Equal("#/$defs/node0", schema.RootElement.GetProperty("$defs").GetProperty("node2").GetProperty("$ref").GetString());
            Assert.Equal(before, calls);
        });
    }
}
