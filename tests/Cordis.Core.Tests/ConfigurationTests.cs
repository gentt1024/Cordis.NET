using Xunit;

namespace Cordis.Core.Tests;

public sealed class ConfigurationTests
{
    private sealed record LazySettings(object? Nested);

    [Fact]
    public async Task LazyPlacementIsCheckedOnTheValidatorsActualInputBeforePocoConversion()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var schema = new ConfigSchema<LazySettings>(
                raw =>
                    ConfigResult<LazySettings>.Success(
                        new LazySettings(((IReadOnlyDictionary<string, object?>)raw!)["nested"])),
                ConfigDescriptor.Object(
                    ("nested",
                        ConfigDescriptor.Lazy(() =>
                            ConfigDescriptor.Object(("live", ConfigDescriptor.Number().Volatile()))))));
            var fiber = ctx.Plugin(
                new Plugin<LazySettings>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                new Dictionary<string, object?>
                {
                    ["nested"] = new Dictionary<string, object?>
                    {
                        ["live"] = 1
                    }
                });
            await Assert.ThrowsAsync<ConfigurationValidationException>(() => fiber.WaitAsync());
            Assert.Equal("configuration", fiber.FailurePhase);
        });
    }

    [Fact]
    public async Task TypedLazyInputUsesExplicitDescriptionDataWithoutPersistenceOrReflection()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var projections = 0;
            var builds = 0;
            var descriptor = ConfigDescriptor.Object(
                ("nested", ConfigDescriptor.Lazy(() =>
                {
                    builds++;
                    return ConfigDescriptor.Number();
                })));
            var schema = new ConfigSchema<LazySettings>(
                    raw => ConfigResult<LazySettings>.Success((LazySettings)raw!),
                    descriptor)
                .WithDescriptionData(value =>
                {
                    projections++;
                    return new Dictionary<string, object?>
                    {
                        ["nested"] = value.Nested
                    };
                })
                .WithSimplify(_ => throw new InvalidOperationException("Persistence must not run during resolution."));
            Assert.False(descriptor.IsVolatileOnly(new LazySettings(1), new LazySettings(2)));
            Assert.Equal(0, projections);
            var fiber = ctx.Plugin(
                new Plugin<LazySettings>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                new LazySettings(1));
            await fiber.WaitAsync();
            Assert.Equal(FiberState.Active, fiber.State);
            Assert.Equal(1, builds);
            Assert.Equal(1, projections);
            var unprojected = ctx.Plugin(
                new Plugin<LazySettings>
                {
                    Configuration = new(raw => ConfigResult<LazySettings>.Success((LazySettings)raw!), descriptor),
                    Apply = (_, _) =>
                    {
                    }
                },
                new LazySettings(1));
            await Assert.ThrowsAsync<ConfigurationValidationException>(() => unprojected.WaitAsync());
        });
    }

    [Fact]
    public async Task LazyUnionSelectsOnlyTheValidatorsDeclaredBranch()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var selections = 0;
            var branches = new[]
            {
                ConfigDescriptor.Number(),
                ConfigDescriptor.Lazy(() => throw new InvalidOperationException("Unused union branch."))
            };
            var descriptor = ConfigDescriptor.Union(
                _ =>
                {
                    selections++;
                    return 0;
                },
                branches);
            Assert.False(descriptor.IsVolatileOnly(1, 2));
            Assert.Equal(0, selections);
            Assert.Throws<InvalidOperationException>(() => descriptor.Serialize());
            Assert.Equal(0, selections);
            var fiber = ctx.Plugin(
                new Plugin<int>
                {
                    Configuration = new(raw => ConfigResult<int>.Success((int)raw!), descriptor),
                    Apply = (_, _) =>
                    {
                    }
                },
                1);
            await fiber.WaitAsync();
            Assert.Equal(FiberState.Active, fiber.State);
            Assert.Equal(1, selections);
            var invalid = ctx.Plugin(
                new Plugin<int>
                {
                    Configuration = new(
                        raw => ConfigResult<int>.Success((int)raw!),
                        ConfigDescriptor.Union(_ => -1, branches)),
                    Apply = (_, _) =>
                    {
                    }
                },
                1);
            await Assert.ThrowsAsync<ConfigurationValidationException>(() => invalid.WaitAsync());
            var ambiguous = ctx.Plugin(
                new Plugin<int>
                {
                    Configuration = new(raw => ConfigResult<int>.Success((int)raw!), ConfigDescriptor.Union(branches)),
                    Apply = (_, _) =>
                    {
                    }
                },
                1);
            await Assert.ThrowsAsync<ConfigurationValidationException>(() => ambiguous.WaitAsync());
        });
    }

    private sealed record Settings(int Limit, object? Payload);

    [Fact]
    public async Task CapturedValidatorAndStableReferencesCommitTogetherWithoutReapply()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int validations = 0, applies = 0;
            Func<object?, ConfigResult<Settings>> validate = raw =>
            {
                validations++;
                return raw is Settings settings && settings.Limit > 0
                    ? ConfigResult<Settings>.Success(settings)
                    : ConfigResult<Settings>.Failure("positive limit required");
            };
            var schema =
                new ConfigSchema<Settings>(
                        validate,
                        ConfigDescriptor.Object(
                            ("Limit", ConfigDescriptor.Number().Volatile()),
                            ("Payload", ConfigDescriptor.Any())))
                    .WithVolatile("Limit", settings => settings.Limit)
                    .WithOrdinaryEquality((left, right) => ReferenceEquals(left.Payload, right.Payload));
            var plugin = new Plugin<Settings>
            {
                Config = validate,
                Configuration = schema,
                Apply = (_, _) => applies++
            };
            var payload = new object();
            var fiber = ctx.Plugin(plugin, new Settings(1, payload));
            await fiber.WaitAsync();
            var reference = fiber.GetConfigReference<int>("Limit");
            Assert.Equal(1, reference.Value);
            Assert.True(fiber.TryPrepareConfigurationUpdate(new Settings(2, payload), out var candidate));
            Assert.Equal(1, reference.Value);
            Assert.Equal(2, validations);
            Assert.True(candidate!.Commit());
            Assert.Equal(2, reference.Value);
            Assert.Same(reference, fiber.GetConfigReference<int>("Limit"));
            Assert.Equal(1, ((Settings)fiber.Config!).Limit);
            Assert.Equal(1, applies);
            Assert.Throws<ConfigurationValidationException>(() =>
                fiber.TryPrepareConfigurationUpdate(new Settings(-1, payload), out _));
            Assert.Equal(2, reference.Value);
        });
    }

    [Fact]
    public async Task DescriptionCannotSelectADifferentValidatorAndOpaqueOrdinaryConfigStillWorks()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var opaque = new object();
            var fiber = ctx.Plugin(
                new Plugin<object>
                {
                    Apply = (_, value) => Assert.Same(opaque, value)
                },
                opaque);
            await fiber.WaitAsync();
            var schema = new ConfigSchema<int>(_ => ConfigResult<int>.Success(1), ConfigDescriptor.Number());
            Assert.Throws<ArgumentException>(() => ctx.Plugin(
                new Plugin<int>
                {
                    Config = _ => ConfigResult<int>.Success(2),
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                }));
        });
    }

    [Fact]
    public async Task LazyRecursiveDescriptionIsCapturedOnceAndRawComparisonDoesNotValidate()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int lazyCalls = 0, validations = 0;
            ConfigDescriptor? recursive = null;
            recursive = ConfigDescriptor.Lazy(() =>
            {
                lazyCalls++;
                return ConfigDescriptor.Object(
                    ("limit", ConfigDescriptor.Number().Default(10)),
                    ("child", recursive!.Optional()));
            });
            var schema = new ConfigSchema<object?>(
                raw =>
                {
                    validations++;
                    return ConfigResult<object?>.Success(raw);
                },
                recursive);
            var fiber = ctx.Plugin(
                new Plugin<object?>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                new Dictionary<string, object?>());
            await fiber.WaitAsync();
            var descriptor = fiber.ConfigDescription!;
            Assert.False(
                descriptor.IsVolatileOnly(
                    new Dictionary<string, object?>(),
                    new Dictionary<string, object?>
                    {
                        ["limit"] = 20
                    }));
            Assert.False(
                descriptor.IsVolatileOnly(
                    new Dictionary<string, object?>(),
                    new Dictionary<string, object?>
                    {
                        ["unknown"] = 20
                    }));
            Assert.Equal(1, lazyCalls);
            Assert.Equal(1, validations);
            Assert.Empty(
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                    descriptor.Simplify(
                        new Dictionary<string, object?>
                        {
                            ["limit"] = 10
                        })));
        });
    }

    [Fact]
    public async Task SnapshotIsDetachedAndCandidateIsSingleUseWithoutUpdateHooks()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var schema = new ConfigSchema<object?>(
                raw => ConfigResult<object?>.Success(raw),
                ConfigDescriptor.Any().Volatile()).WithVolatileValue();
            var input = new Dictionary<string, object?>
            {
                ["list"] = new object?[] { 1, double.NaN }
            };
            var fiber = ctx.Plugin(
                new Plugin<object?>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                input);
            await fiber.WaitAsync();
            var reference = fiber.GetConfigReference<object?>();
            input["list"] = "mutated";
            var snapshot = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(reference.Value);
            Assert.IsAssignableFrom<IReadOnlyList<object?>>(snapshot["list"]);
            var cycle = new Dictionary<string, object?>();
            cycle["self"] = cycle;
            Assert.Throws<ConfigurationValidationException>(() => fiber.TryPrepareConfigurationUpdate(cycle, out _));
            Assert.True(fiber.TryPrepareConfigurationUpdate(2, out var candidate));
            int updates = 0;
            await using var veto = fiber.Context.On(
                "internal/update",
                (_, _) =>
                {
                    updates++;
                    return false;
                });
            Assert.True(candidate!.Commit());
            Assert.Equal(2, reference.Value);
            Assert.Equal(0, updates);
            Assert.Throws<InvalidOperationException>(() =>
            {
                candidate.Commit();
            });
        });
    }

    [Fact]
    public async Task AllFieldsCommitTogetherAndAStaleCandidateCannotOverwriteNewerValues()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var schema = new ConfigSchema<IReadOnlyDictionary<string, object?>>(
                    raw =>
                        ConfigResult<IReadOnlyDictionary<string, object?>>.Success(
                            (IReadOnlyDictionary<string, object?>)raw!),
                    ConfigDescriptor.Object(
                        ("a", ConfigDescriptor.Number().Volatile()),
                        ("b", ConfigDescriptor.Number().Volatile())))
                .WithVolatile("a", value => (int)value["a"]!)
                .WithVolatile("b", value => (int)value["b"]!);
            var fiber = ctx.Plugin(
                new Plugin<IReadOnlyDictionary<string, object?>>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                new Dictionary<string, object?>
                {
                    ["a"] = 1,
                    ["b"] = 2
                });
            await fiber.WaitAsync();
            var a = fiber.GetConfigReference<int>("a");
            var b = fiber.GetConfigReference<int>("b");
            Assert.True(
                fiber.TryPrepareConfigurationUpdate(
                    new Dictionary<string, object?>
                    {
                        ["a"] = 3,
                        ["b"] = 4
                    },
                    out var stale));
            Assert.True(
                fiber.TryPrepareConfigurationUpdate(
                    new Dictionary<string, object?>
                    {
                        ["a"] = 5,
                        ["b"] = 6
                    },
                    out var current));
            Assert.Equal(["a", "b"], current!.ChangedPaths);
            Assert.Equal(1, a.Value);
            Assert.Equal(2, b.Value);
            Assert.True(current.Commit());
            Assert.Equal(5, a.Value);
            Assert.Equal(6, b.Value);
            Assert.False(stale!.Commit());
            Assert.Equal(5, a.Value);
            Assert.Equal(6, b.Value);
        });
    }

    [Fact]
    public void RawDiffDescendsOnlyObjectDefaultsAndKeepsScalarArrayAndUnknownValuesStrict()
    {
        var shared = ConfigDescriptor
            .Object(("live", ConfigDescriptor.Number().Volatile()))
            .Default(
                new Dictionary<string, object?>
                {
                    ["live"] = 1
                });
        var descriptor = ConfigDescriptor.Object(
            ("child", shared.Alias()),
            ("scalar", ConfigDescriptor.Number().Default(10)),
            ("array", ConfigDescriptor.Array(ConfigDescriptor.Number().Volatile())));
        var empty = new Dictionary<string, object?>();
        Assert.True(
            descriptor.IsVolatileOnly(
                empty,
                new Dictionary<string, object?>
                {
                    ["child"] = new Dictionary<string, object?>
                    {
                        ["live"] = 2
                    }
                }));
        Assert.True(
            descriptor.IsVolatileOnly(
                new Dictionary<string, object?>
                {
                    ["child"] = null
                },
                new Dictionary<string, object?>
                {
                    ["child"] = new Dictionary<string, object?>
                    {
                        ["live"] = 2
                    }
                }));
        Assert.False(
            descriptor.IsVolatileOnly(
                empty,
                new Dictionary<string, object?>
                {
                    ["scalar"] = 10
                }));
        Assert.False(
            descriptor.IsVolatileOnly(
                new Dictionary<string, object?>
                {
                    ["array"] = new object?[] { 1 }
                },
                new Dictionary<string, object?>
                {
                    ["array"] = new object?[] { 2 }
                }));
        Assert.False(
            descriptor.IsVolatileOnly(
                empty,
                new Dictionary<string, object?>
                {
                    ["unknown"] = 1
                }));
    }

    [Theory]
    [InlineData("array")]
    [InlineData("lazy")]
    [InlineData("enclosing")]
    public async Task SharedVolatileNodeCannotEscapeBlockedPlacement(string placement)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var shared = ConfigDescriptor.Number().Volatile();
            var blocked = placement switch
            {
                "array" => ConfigDescriptor.Array(shared),
                "lazy" => ConfigDescriptor.Lazy(() => shared),
                _ => ConfigDescriptor.Object(("nested", shared)).Volatile()
            };
            var schema = new ConfigSchema<object?>(
                raw => ConfigResult<object?>.Success(raw),
                ConfigDescriptor.Object(("fixed", shared), ("blocked", blocked))).WithVolatile("fixed", _ => 1);
            var plugin = new Plugin<object?>
            {
                Configuration = schema,
                Apply = (_, _) => Assert.Fail("Blocked schema must not apply.")
            };
            if (placement != "lazy")
            {
                Assert.Throws<ConfigurationValidationException>(() => ctx.Plugin(
                    plugin,
                    new Dictionary<string, object?>()));
                return;
            }

            var fiber = ctx.Plugin(
                plugin,
                new Dictionary<string, object?>
                {
                    ["blocked"] = 1
                });
            await Assert.ThrowsAsync<ConfigurationValidationException>(() => fiber.WaitAsync());
            Assert.Equal("configuration", fiber.FailurePhase);
        });
    }

    [Fact]
    public async Task CaptureDoesNotInvokeLazyAndTypedSimplificationUsesExplicitProjection()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int lazy = 0;
            var schema = new ConfigSchema<Settings>(
                raw => ConfigResult<Settings>.Success((Settings)raw!),
                ConfigDescriptor.Lazy(() =>
                {
                    lazy++;
                    return ConfigDescriptor.Object(("limit", ConfigDescriptor.Number().Default(10)));
                })).WithSimplify(value => value.Limit == 10
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?>
                {
                    ["limit"] = value.Limit
                });
            var fiber = ctx.Plugin(
                new Plugin<Settings>
                {
                    Inject = ["missing"],
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                new Settings(10, null));
            Assert.Equal(0, lazy);
            Assert.False(
                fiber.ConfigDescription!.IsVolatileOnly(
                    new Dictionary<string, object?>
                    {
                        ["limit"] = 1
                    },
                    new Dictionary<string, object?>
                    {
                        ["limit"] = 2
                    }));
            Assert.Equal(0, lazy);
            ctx.Provide("missing", new object());
            await fiber.WaitAsync();
            Assert.Equal(1, lazy);
            Assert.Empty(
                Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(
                    fiber.SimplifyConfiguration(fiber.Config)));
        });
    }

    [Fact]
    public async Task SnapshotsDetachSharedBranchesAndRejectFunctionsAndCyclesBeforePublication()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var schema = new ConfigSchema<object?>(
                raw => ConfigResult<object?>.Success(raw),
                ConfigDescriptor.Any().Volatile()).WithVolatileValue();
            var shared = new Dictionary<string, object?>
            {
                ["n"] = 1
            };
            var fiber = ctx.Plugin(
                new Plugin<object?>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                new Dictionary<string, object?>
                {
                    ["a"] = shared,
                    ["b"] = shared
                });
            await fiber.WaitAsync();
            var reference = fiber.GetConfigReference<object?>();
            var snapshot = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(reference.Value);
            Assert.NotSame(snapshot["a"], snapshot["b"]);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)snapshot)["a"] = 2);
            var branch = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(snapshot["a"]);
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)branch)["n"] = 2);
            Assert.Throws<ConfigurationValidationException>(() => fiber.TryPrepareConfigurationUpdate(
                (Action)(() =>
                {
                }),
                out _));
            Assert.Same(snapshot, reference.Value);
        });
    }

    [Fact]
    public async Task DescriptionSerializationRetainsSharingRecursionAndMetadata()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            ConfigDescriptor? recursive = null;
            var shared = ConfigDescriptor.Number().Optional().Default(double.NaN);
            recursive = ConfigDescriptor.Lazy(() => ConfigDescriptor.Object(
                ("a", shared),
                ("b", shared.Alias()),
                ("child", recursive!)));
            var fiber = ctx.Plugin(
                new Plugin<object?>
                {
                    Configuration = new(raw => ConfigResult<object?>.Success(raw), recursive),
                    Apply = (_, _) =>
                    {
                    }
                },
                new Dictionary<string, object?>());
            await fiber.WaitAsync();
            var recovered = ConfigDescriptor.Deserialize(fiber.ConfigDescription!.Serialize());
            Assert.Same(recovered.Inner!.Properties["a"], recovered.Inner.Properties["b"]);
            Assert.Same(recovered, recovered.Inner.Properties["child"]);
            Assert.True(recovered.Inner.Properties["a"].IsOptional);
            Assert.True(double.IsNaN(Assert.IsType<double>(recovered.Inner.Properties["a"].DefaultValue)));
            var marked = ConfigDescriptor.Deserialize(ConfigDescriptor.Number().Required().Volatile().Serialize());
            Assert.True(marked.IsVolatile);
            Assert.False(marked.IsOptional);
        });
    }

    [Fact]
    public void StrictComparisonPreservesNaNMissingUndefinedOpaqueAndCyclicSemantics()
    {
        object nan = double.NaN;
        Assert.False(ConfigDescriptor.StrictEquals(nan, nan));
        Assert.True(ConfigDescriptor.StrictEquals(0d, -0d));
        Assert.True(ConfigDescriptor.StrictEquals(1, 1d));
        Assert.True(
            ConfigDescriptor.StrictEquals(
                new Dictionary<string, object?>(),
                new Dictionary<string, object?>
                {
                    ["absent"] = Undefined.Value
                }));
        Assert.False(ConfigDescriptor.StrictEquals(null, Undefined.Value));
        var opaque = new object();
        Assert.True(
            ConfigDescriptor.StrictEquals(
                new Dictionary<string, object?>
                {
                    ["opaque"] = opaque
                },
                new Dictionary<string, object?>
                {
                    ["opaque"] = opaque
                }));
        Assert.False(ConfigDescriptor.StrictEquals(new object(), new object()));
        var a = new Dictionary<string, object?>();
        var b = new Dictionary<string, object?>();
        a["cycle"] = a;
        b["cycle"] = b;
        Assert.True(ConfigDescriptor.StrictEquals(a, a));
        Assert.False(ConfigDescriptor.StrictEquals(a, b));
        Assert.True(
            ConfigDescriptor.StrictEquals(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        Assert.True(
            ConfigDescriptor.StrictEquals(new Uri("https://example.com/path"), new Uri("https://example.com/path")));
        Assert.True(
            ConfigDescriptor.StrictEquals(
                new System.Text.RegularExpressions.Regex("x", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                new System.Text.RegularExpressions.Regex("x", System.Text.RegularExpressions.RegexOptions.IgnoreCase)));
        Assert.True(ConfigDescriptor.StrictEquals(new byte[] { 1, 2 }, new ReadOnlyMemory<byte>(new byte[] { 1, 2 })));
    }

    [Fact]
    public void ContainerDescriptionsRetainShapeAndDoNotSelectUnionOrTransformBranches()
    {
        var number = ConfigDescriptor.Number().Required();
        var descriptor = ConfigDescriptor.Object(
            ("tuple", ConfigDescriptor.Tuple(number, number.Alias())),
            ("dict", ConfigDescriptor.Dict(number, ConfigDescriptor.String())),
            ("union", ConfigDescriptor.Union(number, ConfigDescriptor.String().Optional())),
            ("intersect", ConfigDescriptor.Intersect(ConfigDescriptor.Object(("n", number)))),
            ("transform", ConfigDescriptor.Transform(number)),
            ("getter", ConfigDescriptor.Getter(number)));
        var copy = ConfigDescriptor.Deserialize(descriptor.Serialize());
        Assert.Same(copy.Properties["tuple"].Children[0], copy.Properties["tuple"].Children[1]);
        Assert.Same(copy.Properties["dict"].Inner, copy.Properties["transform"].Inner);
        Assert.Same(copy.Properties["dict"].Inner, copy.Properties["getter"].Inner);
        Assert.Equal("string", copy.Properties["dict"].Key!.Kind);
        Assert.Equal("union", copy.Properties["union"].Kind);
        Assert.False(
            copy.IsVolatileOnly(
                new Dictionary<string, object?>
                {
                    ["tuple"] = new object?[] { 1, 2 }
                },
                new Dictionary<string, object?>
                {
                    ["tuple"] = new object?[] { 1, 3 }
                }));
        Assert.Throws<InvalidOperationException>(() => copy.Properties["union"].Simplify(1));
    }

    [Fact]
    public async Task DeclaredVolatileFieldsAndExplicitProjectionsMustMatchCompletely()
    {
        await using var root = new Context();
        await root.RunAsync(ctx =>
        {
            var described = ConfigDescriptor.Object(
                ("a", ConfigDescriptor.Number().Volatile()),
                ("b", ConfigDescriptor.Number().Volatile()));
            var missing = new ConfigSchema<object?>(value => ConfigResult<object?>.Success(value), described)
                .WithVolatile("a", _ => 1);
            Assert.Throws<ConfigurationValidationException>(() => ctx.Plugin(
                new Plugin<object?>
                {
                    Configuration = missing,
                    Apply = (_, _) =>
                    {
                    }
                }));
            var ordinary = new ConfigSchema<object?>(
                value => ConfigResult<object?>.Success(value),
                ConfigDescriptor.Object(("a", ConfigDescriptor.Number()))).WithVolatile("a", _ => 1);
            Assert.Throws<ConfigurationValidationException>(() => ctx.Plugin(
                new Plugin<object?>
                {
                    Configuration = ordinary,
                    Apply = (_, _) =>
                    {
                    }
                }));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RootWholeValueReferenceUpdatesAndOldActivationRemainsFinal()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var schema = new ConfigSchema<int>(
                value => ConfigResult<int>.Success((int)value!),
                ConfigDescriptor.Number().Volatile()).WithVolatileValue();
            var fiber = ctx.Plugin(
                new Plugin<int>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                1);
            await fiber.WaitAsync();
            var old = fiber.GetConfigReference<int>();
            Assert.True(fiber.TryPrepareConfigurationUpdate(2, out var candidate));
            Assert.Equal([""], candidate!.ChangedPaths);
            Assert.True(candidate.Commit());
            Assert.Equal(2, old.Value);
            fiber.Update(3);
            await fiber.WaitAsync();
            Assert.Equal(2, old.Value);
            Assert.Equal(3, fiber.GetConfigReference<int>().Value);
            Assert.NotSame(old, fiber.GetConfigReference<int>());
        });
    }

    [Fact]
    public async Task EqualSnapshotKeepsValueIdentityWhileAnotherFieldCommits()
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            int validations = 0;
            var schema = new ConfigSchema<IReadOnlyDictionary<string, object?>>(
                    raw =>
                    {
                        validations++;
                        return ConfigResult<IReadOnlyDictionary<string, object?>>.Success(
                            (IReadOnlyDictionary<string, object?>)raw!);
                    },
                    ConfigDescriptor.Object(
                        ("map", ConfigDescriptor.Any().Volatile()),
                        ("number", ConfigDescriptor.Number().Volatile())))
                .WithVolatile("map", value => value["map"])
                .WithVolatile("number", value => (int)value["number"]!);
            var fiber = ctx.Plugin(
                new Plugin<IReadOnlyDictionary<string, object?>>
                {
                    Configuration = schema,
                    Apply = (_, _) =>
                    {
                    }
                },
                new Dictionary<string, object?>
                {
                    ["map"] = new Dictionary<string, object?>
                    {
                        ["n"] = 1
                    },
                    ["number"] = 1
                });
            await fiber.WaitAsync();
            var map = fiber.GetConfigReference<object?>("map");
            var number = fiber.GetConfigReference<int>("number");
            var originalSnapshot = map.Value;
            Assert.True(
                fiber.TryPrepareConfigurationUpdate(
                    new Dictionary<string, object?>
                    {
                        ["map"] = new Dictionary<string, object?>
                        {
                            ["n"] = 1
                        },
                        ["number"] = 2
                    },
                    out var candidate));
            Assert.Equal(2, validations);
            Assert.Same(originalSnapshot, map.Value);
            Assert.Equal(1, number.Value);
            Assert.Equal(["number"], candidate!.ChangedPaths);
            Assert.True(candidate.Commit());
            Assert.Equal(2, number.Value);
            Assert.Same(originalSnapshot, map.Value);
        });
    }

    [Fact]
    public void ObjectAndDictionarySimplificationRechecksDefaultAfterChildren()
    {
        var objectDescriptor = ConfigDescriptor
            .Object(("count", ConfigDescriptor.Number().Default(1)))
            .Default(new Dictionary<string, object?>());
        Assert.Null(
            objectDescriptor.Simplify(
                new Dictionary<string, object?>
                {
                    ["count"] = 1
                }));
        var dictionaryDescriptor = ConfigDescriptor
            .Dict(ConfigDescriptor.Number().Default(1))
            .Default(
                new Dictionary<string, object?>
                {
                    ["count"] = null
                });
        Assert.Null(
            dictionaryDescriptor.Simplify(
                new Dictionary<string, object?>
                {
                    ["count"] = 1
                }));
    }
}
