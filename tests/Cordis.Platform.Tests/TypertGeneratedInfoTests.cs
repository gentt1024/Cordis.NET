using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis.Composition;
using Xunit;

namespace Cordis.Platform.Tests;

public sealed class TypertGeneratedInfoTests
{
    [Fact]
    public void Generated_host_metadata_reuses_descriptors_and_preserves_authored_parsing()
    {
        var info = GeneratedInfoServiceTypert.Info("metadata-tests");
        var descriptor = Assert.Single(info.Descriptors);
        Assert.Same(descriptor, info.Methods["generatedInfo/normalize"]);
        Assert.Equal("Normalizes the submitted label.", descriptor.Description);
        Assert.Equal("The normalized label.", descriptor.ResultDescription);
        Assert.Equal("The authored input.", descriptor.Parameters[0].Description);
        Assert.Equal("Optional suffix.", descriptor.Parameters[1].Description);
        Assert.Same(descriptor.Input(), descriptor.Input());
        Assert.Same(descriptor.Input(true), descriptor.Input(true));
        Assert.NotSame(descriptor.Input(), descriptor.Input(true));
        using var input = JsonDocument.Parse("{\"input\":{\"label\":\"  value  \"}}");
        var parsed = descriptor.Input().Parse(input.RootElement);
        Assert.Equal("VALUE", Assert.IsType<GeneratedInfoInput>(parsed["input"]).Label);
        Assert.False(parsed.ContainsKey("suffix"));
        using var strict = JsonDocument.Parse("{\"input\":{\"label\":\"value\"},\"suffix\":\"ok\"}");
        Assert.Equal(
            "VALUE",
            Assert.IsType<GeneratedInfoInput>(descriptor.Input(true).Parse(strict.RootElement)["input"]).Label);
        using var result = JsonDocument.Parse("\"answer\"");
        Assert.Equal("answer", descriptor.ResultValue.Parse(result.RootElement));
    }
}

public sealed class GeneratedInfoInput
{
    private string label = "";

    public required string Label
    {
        get => label;
        set => label = value.Trim().ToUpperInvariant();
    }
}

[RemoteService("generatedInfo", typeof(GeneratedInfoJson))]
public partial class GeneratedInfoService
{
    /// <summary>Normalizes the submitted label.</summary>
    /// <param name="input">The authored input.</param>
    /// <param name="suffix">Optional suffix.</param>
    /// <returns>The normalized label.</returns>
    [RemoteMethod("normalize")]
    public Task<string> NormalizeAsync(GeneratedInfoInput input, string suffix = "!") =>
        Task.FromResult(input.Label + suffix);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GeneratedInfoInput))]
[JsonSerializable(typeof(string))]
public partial class GeneratedInfoJson : JsonSerializerContext;
