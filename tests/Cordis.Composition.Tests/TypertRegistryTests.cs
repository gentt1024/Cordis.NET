using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertRegistryTests
{
    [Fact]
    public async Task Discovered_loader_builtin_is_not_an_artifact_but_explicit_configuration_still_fails()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var artifacts = new StaticTypertArtifactResolver();
        artifacts.Register(
            "cordis:group",
            () => throw new InvalidOperationException("Builtin reached a module resolver."));
        Loader loader = null!;
        await root.RunAsync(async context =>
        {
            loader = new(context, new StaticModuleResolver());
            await loader.CreateAsync(
                new()
                {
                    Id = "group",
                    Name = "cordis:group",
                    Config = new List<EntryOptions>()
                });
            await loader.WaitAsync();
            await TypertLoader.StartAsync(context, loader, registry, artifacts);
        });
        var failure = await Assert.ThrowsAsync<AggregateException>(() => root.RunAsync(async context =>
            await TypertLoader.StartAsync(context, loader, registry, artifacts, ["cordis:group"])));
        var cause = Assert.IsType<InvalidOperationException>(Assert.Single(failure.InnerExceptions));
        Assert.Equal("Configured Typert package 'cordis:group' exports no artifact.", cause.Message);
    }

    [Fact]
    public async Task Contributions_reject_atomic_conflicts_and_withdraw_with_their_actual_fiber()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var inputs = new[] { new TypertInvocationParameter("value", "value", Integer()) };
        var descriptors = new[]
        {
            Descriptor() with
            {
                Parameters = inputs
            }
        };
        var schemaCalls = 0;
        var contribution = new TypertContribution(
            "example",
            "host",
            [
                new(
                    "Count",
                    () =>
                    {
                        schemaCalls++;
                        return Json("{\"type\":\"integer\"}");
                    })
            ],
            TypertPackageModel.Empty,
            descriptors);
        Fiber fiber = null!;
        var notifications = new List<string>();
        await root.RunAsync(ctx =>
        {
            registry.Subscribe(ctx, _ => throw new InvalidOperationException("observer failure"));
            registry.Subscribe(ctx, change => notifications.Add(change.Kind + ":" + change.Key));
            fiber = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (owner, _) => registry.Register(owner, contribution)
                });
            return Task.CompletedTask;
        });
        await fiber.WaitAsync();
        inputs[0] = new("wrong", "wrong", Integer());
        descriptors[0] = Descriptor("other");
        await root.RunAsync(ctx =>
        {
            Assert.Equal("value", Assert.Single(registry.GetLocal("counter/add")!.Parameters).Wire);
            var conflicting = new TypertContribution(
                "conflict",
                "host",
                [new("Transient", () => Json("true"))],
                TypertPackageModel.Empty,
                [Descriptor()]);
            Assert.Throws<InvalidOperationException>(() => registry.Register(ctx, conflicting));
            Assert.Null(registry.GetPackage("conflict"));
            Assert.Null(registry.GetSchema("conflict#Transient"));
            Assert.Equal(JsonValueKind.Object, registry.ResolveSchema("example#Count").ValueKind);
            _ = registry.ResolveSchema("example#Count");
            Assert.Equal(1, schemaCalls);
            Assert.Single(registry.ListLocal());
            registry.RegisterRemotes(ctx, new("example", [Descriptor()]));
            Assert.NotNull(registry.GetRemote("counter/add"));
            return Task.CompletedTask;
        });
        await fiber.DisposeAsync();
        await root.RunAsync(ctx =>
        {
            Assert.Null(registry.GetLocal("counter/add"));
            Assert.Null(registry.GetSchema("example#Count"));
            Assert.True(registry.HasSeenLocal("counter/add"));
            Assert.NotNull(registry.GetRemote("counter/add"));
            registry.Register(ctx, new("example", [Descriptor()]));
            Assert.NotNull(registry.GetLocal("counter/add"));
            Assert.Equal(
                new[] { "local:counter/add", "remote:counter/add", "local:counter/add", "local:counter/add" },
                notifications);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Lookup_replacement_keeps_its_wire_declaration_and_override_ownership()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var provider = new TypertLookupProvider(
            "document",
            "documentId",
            "example#Document",
            "System.String",
            id => ValueTask.FromResult<object?>("default:" + id));
        Fiber declared = null!;
        Fiber configured = null!;
        await root.RunAsync(ctx =>
        {
            declared = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (owner, _) => registry.RegisterLookup(owner, "@scope/document", provider)
                });
            configured = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (owner, _) => registry.ConfigureLookup(
                        owner,
                        "@scope/document",
                        id => ValueTask.FromResult<object?>("selected:" + id))
                });
            return Task.CompletedTask;
        });
        await declared.WaitAsync();
        await configured.WaitAsync();
        await root.RunAsync(async _ => Assert.Equal(
            "selected:one",
            await registry.GetLookup("@scope/document")!.Resolve("one")));
        await declared.DisposeAsync();
        await root.RunAsync(ctx =>
        {
            Assert.Null(registry.GetLookup("@scope/document"));
            Assert.Equal("documentId", Assert.Single(registry.LookupDefinitions()).Wire);
            Assert.Throws<InvalidOperationException>(() => registry.RegisterLookup(
                ctx,
                "@scope/document",
                provider with
                {
                    Wire = "differentId"
                }));
            registry.RegisterLookup(ctx, "@scope/document", provider);
            return Task.CompletedTask;
        });
        await configured.DisposeAsync();
        await root.RunAsync(async _ => Assert.Equal(
            "default:one",
            await registry.GetLookup("@scope/document")!.Resolve("one")));
    }

    [Fact]
    public void Strict_codecs_follow_local_references_and_refuse_unknown_schema_keywords()
    {
        var codec = TypertCodec.Create(
            TypertRegistryJson.Default.TypertEnvelope,
            schema: Json(
                """
                {"type":"object","properties":{"payload":{"$ref":"#/$defs/payload"}},"required":["payload"],
                 "$defs":{"payload":{"type":"object","properties":{"value":{"type":"integer","minimum":0}},"required":["value"]}}}
                """));
        var result = Assert.IsType<TypertEnvelope>(codec.Decode(Json("{\"payload\":{\"value\":4}}")));
        Assert.Equal(4, result.Payload.Value);
        Assert.Throws<JsonException>(() => codec.Decode(Json("{\"payload\":{}}")));
        Assert.Throws<JsonException>(() => codec.Decode(Json("{\"payload\":{\"value\":-1}}")));
        var unsupported = TypertCodec.Create(
            TypertRegistryJson.Default.Int32,
            schema: Json("{\"type\":\"integer\",\"madeUpRule\":true}"));
        Assert.Throws<NotSupportedException>(() => unsupported.Decode(Json("4")));
    }

    [Fact]
    public async Task Loader_shares_a_contribution_until_the_last_entry_unmounts()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        object value = 4;
        var packagePlugin = new Plugin<object?>
        {
            Apply = (owner, _) => owner.Provide("user:counter", new ConstantRemote("counter", value))
        };
        var independentPlugin = new Plugin<object?>
        {
            Apply = (owner, _) => owner.Provide("user:independent", new ConstantRemote("independent", 17))
        };
        var modules = new StaticModuleResolver()
            .Register(
                "package",
                packagePlugin)
            .Register("independent", independentPlugin);
        var artifacts = new StaticTypertArtifactResolver();
        var factoryCalls = 0;
        var contribution = new TypertContribution("package", [Descriptor()]);
        artifacts.Register(
            "package",
            () =>
            {
                factoryCalls++;
                return contribution;
            });
        artifacts.Register(
            "independent",
            () => new(
                "independent",
                [
                    Descriptor() with
                    {
                        Id = "independent#add",
                        Service = "user:independent",
                        Namespace = "independent"
                    }
                ]));
        Loader loader = null!;
        TypertLoader typert = null!;
        await root.RunAsync(ctx =>
        {
            loader = new(ctx, modules);
            return Task.CompletedTask;
        });
        await loader.CreateAsync(
            new()
            {
                Id = "first",
                Name = "package"
            });
        await loader.CreateAsync(
            new()
            {
                Id = "second",
                Name = "package"
            });
        await loader.CreateAsync(
            new()
            {
                Id = "independent",
                Name = "independent"
            });
        await root.RunAsync(async ctx => typert = await TypertLoader.StartAsync(ctx, loader, registry, artifacts));
        var independentFiber = loader.Resolve("independent").Fiber;
        TypertInvocationDescriptor independentDescriptor = null!;
        await root.RunAsync(_ =>
        {
            independentDescriptor = registry.GetLocal("independent/add")!;
            return Task.CompletedTask;
        });
        await loader.RemoveAsync("first");
        await root.RunAsync(async _ =>
        {
            await typert.WaitForIdleAsync();
            Assert.NotNull(registry.GetLocal("counter/add"));
        });
        await loader.RemoveAsync("second");
        await root.RunAsync(async _ =>
        {
            await typert.WaitForIdleAsync();
            Assert.Null(registry.GetLocal("counter/add"));
            Assert.True(registry.HasSeenLocal("counter/add"));
        });
        await loader.CreateAsync(
            new()
            {
                Id = "again",
                Name = "package"
            });
        await root.RunAsync(async _ =>
        {
            await typert.WaitForIdleAsync();
            Assert.NotNull(registry.GetLocal("counter/add"));
            Assert.Equal(1, factoryCalls);
        });
        var gateway = new TypertGateway(root, registry);
        Assert.Equal(4, (await gateway.InvokeAsync("counter/add", Json("{}"))).Value!.Value.GetInt32());
        await typert.SuspendAsync(["package"]);
        await root.RunAsync(_ =>
        {
            Assert.Null(registry.GetPackage("package"));
            Assert.Null(registry.GetLocal("counter/add"));
            Assert.Same(independentDescriptor, registry.GetLocal("independent/add"));
            return Task.CompletedTask;
        });
        value = "generation two";
        contribution = new(
            "package",
            "host",
            [new("Result", () => Json("{\"type\":\"string\"}"))],
            new([new("user:counter", "Counter", [], [new("Result", "export type Result = string;")])], [], []),
            [
                Descriptor() with
                {
                    Result = TypertCodec.Create(TypertRegistryJson.Default.String)
                }
            ]);
        await loader.ReplacePluginAsync(
            packagePlugin,
            new Plugin<object?>
            {
                Apply = (owner, _) => owner.Provide("user:counter", new ConstantRemote("counter", value))
            });
        await typert.ResumeAsync(["package"]);
        Assert.Equal("generation two", (await gateway.InvokeAsync("counter/add", Json("{}"))).Value!.Value.GetString());
        Assert.Equal(17, (await gateway.InvokeAsync("independent/add", Json("{}"))).Value!.Value.GetInt32());
        await root.RunAsync(_ =>
        {
            Assert.Same(independentFiber, loader.Resolve("independent").Fiber);
            Assert.Same(independentDescriptor, registry.GetLocal("independent/add"));
            Assert.Equal(
                "export type Result = string;",
                Assert.Single(Assert.Single(registry.GetPackage("package")!.Model.Services).Types).Declaration);
            Assert.Equal("string", registry.ResolveSchema("package#Result").GetProperty("type").GetString());
            Assert.Equal(2, factoryCalls);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suspension_detaches_old_imports_without_resurrecting_or_forgetting_the_new_import(bool oldFails)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        Loader loader = null!;
        TypertLoader typert = null!;
        var resolver = new SequencedResolver();
        await root.RunAsync(async ctx =>
        {
            loader = new(
                ctx,
                new StaticModuleResolver().Register(
                    "package",
                    new Plugin<object?>
                    {
                        Apply = (_, _) =>
                        {
                        }
                    }));
            typert = await TypertLoader.StartAsync(ctx, loader, registry, resolver);
        });
        await loader.CreateAsync(
            new()
            {
                Id = "entry",
                Name = "package"
            });
        await resolver.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var oldIdle = root.RunAsync(_ => typert.WaitForIdleAsync());
        await typert.SuspendAsync(["package"]);
        await oldIdle.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(resolver.First.Task.IsCompleted);
        var resuming = typert.ResumeAsync(["package"]);
        await resolver.SecondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (oldFails)
            resolver.First.SetException(new InvalidOperationException("retired import failed"));
        else
            resolver.First.SetResult(new("retired", [Descriptor("retired")]));
        // Wait until the old resolver continuation reaches the execution domain before taking a new idle snapshot.
        await root.RunAsync(async _ => await Task.Yield());
        var currentIdle = root.RunAsync(_ => typert.WaitForIdleAsync());
        await root.RunAsync(_ =>
        {
            Assert.Null(registry.GetLocal("counter/retired"));
            Assert.False(resuming.IsCompleted);
            Assert.False(currentIdle.IsCompleted);
            return Task.CompletedTask;
        });
        await typert.SuspendAsync(["package"]);
        var latestResume = typert.ResumeAsync(["package"]);
        await resolver.ThirdEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resuming);
        resolver.Second.SetException(new InvalidOperationException("superseded resume import failed"));
        await root.RunAsync(async _ => await Task.Yield());
        var latestIdle = root.RunAsync(_ => typert.WaitForIdleAsync());
        await root.RunAsync(_ =>
        {
            Assert.False(latestResume.IsCompleted);
            Assert.False(latestIdle.IsCompleted);
            return Task.CompletedTask;
        });
        resolver.Third.SetResult(new("current", [Descriptor("current")]));
        await latestResume.WaitAsync(TimeSpan.FromSeconds(5));
        await currentIdle.WaitAsync(TimeSpan.FromSeconds(5));
        await latestIdle.WaitAsync(TimeSpan.FromSeconds(5));
        await root.RunAsync(_ =>
        {
            Assert.Null(registry.GetLocal("counter/retired"));
            Assert.NotNull(registry.GetLocal("counter/current"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task A_failed_scoped_reload_withdraws_the_whole_batch_and_preserves_its_error()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var artifacts = new StaticTypertArtifactResolver();
        var conflicting = false;
        artifacts.Register("first", () => new("first", [Descriptor("first")]));
        artifacts.Register("second", () => new("second", [Descriptor(conflicting ? "first" : "second")]));
        artifacts.Register("pending", () => new("pending", [Descriptor("pending")]));
        var resolver = new DelayedBatchResolver(artifacts);
        var names = new[] { "first", "second", "pending" };
        Loader loader = null!;
        TypertLoader typert = null!;
        await root.RunAsync(async ctx =>
        {
            loader = new(ctx, new StaticModuleResolver());
            typert = await TypertLoader.StartAsync(ctx, loader, registry, resolver, names);
        });
        await typert.SuspendAsync(names);
        conflicting = true;
        resolver.Hold = true;
        var reentries = new List<Task>();
        EffectHandle observer = null!;
        await root.RunAsync(ctx =>
        {
            observer = registry.Subscribe(
                ctx,
                change =>
                {
                    if (change.Key == "counter/first")
                        reentries.Add(typert.SuspendAsync(["first"]));
                });
            return Task.CompletedTask;
        });
        var resuming = typert.ResumeAsync(names);
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resuming.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Contains("endpoint 'counter/first' is already registered", error.Message);
        Assert.False(resolver.Resolved.Task.IsCompleted);
        Assert.Equal(2, reentries.Count);
        foreach (var reentry in reentries)
            await Assert.ThrowsAsync<InvalidOperationException>(() => reentry);
        await observer.DisposeAsync();
        await root.RunAsync(async _ =>
        {
            await typert.WaitForIdleAsync();
            Assert.Empty(registry.ListLocal());
            Assert.Null(registry.GetPackage("first"));
            Assert.Null(registry.GetPackage("second"));
            Assert.Null(registry.GetPackage("pending"));
        });
        resolver.Resolved.SetResult(new("pending", [Descriptor("late")]));
        await root.RunAsync(async _ => await Task.Yield());
        await root.RunAsync(_ =>
        {
            Assert.Null(registry.GetLocal("counter/late"));
            Assert.False(registry.HasSeenLocal("counter/late"));
            return Task.CompletedTask;
        });
        conflicting = false;
        resolver.Hold = false;
        await typert.ResumeAsync(names);
        await root.RunAsync(_ =>
        {
            Assert.Equal(3, registry.ListLocal().Count);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task An_import_finishing_after_entry_removal_cannot_register_its_contribution()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var modules = new StaticModuleResolver().Register(
            "package",
            new Plugin<object?>
            {
                Apply = (_, _) =>
                {
                }
            });
        var resolver = new DeferredResolver();
        Loader loader = null!;
        await root.RunAsync(ctx =>
        {
            loader = new(ctx, modules);
            return Task.CompletedTask;
        });
        await loader.CreateAsync(
            new()
            {
                Id = "entry",
                Name = "package"
            });
        var starting = root.RunAsync(async ctx => _ = await TypertLoader.StartAsync(ctx, loader, registry, resolver));
        await resolver.Entered.Task;
        await loader.RemoveAsync("entry");
        resolver.Resolved.SetResult(new("package", [Descriptor()]));
        await starting;
        await root.RunAsync(_ =>
        {
            Assert.Null(registry.GetLocal("counter/add"));
            Assert.False(registry.HasSeenLocal("counter/add"));
            return Task.CompletedTask;
        });
    }

    private static TypertCodec Integer() => TypertCodec.Create(TypertRegistryJson.Default.Int32);

    private static TypertInvocationDescriptor Descriptor(string method = "add") => new(
        "example#counter/" + method,
        "user:counter",
        "counter",
        method,
        [],
        Integer());

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class DeferredResolver : ITypertArtifactResolver
    {
        public TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<TypertContribution?> Resolved
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TypertContribution?> ResolveAsync(
            string specifier,
            Uri baseUri,
            CancellationToken cancellationToken = default)
        {
            Entered.SetResult();
            return new(Resolved.Task);
        }
    }

    private sealed class SequencedResolver : ITypertArtifactResolver
    {
        private int calls;

        public TaskCompletionSource FirstEntered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondEntered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThirdEntered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<TypertContribution?> First
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<TypertContribution?> Second
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<TypertContribution?> Third
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TypertContribution?> ResolveAsync(
            string specifier,
            Uri baseUri,
            CancellationToken cancellationToken = default)
        {
            if (++calls == 1)
            {
                FirstEntered.SetResult();
                return new(First.Task);
            }

            if (calls == 2)
            {
                SecondEntered.SetResult();
                return new(Second.Task);
            }

            ThirdEntered.SetResult();
            return new(Third.Task);
        }
    }

    private sealed class DelayedBatchResolver(ITypertArtifactResolver immediate) : ITypertArtifactResolver
    {
        public bool Hold
        {
            get;
            set;
        }

        public TaskCompletionSource Entered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<TypertContribution?> Resolved
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<TypertContribution?> ResolveAsync(
            string specifier,
            Uri baseUri,
            CancellationToken cancellationToken = default)
        {
            if (specifier != "pending" || !Hold)
                return immediate.ResolveAsync(specifier, baseUri, cancellationToken);
            Entered.SetResult();
            return new(Resolved.Task);
        }
    }

    private sealed class ConstantRemote(string name, object value) : ITypertRemoteService
    {
        public TypertRemoteBinding TypertRemote
        {
            get;
        } = new(
            "user:" + name,
            name,
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["add"] = (_, _, _) => Task.FromResult<object?>(value)
            },
            new Dictionary<string, TypertStreamInvoker>());
    }
}

internal sealed record TypertEnvelope(
    [property: JsonPropertyName("payload")]
    TypertPayload Payload);

internal sealed record TypertPayload([property: JsonPropertyName("value")] int Value);

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(TypertEnvelope))]
internal partial class TypertRegistryJson : JsonSerializerContext;
