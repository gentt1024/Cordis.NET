using Cordis;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class VolatileEntryTests
{
    private static EntryOptions Config(int limit, string ordinary = "unchanged") =>
        new()
        {
            ["limit"] = limit,
            ["ordinary"] = ordinary
        };

    private static Plugin<EntryOptions> Plugin(
        Action<Context, EntryOptions> apply,
        Func<object?, ConfigResult<EntryOptions>>? validate = null)
    {
        validate ??= raw => raw is EntryOptions config && Convert.ToInt32(config["limit"]) > 0
            ? ConfigResult<EntryOptions>.Success(config)
            : ConfigResult<EntryOptions>.Failure("positive limit required");
        var schema = new ConfigSchema<EntryOptions>(
            validate,
            ConfigDescriptor.Object(
                ("limit", ConfigDescriptor.Number().Volatile()),
                ("ordinary", ConfigDescriptor.String()))).WithVolatile(
            "limit",
            value => Convert.ToInt32(value["limit"]));
        return new()
        {
            Configuration = schema,
            Apply = apply
        };
    }

    [Fact]
    public async Task VolatileUpdateKeepsActivationAndCommitsBeforeOwnerNotification()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int applies = 0, disposals = 0, configHooks = 0, updateHooks = 0, notices = 0, unrelated = 0;
            ConfigReference<int>? reference = null;
            var plugin = Plugin((owner, _) =>
            {
                applies++;
                reference ??= owner.Fiber.GetConfigReference<int>("limit");
                owner.Effect(() => (Action)(() => disposals++));
                owner.On(
                    "internal/config",
                    (evt, _) =>
                    {
                        configHooks++;
                        return evt.Next();
                    });
                owner.On(
                    "internal/update",
                    (evt, _) =>
                    {
                        updateHooks++;
                        return evt.Next();
                    });
                owner.On(
                    "loader/volatile-update",
                    (evt, args) =>
                    {
                        notices++;
                        Assert.Equal(2, reference.Value);
                        Assert.IsType<Context>(evt.Receiver);
                        Assert.NotEmpty(Assert.IsAssignableFrom<IEnumerable<string>>(args[0]));
                        return null;
                    });
            });
            ctx.On(
                "loader/volatile-update",
                (_, _) =>
                {
                    unrelated++;
                    return null;
                });
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = Config(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var fiber = entry.Fiber;
            var context = entry.Context;
            var effective = fiber!.Config;
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(2)
                });
            await loader.WaitAsync();
            Assert.Same(fiber, entry.Fiber);
            Assert.Same(context, entry.Context);
            Assert.Same(effective, fiber.Config);
            Assert.Same(reference, fiber.GetConfigReference<int>("limit"));
            Assert.Equal(1, applies);
            Assert.Equal(0, disposals);
            Assert.Equal(0, updateHooks);
            Assert.Equal(1, configHooks);
            Assert.Equal(1, notices);
            Assert.Equal(0, unrelated);
            Assert.Equal(2, Assert.IsType<EntryOptions>(entry.Options.Config)["limit"]);
            Assert.Same(entry.Options.Config, fiber.RawConfig);
        });
    }

    [Fact]
    public async Task RejectedVolatileInputRetainsRawAndRunningReferences()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int applies = 0;
            var plugin = Plugin((_, _) => applies++);
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = Config(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var fiber = entry.Fiber!;
            var effective = fiber.Config;
            var reference = fiber.GetConfigReference<int>("limit");
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(-1)
                });
            Assert.Equal(FiberState.Active, fiber.State);
            Assert.Same(effective, fiber.Config);
            Assert.Equal(1, reference.Value);
            Assert.Equal(1, applies);
            Assert.Equal(-1, Assert.IsType<EntryOptions>(fiber.RawConfig)["limit"]);
            Assert.Equal(-1, Assert.IsType<EntryOptions>(entry.Options.Config)["limit"]);
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(3)
                });
            Assert.Equal(3, reference.Value);
            Assert.Equal(1, applies);
        });
    }

    [Fact]
    public async Task MixedUpdateRestartsAndDoesNotRewriteTheOldActivationReference()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var references = new List<ConfigReference<int>>();
            var plugin = Plugin((owner, _) => references.Add(owner.Fiber.GetConfigReference<int>("limit")));
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = Config(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(2)
                });
            Assert.Equal(2, references[0].Value);
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(3, "changed")
                });
            await loader.WaitAsync();
            Assert.Equal(2, references.Count);
            Assert.NotSame(references[0], references[1]);
            Assert.Equal(2, references[0].Value);
            Assert.Equal(3, references[1].Value);
        });
    }

    [Fact]
    public async Task DirectNoSaveUpdateRemainsAnOrdinaryRestartAndUnchangedEntryDoesNotReplaceItsRawInput()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var references = new List<ConfigReference<int>>();
            var plugin = Plugin((owner, _) => references.Add(owner.Fiber.GetConfigReference<int>("limit")));
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = Config(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var fiber = entry.Fiber!;
            var saved = entry.Options.Config;
            var transient = Config(2);
            fiber.Update(transient, noSave: true);
            await loader.WaitAsync();
            Assert.Equal(2, references.Count);
            Assert.Same(saved, entry.Options.Config);
            Assert.Equal(1, references[0].Value);
            Assert.Equal(2, references[1].Value);
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(1)
                },
                force: true);
            Assert.Same(transient, fiber.RawConfig);
            Assert.Equal(2, references[1].Value);
            Assert.Equal(2, references.Count);
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(3)
                });
            Assert.Equal(3, references[1].Value);
            Assert.Equal(1, references[0].Value);
        });
    }

    [Fact]
    public async Task ForcedLiveUpdateRetainsPartialDisposeAndContextWaterfallWithoutRestart()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int applies = 0, partial = 0, patches = 0;
            var plugin = Plugin((owner, _) =>
            {
                applies++;
                owner.On(
                    "loader/partial-dispose",
                    (_, _) =>
                    {
                        partial++;
                        return null;
                    });
                owner.On(
                    "loader/patch-context",
                    (evt, _) =>
                    {
                        patches++;
                        return evt.Next();
                    });
                owner.On(
                    "loader/volatile-update",
                    (_, _) => throw new InvalidOperationException("listener failed after publication"));
            });
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = Config(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var reference = entry.Fiber!.GetConfigReference<int>("limit");
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(2)
                },
                force: true);
            Assert.Equal(2, reference.Value);
            Assert.Equal(1, applies);
            Assert.Equal(1, partial);
            Assert.Equal(1, patches);
        });
    }

    [Fact]
    public async Task VolatileRawChangeWithNewOrdinaryEffectiveIdentityFallsBackToTheOriginalLifecycle()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int applies = 0;
            Func<object?, ConfigResult<EntryOptions>> validator = raw =>
            {
                var config = new EntryOptions(Assert.IsType<EntryOptions>(raw))
                {
                    ["opaque"] = new object()
                };
                return ConfigResult<EntryOptions>.Success(config);
            };
            var plugin = Plugin((_, _) => applies++, validator);
            var loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin));
            await loader.Root.UpdateAsync(
            [
                new()
                {
                    Id = "p",
                    Name = "plugin",
                    Config = Config(1)
                }
            ]);
            await loader.WaitAsync();
            var entry = loader.Resolve("p");
            var old = entry.Fiber!.GetConfigReference<int>("limit");
            await entry.UpdateAsync(
                new()
                {
                    Config = Config(2)
                });
            await loader.WaitAsync();
            Assert.Equal(2, applies);
            Assert.Equal(1, old.Value);
            Assert.Equal(2, entry.Fiber!.GetConfigReference<int>("limit").Value);
        });
    }

    [Fact]
    public async Task LiteralIncludeRefreshAndProfileReconcileUseTheExistingVolatileEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cordis-volatile-include-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var filename = Path.Combine(directory, "cordis.yml");
        try
        {
            await File.WriteAllTextAsync(
                filename,
                "- id: p\n  name: plugin\n  config: { limit: 1, ordinary: unchanged }\n");
            await using var root = new Context();
            Loader? loader = null;
            ConfigReference<int>? reference = null;
            int applies = 0;
            await root.RunAsync(ctx =>
            {
                var plugin = Plugin(
                    (owner, _) =>
                    {
                        applies++;
                        reference = owner.Fiber.GetConfigReference<int>("limit");
                    },
                    raw => raw is IDictionary<string, object?> map
                        ? ConfigResult<EntryOptions>.Success(raw as EntryOptions ?? new EntryOptions(map))
                        : ConfigResult<EntryOptions>.Failure("object required"));
                loader = new Loader(ctx, new StaticModuleResolver().Register("plugin", plugin), new Uri(filename));
                return Task.CompletedTask;
            });
            var include = await ApplicationBoot.MountAsync(loader!, filename);
            var oldReference = reference;
            Entry? entry = null;
            Fiber? fiber = null;
            await root.RunAsync(_ =>
            {
                entry = loader!.Resolve("root:p");
                fiber = entry.Fiber;
                return Task.CompletedTask;
            });
            await File.WriteAllTextAsync(
                filename,
                "- id: p\n  name: plugin\n  config: { limit: 2, ordinary: unchanged }\n");
            await root.RunAsync(async _ =>
            {
                await include.RefreshAsync();
                await loader!.WaitAsync();
            });
            Assert.Same(oldReference, reference);
            Assert.Equal(2, reference!.Value);
            Assert.Equal(1, applies);
            var diagnostics = await ApplicationBoot.ReconcileAsync(
                include,
                [
                    new()
                    {
                        Id = "p",
                        Config = Config(3)
                    }
                ]);
            Assert.Empty(diagnostics);
            Assert.Same(fiber, entry!.Fiber);
            Assert.Same(oldReference, reference);
            Assert.Equal(3, reference.Value);
            Assert.Equal(1, applies);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
