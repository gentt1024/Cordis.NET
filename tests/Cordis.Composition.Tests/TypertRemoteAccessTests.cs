using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertRemoteAccessTests
{
    private static readonly TypertPrincipal Guest = Principal("guest");
    private static readonly TypertAccessResource Room = TypertAccessResource.Create("room", "one");
    private static readonly TypertAccessResource OtherRoom = TypertAccessResource.Create("room", "two");
    private static readonly TypertAccessRole Reader = new("reader", "room", ["notes/read"]);

    [Fact]
    public void Resource_identity_is_immutable_case_sensitive_and_global_has_no_id()
    {
        Assert.Equal(Room, TypertAccessResource.Create("room", "one"));
        Assert.NotEqual(Room, TypertAccessResource.Create("Room", "one"));
        Assert.NotEqual(Room, TypertAccessResource.Create("room", "One"));
        Assert.Equal(Room.GetHashCode(), TypertAccessResource.Create("room", "one").GetHashCode());
        Assert.Equal("global", TypertAccessResource.Global.Kind);
        Assert.Null(TypertAccessResource.Global.Id);
        Assert.Throws<ArgumentException>(() => TypertAccessResource.Create("global", "one"));
        Assert.Throws<ArgumentException>(() => TypertAccessResource.Create("", "one"));
        Assert.Throws<ArgumentException>(() => TypertAccessResource.Create("room", ""));
        Assert.Throws<ArgumentNullException>(() => TypertAccessResource.Create(null!, "one"));
        Assert.Throws<ArgumentNullException>(() => TypertAccessResource.Create("room", null!));
    }

    [Fact]
    public async Task Owner_and_follow_are_allowed_and_an_unregistered_kind_is_refused()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        await root.RunAsync(_ =>
        {
            Assert.Same(TypertAccessDecision.Allowed, access.Decide(Ask(TypertPrincipal.Owner, "notes/hidden", Room)));
            Denied("no-allow", access.Decide(Ask(Guest, "notes/read", Room)));
            Assert.IsType<TypertAccessDecision.Allow>(
                access.Decide(Ask(Guest, "remoteAccess/follow", TypertAccessResource.Global)));
            Assert.True(access.Allows(Room));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Bindings_are_read_once_per_decision_and_every_resource_requires_its_exact_action()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var calls = 0;
        IReadOnlyList<TypertAccessBinding> held = [new(Reader, Room)];
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(
                ctx,
                "guest",
                new(principal =>
                {
                    Assert.Same(Guest, principal);
                    calls++;
                    return held;
                }));
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room)));
            Denied("no-allow", access.Decide(Ask(Guest, "notes/read", Room, OtherRoom)));
            held = [new(Reader, Room), new(Reader, OtherRoom)];
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room, OtherRoom)));
            Denied("no-allow", access.Decide(Ask(Guest, "notes/write", Room)));
            Denied("no-allow", access.Decide(Ask(Guest, "notes/read", TypertAccessResource.Global)));
            Assert.Equal(5, calls);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Global_and_named_resources_do_not_imply_one_another_and_principal_kinds_are_exact()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(
                ctx,
                "guest",
                new(_ => [new(new("global-reader", "global", ["notes/read"]), TypertAccessResource.Global)]));
            Assert.IsType<TypertAccessDecision.Allow>(
                access.Decide(Ask(Guest, "notes/read", TypertAccessResource.Global)));
            Denied("no-allow", access.Decide(Ask(Guest, "notes/read", Room)));
            Denied("no-allow", access.Decide(Ask(Principal("Guest"), "notes/read", TypertAccessResource.Global)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Conditions_only_run_for_matching_bindings_and_receive_the_exact_call_arguments()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var calls = 0;
        using var document = JsonDocument.Parse("{\"text\":\"ok\"}");
        var decoded = new
        {
            Text = "ok"
        };
        IReadOnlyDictionary<string, object?> arguments = new Dictionary<string, object?>
        {
            ["request"] = decoded,
            ["lookup"] = document.RootElement.Clone(),
            ["nullable"] = null
        };
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(
                ctx,
                "guest",
                new(_ =>
                [
                    new(new("writer", "room", ["notes/write"]), Room, _ => throw new Exception("wrong action")),
                    new(Reader, OtherRoom, _ => throw new Exception("wrong resource")),
                    new(
                        Reader,
                        Room,
                        condition =>
                        {
                            calls++;
                            Assert.Equal("notes/read", condition.Action);
                            Assert.Equal(Room, condition.Resource);
                            Assert.Same(arguments, condition.Arguments);
                            Assert.Same(decoded, condition.Arguments!["request"]);
                            Assert.Equal(
                                "ok",
                                Assert
                                    .IsType<JsonElement>(condition.Arguments["lookup"])
                                    .GetProperty("text")
                                    .GetString());
                            Assert.True(condition.Arguments.ContainsKey("nullable"));
                            Assert.Null(condition.Arguments["nullable"]);
                            Assert.False(condition.Arguments.ContainsKey("omitted"));
                            return new TypertAccessDecision.Deny("paused");
                        })
                ]));
            Denied("paused", access.Decide(new(Guest, "notes/read", [Room], arguments)));
            Assert.Equal(1, calls);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task First_condition_refusal_wins_unless_a_later_matching_binding_allows()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var evaluated = new List<string>();
        IReadOnlyList<TypertAccessBinding> held =
        [
            new(
                Reader,
                Room,
                condition =>
                {
                    Assert.Null(condition.Arguments);
                    evaluated.Add("first");
                    return new TypertAccessDecision.Deny("paused");
                }),
            new(
                Reader,
                Room,
                _ =>
                {
                    evaluated.Add("second");
                    return new TypertAccessDecision.Deny("locked");
                })
        ];
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(ctx, "guest", new(_ => held, _ => throw new Exception("must not explain")));
            Denied("paused", access.Decide(Ask(Guest, "notes/read", Room)));
            Assert.Equal(["first", "second"], evaluated);
            held = [.. held, new(Reader, Room, _ => TypertAccessDecision.Allowed)];
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room)));
            held = [held[0], new(Reader, Room)];
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task First_refused_resource_ends_the_decision_before_later_resources()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(
                ctx,
                "guest",
                new(_ =>
                [
                    new(Reader, Room, _ => new TypertAccessDecision.Deny("first-resource")),
                    new(Reader, OtherRoom, _ => throw new Exception("later resource"))
                ]));
            Denied("first-resource", access.Decide(Ask(Guest, "notes/read", Room, OtherRoom)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Explain_only_names_an_unmatched_refusal_and_may_return_an_empty_reason()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var explanations = 0;
        string? reason = "business-refusal";
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(
                ctx,
                "guest",
                new(
                    _ => [new(Reader, Room)],
                    request =>
                    {
                        Assert.Same(Guest, request.Principal);
                        Assert.Equal("notes/read", request.Action);
                        Assert.Equal([OtherRoom], request.Resources);
                        explanations++;
                        return reason;
                    }));
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room)));
            Assert.Equal(0, explanations);
            Denied("business-refusal", access.Decide(Ask(Guest, "notes/read", OtherRoom)));
            reason = null;
            Denied("no-allow", access.Decide(Ask(Guest, "notes/read", OtherRoom)));
            reason = "";
            Denied("", access.Decide(Ask(Guest, "notes/read", OtherRoom)));
            Assert.Equal(3, explanations);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("bindings")]
    [InlineData("condition")]
    [InlineData("explain")]
    public async Task Policy_exceptions_fail_closed_and_log_once_until_invalidated(string phase)
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var messages = new List<LogMessage>();
        await root.RunAsync(ctx =>
        {
            ctx.Logger.Exporter(new DelegateLogExporter(messages.Add, (int)LogLevel.Warn));
            access.RegisterPolicy(
                ctx,
                "guest",
                new(
                    _ => phase == "bindings"
                        ? throw new InvalidOperationException("bindings failed")
                        : phase == "condition"
                            ?
                            [
                                new(Reader, Room, _ => throw new InvalidOperationException("condition failed")),
                                new(Reader, Room)
                            ]
                            : [],
                    _ => throw new InvalidOperationException("explain failed")));
            Denied("policy-failed", access.Decide(Ask(Guest, "notes/read", Room)));
            Denied("policy-failed", access.Decide(Ask(Guest, "notes/read", Room)));
            Assert.Equal(2, messages.Count);
            Assert.Contains("guest", Assert.IsType<string>(messages[0].Arguments[0]), StringComparison.Ordinal);
            Assert.IsType<InvalidOperationException>(messages[1].Arguments[0]);
            access.Invalidate();
            Denied("policy-failed", access.Decide(Ask(Guest, "notes/read", Room)));
            Assert.Equal(4, messages.Count);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Diagnostic_suppression_is_per_registration_and_registration_changes_reset_it()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var messages = new List<LogMessage>();
        var policy = new TypertAccessPolicy(_ => throw new Exception("failed"));
        await root.RunAsync(async ctx =>
        {
            ctx.Logger.Exporter(new DelegateLogExporter(messages.Add, (int)LogLevel.Warn));
            var first = access.RegisterPolicy(ctx, "guest", policy);
            access.RegisterPolicy(ctx, "other", policy);
            Denied("policy-failed", access.Decide(Ask(Guest, "notes/read", Room)));
            Denied("policy-failed", access.Decide(Ask(Principal("other"), "notes/read", Room)));
            Assert.Equal(4, messages.Count);
            Denied("policy-failed", access.Decide(Ask(Guest, "notes/read", Room)));
            Assert.Equal(4, messages.Count);
            await first.DisposeAsync();
            Denied("policy-failed", access.Decide(Ask(Principal("other"), "notes/read", Room)));
            Assert.Equal(6, messages.Count);
            access.RegisterPolicy(ctx, "guest", policy);
            Denied("policy-failed", access.Decide(Ask(Guest, "notes/read", Room)));
            Assert.Equal(8, messages.Count);
        });
    }

    [Theory]
    [InlineData("null-bindings")]
    [InlineData("null-binding")]
    [InlineData("null-role")]
    [InlineData("null-resource")]
    [InlineData("null-actions")]
    [InlineData("null-action")]
    [InlineData("wrong-role-kind")]
    [InlineData("null-name")]
    [InlineData("null-decision")]
    [InlineData("null-reason")]
    [InlineData("enumerator-failed")]
    public async Task Malformed_native_policy_outputs_are_contained(string malformed)
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var binding = malformed switch
        {
            "null-binding" => null!,
            "null-role" => new TypertAccessBinding(null!, Room),
            "null-resource" => new(Reader, null!),
            "null-actions" => new(
                Reader with
                {
                    Actions = null!
                },
                Room),
            "null-action" => new(
                Reader with
                {
                    Actions = [null!, "notes/read"]
                },
                Room),
            "wrong-role-kind" => new(
                Reader with
                {
                    Resource = "global"
                },
                Room),
            "null-name" => new(
                Reader with
                {
                    Name = null!
                },
                Room),
            "null-decision" => new(Reader, Room, _ => null!),
            "null-reason" => new(Reader, Room, _ => new TypertAccessDecision.Deny(null!)),
            _ => new(Reader, Room)
        };
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(
                ctx,
                "guest",
                new(_ => malformed switch
                {
                    "null-bindings" => null!,
                    "enumerator-failed" => new BrokenBindings(),
                    _ => [binding]
                }));
            Denied("policy-failed", access.Decide(Ask(Guest, "notes/read", Room)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Registration_is_fiber_owned_and_old_handles_cannot_remove_a_replacement()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var policy = new TypertAccessPolicy(_ => [new(Reader, Room)]);
        Fiber fiber = null!;
        EffectHandle old = null!;
        await root.RunAsync(ctx =>
        {
            fiber = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (child, _) => old = access.RegisterPolicy(child, "guest", policy)
                });
            return Task.CompletedTask;
        });
        await fiber.WaitAsync();
        await root.RunAsync(_ =>
        {
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room)));
            return Task.CompletedTask;
        });
        await fiber.DisposeAsync();
        await root.RunAsync(ctx =>
        {
            Denied("no-allow", access.Decide(Ask(Guest, "notes/read", Room)));
            access.RegisterPolicy(ctx, "guest", policy);
            return Task.CompletedTask;
        });
        await old.DisposeAsync();
        await root.RunAsync(_ =>
        {
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Invalid_and_duplicate_registrations_leave_the_original_policy_unchanged()
    {
        await using var root = new Context();
        await using var otherRoot = new Context();
        var access = new TypertRemoteAccess(root);
        var policy = new TypertAccessPolicy(_ => [new(Reader, Room)]);
        await root.RunAsync(ctx =>
        {
            Assert.Throws<ArgumentNullException>(() => access.RegisterPolicy(null!, "guest", policy));
            Assert.Throws<ArgumentException>(() => access.RegisterPolicy(otherRoot, "guest", policy));
            Assert.Throws<ArgumentException>(() => access.RegisterPolicy(ctx, "", policy));
            Assert.Throws<ArgumentNullException>(() => access.RegisterPolicy(ctx, null!, policy));
            Assert.Throws<ArgumentException>(() => access.RegisterPolicy(ctx, "owner", policy));
            Assert.Throws<ArgumentNullException>(() => access.RegisterPolicy(ctx, "guest", null!));
            Assert.Throws<ArgumentNullException>(() => access.RegisterPolicy(ctx, "guest", new(null!)));
            access.RegisterPolicy(ctx, "guest", policy);
            Assert.Throws<InvalidOperationException>(() => access.RegisterPolicy(ctx, "guest", new(_ => [])));
            Assert.IsType<TypertAccessDecision.Allow>(access.Decide(Ask(Guest, "notes/read", Room)));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Policy_operations_require_the_context_execution_domain()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        Assert.Throws<InvalidOperationException>(() => access.RegisterPolicy(root, "guest", new(_ => [])));
        Assert.Throws<InvalidOperationException>(() => access.Decide(Ask(Guest, "notes/read", Room)));
        Assert.Throws<InvalidOperationException>(() => access.Allows(Room));
        Assert.Throws<InvalidOperationException>(access.Invalidate);
    }

    [Fact]
    public async Task Owner_gateway_list_checks_use_the_real_current_invocation()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var service = new OwnerList(access);
        await root.RunAsync(ctx =>
        {
            ctx.Provide("remoteAccess", access);
            registry.Register(
                ctx,
                new TypertContribution(
                    "notes",
                    [
                        new(
                            "notes#read",
                            "notes",
                            "notes",
                            "read",
                            [],
                            TypertCodec.Create(TypertRegistryJson.Default.Int32))
                    ]));
            ctx.Provide("notes", service);
            return Task.CompletedTask;
        });
        using var document = JsonDocument.Parse("{}");
        var result = await gateway.InvokeAsync("notes/read", document.RootElement);
        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, result.Value!.Value.GetInt32());
        Assert.Null(TypertInvocation.Current);
    }

    private static TypertAccessRequest Ask(
        TypertPrincipal principal,
        string action,
        params TypertAccessResource[] resources) =>
        new(principal, action, resources);

    private static void Denied(string reason, TypertAccessDecision decision) =>
        Assert.Equal(reason, Assert.IsType<TypertAccessDecision.Deny>(decision).Reason);

    private static TypertPrincipal Principal(string kind)
    {
        using var document = JsonDocument.Parse("{\"kind\":\"" + kind + "\"}");
        return new(document.RootElement);
    }

    private sealed class BrokenBindings : IReadOnlyList<TypertAccessBinding>
    {
        public int Count => throw new InvalidOperationException("count failed");
        public TypertAccessBinding this[int index] => throw new InvalidOperationException("index failed");

        public IEnumerator<TypertAccessBinding> GetEnumerator() =>
            throw new InvalidOperationException("enumeration failed");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OwnerList(TypertRemoteAccess access) : ITypertRemoteService
    {
        public TypertRemoteBinding TypertRemote => new(
            "notes",
            "notes",
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["read"] = (_, _, _) =>
                {
                    Assert.Equal("notes/read", TypertInvocation.Current?.Endpoint);
                    Assert.Equal("owner", TypertInvocation.Current?.Peer?.Principal.Kind);
                    Assert.True(access.Allows(Room));
                    return Task.FromResult<object?>(1);
                }
            },
            new Dictionary<string, TypertStreamInvoker>());
    }
}
