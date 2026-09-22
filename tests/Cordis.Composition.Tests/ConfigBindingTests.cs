using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ConfigBindingTests
{
    private static Plugin<T> Plugin<T>(Func<object?, ConfigResult<T>> bind) => new() { Config = bind, Apply = (_, _) => { } };
    private static T Resolve<T>(Func<object?, ConfigResult<T>> bind, object? raw) => (T)((IPlugin)Plugin(bind)).ResolveConfig(raw)!;
    private static ConfigurationValidationException Reject<T>(Func<object?, ConfigResult<T>> bind, object? raw) =>
        Assert.Throws<ConfigurationValidationException>(() => Resolve(bind, raw));
    private static readonly Func<object?, ConfigResult<BindingOptions>> Bind =
        ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.BindingOptions);

    [Fact]
    public void ParsedYamlAndJsonBindInitOnlyAndExplicitValues()
    {
        foreach (var raw in new[]
        {
            ConfigurationFile.Parse("count: 0\nenabled: false\nlabel: ''\noptional: null\nmode: Slow\nchildren: [{name: one, count: 2}]\n"),
            ConfigurationFile.Parse("""{"count":0,"enabled":false,"label":"","optional":null,"mode":"Slow","children":[{"name":"one","count":2}]}""", json: true)
        })
        {
            var actual = Resolve(Bind, raw);
            Assert.Equal(0, actual.Count);
            Assert.False(actual.Enabled);
            Assert.Equal("", actual.Label);
            Assert.Null(actual.Optional);
            Assert.Equal(BindingMode.Slow, actual.Mode);
            var child = Assert.Single(actual.Children);
            Assert.Equal("one", child.Name);
            Assert.Equal(2, child.Count);
            var map = Assert.IsType<EntryOptions>(raw);
            Assert.Equal(0L, map["count"]);
            Assert.Null(map["optional"]);
        }
    }

    [Fact]
    public void MissingInitOnlyMembersFollowActualGeneratedMetadataRatherThanInventingDefaults()
    {
        var direct = JsonSerializer.Deserialize("{}", BindingJsonContext.Default.BindingOptions)!;
        var bound = Resolve(Bind, new EntryOptions());
        Assert.Equal(direct.Count, bound.Count);
        Assert.Equal(direct.Enabled, bound.Enabled);
        Assert.Equal(direct.Label, bound.Label);
        Assert.Equal(direct.Optional, bound.Optional);
        Assert.Equal(direct.Children, bound.Children);
        // .NET 10's generated object initializer writes missing init-only values too.
        Assert.Equal(0, bound.Count);
        Assert.False(bound.Enabled);
        Assert.Null(bound.Label);
        Assert.Null(bound.Optional);
        Assert.Null(bound.Children);
        Assert.Equal(9, new BindingOptions().Count);

        var setter = Resolve(ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.SetterBindingOptions), new EntryOptions());
        Assert.Equal(9, setter.Count);
    }

    [Fact]
    public void RootMissingNullAndEmptyObjectAreDistinctAndPolicyCanBeWrapped()
    {
        Assert.Contains("missing", Assert.Single(Reject(Bind, Undefined.Value).Issues));
        Assert.Contains("explicitly null", Assert.Single(Reject(Bind, null).Issues));
        Assert.Contains("missing", Assert.Single(Reject(Bind, default(JsonElement)).Issues));
        using var nullDocument = JsonDocument.Parse("null");
        Assert.Contains("explicitly null", Assert.Single(Reject(Bind, nullDocument.RootElement).Issues));
        Assert.Equal(0, Resolve(Bind, new EntryOptions()).Count);
        Func<object?, ConfigResult<BindingOptions>> withMissingDefault = raw => raw is Undefined
            ? ConfigResult<BindingOptions>.Success(new BindingOptions { Count = 5 }) : Bind(raw);
        Assert.Equal(5, Resolve(withMissingDefault, Undefined.Value).Count);
        Reject(withMissingDefault, null);
    }

    [Fact]
    public void ScalarRootsAreDataNotMissingAndStringsAreNotParsedAsJson()
    {
        Assert.Equal(0, Resolve(ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.Int32), 0L));
        Assert.False(Resolve(ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.Boolean), false));
        Assert.Equal("", Resolve(ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.String), ""));
        Reject(Bind, "{}");
    }

    [Fact]
    public void ConstructorDefaultsRequiredPropertiesAndUnknownMembersFollowMetadata()
    {
        var constructor = ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.BindingChild);
        Assert.Equal(4, Resolve(constructor, new EntryOptions { ["name"] = "child" }).Count);
        Reject(constructor, new EntryOptions());
        var strict = ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.StrictBindingOptions);
        Reject(strict, new EntryOptions());
        Assert.Equal("present", Resolve(strict, new EntryOptions { ["name"] = "present" }).Name);
        Assert.Contains("extra", Assert.Single(Reject(strict, new EntryOptions { ["name"] = "present", ["extra"] = 1 }).Issues));
        Assert.Equal(0, Resolve(Bind, new EntryOptions { ["extra"] = 1 }).Count);
        Assert.Contains("$.mode", Assert.Single(Reject(Bind, new EntryOptions { ["mode"] = "Unknown" }).Issues));
        Assert.Contains("$.count", Assert.Single(Reject(Bind, new EntryOptions { ["count"] = null }).Issues));
    }

    [Fact]
    public void JsonElementReadOnlyMapsTypedListsAndSharedChildrenAreAccepted()
    {
        using var document = JsonDocument.Parse("""{"children":[{"name":"element"}],"count":3}""");
        var actual = Resolve(Bind, document.RootElement);
        Assert.Equal(3, actual.Count);
        Assert.Equal("element", Assert.Single(actual.Children).Name);
        var child = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["name"] = "shared" });
        actual = Resolve(Bind, new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?> { ["children"] = new[] { child, child } }));
        Assert.Equal(new[] { "shared", "shared" }, actual.Children.Select(value => value.Name));
        Assert.Equal(new[] { 0, 2 }, Resolve(ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.Int32Array), new List<int> { 0, 2 }));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteNumbersFailEvenInsideIgnoredProperties(double number)
    {
        var issue = Assert.Single(Reject(Bind, new EntryOptions { ["ignored"] = new object?[] { number } }).Issues);
        Assert.Contains("binding at $['ignored'][0]", issue);
        Assert.Contains("finite", issue);
    }

    [Fact]
    public void JsonElementNonFiniteRangeAndUnsupportedValuesReportTheirActualPaths()
    {
        using var document = JsonDocument.Parse("""{"ignored":[1e999]}""");
        Assert.Contains("$['ignored'][0]", Assert.Single(Reject(Bind, document.RootElement).Issues));
        foreach (var value in new object[] { new object(), (Action)(() => { }), Undefined.Value, new JsExpression("1"), float.NaN, DateTime.UnixEpoch })
            Assert.Contains("$['ignored'][0]", Assert.Single(Reject(Bind, new EntryOptions { ["ignored"] = new[] { value } }).Issues));
    }

    [Fact]
    public void CyclesAndExcessiveDepthFailWithoutEnumeratingArbitraryObjects()
    {
        var map = new EntryOptions();
        map["cycle"] = map;
        Assert.Contains("binding at $['cycle']: cyclic", Assert.Single(Reject(Bind, map).Issues));
        var list = new List<object?>();
        list.Add(list);
        Assert.Contains("$['ignored'][0]", Assert.Single(Reject(Bind, new EntryOptions { ["ignored"] = list }).Issues));
        object deep = new EntryOptions();
        for (var index = 0; index < 65; index++) deep = new EntryOptions { ["next"] = deep };
        Assert.Contains("maximum depth", Assert.Single(Reject(Bind, deep).Issues));
        Reject(Bind, new ExplodingProperties());
        Reject(Bind, Enumerable.Range(0, 3).Select(value => value));
    }

    [Fact]
    public void BusinessValidationRunsAfterBindingAndPreservesUserExceptions()
    {
        var calls = 0;
        var bind = ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.BindingOptions, value =>
        {
            calls++;
            return value.Count > 0 ? [] : new[] { "$.count must be positive", "$.label needs a nonempty label" };
        });
        Assert.StartsWith("binding", Assert.Single(Reject(bind, new EntryOptions { ["count"] = "bad" }).Issues));
        Assert.Equal(0, calls);
        Assert.Equal(new[] { "validation: $.count must be positive", "validation: $.label needs a nonempty label" }, Reject(bind, new EntryOptions { ["count"] = 0 }).Issues);
        Assert.Equal(1, calls);
        Assert.Equal(9, Resolve(bind, new EntryOptions { ["count"] = 9 }).Count);
        Assert.Equal(2, calls);
        var failure = new InvalidOperationException("rule bug");
        var broken = ConfigBinding.FromJsonTypeInfo(BindingJsonContext.Default.BindingOptions, _ => throw failure);
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => Resolve(broken, new EntryOptions())));
    }

    [Fact]
    public void ExistingCustomConfigHookStillAcceptsNonDataObjects()
    {
        var service = new object();
        Func<object?, ConfigResult<object>> custom = raw => ConfigResult<object>.Success(raw!);
        Assert.Same(service, Resolve(custom, service));
        Reject(Bind, service);
    }

    [Fact]
    public async Task LoaderBindsEvaluatedDataWhileKeepingRawExpressionAndInputUntouched()
    {
        await using var context = new Context();
        var expression = new JsExpression("options");
        var evaluated = new EntryOptions { ["count"] = 3L };
        var evaluator = new BindingEvaluator(evaluated);
        BindingOptions? applied = null;
        var plugin = new Plugin<BindingOptions> { Config = Bind, Apply = (_, value) => applied = value };
        Loader loader = null!;
        await context.RunAsync(_ =>
        {
            loader = new Loader(context, new StaticModuleResolver().Register("bound", plugin), expressionEvaluator: evaluator);
            return Task.CompletedTask;
        });
        await loader.CreateAsync(new() { Id = "configured", Name = "bound", Config = expression });
        await loader.WaitAsync();
        Assert.Equal(3, applied!.Count);
        Assert.Same(expression, loader.Resolve("configured").Fiber!.RawConfig);
        Assert.Same(expression, loader.Resolve("configured").Options.Config);
        Assert.Equal(3L, evaluated["count"]);
        Assert.Single(evaluated);
        Assert.Equal(1, evaluator.Calls);
    }

    private sealed class ExplodingProperties
    {
        public string Value => throw new InvalidOperationException("Arbitrary properties must never be reflected.");
    }

    private sealed class BindingEvaluator(object value) : IExpressionEvaluator
    {
        public int Calls { get; private set; }
        public object Evaluate(string expression, Context context) { Calls++; return value; }
    }
}

public sealed class BindingOptions
{
    public int Count { get; init; } = 9;
    public bool Enabled { get; init; } = true;
    public string Label { get; init; } = "initial";
    public string? Optional { get; init; } = "optional";
    public BindingMode Mode { get; init; }
    public BindingChild[] Children { get; init; } = [];
}

public enum BindingMode { Fast, Slow }

public sealed class SetterBindingOptions
{
    public int Count { get; set; } = 9;
}

public sealed class BindingChild(string name, int count = 4)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class StrictBindingOptions
{
    public required string Name { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true, RespectRequiredConstructorParameters = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BindingOptions))]
[JsonSerializable(typeof(BindingChild))]
[JsonSerializable(typeof(StrictBindingOptions))]
[JsonSerializable(typeof(SetterBindingOptions))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int[]))]
internal partial class BindingJsonContext : JsonSerializerContext;
