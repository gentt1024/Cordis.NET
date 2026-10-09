using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Cordis.Clr;
using Cordis.Composition;
using Cordis.Fixtures;
using Cordis.Extensions;
using Xunit;

namespace Cordis.Platform.Tests;

// These tests observe process-wide GC/finalization and native DLL release as separate steps.
[Collection("Collectible CLR")]
public sealed class ClrTests
{
    private static object? retainedConfigReference;

    [Theory]
    [InlineData("ConfigurationEntry")]
    [InlineData("ComposedConfigurationEntry")]
    public async Task Scalar_reference_does_not_keep_collectible_effective_poco_alive(string entryType)
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-scalar-poco-" + Guid.NewGuid().ToString("N"));
        ClrUnloadObservation[] observations = [];
        try
        {
            observations = await RetainScalarFromCollectiblePocoAsync(shadow, entryType);
            var observation = Assert.Single(observations);
            Assert.True(observation.UnloadRequested);
            for (var attempt = 0;attempt < 12 && !observation.IsCollected;attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Yield();
            }

            Assert.Equal("old-value", Assert.IsType<ConfigReference<string>>(retainedConfigReference).Value);
            Assert.True(observation.IsCollected);
            Assert.True(observation.TryDeleteShadow());
        }
        finally
        {
            // Release test-owned state even on the expected pre-fix assertion failure.
            retainedConfigReference = null;
            for (var attempt = 0;attempt < 12 && observations.Any(item => !item.IsCollected);attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Yield();
            }

            foreach (var observation in observations)
                if (observation.IsCollected)
                    observation.TryDeleteShadow();
            if (Directory.Exists(shadow) && !Directory.EnumerateFileSystemEntries(shadow).Any())
                Directory.Delete(shadow);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClrUnloadObservation[]> RetainScalarFromCollectiblePocoAsync(
        string shadow,
        string entryType)
    {
        await using var context = new Context();
        await using var resolver = new ClrModuleResolver(shadow);
        resolver.Register(
            "fixture",
            Definition("v1") with
            {
                EntryType = "VersionedPlugin." + entryType
            });
        await context.RunAsync(async ctx =>
        {
            var module = await resolver.ResolveAsync("fixture", new Uri("file:///"));
            var fiber = ctx.Plugin(
                module,
                entryType == "ConfigurationEntry"
                    ? "old-value"
                    : new Dictionary<string, object?>
                    {
                        ["value"] = "old-value"
                    });
            await fiber.WaitAsync();
            Assert.Equal(FiberState.Active, fiber.State);
            Assert.NotNull(fiber.Config);
            Assert.True(AssemblyLoadContext.GetLoadContext(fiber.Config!.GetType().Assembly)!.IsCollectible);
            retainedConfigReference = ctx.Get("configured-value");
            Assert.IsType<ConfigReference<string>>(retainedConfigReference);
            // Never retain configured-reference, fiber.Config, module, Assembly or Type outside this boundary.
        });
        await context.DisposeAsync();
        await resolver.DisposeAsync();
        return resolver.Unloads.ToArray();
    }

    [Fact]
    public async Task Primitive_configuration_reference_with_plain_effective_data_does_not_retain_the_bundle()
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-clr-primitive-ref-" + Guid.NewGuid().ToString("N"));
        ClrUnloadObservation[] observations;
        try
        {
            observations = await ExercisePrimitiveConfigurationLifetime(shadow);
            for (var attempt = 0;attempt < 12 && observations.Any(item => !item.IsCollected);attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Yield();
            }

            Assert.Equal(7, Assert.IsType<ConfigReference<int>>(retainedConfigReference).Value);
            Assert.All(observations, item => Assert.True(item.IsCollected));
            Assert.All(observations, item => Assert.True(item.TryDeleteShadow()));
        }
        finally
        {
            retainedConfigReference = null;
        }

        Directory.Delete(shadow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClrUnloadObservation[]> ExercisePrimitiveConfigurationLifetime(string shadow)
    {
        await using var context = new Context();
        await using var resolver = new ClrModuleResolver(shadow);
        resolver.Register(
            "fixture",
            Definition("v1") with
            {
                EntryType = "VersionedPlugin.PrimitiveConfigurationEntry"
            });
        await context.RunAsync(async ctx =>
        {
            var module = await resolver.ResolveAsync("fixture", new Uri("file:///"));
            var fiber = ctx.Plugin(
                module,
                new Dictionary<string, object?>
                {
                    ["value"] = 7
                });
            await fiber.WaitAsync();
            Assert.Equal(FiberState.Active, fiber.State);
            Assert.IsType<Dictionary<string, object?>>(fiber.Config);
            retainedConfigReference = ctx.Get("primitive-reference");
            Assert.IsType<ConfigReference<int>>(retainedConfigReference);
        });
        await context.DisposeAsync();
        await resolver.DisposeAsync();
        return resolver.Unloads.ToArray();
    }

    [Fact]
    public async Task Automatic_unload_observations_do_not_retain_collectible_exception_types_after_actual_unload()
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-clr-unload-snapshot-" + Guid.NewGuid().ToString("N"));
        var state = await ExerciseCollectibleUnloadFailure(shadow);
        for (var attempt = 0;
             attempt < 12 && (state.Failure.IsAlive || state.Assembly.IsAlive ||
                 state.Resolver.Unloads.Any(item => !item.IsCollected));
             attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.False(state.Failure.IsAlive);
        Assert.False(state.Assembly.IsAlive);
        Assert.IsType<string>(state.Resolver.Unloads[0].UnloadError);
        Assert.All(state.Resolver.Unloads, item => Assert.True(item.IsCollected));
        Assert.All(state.Resolver.Unloads, item => Assert.True(item.TryDeleteShadow()));
        GC.KeepAlive(state.Resolver);
        Directory.Delete(shadow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(ClrModuleResolver Resolver, WeakReference Failure, WeakReference Assembly)>
        ExerciseCollectibleUnloadFailure(string shadow)
    {
        var resolver = new ClrModuleResolver(shadow, [typeof(IVersionedService).Assembly]);
        resolver.Register("fixture", Definition("v1"));
        var previous = await resolver.ResolveAsync("fixture", new Uri("file:///"));
        System.Reflection.Assembly? candidateAssembly = null;
        var primary = new InvalidOperationException("switch rejected before teardown");
        Assert.Same(
            primary,
            await Assert.ThrowsAsync<InvalidOperationException>(() => resolver
                .ReplaceAsync(
                    "fixture",
                    Definition("v2") with
                    {
                        EntryType = "VersionedPlugin.UnloadFailureEntry"
                    },
                    (_, candidate) =>
                    {
                        candidateAssembly = Assert.IsType<Plugin<object?>>(candidate).Apply!.Method.Module.Assembly;
                        throw primary;
                    })
                .AsTask()));
        Assert.Same(previous, await resolver.ResolveAsync("fixture", new Uri("file:///")));
        var observation = Assert.Single(resolver.Unloads);
        Assert.True(observation.UnloadRequested);
        // An Unloading handler has thrown before Unload completes. The request is not
        // proof of collection; this helper still holds the actual assembly/context.
        Assert.False(observation.IsCollected);
        Assert.False(observation.TryDeleteShadow());
        var diagnostic = observation.UnloadError!.ToString();
        Assert.Contains("VersionedPlugin.CollectibleUnloadException", diagnostic);
        Assert.Contains("collectible unload observer rejected", diagnostic);
        Assert.Contains("UnloadFailureEntry", diagnostic);
        var failure = Assert.IsType<WeakReference>(
            candidateAssembly!.GetType("VersionedPlugin.UnloadFailureEntry")!.GetProperty("LastFailure")!
                .GetValue(null));
        var assembly = new WeakReference(candidateAssembly);
        // Unloading observers run before the CLR completes release. A throwing observer
        // can suppress that release even after the managed ALC wrapper becomes unreachable.
        // The fixture removed its own failing handler, so retry the real CLR request while
        // holding the actual context. No production retry or fabricated collection is used.
        AssemblyLoadContext.GetLoadContext(candidateAssembly)!.Unload();
        candidateAssembly = null;
        previous = null;
        await resolver.DisposeAsync();
        return (resolver, failure, assembly);
    }

    [Theory]
    [InlineData("ConfigurationEntry")]
    [InlineData("ComposedConfigurationEntry")]
    public async Task Typed_configuration_reference_retains_its_bundle_only_until_the_host_releases_it(string entryType)
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-clr-config-lifetime-" + Guid.NewGuid().ToString("N"));
        ClrUnloadObservation[] observations;
        try
        {
            observations = await ExerciseTypedConfigurationLifetime(shadow, entryType);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(observations[0].IsCollected);
            Assert.True(ReferenceTypeBelongsTo(retainedConfigReference!, observations[0]));
        }
        finally
        {
            retainedConfigReference = null;
        }

        for (var attempt = 0;attempt < 12 && observations.Any(item => !item.IsCollected);attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.All(observations, item => Assert.True(item.IsCollected));
        Assert.All(observations, item => Assert.True(item.TryDeleteShadow()));
        Directory.Delete(shadow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ReferenceTypeBelongsTo(object reference, ClrUnloadObservation observation) =>
        ReferenceEquals(
            AssemblyLoadContext.GetLoadContext(reference.GetType().GetGenericArguments()[0].Assembly),
            observation.LoadContext.Target);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClrUnloadObservation[]> ExerciseTypedConfigurationLifetime(
        string shadow,
        string entryType)
    {
        await using var context = new Context();
        await using var resolver = new ClrModuleResolver(shadow, [typeof(IVersionedService).Assembly]);
        resolver.Register(
            "fixture",
            Definition("v1") with
            {
                EntryType = "VersionedPlugin." + entryType
            });
        Loader? loader = null;
        await context.RunAsync(async ctx =>
        {
            ctx.Provide("trace", new List<string>());
            loader = new Loader(ctx, resolver);
            await loader.CreateAsync(
                new()
                {
                    Id = "row",
                    Name = "fixture",
                    Config = entryType == "ConfigurationEntry"
                        ? "old"
                        : new Dictionary<string, object?>
                        {
                            ["value"] = "old"
                        }
                });
            await loader.WaitAsync();
            retainedConfigReference = ctx.Get("configured-reference");
            Assert.NotNull(retainedConfigReference);
            Assert.NotNull(loader.Resolve("row").Fiber!.ConfigDescription);
            Assert.Equal("old", loader.Resolve("row").Fiber!.GetConfigReference<string>("value").Value);
        });
        await resolver.ReplaceAsync(
            "fixture",
            Definition("v2") with
            {
                EntryType = "VersionedPlugin." + entryType
            },
            async (previous, replacement) => await loader!.ReplacePluginAsync(previous, replacement));
        await context.RunAsync(ctx =>
        {
            Assert.NotSame(retainedConfigReference, ctx.Get("configured-reference"));
            Assert.Equal("old", ctx.Get<ConfigReference<string>>("configured-value")!.Value);
            return Task.CompletedTask;
        });
        await context.DisposeAsync();
        await resolver.DisposeAsync();
        return resolver.Unloads.ToArray();
    }

    [Fact]
    public async Task Candidate_unload_observer_failure_does_not_replace_the_switch_failure()
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-clr-unload-error-" + Guid.NewGuid().ToString("N"));
        await using var resolver = new ClrModuleResolver(shadow, [typeof(IVersionedService).Assembly]);
        resolver.Register("fixture", Definition("v1"));
        var previous = await resolver.ResolveAsync("fixture", new Uri("file:///"));
        var primary = new InvalidOperationException("switch rejected");
        var secondary = new IOException("unloading observer rejected");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver
            .ReplaceAsync(
                "fixture",
                Definition("v2"),
                (_, candidate) =>
                {
                    var assembly = Assert.IsType<Plugin<object?>>(candidate).Apply!.Method.Module.Assembly;
                    AssemblyLoadContext.GetLoadContext(assembly)!.Unloading += _ => throw secondary;
                    throw primary;
                })
            .AsTask());
        Assert.Same(primary, error);
        Assert.Same(previous, await resolver.ResolveAsync("fixture", new Uri("file:///")));
        Assert.True(Assert.Single(resolver.Unloads).UnloadRequested);
        Assert.Equal(secondary.ToString(), Assert.Single(resolver.Unloads).UnloadError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_private_dependency_binds_to_host_only_when_explicitly_shared(bool shareDependency)
    {
        var source = Directory.CreateTempSubdirectory("cordis-clr-private-");
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-clr-private-shadow-" + Guid.NewGuid().ToString("N"));
        try
        {
            var original = Definition("v1").BundleDirectory;
            var privateFile = Path.Combine(original, "Dependency.dll");
            var hostAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(privateFile);
            Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(hostAssembly));
            foreach (var file in Directory.GetFiles(original, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(source.FullName, Path.GetRelativePath(original, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            File.Delete(Path.Combine(source.FullName, "Dependency.dll"));
            var observations = await ExerciseMissingPrivateDependency(
                source.FullName,
                shadow,
                shareDependency ? hostAssembly : null);
            for (var attempt = 0;attempt < 12 && observations.Any(item => !item.IsCollected);attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Yield();
            }

            Assert.All(observations, item => Assert.True(item.TryDeleteShadow()));
            Directory.Delete(shadow);
        }
        finally
        {
            source.Delete(true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClrUnloadObservation[]> ExerciseMissingPrivateDependency(
        string source,
        string shadow,
        System.Reflection.Assembly? sharedDependency)
    {
        await using var context = new Context();
        await using var resolver = new ClrModuleResolver(
            shadow,
            sharedDependency is null
                ? [typeof(IVersionedService).Assembly]
                : [typeof(IVersionedService).Assembly, sharedDependency]);
        resolver.Register("fixture", new(source, "VersionedPlugin.dll", "VersionedPlugin.Entry"));
        await context.RunAsync(async ctx =>
        {
            ctx.Provide("trace", new List<string>());
            var fiber = ctx.Plugin(await resolver.ResolveAsync("fixture", new Uri("file:///")), "private");
            await fiber.WaitAsync();
            var service = ctx.Get<IVersionedService>("versioned")!;
            if (sharedDependency is null)
                Assert.Throws<FileNotFoundException>(() => service.Dependency);
            else
                Assert.Equal("private-bundle-dependency", service.Dependency);
            Assert.Equal("v1", service.Version);
        });
        await context.DisposeAsync();
        await resolver.DisposeAsync();
        return resolver.Unloads.ToArray();
    }

    [Fact]
    public async Task Callback_can_resolve_modules_and_external_disposal_waits_for_replacement_ownership()
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-clr-concurrency-" + Guid.NewGuid().ToString("N"));
        var observations = await ExerciseReplacementOwnership(shadow);
        for (var attempt = 0;attempt < 12 && observations.Any(item => !item.IsCollected);attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.All(observations, item => Assert.True(item.IsCollected));
        Assert.All(observations, item => Assert.True(item.TryDeleteShadow()));
        Directory.Delete(shadow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClrUnloadObservation[]> ExerciseReplacementOwnership(string shadow)
    {
        await using var resolver = new ClrModuleResolver(shadow, [typeof(IVersionedService).Assembly]);
        resolver.Register("fixture", Definition("v1"));
        resolver.Register("dependency", Definition("v1"));
        var previous = await resolver.ResolveAsync("fixture", new Uri("file:///"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = resolver
            .ReplaceAsync(
                "fixture",
                Definition("v2"),
                async (old, candidate) =>
                {
                    Assert.Same(previous, old);
                    Assert.NotSame(old, candidate);
                    Assert.Same(old, await resolver.ResolveAsync("fixture", new Uri("file:///")));
                    Assert.Same(old, await resolver.ResolveAsync("dependency", new Uri("file:///")));
                    Assert.Throws<InvalidOperationException>(() => resolver.Register(
                        "candidate-alias",
                        Definition("v2")));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.DisposeAsync().AsTask());
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        resolver.ReplaceAsync("fixture", Definition("v1"), (_, _) => ValueTask.CompletedTask).AsTask());
                    entered.SetResult();
                    await release.Task;
                    Assert.Same(old, await resolver.ResolveAsync("fixture", new Uri("file:///")));
                })
            .AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var disposal = resolver.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        release.SetResult();
        await replacement.WaitAsync(TimeSpan.FromSeconds(10));
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, resolver.Unloads.Count);
        Assert.All(resolver.Unloads, item => Assert.True(item.UnloadRequested));
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            resolver.ResolveAsync("fixture", new Uri("file:///")).AsTask());
        previous = null;
        return resolver.Unloads.ToArray();
    }

    [Fact]
    public async Task Failed_candidate_restores_current_entry_config_and_direct_fibers_then_collects()
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-clr-rollback-" + Guid.NewGuid().ToString("N"));
        var observations = await ExerciseFailedReplacement(shadow);
        for (var attempt = 0;attempt < 12 && observations.Any(item => !item.IsCollected);attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.All(observations, item => Assert.True(item.IsCollected, item.ShadowDirectory));
        Assert.All(observations, item => Assert.True(item.TryDeleteShadow(), item.ShadowDirectory));
        Directory.Delete(shadow);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClrUnloadObservation[]> ExerciseFailedReplacement(string shadow)
    {
        await using var context = new Context();
        await using var resolver = new ClrModuleResolver(shadow, [typeof(IVersionedService).Assembly]);
        await using var hmr = new HmrCoordinator();
        resolver.Register("fixture", Definition("v1"));
        var trace = new List<string>();
        Loader? loader = null;
        Context? isolated = null;
        await context.RunAsync(async ctx =>
        {
            ctx.Provide("trace", trace);
            loader = new Loader(ctx, resolver);
            await loader.CreateAsync(
                new()
                {
                    Id = "row",
                    Name = "fixture",
                    Config = "old-config"
                });
            await loader.WaitAsync();
            isolated = ctx.Isolate("versioned");
            await isolated
                .Plugin(await resolver.ResolveAsync("fixture", new Uri("file:///")), "direct-config")
                .WaitAsync();
            // Entry options are public mutable configuration; HMR must use their latest value,
            // even before that value has reached the old fiber's raw/running configuration.
            loader.Resolve("row").Options.Config = "latest-config";
            Assert.Equal("old-config", loader.Resolve("row").Fiber!.RawConfig);
        });
        using var tracking = hmr.TrackLoader(loader!, resolver.LocateAsync);
        var candidate = Definition("bad");
        var modulePath = Path.Combine(shadow, "deployed-plugin.dll");
        var reloaded = 0;
        hmr.Reloaded += _ => reloaded++;
        hmr.RegisterModule(
            modulePath,
            async () => await resolver.ReplaceAsync(
                "fixture",
                candidate,
                async (previous, replacement) => await loader!.ReplacePluginAsync(previous, replacement)));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => hmr.NotifyChangedAsync(modulePath));
        Assert.Equal(0, reloaded);
        Assert.Equal("bad candidate activation", failure.Message);
        var failed = Assert.Single(resolver.Unloads);
        Assert.True(failed.UnloadRequested);
        Assert.Equal(2, trace.Count(item => item == "stop:bad-v2"));
        // Both failed-candidate streams are closed before the callback failure escapes.
        var locks = Directory.GetFiles(failed.ShadowDirectory, "resource-*.lock");
        Assert.Equal(2, locks.Length);
        foreach (var filename in locks)
        {
            using var stream = File.Open(filename, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        await context.RunAsync(async ctx =>
        {
            Assert.Equal("v1", ctx.Get<IVersionedService>("versioned")!.Version);
            Assert.Equal("v1", isolated!.Get<IVersionedService>("versioned")!.Version);
            var entry = loader!.Resolve("row");
            Assert.Equal("latest-config", entry.Fiber!.RawConfig);
            Assert.Equal("latest-config", entry.Fiber.Config);
            Assert.False(entry.Disabled);
            Assert.Equal(
                2,
                ctx.Registry.Get(await resolver.ResolveAsync("fixture", new Uri("file:///")))!.Fibers.Count);
        });
        Assert.Equal(
            new[]
            {
                "start:v1:old-config",
                "start:v1:direct-config",
                "stop:v1",
                "stop:v1",
                "start:bad-v2:latest-config",
                "start:bad-v2:direct-config",
                "stop:bad-v2",
                "stop:bad-v2",
                "start:v1:latest-config",
                "start:v1:direct-config"
            },
            trace);
        candidate = Definition("v2");
        await hmr.NotifyChangedAsync(modulePath);
        Assert.Equal(1, reloaded);
        await context.RunAsync(ctx =>
        {
            Assert.Equal("v2", ctx.Get<IVersionedService>("versioned")!.Version);
            Assert.Equal("v2", isolated!.Get<IVersionedService>("versioned")!.Version);
            Assert.Equal("latest-config", loader!.Resolve("row").Fiber!.Config);
            return Task.CompletedTask;
        });
        await context.DisposeAsync();
        await resolver.DisposeAsync();
        return resolver.Unloads.ToArray();
    }

    [Fact]
    public async Task Real_bundles_replace_cleanup_share_contracts_and_collect()
    {
        var root = Path.Combine(Path.GetTempPath(), "cordis-clr-" + Guid.NewGuid().ToString("N"));
        var observations = await ExerciseReplacement(root);
        // Collection is a test observation, never a production success criterion. No arbitrary sleeps.
        for (var attempt = 0;attempt < 12 && observations.Any(item => !item.IsCollected);attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }

        Assert.All(observations, item => Assert.True(item.IsCollected, item.ShadowDirectory));
        Assert.All(observations, item => Assert.True(item.TryDeleteShadow(), item.ShadowDirectory));
        Assert.All(observations, item => Assert.True(item.ShadowDeleted));
        Directory.Delete(root);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<ClrUnloadObservation[]> ExerciseReplacement(string shadowRoot)
    {
        await using var context = new Context();
        await using var resolver = new ClrModuleResolver(shadowRoot, [typeof(IVersionedService).Assembly]);
        var v1 = Definition("v1");
        var v2 = Definition("v2");
        resolver.Register("fixture", v1);
        var trace = new List<string>();
        Loader? loader = null;
        Context? isolated = null;
        await context.RunAsync(async ctx =>
        {
            ctx.Provide("trace", trace);
            loader = new Loader(ctx, resolver);
            await loader.Root.UpdateAsync(
            [
                new EntryOptions
                {
                    Id = "row",
                    Name = "fixture",
                    Config = "raw-config"
                }
            ]);
            await loader.WaitAsync();
            Assert.Equal(FiberState.Active, loader.Resolve("row").Fiber!.State);
            var service = ctx.Get<IVersionedService>("versioned")!;
            Assert.Equal("v1", service.Version);
            Assert.Equal("shadow-copied-resource", service.Resource);
            Assert.Equal("private-bundle-dependency", service.Dependency);
            isolated = ctx.Isolate("versioned");
            await isolated
                .Plugin(await resolver.ResolveAsync("fixture", new Uri("file:///")), "direct-config")
                .WaitAsync();
            Assert.Equal("v1", isolated.Get<IVersionedService>("versioned")!.Version);
        });
        // Preparation errors do not touch running fibers.
        var previousFiber = loader!.Resolve("row").Fiber;
        var previousPlugin = await resolver.ResolveAsync("fixture", new Uri("file:///"));
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await resolver.ReplaceAsync(
            "fixture",
            v2 with
            {
                AssemblyPath = "missing.dll"
            },
            (_, _) => throw new InvalidOperationException("must not switch")));
        await Assert.ThrowsAsync<TypeLoadException>(async () => await resolver.ReplaceAsync(
            "fixture",
            v2 with
            {
                EntryType = "MissingEntry"
            },
            (_, _) => throw new InvalidOperationException("must not switch")));
        var invalid = await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ReplaceAsync(
            "fixture",
            v2 with
            {
                EntryType = "VersionedPlugin.InvalidEntry"
            },
            (_, _) => throw new InvalidOperationException("must not switch")));
        Assert.Contains("no plugin", invalid.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await resolver.ReplaceAsync(
            "fixture",
            v2,
            (_, _) => throw new InvalidOperationException("switch rejected before teardown")));
        Assert.Equal(new[] { "start:v1:raw-config", "start:v1:direct-config" }, trace);
        Assert.Same(previousFiber, loader.Resolve("row").Fiber);
        Assert.Same(previousPlugin, await resolver.ResolveAsync("fixture", new Uri("file:///")));
        previousFiber = null;
        previousPlugin = null;
        for (var index = 0;index < 3;index++)
        {
            var target = index % 2 == 0 ? v2 : v1;
            await resolver.ReplaceAsync(
                "fixture",
                target,
                async (previous, replacement) =>
                {
                    Assert.Same(previous, await resolver.ResolveAsync("fixture", new Uri("file:///")));
                    await loader!.ReplacePluginAsync(previous, replacement);
                    await context.RunAsync(async ctx =>
                    {
                        await loader.WaitAsync();
                        Assert.Equal("raw-config", loader.Resolve("row").Fiber!.RawConfig);
                        Assert.False(loader.Resolve("row").Disabled);
                        Assert.Equal(index % 2 == 0 ? "v2" : "v1", ctx.Get<IVersionedService>("versioned")!.Version);
                        Assert.Equal(
                            index % 2 == 0 ? "v2" : "v1",
                            isolated!.Get<IVersionedService>("versioned")!.Version);
                    });
                });
        }

        await context.DisposeAsync();
        loader = null;
        isolated = null;
        await resolver.DisposeAsync();
        Assert.Equal(
            new[]
            {
                "start:v1:raw-config",
                "start:v1:direct-config",
                "stop:v1",
                "stop:v1",
                "start:v2:raw-config",
                "start:v2:direct-config",
                "stop:v2",
                "stop:v2",
                "start:v1:raw-config",
                "start:v1:direct-config",
                "stop:v1",
                "stop:v1",
                "start:v2:raw-config",
                "start:v2:direct-config",
                "stop:v2",
                "stop:v2"
            },
            trace);
        Assert.All(resolver.Unloads, item => Assert.True(item.UnloadRequested));
        return resolver.Unloads.ToArray();
    }

    private static ClrModuleDefinition Definition(string version) =>
        new(
            Path.Combine(AppContext.BaseDirectory, "fixtures", version),
            "VersionedPlugin.dll",
            "VersionedPlugin.Entry");

    [Fact]
    public async Task Explicit_map_does_not_discover_or_accept_external_entry_paths()
    {
        var shadow = Path.Combine(Path.GetTempPath(), "cordis-map-" + Guid.NewGuid().ToString("N"));
        await using var resolver = new ClrModuleResolver(shadow);
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await resolver.ResolveAsync("unknown", new Uri("file:///")));
        await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
            await resolver.LocateAsync("unknown", new Uri("file:///")));
        resolver.Register(
            "outside",
            Definition("v1") with
            {
                AssemblyPath = "../outside.dll"
            });
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync("outside", new Uri("file:///")));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.LocateAsync("outside", new Uri("file:///")));
        Assert.False(Directory.Exists(shadow));
    }

    [Fact]
    public async Task Shadow_directory_cannot_be_nested_inside_source_bundle()
    {
        var definition = Definition("v1");
        await using var resolver = new ClrModuleResolver(Path.Combine(definition.BundleDirectory, "shadows"));
        resolver.Register("fixture", definition);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await resolver.ResolveAsync("fixture", new Uri("file:///")));
        Assert.False(Directory.Exists(Path.Combine(definition.BundleDirectory, "shadows")));
    }
}
