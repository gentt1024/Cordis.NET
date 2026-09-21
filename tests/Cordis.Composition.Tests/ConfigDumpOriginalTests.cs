using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

[Collection("Console output")]
public sealed class ConfigDumpOriginalTests
{
    [Fact]
    public async Task ComposeLayersPrintRawExpressionsAndLabelEverySourceRun()
    {
        using var files = new Files(); var expression = new JsExpression("process.env.DSH_DUMP_SPEC");
        var layers = new[] { new ConfigurationLayer("surface.yml", [new() { Id = "shared", Config = new EntryOptions { ["value"] = "surface", ["key"] = expression } }, new() { ["insert"] = new[] { new EntryOptions { Id = "surface-extra", Name = new Uri(files.Path + "/noop.dll").AbsoluteUri } } }]), new ConfigurationLayer("user.yml", [new() { Id = "surface-extra", Config = new EntryOptions { ["value"] = "user" } }]) };
        var dump = (await ProfileComposition.PreviewAsync(files.Base, layers)).Replace("\r\n", "\n", StringComparison.Ordinal); var rows = ConfigurationFile.ParseEntries(dump);
        Assert.Equal(new[] { "shared", "untouched", "surface-extra" }, rows.Select(row => row.Id)); Assert.Equal("surface", Assert.IsType<EntryOptions>(rows[0].Config)["value"]); Assert.Equal(expression, Assert.IsType<EntryOptions>(rows[0].Config)["key"]); Assert.Equal("user", Assert.IsType<EntryOptions>(rows[2].Config)["value"]); Assert.Equal(new Uri(files.Path + "/noop.dll").AbsoluteUri, rows[2].Name);
        Assert.Contains("!!js process.env.DSH_DUMP_SPEC", dump); Assert.Contains("# == base.yml, patched by surface.yml", dump); Assert.Contains("# == base.yml\n- id: untouched", dump.Replace("\r\n", "\n", StringComparison.Ordinal)); Assert.Contains("# == surface.yml, patched by user.yml\n- id: surface-extra", dump.Replace("\r\n", "\n", StringComparison.Ordinal)); Assert.True(dump.IndexOf("# == base.yml, patched by surface.yml", StringComparison.Ordinal) < dump.IndexOf("# == base.yml\n", StringComparison.Ordinal));
    }
    [Fact]
    public async Task ContiguousRowsShareOneSourceSeparator()
    {
        using var files = new Files(); var dump = await ProfileComposition.PreviewAsync(files.Base, []); Assert.Equal(1, dump.Split("# == base.yml", StringSplitOptions.None).Length - 1); Assert.Contains("# == base.yml\n- id: shared", dump.Replace("\r\n", "\n", StringComparison.Ordinal));
    }
    [Fact]
    public async Task FlattenedPatchesDoNotIndexReplacedChildren()
    {
        using var files = new Files(); await File.WriteAllTextAsync(files.Base, "- id: g\n  name: group\n  group: true\n  config: []\n"); var warnings = new List<string>();
        var layers = new[] { new ConfigurationLayer("a.yml", [new() { Id = "g", Config = new[] { new EntryOptions { Id = "child", Name = "p", Config = new EntryOptions { ["v"] = 1L } } } }]), new ConfigurationLayer("b.yml", [new() { Id = "child", Config = new EntryOptions { ["v"] = 2L } }]) };
        var dump = await ProfileComposition.PreviewAsync(files.Base, layers, warnings.Add, "test"); Assert.Equal("test: [b.yml] patch: entry \"child\" not found", Assert.Single(warnings)); Assert.Equal(1L, Assert.IsType<EntryOptions>(Data.Entries(ConfigurationFile.ParseEntries(dump)[0].Config)[0].Config)["v"]); Assert.Contains("# == base.yml, patched by a.yml", dump); Assert.DoesNotContain("patched by a.yml, b.yml", dump);
    }
    [Fact]
    public async Task MissingTargetsWarnAndDoNotStopLaterOverrides()
    {
        using var files = new Files(); var warnings = new List<string>(); var dump = await ProfileComposition.PreviewAsync(files.Base, [new("overlay.yml", [new() { Id = "absent" }, new() { Id = "shared", Config = "patched" }])], warnings.Add, "test"); Assert.Equal("test: [overlay.yml] patch: entry \"absent\" not found", Assert.Single(warnings)); Assert.Equal("patched", ConfigurationFile.ParseEntries(dump)[0].Config);
    }
    [Fact]
    public async Task DefaultWarningSinkWritesOneStandardErrorLine()
    {
        using var files = new Files(); var prior = Console.Error; using var output = new StringWriter(); Console.SetError(output);
        try { await ProfileComposition.PreviewAsync(files.Base, [new("x.yml", [new() { Id = "absent" }])], diagnosticName: "test"); Assert.Equal("test: [x.yml] patch: entry \"absent\" not found" + Environment.NewLine, output.ToString()); }
        finally { Console.SetError(prior); }
    }
    [Fact]
    public async Task MissingMalformedAndNonArrayBasesFailLoud()
    {
        using var files = new Files(); Assert.Contains("failed to read config", (await Assert.ThrowsAsync<IOException>(() => ProfileComposition.PreviewAsync(files.Path + "/absent.yml", []))).Message);
        await File.WriteAllTextAsync(files.Base, "invalid: [unclosed"); Assert.Contains("failed to parse config", (await Assert.ThrowsAsync<FormatException>(() => ProfileComposition.PreviewAsync(files.Base, []))).Message);
        await File.WriteAllTextAsync(files.Base, "id: not-a-list"); Assert.Contains("top-level array", (await Assert.ThrowsAsync<FormatException>(() => ProfileComposition.PreviewAsync(files.Base, []))).Message);
    }
    private sealed class Files : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cordis-dump-" + Guid.NewGuid().ToString("N")); public string Base => System.IO.Path.Combine(Path, "base.yml");
        public Files() { Directory.CreateDirectory(Path); File.WriteAllText(Base, "- id: shared\n  name: ./noop.dll\n  config:\n    value: base\n    key: !!js process.env.DSH_DUMP_SPEC\n- id: untouched\n  name: ./noop.dll\n"); }
        public void Dispose() => Directory.Delete(Path, true);
    }
}
[CollectionDefinition("Console output", DisableParallelization = true)] public sealed class ConsoleOutputCollection { }

