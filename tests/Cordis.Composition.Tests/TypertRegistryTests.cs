using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertRegistryTests
{
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
        var modules = new StaticModuleResolver().Register(
            "package",
            new Plugin<object?>
            {
                Apply = (_, _) =>
                {
                }
            });
        var artifacts = new StaticTypertArtifactResolver();
        var factoryCalls = 0;
        artifacts.Register(
            "package",
            () =>
            {
                factoryCalls++;
                return new("package", [Descriptor()]);
            });
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
        await root.RunAsync(async ctx => typert = await TypertLoader.StartAsync(ctx, loader, registry, artifacts));
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
}

internal sealed record TypertEnvelope(
    [property: JsonPropertyName("payload")]
    TypertPayload Payload);

internal sealed record TypertPayload([property: JsonPropertyName("value")] int Value);

[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(TypertEnvelope))]
internal partial class TypertRegistryJson : JsonSerializerContext;
