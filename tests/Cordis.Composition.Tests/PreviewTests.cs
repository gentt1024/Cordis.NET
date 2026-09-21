using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class PreviewTests
{
    [Fact]
    public async Task DumpComposesFlattenedLayersAndLabelsContiguousOrigins()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "- id: a\n  name: p\n- id: b\n  name: p\n");
            var layers = new[] { new ConfigurationLayer("bundle", [new() { ["insert"] = new[] { new EntryOptions { Id = "added", Name = "p" } } }]), new ConfigurationLayer("user", [new() { Id = "added", Config = new JsExpression("answer") }]) };
            var dump = await ProfileComposition.PreviewAsync(file, layers);
            Assert.Contains("# == " + Path.GetFileName(file), dump); Assert.Contains("# == bundle, patched by user", dump); Assert.Contains("!!js", dump);
            Assert.Equal(2, dump.Split("# == ").Length - 1); Assert.Equal(3, ConfigurationFile.ParseEntries(dump).Count);
        }
        finally { File.Delete(file); }
    }
    [Fact]
    public async Task DumpDoesNotReindexConfigReplacementAndLabelsWarnings()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "- id: group\n  name: cordis:group\n  group: true\n  config: []\n");
            var warnings = new List<string>(); var layers = new[] { new ConfigurationLayer("first", [new() { Id = "group", Config = new[] { new EntryOptions { Id = "new-child", Name = "p" } } }]), new ConfigurationLayer("second", [new() { Id = "new-child", Disabled = true }]) };
            var dump = await ProfileComposition.PreviewAsync(file, layers, warnings.Add); Assert.Contains("[second]", Assert.Single(warnings)); Assert.Null(Data.Entries(ConfigurationFile.ParseEntries(dump)[0].Config)[0].Disabled);
        }
        finally { File.Delete(file); }
    }
}
