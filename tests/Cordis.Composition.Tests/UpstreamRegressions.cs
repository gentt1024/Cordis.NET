using Cordis;
using Cordis.Composition;
using Xunit;
namespace Cordis.Composition.Tests;

public sealed class UpstreamRegressions
{
    [Fact]
    public void TruthinessMatchesJavaScriptForPrimitiveAndContainerValues()
    {
        Assert.False(Data.Truthy(null));
        Assert.False(Data.Truthy(Undefined.Value));
        Assert.False(Data.Truthy(false));
        Assert.False(Data.Truthy(0));
        Assert.False(Data.Truthy(0L));
        Assert.False(Data.Truthy(0d));
        Assert.False(Data.Truthy(double.NaN));
        Assert.False(Data.Truthy(""));
        Assert.True(Data.Truthy(true));
        Assert.True(Data.Truthy("value"));
        Assert.True(Data.Truthy(Array.Empty<object?>()));
        Assert.True(Data.Truthy(new EntryOptions()));
    }

    [Fact]
    public async Task UndefinedDisabledExpressionKeepsEntryEnabled()
    {
        await using var context = new Context();
        var applies = 0;
        Loader loader = null!;
        await context.RunAsync(_ =>
        {
            loader = new Loader(context,
                new StaticModuleResolver().Register("p", new Plugin<object?> { Apply = (_, _) => applies++ }),
                expressionEvaluator: new UndefinedEvaluator());
            return Task.CompletedTask;
        });

        await loader.CreateAsync(new() { Id = "row", Name = "p", Disabled = new JsExpression("undefined") });
        await loader.WaitAsync();

        Assert.False(loader.Resolve("row").Disabled);
        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task LoaderPreservesMissingConfigThroughValidationAndRawConfig()
    {
        await using var context = new Context();
        Loader loader = null!;
        var validated = new List<object?>();
        var applied = new List<string>();
        var payload = new EntryOptions { ["answer"] = 42L };
        var plugin = new Plugin<string>
        {
            Config = raw =>
            {
                validated.Add(raw);
                return ConfigResult<string>.Success(raw switch
                {
                    Undefined => "missing",
                    null => "null",
                    false => "false",
                    long number when number == 0 => "zero",
                    IDictionary<string, object?> map when Equals(map["answer"], 42L) => "object",
                    _ => "unexpected"
                });
            },
            Apply = (_, value) => applied.Add(value)
        };
        await context.RunAsync(_ =>
        {
            loader = new Loader(context, new StaticModuleResolver().Register("probe", plugin));
            return Task.CompletedTask;
        });

        await loader.Root.UpdateAsync([
            new() { Id = "missing", Name = "probe" },
            new() { Id = "null", Name = "probe", Config = null },
            new() { Id = "false", Name = "probe", Config = false },
            new() { Id = "zero", Name = "probe", Config = 0L },
            new() { Id = "object", Name = "probe", Config = payload }
        ]);
        await loader.WaitAsync();

        Assert.Same(Undefined.Value, loader.Resolve("missing").Fiber!.RawConfig);
        Assert.Null(loader.Resolve("null").Fiber!.RawConfig);
        Assert.Equal(false, loader.Resolve("false").Fiber!.RawConfig);
        Assert.Equal(0L, loader.Resolve("zero").Fiber!.RawConfig);
        Assert.Same(payload, loader.Resolve("object").Fiber!.RawConfig);
        Assert.Contains(validated, value => ReferenceEquals(value, Undefined.Value));
        Assert.Contains(validated, value => value is null);
        Assert.Equal(new[] { "false", "missing", "null", "object", "zero" }, applied.Order().ToArray());

        var explicitNull = loader.Resolve("null");
        await explicitNull.UpdateAsync(new() { Config = null });
        await loader.WaitAsync();
        Assert.False(explicitNull.Options.ContainsKey("config"));
        Assert.Same(Undefined.Value, explicitNull.Fiber!.RawConfig);
        Assert.Equal(2, applied.Count(value => value == "missing"));
    }

    [Fact]
    public async Task LoaderWaitReobservesConsumerStartedByLaterProvider()
    {
        await using var context = new Context();
        var providerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new StaticModuleResolver()
            .Register("consumer", new Plugin<object?>
            {
                Inject = ["service"],
                ApplyAsync = async (_, _) =>
                {
                    consumerEntered.TrySetResult();
                    await consumerRelease.Task;
                }
            })
            .Register("provider", new Plugin<object?>
            {
                ApplyAsync = async (ctx, _) =>
                {
                    await providerRelease.Task;
                    ctx.Provide("service", true);
                }
            });
        Loader loader = null!;
        await context.RunAsync(_ =>
        {
            loader = new Loader(context, resolver);
            return Task.CompletedTask;
        });
        await loader.Root.UpdateAsync([
            new() { Id = "consumer", Name = "consumer" },
            new() { Id = "provider", Name = "provider" }
        ]);

        var wait = loader.WaitAsync();
        providerRelease.SetResult();
        await consumerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(FiberState.Loading, loader.Resolve("consumer").Fiber!.State);
            Assert.False(wait.IsCompleted);
        }
        finally { consumerRelease.TrySetResult(); }
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FiberState.Active, loader.Resolve("consumer").Fiber!.State);
    }

    [Fact]
    public async Task LoaderWaitRecordsFailureFromConsumerStartedByLaterProvider()
    {
        await using var context = new Context();
        var providerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumerRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("late consumer failed");
        var resolver = new StaticModuleResolver()
            .Register("consumer", new Plugin<object?>
            {
                Inject = ["service"],
                ApplyAsync = async (_, _) =>
                {
                    consumerEntered.TrySetResult();
                    await consumerRelease.Task;
                    throw failure;
                }
            })
            .Register("provider", new Plugin<object?>
            {
                ApplyAsync = async (ctx, _) =>
                {
                    await providerRelease.Task;
                    ctx.Provide("service", true);
                }
            });
        Loader loader = null!;
        await context.RunAsync(_ =>
        {
            loader = new Loader(context, resolver);
            return Task.CompletedTask;
        });
        await loader.Root.UpdateAsync([
            new() { Id = "consumer", Name = "consumer" },
            new() { Id = "provider", Name = "provider" }
        ]);

        var wait = loader.WaitAsync();
        providerRelease.SetResult();
        await consumerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(wait.IsCompleted);
        consumerRelease.SetResult();
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        var consumer = loader.Resolve("consumer");
        Assert.Equal(FiberState.Failed, consumer.Fiber!.State);
        Assert.Same(failure, consumer.LastError);
    }

    [Fact]
    public async Task ChildPluginConfigurationRetainsCallerIdentity()
    {
        await using var context = new Context(); Loader loader = null!; var childConfig = new EntryOptions { ["value"] = new JsExpression("not evaluated") }; object? seen = null;
        Fiber? childFiber = null; var child = new Plugin<object?> { Apply = (_, config) => seen = config };
        var parent = new Plugin<object?> { Apply = (ctx, _) => childFiber = ctx.Plugin(child, childConfig) };
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("parent", parent)); return Task.CompletedTask; });
        await loader.CreateAsync(new() { Id = "row", Name = "parent" }); await loader.WaitAsync();
        await childFiber!.WaitAsync(); Assert.Same(childConfig, seen); Assert.IsType<JsExpression>(childConfig["value"]);
    }
    [Fact]
    public async Task DisabledExpressionsRemainRawAndReevaluateOnUpdates()
    {
        await using var context = new Context(); Loader loader = null!; var applies = 0;
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("p", new Plugin<object?> { Apply = (_, _) => applies++ }), expressionEvaluator: new BooleanEvaluator()); return Task.CompletedTask; });
        var expression = new JsExpression("true"); await loader.CreateAsync(new() { Id = "row", Name = "p", Disabled = expression }); await loader.WaitAsync(); var row = loader.Resolve("row");
        Assert.True(row.Disabled); Assert.Null(row.Fiber); Assert.Same(expression, row.Options.Disabled);
        await row.UpdateAsync(new() { Disabled = new JsExpression("false") }); await loader.WaitAsync(); Assert.Equal(1, applies); Assert.False(row.Disabled); var previous = row.Fiber; Assert.NotNull(previous);
        await row.UpdateAsync(new() { Disabled = expression }); await loader.WaitAsync(); Assert.True(row.Disabled); Assert.Null(previous.Uid); Assert.Null(row.Fiber); Assert.Contains("!!js", ConfigurationFile.Write(row.Options));
        await row.UpdateAsync(new() { Disabled = new JsExpression("false") }); await loader.WaitAsync(); Assert.False(row.Disabled); Assert.NotNull(row.Fiber); Assert.Equal(2, applies);
    }
    [Fact]
    public async Task SchemaRejectionRetainsOldFiberAndNewEntryOptions()
    {
        await using var context = new Context(); Loader loader = null!;
        var plugin = new Plugin<long> { Config = raw => raw is long number ? ConfigResult<long>.Success(number) : ConfigResult<long>.Failure("expected number"), Apply = (ctx, value) => ctx.Provide("validated", value) };
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("schema", plugin)); return Task.CompletedTask; });
        await loader.CreateAsync(new() { Id = "row", Name = "schema", Config = 1L }); await loader.WaitAsync(); var row = loader.Resolve("row"); var fiber = row.Fiber;
        await Assert.ThrowsAsync<ConfigurationValidationException>(() => row.UpdateAsync(new() { Config = "invalid" }));
        Assert.Equal("invalid", row.Options.Config); Assert.Equal(1L, fiber!.Config);
        await context.RunAsync(_ => { Assert.Equal(1L, context.Get("validated")); return Task.CompletedTask; });
        await row.UpdateAsync(new() { Config = 2L }); await loader.WaitAsync(); Assert.Same(fiber, row.Fiber); Assert.Equal(2L, fiber.Config);
        await context.RunAsync(_ => { Assert.Equal(2L, context.Get("validated")); return Task.CompletedTask; });
    }
    [Fact]
    public async Task LazyConfigReevaluatesAfterProviderReplacement()
    {
        await using var context = new Context(); Loader loader = null!; var observations = new List<object?>();
        var resolver = new StaticModuleResolver().Register("provider", new Plugin<object?> { Apply = (ctx, raw) => ctx.Provide("value", raw) }).Register("reader", new Plugin<object?> { Inject = ["value"], Apply = (_, raw) => observations.Add(raw) });
        await context.RunAsync(_ => { loader = new Loader(context, resolver, expressionEvaluator: new ServiceEvaluator()); return Task.CompletedTask; });
        await loader.Root.UpdateAsync([new() { Id = "reader", Name = "reader", Config = new JsExpression("value") }, new() { Id = "provider", Name = "provider", Config = "first" }]); await loader.WaitAsync();
        await loader.Resolve("provider").UpdateAsync(new() { Config = "second" }); await loader.WaitAsync(); Assert.Equal(new object?[] { "first", "second" }, observations);
    }
    [Fact]
    public async Task TreeMoveDeleteLocateAndEntryIdentityAreStable()
    {
        await using var context = new Context(); Loader loader = null!;
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("p", new Plugin<object?> { Apply = (_, _) => { } })); return Task.CompletedTask; });
        await loader.Root.UpdateAsync([new() { Id = "g", Name = "cordis:group", Group = true, Config = Array.Empty<EntryOptions>() }, new() { Id = "row", Name = "p", Config = 1L }]); await loader.WaitAsync();
        var entry = loader.Resolve("row"); var fiber = entry.Fiber;
        await loader.UpdateAsync("row", new() { Config = 2L }, "g", move: true); await loader.WaitAsync(); Assert.Same(entry, loader.Resolve("row")); Assert.Same(fiber, entry.Fiber); Assert.Equal("row", loader.Locate(fiber!));
        Assert.Single(loader.Resolve("g").Subgroup!.Data); await loader.RemoveAsync("row"); Assert.Empty(loader.Resolve("g").Subgroup!.Data);
    }
    [Fact]
    public async Task RequiredAuditIgnoresAbsentAndDisabledRowsButRejectsPending()
    {
        await using var context = new Context(); Loader loader = null!;
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("wait", new Plugin<object?> { Inject = ["missing"], Apply = (_, _) => { } })); return Task.CompletedTask; });
        await loader.CreateAsync(new() { Id = "required", Name = "wait", Disabled = true }); Assert.Empty(await ApplicationBoot.AuditAsync(loader, new HashSet<string> { "absent", "required" }));
        await loader.Resolve("required").UpdateAsync(new() { Disabled = false }); var failure = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.AuditAsync(loader, new HashSet<string> { "required" })); Assert.Equal("missing", Assert.Single(Assert.Single(failure.Diagnostics).Missing));
    }
    [Fact]
    public async Task ProfileReconcileDistinguishesUnchangedAndIntroducedFailures()
    {
        using var files = new TemporaryDirectory(); var path = files.File("cordis.yml"); await System.IO.File.WriteAllTextAsync(path, "[]\n");
        await using var context = new Context(); Loader loader = null!;
        await context.RunAsync(_ => { loader = new Loader(context, new StaticModuleResolver().Register("fail", new Plugin<object?> { Apply = (_, _) => throw new InvalidOperationException("candidate failed") })); return Task.CompletedTask; });
        var patches = new List<EntryOptions> { new() { ["insert"] = new[] { new EntryOptions { Id = "bad", Name = "fail", Config = "old" } } } };
        var include = await ApplicationBoot.MountAsync(loader, path, patches); Assert.Single(await ApplicationBoot.ReconcileAsync(include, patches));
        await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.ReconcileAsync(include, [new() { ["insert"] = new[] { new EntryOptions { Id = "bad", Name = "fail", Config = "changed" } } }]));
        Assert.Empty(await ApplicationBoot.ReconcileAsync(include, [])); Assert.DoesNotContain(loader.Entries(), entry => entry.Options.Id == "bad"); Assert.Empty(await ApplicationBoot.ReconcileAsync(include, []));
    }
    [Fact]
    public async Task PatchPathsAnchorOnlyInsertedEntriesAndKeepNamesLiteral()
    {
        using var files = new TemporaryDirectory(); var path = files.File("cordis.patch.yml"); await System.IO.File.WriteAllTextAsync(path, "- id: existing\n  name: ./assertion.dll\n- insert:\n  - id: row\n    name: ./plugin #100%.dll\n  - id: group\n    name: cordis:group\n    group: true\n    config:\n    - id: child\n      name: ../child.dll\n");
        var patches = await Profiles.ReadPatchesAsync(path); Assert.Equal("./assertion.dll", patches[0].Name); var inserted = Data.Entries(patches[1]["insert"]); Assert.StartsWith("file:", inserted[0].Name); Assert.EndsWith("child.dll", Data.Entries(inserted[1].Config)[0].Name);
    }
    [Theory]
    [InlineData("invalid: [unclosed")]
    [InlineData("- config: !!js\n")]
    [InlineData("id: not-list")]
    [InlineData("- string")]
    public async Task MalformedPatchFilesFailLoud(string text)
    { using var files = new TemporaryDirectory(); var path = files.File("cordis.patch.yml"); await System.IO.File.WriteAllTextAsync(path, text); await Assert.ThrowsAnyAsync<Exception>(() => Profiles.ReadPatchesAsync(path, true)); }
    [Fact]
    public async Task OnlyMissingOptionalPatchesAreIgnored()
    { using var files = new TemporaryDirectory(); Assert.Empty(await Profiles.ReadPatchesAsync(files.File("missing.yml"), true)); Assert.Empty(await Profiles.ReadPatchesAsync(files.File("absent-parent/missing.yml"), true)); await Assert.ThrowsAsync<DirectoryNotFoundException>(() => Profiles.ReadPatchesAsync(files.File("absent-parent/missing.yml"))); await Assert.ThrowsAsync<FileNotFoundException>(() => Profiles.ReadPatchesAsync(files.File("missing.yml"))); await Assert.ThrowsAnyAsync<Exception>(() => Profiles.ReadPatchesAsync(files.Path, true)); }
    [Fact]
    public async Task ProfileManifestLayersFollowInstallationPrecedence()
    {
        using var files = new TemporaryDirectory(); var profile = files.Directory("profile"); var installed = files.Directory("installed"); var local = files.Directory("local");
        Profiles.Initialize(profile, ["bundle"]); await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(installed, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":\"cordis.patch.yml\"}}}");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(installed, "cordis.patch.yml"), "- insert:\n  - id: row\n    name: installed\n");
        var loaded = await Profiles.LoadAsync(profile, new Dictionary<string, string> { ["bundle"] = installed }, new Dictionary<string, string> { ["bundle"] = local }); Assert.Equal("installed", Assert.Single(Profiles.Compose(loaded.Layers)).Name);
        Profiles.Initialize(profile, ["other"]); Assert.Equal("bundle", Assert.Single(PackageManifest.Read(System.IO.Path.Combine(profile, "package.json")).Bundles));
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(local, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":\"cordis.patch.yml\"}}}");
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(local, "cordis.patch.yml"), "- insert:\n  - id: row\n    name: local\n");
        var fallback = await Profiles.LoadAsync(profile, new Dictionary<string, string>(), new Dictionary<string, string> { ["bundle"] = local }); Assert.Equal("local", Assert.Single(Profiles.Compose(fallback.Layers)).Name);
        await Assert.ThrowsAsync<FileNotFoundException>(() => Profiles.LoadAsync(profile, new Dictionary<string, string>()));
    }
    private sealed class BooleanEvaluator : IExpressionEvaluator { public object Evaluate(string expression, Context context) => bool.Parse(expression); }
    private sealed class UndefinedEvaluator : IExpressionEvaluator { public object Evaluate(string expression, Context context) => Undefined.Value; }
    private sealed class ServiceEvaluator : IExpressionEvaluator { public object? Evaluate(string expression, Context context) => context.Get(expression); }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cordis-regression-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => System.IO.Directory.CreateDirectory(Path);
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public string Directory(string name) { var path = File(name); System.IO.Directory.CreateDirectory(path); return path; }
        public void Dispose() => System.IO.Directory.Delete(Path, true);
    }
}
