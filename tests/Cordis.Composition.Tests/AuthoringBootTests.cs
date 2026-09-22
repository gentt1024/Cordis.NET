using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class AuthoringBootTests
{
    [Fact]
    public async Task GenericBootLeavesPendingLegalAndDoesNotInstallDshPolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            await File.WriteAllTextAsync(path, "- id: webserver\n  name: waiting\n");
            var resolver = new StaticModuleResolver().Register("waiting", new Plugin<object?> { Inject = ["later"], Apply = (_, _) => { } });
            var warnings = new List<string>();
            await using var context = await ApplicationBoot.BootGenericAsync(path, resolver, prepare: ctx =>
            {
                Assert.Null(ctx.Get("dshHomePath"));
                Assert.Empty(ctx.Get<Loader>("loader")!.Entries());
                return Task.CompletedTask;
            }, warn: warnings.Add);
            Loader loader = null!;
            await context.RunAsync(ctx => { loader = ctx.Get<Loader>("loader")!; return Task.CompletedTask; });
            var pending = Assert.Single(await ApplicationBoot.AuditAsync(loader));
            Assert.Equal(FiberState.Pending, pending.State);
            Assert.Equal(["later"], pending.Missing);
            Assert.False(pending.Required);
            Assert.Null(pending.Error);
            Assert.Contains("waiting for service: later", Assert.Single(warnings));

            var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootAsync(path, resolver, prepare: ctx =>
            {
                Assert.IsType<DshHomePath>(ctx.Get("dshHomePath"));
                return Task.CompletedTask;
            }));
            Assert.True(Assert.Single(error.Diagnostics).Required);
            var required = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootGenericAsync(path, resolver,
                required: new HashSet<string> { "webserver" }));
            Assert.Equal(FiberState.Pending, Assert.Single(required.Diagnostics).State);
            Assert.Null(required.InnerException);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task GenericRequiredPolicyCleansEffectsAndPreservesOriginalFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml");
        var failure = new InvalidOperationException("apply rejected");
        var cleaned = false;
        try
        {
            await File.WriteAllTextAsync(path, "- id: application\n  name: rejected\n");
            var resolver = new StaticModuleResolver().Register("rejected", new Plugin<object?> { Apply = (_, _) => throw failure });
            var error = await Assert.ThrowsAsync<StartupException>(() => ApplicationBoot.BootGenericAsync(path, resolver,
                prepare: ctx => { ctx.Effect(() => (Action)(() => cleaned = true)); return Task.CompletedTask; },
                required: new HashSet<string> { "application" }));
            Assert.True(cleaned);
            Assert.Equal(path, error.ConfigurationPath);
            Assert.Same(failure, Assert.Single(error.Diagnostics).Error);
            Assert.Contains(failure, Assert.IsType<AggregateException>(error.InnerException).InnerExceptions);
            Assert.Contains(failure, error.StartupMessages);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RequiredNamesDoNotRequireAbsentEntriesInEitherBootPolicy()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml");
        try
        {
            await File.WriteAllTextAsync(path, "[]\n");
            await using var generic = await ApplicationBoot.BootGenericAsync(path, new StaticModuleResolver(), required: new HashSet<string> { "not-installed" });
            await using var dsh = await ApplicationBoot.BootAsync(path, new StaticModuleResolver());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AuditClassifiesByOperationAndRetainsOriginalErrorsThroughRecovery()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".yml");
        var configuration = new InvalidOperationException("configuration rejected");
        var apply = new ConfigurationValidationException(["thrown by apply, not binding"]);
        var reject = true;
        try
        {
            await File.WriteAllTextAsync(path, "- id: import\n  name: absent\n- id: config\n  name: config\n- id: apply\n  name: apply\n");
            var resolver = new StaticModuleResolver()
                .Register("config", new Plugin<object?> { Config = raw => reject ? throw configuration : ConfigResult<object?>.Success(raw), Apply = (_, _) => { } })
                .Register("apply", new Plugin<object?> { Apply = (_, _) => { if (reject) throw apply; } });
            await using var context = await ApplicationBoot.BootGenericAsync(path, resolver, warn: _ => { });
            Loader loader = null!;
            await context.RunAsync(ctx => { loader = ctx.Get<Loader>("loader")!; return Task.CompletedTask; });
            var diagnostics = await ApplicationBoot.AuditAsync(loader);
            var imported = Assert.Single(diagnostics, d => d.Id == "root:import");
            Assert.Equal("module resolution", imported.Phase);
            Assert.IsType<FileNotFoundException>(imported.Error);
            var configured = Assert.Single(diagnostics, d => d.Id == "root:config");
            Assert.Equal("configuration", configured.Phase);
            Assert.Same(configuration, configured.Error);
            var applied = Assert.Single(diagnostics, d => d.Id == "root:apply");
            Assert.Equal("apply", applied.Phase);
            Assert.Same(apply, applied.Error);
            Assert.Equal("apply", applied.ToSnapshot().Phase);
            reject = false;
            await loader.Resolve("root:config").Fiber!.RestartAsync();
            await loader.Resolve("root:apply").Fiber!.RestartAsync();
            Assert.Null(loader.Resolve("root:config").Fiber!.FailurePhase);
            Assert.Null(loader.Resolve("root:apply").Fiber!.FailurePhase);
            Assert.Equal("root:import", Assert.Single(await ApplicationBoot.AuditAsync(loader)).Id);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SnapshotCopiesErrorEvidenceAndDependencyNamesWithoutKeepingException()
    {
        var missing = new List<string> { "dependency" };
        var error = new InvalidOperationException("outer evidence", new ArgumentException("inner evidence"));
        var original = new EntryDiagnostic("root:probe", "probe-module", FiberState.Failed, error, missing, true) { Phase = "known stage" };
        var snapshot = original.ToSnapshot();
        missing.Clear();
        Assert.Equal(original.Id, snapshot.Id);
        Assert.Equal(original.Module, snapshot.Module);
        Assert.Equal(original.State, snapshot.State);
        Assert.True(snapshot.Required);
        Assert.Equal("known stage", snapshot.Phase);
        Assert.Equal(["dependency"], snapshot.Missing);
        Assert.Contains("outer evidence", snapshot.Error);
        Assert.Contains("inner evidence", snapshot.Error);
        Assert.Same(error, original.Error);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Missing).Add("changed"));
        Assert.Null((original with { Error = null }).ToSnapshot().Error);
    }

    [Fact]
    public void PatchReadsDeployedAssemblyAndReturnsIndependentRowsWithoutRootingIt()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cordis-resource-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var deployed = Path.Combine(directory, "deployed.dll");
            File.Copy(typeof(AuthoringBootTests).Assembly.Location, deployed);
            var weak = ReadDeployedResource(deployed);
            for (var attempt = 0; attempt < 10 && weak.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            Assert.False(weak.IsAlive);
        }
        finally { Directory.Delete(directory, true); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ReadDeployedResource(string path)
    {
        var loadContext = new AssemblyLoadContext("patch-resource", isCollectible: true);
        using var deployed = File.OpenRead(path);
        var assembly = loadContext.LoadFromStream(deployed);
        var first = PatchResources.Read(assembly, "Authoring.patch.yml");
        var expected = ConfigurationFile.Write(first);
        first.Clear();
        var second = PatchResources.Read(assembly, "Authoring.patch.yml");
        Assert.Equal(expected, ConfigurationFile.Write(second));
        var inserted = Assert.Single(Data.Entries(second[0]["insert"]));
        var rawConfig = Assert.IsAssignableFrom<IDictionary<string, object?>>(inserted.Config);
        Assert.Equal("context.value", Assert.IsType<JsExpression>(rawConfig["expression"]).Source);
        var row = Assert.Single(EntryPatches.Apply([], second));
        Assert.Equal("probe", row.Id);
        Assert.Equal("probe-module", row.Name);
        var config = Assert.IsAssignableFrom<IDictionary<string, object?>>(row.Config);
        Assert.Equal("patched", config["value"]);
        Assert.False(config.ContainsKey("expression")); // Existing patch semantics replace the config value.
        Assert.Equal("json-probe", Assert.Single(EntryPatches.Apply([], PatchResources.Read(assembly, "Authoring.patch.json", json: true))).Id);
        var missing = Assert.Throws<FileNotFoundException>(() => PatchResources.Read(assembly, "absent.yml"));
        Assert.Contains("absent.yml", missing.Message);
        Assert.Contains(assembly.GetName().Name!, missing.Message);
        var malformed = Assert.Throws<FormatException>(() => PatchResources.Read(assembly, "Authoring.invalid.yml"));
        Assert.Contains("Authoring.invalid.yml", malformed.Message);
        Assert.NotNull(malformed.InnerException);
        var syntax = Assert.Throws<FormatException>(() => PatchResources.Read(assembly, "Authoring.patch.yml", json: true));
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(syntax.InnerException);
        var weak = new WeakReference(loadContext);
        loadContext.Unload();
        return weak;
    }
}
