using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertResourceRegistryTests
{
    [Fact]
    public async Task Resources_withdraw_with_the_registering_fiber_and_notify_committed_changes()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var changes = new List<TypertRegistryChange>();
        Fiber owner = null!;
        await root.RunAsync(ctx =>
        {
            registry.Subscribe(ctx, _ => throw new InvalidOperationException("observer failed"));
            registry.Subscribe(ctx, changes.Add);
            owner = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (child, _) => registry.RegisterResource(child, "document", new("documentId"))
                });
            return Task.CompletedTask;
        });
        await owner.WaitAsync();
        await root.RunAsync(_ =>
        {
            Assert.Equal(new TypertResourceDeclaration("documentId"), registry.GetResource("document"));
            Assert.Equal(new TypertResourceEntry("document", "documentId"), Assert.Single(registry.ListResources()));
            Assert.Equal([new TypertRegistryChange("resource", "document")], changes);
            return Task.CompletedTask;
        });
        await owner.DisposeAsync();
        await root.RunAsync(_ =>
        {
            Assert.Null(registry.GetResource("document"));
            Assert.Empty(registry.ListResources());
            Assert.Equal(
                [new TypertRegistryChange("resource", "document"), new("resource", "document")],
                changes);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Resource_subscription_withdrawal_stops_later_notifications()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var changes = new List<TypertRegistryChange>();
        Fiber observer = null!;
        EffectHandle registration = null!;
        await root.RunAsync(ctx =>
        {
            observer = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (child, _) => registry.Subscribe(child, changes.Add)
                });
            return Task.CompletedTask;
        });
        await observer.WaitAsync();
        await root.RunAsync(ctx =>
        {
            registration = registry.RegisterResource(ctx, "document", new("documentId"));
            return Task.CompletedTask;
        });
        await observer.DisposeAsync();
        await registration.DisposeAsync();
        Assert.Equal(new TypertRegistryChange("resource", "document"), Assert.Single(changes));
    }

    [Fact]
    public async Task Resource_snapshots_remain_independent_and_replacement_moves_to_the_end()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        EffectHandle original = null!;
        IReadOnlyList<TypertResourceEntry> snapshot = null!;
        await root.RunAsync(ctx =>
        {
            var declaration = new TypertResourceDeclaration("documentId");
            original = registry.RegisterResource(ctx, "document", declaration);
            registry.RegisterResource(ctx, "agent", new("agentId"));
            snapshot = registry.ListResources();
            Assert.NotSame(declaration, registry.GetResource("document"));
            return Task.CompletedTask;
        });
        await original.DisposeAsync();
        await root.RunAsync(ctx =>
        {
            registry.RegisterResource(ctx, "document", new("replacementId"));
            Assert.Equal(
                [new TypertResourceEntry("document", "documentId"), new("agent", "agentId")],
                snapshot);
            Assert.Equal(
                [new TypertResourceEntry("agent", "agentId"), new("document", "replacementId")],
                registry.ListResources());
            return Task.CompletedTask;
        });
        await original.DisposeAsync();
        await root.RunAsync(_ =>
        {
            Assert.Equal(new TypertResourceDeclaration("replacementId"), registry.GetResource("document"));
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("", "documentId")]
    [InlineData("bad#kind", "documentId")]
    [InlineData("global", "documentId")]
    [InlineData("document", "")]
    [InlineData("document", ".")]
    [InlineData("document", "..")]
    [InlineData("document", "document/id")]
    [InlineData("document", "文档")]
    [InlineData("document", "document id")]
    public async Task Invalid_resource_declarations_do_not_register_or_notify(string kind, string field)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var changes = new List<TypertRegistryChange>();
        await root.RunAsync(ctx =>
        {
            registry.Subscribe(ctx, changes.Add);
            Assert.Throws<ArgumentException>(() => registry.RegisterResource(ctx, kind, new(field)));
            Assert.Empty(registry.ListResources());
            Assert.Empty(changes);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Resource_validation_matches_segment_and_wire_rules_and_rejects_duplicates()
    {
        await using var root = new Context();
        await using var otherRoot = new Context();
        var registry = new TypertRegistry(root);
        await root.RunAsync(ctx =>
        {
            Assert.Throws<ArgumentNullException>(() => registry.RegisterResource(ctx, "document", null!));
            Assert.Throws<ArgumentException>(() => registry.RegisterResource(otherRoot, "document", new("documentId")));
            registry.RegisterResource(ctx, "@scope/document with spaces", new("$document.id-1_"));
            Assert.Throws<InvalidOperationException>(() => registry.RegisterResource(
                ctx,
                "@scope/document with spaces",
                new("otherId")));
            Assert.Equal(
                new TypertResourceDeclaration("$document.id-1_"),
                registry.GetResource("@scope/document with spaces"));
            Assert.Null(registry.GetResource("global"));
            Assert.Single(registry.ListResources());
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lookup_resolver_configuration_preserves_provider_resource_and_stable_wire_contract(
        bool configureFirst)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var provider = new TypertLookupProvider(
            "document",
            "documentId",
            "example#Document",
            "System.String",
            id => ValueTask.FromResult<object?>("default:" + id))
        {
            Resource = "document"
        };
        EffectHandle configured = null!;
        EffectHandle registered = null!;
        await root.RunAsync(async ctx =>
        {
            if (configureFirst)
                configured = Configure(ctx);
            registered = registry.RegisterLookup(ctx, "document", provider);
            if (!configureFirst)
                configured = Configure(ctx);
            Assert.Equal("document", registry.GetLookup("document")!.Resource);
            Assert.Equal("configured:one", await registry.GetLookup("document")!.Resolve("one"));
        });
        await configured.DisposeAsync();
        await root.RunAsync(async _ =>
        {
            Assert.Equal("document", registry.GetLookup("document")!.Resource);
            Assert.Equal("default:one", await registry.GetLookup("document")!.Resolve("one"));
        });
        await registered.DisposeAsync();
        await root.RunAsync(ctx =>
        {
            Assert.Null(registry.GetLookup("document"));
            Assert.Equal(
                new TypertLookupDefinition("document", "document", "documentId", "example#Document", "System.String"),
                Assert.Single(registry.LookupDefinitions()));
            registry.RegisterLookup(
                ctx,
                "document",
                provider with
                {
                    Resource = "replacement"
                });
            Assert.Equal("replacement", registry.GetLookup("document")!.Resource);
            return Task.CompletedTask;
        });

        EffectHandle Configure(Context owner) => registry.ConfigureLookup(
            owner,
            "document",
            id => ValueTask.FromResult<object?>("configured:" + id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Host_resolver_configuration_preserves_provider_resource_before_and_after_withdrawal(
        bool configureFirst)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var adapter = new TypertHostContextAdapter(
            "agentId",
            "System.String",
            _ => ValueTask.FromResult<Context?>(null))
        {
            Resource = "agent"
        };
        EffectHandle configured = null!;
        await root.RunAsync(async ctx =>
        {
            if (configureFirst)
                configured = Configure(ctx);
            registry.RegisterHostContext(ctx, "agent", adapter);
            if (!configureFirst)
                configured = Configure(ctx);
            Assert.Equal("agent", registry.GetHostContext("agent")!.Resource);
            Assert.Same(root, await registry.GetHostContext("agent")!.Resolve("one"));
        });
        await configured.DisposeAsync();
        await root.RunAsync(async _ =>
        {
            Assert.Equal("agent", registry.GetHostContext("agent")!.Resource);
            Assert.Null(await registry.GetHostContext("agent")!.Resolve("one"));
        });

        EffectHandle Configure(Context owner) => registry.ConfigureHostContext(
            owner,
            "agent",
            _ => ValueTask.FromResult<Context?>(root));
    }
}
