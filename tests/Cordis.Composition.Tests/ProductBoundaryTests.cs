using Cordis;
using Cordis.Composition;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class ProductBoundaryTests
{
    [Fact]
    public async Task LauncherArgumentsCopyCallerInputAndRemainReadOnlyForMultipleConsumers()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var source = new List<string>
            {
                "--resume",
                "abc"
            };
            var effect = CommandLineArguments.Provide(ctx, source);
            source.Add("--tampered");
            var snapshot = ctx.Get<CommandLineArguments>("cmdlineArgs")!;
            Assert.Equal(new[] { "--resume", "abc" }, snapshot.Get());
            Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Get()).Add("--also-tampered"));
            var values = new List<IReadOnlyList<string>>();
            var plugin = new Plugin<object?>
            {
                Inject = ["cmdlineArgs"],
                Apply = (child, _) => values.Add(child.Get<CommandLineArguments>("cmdlineArgs")!.Get())
            };
            await ctx.Plugin(plugin).WaitAsync();
            await ctx.Plugin(plugin).WaitAsync();
            Assert.Equal(2, values.Count);
            Assert.Same(snapshot.Get(), values[0]);
            Assert.Same(values[0], values[1]);
            await effect.DisposeAsync();
            Assert.Null(ctx.Get("cmdlineArgs"));
        });
    }

    [Theory]
    [InlineData(false, false, 8080)]
    [InlineData(true, false, 8080)]
    [InlineData(false, true, 3080)]
    public async Task LauncherValuesReachInjectionReadyRowsAndSurviveConfigUpdates(bool objectInject, bool emptyArguments, int expected)
    {
        await using var root = new Context();
        var observed = new List<object?>();
        var modules = new StaticModuleResolver().Register("startup", new Plugin<object?>
        {
            Inject = ["cmdlineArgs"],
            Apply = (ctx, _) =>
        {
            var args = ctx.Get<CommandLineArguments>("cmdlineArgs")!.Get();
            ctx.Provide("demoStartup", args.Count == 0 ? null : int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture));
        }
        }).Register("reader", new Plugin<object?> { Apply = (_, config) => observed.Add(config) });
        await root.RunAsync(async ctx =>
        {
            var loader = new Loader(ctx, modules, expressionEvaluator: new StartupEvaluator());
            var reader = new EntryOptions
            {
                Id = "reader",
                Name = "reader",
                Config = new JsExpression("demoStartup ?? 3080"),
                ["inject"] = objectInject ? new EntryOptions
                {
                    ["demoStartup"] = new EntryOptions
                    {
                        ["required"] = true
                    }
                }

                : new[]
                {
                    "demoStartup"
                }
            };
            await loader.Root.UpdateAsync([new() { Id = "startup", Name = "startup" }, reader]);
            await loader.WaitAsync();
            Assert.Empty(observed);
            var source = emptyArguments ? new List<string>() : new List<string>
            {
                "--port",
                "8080"
            };
            CommandLineArguments.Provide(ctx, source);
            source.Clear();
            await loader.WaitAsync();
            Assert.Equal(expected, Assert.Single(observed));
            await loader.UpdateAsync("reader", new() { Config = new JsExpression("demoStartup ?? 3080") });
            await loader.WaitAsync();
            Assert.Equal(expected, observed[^1]);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DynamicBackendCleanupJoinsAndSelfDisposalPersistsOnlyOwner(bool removedFirst)
    {
        await using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Modules.Register("backend", new Plugin<object?>
        {
            Apply = (ctx, _) =>
        {
            ctx.Provide("picker", true);
            ctx.Effect(() => new Cleanup(async () =>
            {
                entered.TrySetResult();
                await release.Task;
            }));
        }
        });
        f.Modules.Register("owner", new Plugin<object?>
        {
            ApplyAsync = async (ctx, _) =>
        {
            ctx.Effect(() => new Cleanup(async () =>
            {
                if (f.Loader.Store.ContainsKey("backend"))
                    await f.Loader.RemoveAsync("backend");
            }));
            await f.Loader.CreateAsync(new() { Id = "backend", Name = "backend" });
        }
        });
        await f.Mount();
        Assert.DoesNotContain("backend", File.ReadAllText(f.Path));
        if (removedFirst)
        {
            release.TrySetResult();
            await f.Loader.RemoveAsync("backend");
        }

        var owner = f.Include.Resolve("owner").Fiber!;
        var disposal = owner.DisposeAsync().AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!removedFirst)
            Assert.False(disposal.IsCompleted);
        release.TrySetResult();
        await disposal;
        Assert.DoesNotContain(f.Loader.Entries(), entry => entry.Options.Name == "backend");
        await f.Root.RunAsync(ctx =>
        {
            Assert.Null(ctx.Get("picker"));
            return Task.CompletedTask;
        });
        var failures = 1;
        var attempts = 0;
        f.Include.ReplaceFileAsync = (source, destination) =>
        {
            attempts++;
            if (failures > 0)
            {
                failures--;
                throw new UnauthorizedAccessException("transient rename");
            }

            File.Move(source, destination, true);
            return Task.CompletedTask;
        };
        f.Include.Write();
        await f.Include.StopAsync();
        Assert.True(attempts >= 2);
        Assert.Equal(0, failures);
        var text = File.ReadAllText(f.Path);
        Assert.Contains("disabled: true", text);
        Assert.DoesNotContain("backend", text);
    }

    [Fact]
    public async Task FailedSurfaceSetupUnwindsAlreadyMountedBackend()
    {
        await using var f = new Fixture();
        f.Modules.Register("backend", new Plugin<object?> { Apply = (ctx, _) => ctx.Provide("picker", true) });
        f.Modules.Register("owner", new Plugin<object?>
        {
            ApplyAsync = async (ctx, _) =>
        {
            ctx.Effect(() => new Cleanup(async () =>
            {
                if (f.Loader.Store.ContainsKey("backend"))
                    await f.Loader.RemoveAsync("backend");
            }));
            await f.Loader.CreateAsync(new() { Id = "backend", Name = "backend" });
            await f.Loader.Resolve("backend").Fiber!.WaitAsync();
            throw new InvalidOperationException("surface load failed");
        }
        });
        await f.Mount();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Include.Resolve("owner").Fiber!.WaitAsync());
        Assert.DoesNotContain(f.Loader.Entries(), entry => entry.Options.Name == "backend");
        await f.Root.RunAsync(ctx =>
        {
            Assert.Null(ctx.Get("picker"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task TerminalDebouncedWriteFailureReachesTeardownOwner()
    {
        await using var f = new Fixture();
        f.Modules.Register("owner", new Plugin<object?>
        {
            Apply = (_, _) =>
        {
        }
        });
        await f.Mount();
        var reported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Diagnostic = error => reported.TrySetResult(error);
        var failure = new IOException("terminal write failure", unchecked((int)0x8007001f));
        f.Include.ReplaceFileAsync = (_, _) => Task.FromException(failure);
        await f.Include.Resolve("owner").Fiber!.DisposeAsync();
        Assert.Same(failure, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => f.Include.StopAsync()));
        await f.Root.Fiber.DisposeAsync();
    }

    [Fact]
    public async Task ExternalDeploymentDoesNotActivateExistingDependencyThatGainsBundleMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cordis-deploy-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var package = Path.Combine(directory, "late-bundle");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"late-bundle\",\"version\":\"1.0.0\"}");
            var manifest = new PackageManifest(new EntryOptions { ["custom"] = true, ["dependencies"] = new EntryOptions { ["late-bundle"] = "file:./late-bundle" }, ["dsh"] = new EntryOptions { ["profile"] = new EntryOptions { ["bundles"] = new[] { "builtin" } } } });
            manifest.Write(Path.Combine(directory, "package.json"));
            var packages = new Dictionary<string, string>
            {
                ["late-bundle"] = package
            };
            Assert.Equal(new[] { "builtin" }, (await PluginConfigurationOperations.ReconcileDeployedPackagesAsync(directory, manifest, packages, new Dictionary<string, string>())).Inventory.Manifest.Bundles);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"late-bundle\",\"version\":\"2.0.0\",\"dsh\":{\"bundle\":{\"patch\":\"patch.yml\"}}}");
            File.WriteAllText(Path.Combine(package, "patch.yml"), "[]\n");
            Assert.Equal(new[] { "builtin" }, (await PluginConfigurationOperations.ReconcileDeployedPackagesAsync(directory, manifest, packages, new Dictionary<string, string>())).Inventory.Manifest.Bundles);
            var added = Path.Combine(directory, "added");
            Directory.CreateDirectory(added);
            File.WriteAllText(Path.Combine(added, "package.json"), "{\"name\":\"added\",\"dsh\":{\"bundle\":{\"patch\":\"patch.yml\"}}}");
            File.WriteAllText(Path.Combine(added, "patch.yml"), "[]\n");
            packages["alias"] = added;
            var after = PackageManifest.Read(Path.Combine(directory, "package.json"));
            ((EntryOptions)after.Raw["dependencies"]!)["alias"] = "file:./added";
            after.Write(Path.Combine(directory, "package.json"));
            var result = await PluginConfigurationOperations.ReconcileDeployedPackagesAsync(directory, manifest, packages, new Dictionary<string, string>());
            Assert.Equal(new[] { "builtin", "alias" }, result.Inventory.Manifest.Bundles);
            Assert.True((bool)result.Inventory.Manifest.Raw["custom"]!);
            Assert.Equal(new[] { "builtin", "alias" }, (await PluginConfigurationOperations.ReconcileDeployedPackagesAsync(directory, result.Inventory.Manifest, packages, new Dictionary<string, string>())).Inventory.Manifest.Bundles);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RemovingProfilePatchRestoresBundleThenHomeLayerAndDisposalSettles()
    {
        await using var f = new Fixture();
        var values = new List<object?>();
        var stopped = 0;
        f.Modules.Register("owner", new Plugin<object?>
        {
            Apply = (ctx, config) =>
        {
            values.Add(config);
            ctx.Effect(() => (Action)(() => stopped++));
        }
        });
        await f.Mount();
        var directory = Path.GetDirectoryName(f.Path)!;
        var home = Path.Combine(directory, "home");
        var profileDirectory = Path.Combine(directory, "profile");
        var bundleDirectory = Path.Combine(directory, "bundle");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(bundleDirectory);
        Profiles.Initialize(profileDirectory, ["bundle"]);
        File.WriteAllText(Path.Combine(bundleDirectory, "package.json"), "{\"dsh\":{\"bundle\":{\"patch\":\"patch.yml\"}}}");
        File.WriteAllText(Path.Combine(bundleDirectory, "patch.yml"), "- id: owner\n  config: bundle-default\n");
        var mappings = new Dictionary<string, string>
        {
            ["bundle"] = bundleDirectory
        };
        var launch = new ProfileLaunch(await Profiles.LoadAsync(profileDirectory, mappings), home, [], mappings);
        var patch = Path.Combine(profileDirectory, "cordis.patch.yml");
        File.WriteAllText(patch, "- id: owner\n  config: 2\n");
        async Task Apply() => await ApplicationBoot.ReconcileAsync(f.Include, ProfileComposition.Flatten((await ProfileComposition.RefreshAsync(launch)).Layers));
        await Apply();
        Assert.Equal(2L, values[^1]);
        File.Delete(patch);
        await Apply();
        Assert.False(File.Exists(patch));
        Assert.Equal("bundle-default", values[^1]);
        File.WriteAllText(Path.Combine(home, "cordis.patch.yml"), "- id: owner\n  config: home\n");
        await Apply();
        Assert.Equal("home", values[^1]);
        await f.Root.DisposeAsync();
        Assert.Equal(values.Count, stopped);
    }

    private sealed class StartupEvaluator : IExpressionEvaluator
    {
        public object? Evaluate(string expression, Context context) => context.Get("demoStartup") ?? 3080;
    }

    private sealed class Cleanup(Func<Task> callback) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(callback());
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cordis-boundary-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(directory, "cordis.yml");
        public Context Root { get; } = new();
        public StaticModuleResolver Modules { get; } = new();
        public Loader Loader { get; private set; } = null!;
        public Include Include { get; private set; } = null!;
        public Action<Exception>? Diagnostic { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path, "- id: owner\n  name: owner\n");
        }

        public async Task Mount()
        {
            await Root.RunAsync(ctx =>
            {
                Loader = new(ctx, Modules, diagnostic: error => Diagnostic?.Invoke(error));
                return Task.CompletedTask;
            });
            Include = await ApplicationBoot.MountAsync(Loader, Path);
        }

        public async ValueTask DisposeAsync()
        {
            await Root.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
}
