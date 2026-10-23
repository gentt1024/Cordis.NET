using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertGatewayAccessTests
{
    private static readonly TypertCodec Text = TypertCodec.Create(AccessJson.Default.String);
    private static readonly TypertCodec Raw = TypertCodec.Create(AccessJson.Default.JsonElement);
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
    private static TypertAccessResource Document(string id) => TypertAccessResource.Create("document", id);

    [Fact]
    public async Task Polymorphic_default_field_requires_a_reader_for_the_selected_derived_contract()
    {
        await using var root = new Context();
        var f = await Setup(root, [new("payload", "payload", TypertCodec.Create(AccessJson.Default.AccessBase))]);
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("documentId"));
            return Task.CompletedTask;
        });
        await Grant(root, f, [Document("d1"), TypertAccessResource.Global]);
        Denied(
            await f.Gateway.InvokeAsync(
                "access/read",
                Json("{\"payload\":{\"$type\":\"derived\",\"documentId\":\"d1\"}}"),
                f.Guest),
            "resource-invalid");
        f.Service.Readers["document"] = (args, _) => [Assert.IsType<AccessDerived>(args[0]).DocumentId];
        Assert.True(
            (await f.Gateway.InvokeAsync(
                "access/read",
                Json("{\"payload\":{\"$type\":\"derived\",\"documentId\":\"d1\"}}"),
                f.Guest)).Ok);
    }

    [Fact]
    public async Task Native_JSON_safety_also_rejects_nonfinite_tokens_in_undeclared_fields()
    {
        await using var root = new Context();
        var f = await Setup(root, [new("payload", "payload", TypertCodec.Create(AccessJson.Default.AccessEmpty))]);
        Assert.Equal(
            "gateway/input-invalid",
            (await f.Gateway.InvokeAsync("access/read", Json("{\"payload\":{\"ignored\":1e400}}"), f.Owner)).Error
            ?.Code);
        Assert.Equal(0, f.Service.Calls);
    }

    [Fact]
    public async Task Reader_receives_cancellation_and_distinguishes_absence_from_explicit_null()
    {
        await using var root = new Context();
        var f = await Setup(
            root,
            [
                new("payload", "payload", TypertCodec.CreateNullable<string>(AccessJson.Default.String))
                {
                    AcceptsUndefined = true
                }
            ]);
        using var source = new CancellationTokenSource();
        object? observed = new object();
        f.Service.Readers["document"] = (args, token) =>
        {
            Assert.Equal(source.Token, token);
            Assert.Single(args);
            observed = args[0];
            return null;
        };
        await Grant(root, f, [TypertAccessResource.Global]);
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{}"), f.Guest, source.Token)).Ok);
        Assert.Same(Undefined.Value, observed);
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{\"payload\":null}"), f.Guest, source.Token)).Ok);
        Assert.Null(observed);
        await using var stream =
            f.Gateway.StreamAsync("access/watch", Json("{}"), f.Guest, source.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.True(stream.Current.Ok);
        Assert.Same(Undefined.Value, observed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_ordinary_JSON_precedes_missing_or_invalid_service(bool invalidBinding)
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        await root.RunAsync(ctx =>
        {
            registry.Register(
                ctx,
                new TypertContribution(
                    "access",
                    [new("access#read", "access", "access", "read", [new("value", "value", Text)], Text)]));
            if (invalidBinding)
                ctx.Provide("access", new object());
            return Task.CompletedTask;
        });
        var gateway = new TypertGateway(root, registry);
        Assert.Equal(
            "gateway/input-invalid",
            (await gateway.InvokeAsync("access/read", Json("{\"value\":1}"))).Error?.Code);
        Assert.Equal(
            invalidBinding ? "gateway/binding-invalid" : "gateway/service-unavailable",
            (await gateway.InvokeAsync("access/read", Json("{\"value\":\"valid\"}"))).Error?.Code);
    }

    [Fact]
    public async Task Optional_default_identity_unwraps_without_changing_the_business_argument()
    {
        await using var root = new Context();
        var f = await Setup(
            root,
            [
                new("documentId", "documentId", TypertCodec.CreateOptional<string>(Text))
                {
                    AcceptsUndefined = true
                }
            ]);
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("documentId"));
            return Task.CompletedTask;
        });
        await Grant(root, f, [Document("d1"), TypertAccessResource.Global]);
        f.Service.Handle = args =>
        {
            Assert.Equal("d1", Assert.IsType<RemoteOptional<string>>(args[0]).Value);
            return 1;
        };
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{\"documentId\":\"d1\"}"), f.Guest)).Ok);
        f.Service.Handle = args =>
        {
            Assert.Same(Undefined.Value, args[0]);
            return 1;
        };
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{}"), f.Guest)).Ok);
    }

    [Fact]
    public async Task Opaque_enumerable_object_requires_reader_instead_of_falling_back_to_global()
    {
        await using var root = new Context();
        var f = await Setup(root, [new("payload", "payload", new EnumerableCodec())]);
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("documentId"));
            return Task.CompletedTask;
        });
        await Grant(root, f, [TypertAccessResource.Global, Document("d1")]);
        Denied(await f.Gateway.InvokeAsync("access/read", Json("{\"payload\":{}}"), f.Guest), "resource-invalid");
        f.Service.Readers["document"] = (args, _) => [Assert.IsType<OpaqueEnumerable>(args[0]).Id];
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{\"payload\":{}}"), f.Guest)).Ok);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_JSON_errors_precede_access_context_service_and_lookup(bool owner)
    {
        await using var root = new Context();
        var f = await Setup(
            root,
            [
                new("payload", "payload", Text), new("id", "id", Text)
                {
                    Lookup = "document"
                }
            ],
            context: true);
        var decisions = 0;
        await root.RunAsync(ctx =>
        {
            f.Access.RegisterPolicy(
                ctx,
                "guest",
                new(_ =>
                {
                    decisions++;
                    return [];
                }));
            return Task.CompletedTask;
        });
        var result = await f.Gateway.InvokeAsync(
            "access/read",
            Json("{\"scope\":\"s1\",\"payload\":42,\"id\":\"d1\"}"),
            owner ? f.Owner : f.Guest);
        Assert.Equal("gateway/input-invalid", result.Error?.Code);
        Assert.Equal(0, decisions);
        Assert.Equal(0, f.Contexts);
        Assert.Equal(0, f.Lookups);
        Assert.Equal(0, f.Service.Calls);
    }

    [Theory]
    [InlineData("no-policy", "no-allow")]
    [InlineData("throw", "policy-failed")]
    [InlineData("refuse", "closed")]
    [InlineData("missing-gate", "no-allow")]
    public async Task Refused_calls_do_not_decode_or_resolve_Context_and_lookup(string mode, string reason)
    {
        await using var root = new Context();
        var codec = new CountingCodec(Text);
        var f = await Setup(
            root,
            [
                new("id", "id", codec)
                {
                    Lookup = "document"
                }
            ],
            context: true,
            gate: mode != "missing-gate");
        await root.RunAsync(ctx =>
        {
            if (mode == "throw")
                f.Access.RegisterPolicy(ctx, "guest", new(_ => throw new InvalidOperationException("private")));
            if (mode == "refuse")
                f.Access.RegisterPolicy(ctx, "guest", new(_ => [], _ => "closed"));
            return Task.CompletedTask;
        });
        var result = await f.Gateway.InvokeAsync("access/read", Json("{\"scope\":12,\"id\":false}"), f.Guest);
        Denied(result, reason);
        Assert.Equal(0, codec.Decodes);
        Assert.Equal(0, f.Contexts);
        Assert.Equal(0, f.Lookups);
        Assert.Equal(0, f.Service.Calls);
    }

    [Fact]
    public async Task Decoded_object_is_shared_with_readers_policy_and_business_without_serialization()
    {
        await using var root = new Context();
        var codec = new CountingCodec(TypertCodec.Create(AccessJson.Default.AccessPayload));
        var f = await Setup(root, [new("payload", "payload", codec)]);
        AccessPayload? observed = null;
        f.Service.Readers["document"] = (args, _) =>
        {
            observed = Assert.IsType<AccessPayload>(args[0]);
            Assert.Equal("D1", observed.DocumentId);
            Assert.Equal(0, observed.Count);
            return [observed.DocumentId];
        };
        await Grant(
            root,
            f,
            [Document("D1")],
            request =>
            {
                Assert.Same(observed, request.Arguments!["payload"]);
                return TypertAccessDecision.Allowed;
            });
        f.Service.Handle = args =>
        {
            Assert.Same(observed, args[0]);
            return 7;
        };
        Assert.True(
            (await f.Gateway.InvokeAsync(
                "access/read",
                Json("{\"payload\":{\"documentId\":\"d1\",\"count\":0,\"ignored\":\"hidden\"}}"),
                f.Guest)).Ok);
        Assert.Equal(1, codec.Decodes);
        Assert.Equal(0, codec.Encodes);
    }

    [Theory]
    [InlineData("{\"payload\":{\"documentId\":\"d1\"}}", "D1")]
    [InlineData("{\"payload\":{}}", "DEFAULT")]
    public async Task Default_field_reads_normalized_native_input_without_unrelated_getters(string input, string id)
    {
        await using var root = new Context();
        var f = await Setup(root, [new("payload", "payload", TypertCodec.Create(AccessJson.Default.AccessPayload))]);
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("documentId"));
            return Task.CompletedTask;
        });
        await Grant(root, f, [Document(id)]);
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json(input), f.Guest)).Ok);
        Assert.Equal(1, f.Service.Calls);
    }

    [Fact]
    public async Task Unknown_fields_do_not_supply_resources_and_first_object_stops_fallback()
    {
        await using var root = new Context();
        var f = await Setup(
            root,
            [new("first", "first", TypertCodec.Create(AccessJson.Default.AccessEmpty)), new("second", "second", Raw)]);
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("documentId"));
            return Task.CompletedTask;
        });
        await Grant(root, f, [TypertAccessResource.Global]);
        Assert.True(
            (await f.Gateway.InvokeAsync(
                "access/read",
                Json("{\"first\":{\"documentId\":\"untrusted\"},\"second\":{\"documentId\":\"later\"}}"),
                f.Guest)).Ok);
    }

    [Fact]
    public async Task Setter_only_default_is_invalid_but_explicit_reader_sees_the_accepted_value()
    {
        await using var root = new Context();
        var f = await Setup(root, [new("payload", "payload", TypertCodec.Create(AccessJson.Default.AccessWriteOnly))]);
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("documentId"));
            return Task.CompletedTask;
        });
        await Grant(root, f, [Document("d1")]);
        Denied(
            await f.Gateway.InvokeAsync("access/read", Json("{\"payload\":{\"documentId\":\"d1\"}}"), f.Guest),
            "resource-invalid");
        f.Service.Readers["document"] = (args, _) => [Assert.IsType<AccessWriteOnly>(args[0]).ReadId()];
        Assert.True(
            (await f.Gateway.InvokeAsync("access/read", Json("{\"payload\":{\"documentId\":\"d1\"}}"), f.Guest)).Ok);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[\"d1\"]")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("\"\"")]
    public async Task Malformed_default_resource_fails_before_policy(string value)
    {
        await using var root = new Context();
        var f = await Setup(root, [new("documentId", "documentId", Raw)]);
        var decisions = 0;
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("documentId"));
            f.Access.RegisterPolicy(
                ctx,
                "guest",
                new(_ =>
                {
                    decisions++;
                    return [];
                }));
            return Task.CompletedTask;
        });
        Denied(
            await f.Gateway.InvokeAsync("access/read", Json("{\"documentId\":" + value + "}"), f.Guest),
            "resource-invalid");
        Assert.Equal(0, decisions);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("throw")]
    public async Task Invalid_reader_does_not_fall_back_to_global(string mode)
    {
        await using var root = new Context();
        var f = await Setup(root, []);
        f.Service.Readers["document"] = (_, _) => mode switch
        {
            "empty" => [],
            "blank" => [""],
            _ => throw new InvalidOperationException("reader failed")
        };
        await Grant(root, f, [TypertAccessResource.Global]);
        Denied(await f.Gateway.InvokeAsync("access/read", Json("{}"), f.Guest), "resource-invalid");
        Assert.Equal(0, f.Service.Calls);
    }

    [Fact]
    public async Task Context_and_lookup_resources_replace_defaults_and_all_identities_require_permission()
    {
        await using var root = new Context();
        var f = await Setup(
            root,
            [
                new("id", "id", Text)
                {
                    Lookup = "document"
                }
            ],
            context: true);
        await root.RunAsync(ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("missing"));
            return Task.CompletedTask;
        });
        var granted = new List<TypertAccessResource>
        {
            Document("scope"),
            Document("item")
        };
        var seen = new List<string>();
        await Grant(
            root,
            f,
            granted,
            request =>
            {
                seen.Add(request.Resource.Id!);
                return TypertAccessDecision.Allowed;
            });
        // Setup provider facts deliberately become active only for this test.
        await root.RunAsync(async ctx =>
        {
            await f.LookupRegistration.DisposeAsync();
            await f.ContextRegistration!.DisposeAsync();
            f.LookupRegistration = f.Registry.RegisterLookup(
                ctx,
                "document",
                new(
                    "id",
                    "id",
                    "host",
                    Text.TypeSymbol,
                    value =>
                    {
                        f.Lookups++;
                        return ValueTask.FromResult(value);
                    })
                {
                    Resource = "document"
                });
            f.ContextRegistration = f.Registry.RegisterHostContext(
                ctx,
                "scope",
                new(
                    "scope",
                    Text.TypeSymbol,
                    _ =>
                    {
                        f.Contexts++;
                        return ValueTask.FromResult<Context?>(root);
                    })
                {
                    Resource = "document"
                });
        });
        Assert.True(
            (await f.Gateway.InvokeAsync("access/read", Json("{\"scope\":\"scope\",\"id\":\"item\"}"), f.Guest)).Ok);
        Assert.Equal(new[] { "scope", "item" }, seen);
        granted.RemoveAt(1);
        Denied(
            await f.Gateway.InvokeAsync("access/read", Json("{\"scope\":\"scope\",\"id\":\"item\"}"), f.Guest),
            "no-allow");
        Assert.Equal(1, f.Contexts);
        Assert.Equal(1, f.Lookups);
    }

    [Fact]
    public async Task Reader_overrides_provider_and_default_per_kind_and_null_removes_that_kind()
    {
        await using var root = new Context();
        var f = await Setup(
            root,
            [
                new("id", "id", Text)
                {
                    Lookup = "document"
                }
            ]);
        await root.RunAsync(async ctx =>
        {
            f.Registry.RegisterResource(ctx, "document", new("id"));
            await f.LookupRegistration.DisposeAsync();
            f.LookupRegistration = f.Registry.RegisterLookup(
                ctx,
                "document",
                new(
                    "id",
                    "id",
                    "host",
                    Text.TypeSymbol,
                    value => ValueTask.FromResult(value))
                {
                    Resource = "document"
                });
        });
        f.Service.Readers["document"] = (args, _) =>
        {
            Assert.Equal("raw", Assert.IsType<JsonElement>(args[0]).GetString());
            return ["selected", "selected"];
        };
        var seen = 0;
        await Grant(
            root,
            f,
            [Document("selected"), TypertAccessResource.Global],
            _ =>
            {
                seen++;
                return TypertAccessDecision.Allowed;
            });
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{\"id\":\"raw\"}"), f.Guest)).Ok);
        Assert.Equal(2, seen);
        f.Service.Readers["document"] = (_, _) => null;
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{\"id\":\"raw\"}"), f.Guest)).Ok);
    }

    [Fact]
    public async Task Owner_bypasses_readers_and_event_admission_precedes_policy()
    {
        await using var root = new Context();
        var f = await Setup(root, []);
        f.Service.Readers["document"] = (_, _) => throw new InvalidOperationException("owner must not read");
        await root.RunAsync(ctx =>
        {
            f.Access.RegisterPolicy(ctx, "guest", new(_ => throw new InvalidOperationException("events must not ask")));
            return Task.CompletedTask;
        });
        Assert.True((await f.Gateway.InvokeAsync("access/read", Json("{}"), f.Owner)).Ok);
        Assert.Equal(
            "gateway/invocation-unavailable",
            (await f.Gateway.InvokeAsync("$events/result", Json("null"), f.Guest)).Error?.Code);
        await using var stream = f.Gateway.StreamAsync("$events", Json("null"), f.Guest).GetAsyncEnumerator();
        var failure = await Assert.ThrowsAsync<RemoteError>(() => stream.MoveNextAsync().AsTask());
        Assert.Equal("gateway/arguments-invalid", failure.Code);
    }

    [Fact]
    public async Task List_filter_reads_current_policy_without_arguments_during_a_live_stream()
    {
        await using var root = new Context();
        var f = await Setup(root, []);
        var visible = true;
        await root.RunAsync(ctx =>
        {
            f.Access.RegisterPolicy(
                ctx,
                "guest",
                new(_ =>
                [
                    Binding(TypertAccessResource.Global),
                    Binding(
                        Document("d1"),
                        request =>
                        {
                            Assert.Null(request.Arguments);
                            return visible ? TypertAccessDecision.Allowed : new TypertAccessDecision.Deny("revoked");
                        })
                ]));
            return Task.CompletedTask;
        });
        f.Service.Handle = _ => f.Access.Allows(Document("d1")) ? 1 : 0;
        await using var reader = f.Gateway.StreamAsync("access/watch", Json("{}"), f.Guest).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, reader.Current.Value!.Value.GetInt32());
        visible = false;
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(0, reader.Current.Value!.Value.GetInt32());
        Assert.False(await reader.MoveNextAsync());
        Assert.Null(TypertInvocation.Current);
    }

    [Theory]
    [InlineData("1e400")]
    [InlineData("{\"nested\":[1e400]}")]
    public async Task Raw_JSON_nonfinite_numbers_fail_even_with_an_unrestricted_codec(string value)
    {
        await using var root = new Context();
        var f = await Setup(root, [new("payload", "payload", new CountingCodec(Raw))]);
        var result = await f.Gateway.InvokeAsync("access/read", Json("{\"payload\":" + value + "}"), f.Owner);
        Assert.Equal("gateway/input-invalid", result.Error?.Code);
        Assert.Equal(0, f.Service.Calls);
    }

    private static void Denied(TypertRemoteResult result, string reason)
    {
        Assert.False(result.Ok);
        Assert.Equal("remote-access/denied", result.Error?.Code);
        Assert.Equal(reason, result.Error?.Details?.GetProperty("reason").GetString());
    }

    private static TypertAccessBinding Binding(
        TypertAccessResource resource,
        Func<TypertAccessCondition, TypertAccessDecision>? condition = null) =>
        new(new("read", resource.Kind, ["access/read", "access/watch"]), resource, condition);

    private static Task Grant(
        Context root,
        Fixture f,
        IReadOnlyList<TypertAccessResource> resources,
        Func<TypertAccessCondition, TypertAccessDecision>? condition = null) =>
        root.RunAsync(ctx =>
        {
            f.Access.RegisterPolicy(
                ctx,
                "guest",
                new(_ => resources.Select(resource => Binding(resource, condition)).ToArray()));
            return Task.CompletedTask;
        });

    private static async Task<Fixture> Setup(
        Context root,
        IReadOnlyList<TypertInvocationParameter> parameters,
        bool context = false,
        bool gate = true)
    {
        var f = new Fixture(new(root), new(root));
        f.Gateway = new(root, f.Registry);
        await root.RunAsync(ctx =>
        {
            var descriptor = new TypertInvocationDescriptor(
                "access#read",
                "access",
                "access",
                "read",
                parameters,
                TypertCodec.Create(AccessJson.Default.Int32))
            {
                Invocation = context ? new("scope", "scope", Text) : null
            };
            f.Registry.Register(
                ctx,
                new TypertContribution(
                    "access",
                    [
                        descriptor, descriptor with
                        {
                            Id = "access#watch",
                            Method = "watch",
                            IsStream = true
                        }
                    ]));
            f.LookupRegistration = f.Registry.RegisterLookup(
                ctx,
                "document",
                new(
                    "id",
                    "id",
                    "host",
                    Text.TypeSymbol,
                    value =>
                    {
                        f.Lookups++;
                        return ValueTask.FromResult(value);
                    }));
            if (context)
                f.ContextRegistration = f.Registry.RegisterHostContext(
                    ctx,
                    "scope",
                    new(
                        "scope",
                        Text.TypeSymbol,
                        _ =>
                        {
                            f.Contexts++;
                            return ValueTask.FromResult<Context?>(root);
                        }));
            ctx.Provide("access", f.Service);
            if (gate)
                ctx.Provide("remoteAccess", f.Access);
            f.Guest = new(ctx, new(new(Json("{\"kind\":\"guest\"}"))));
            f.Owner = new(ctx, new(TypertPrincipal.Owner));
            return Task.CompletedTask;
        });
        return f;
    }

    private sealed class Fixture(TypertRegistry registry, TypertRemoteAccess access)
    {
        internal TypertRegistry Registry = registry;
        internal TypertRemoteAccess Access = access;
        internal TypertGateway Gateway = null!;
        internal TypertPeer Guest = null!;
        internal TypertPeer Owner = null!;
        internal TestService Service = new();
        internal EffectHandle LookupRegistration = null!;
        internal EffectHandle? ContextRegistration;
        internal int Contexts;
        internal int Lookups;
    }

    private sealed class TestService : ITypertRemoteService, ITypertRemoteResourceSource
    {
        internal readonly Dictionary<string, TypertResourceReader> Readers = [];
        internal Func<IReadOnlyList<object?>, object?> Handle = _ => 1;
        internal int Calls;

        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, TypertResourceReader>> ResourceReaders =>
            new Dictionary<string, IReadOnlyDictionary<string, TypertResourceReader>>
            {
                ["read"] = Readers,
                ["watch"] = Readers
            };

        public TypertRemoteBinding TypertRemote => new(
            "access",
            "access",
            new Dictionary<string, TypertUnaryInvoker>
            {
                ["read"] = (_, args, _) =>
                {
                    Calls++;
                    return Task.FromResult(Handle(args));
                }
            },
            new Dictionary<string, TypertStreamInvoker>
            {
                ["watch"] = (_, args, token) => Watch(args, token)
            });

        private async IAsyncEnumerable<object?> Watch(
            IReadOnlyList<object?> args,
            [EnumeratorCancellation] CancellationToken token)
        {
            for (var index = 0;index < 2;index++)
            {
                token.ThrowIfCancellationRequested();
                Calls++;
                yield return Handle(args);
                await Task.Yield();
            }
        }
    }

    private sealed class CountingCodec(TypertCodec inner) : TypertCodec
    {
        internal int Decodes;
        internal int Encodes;
        public override string TypeSymbol => inner.TypeSymbol;
        public override JsonElement Schema => inner.Schema;

        public override object? Decode(JsonElement value)
        {
            Decodes++;
            return inner.Decode(value);
        }

        public override JsonElement Encode(object? value)
        {
            Encodes++;
            throw new InvalidOperationException("Access must not serialize decoded inputs.");
        }
    }

    private sealed class EnumerableCodec : TypertCodec
    {
        public override string TypeSymbol => "opaque";
        public override JsonElement Schema => Json("true");
        public override object Decode(JsonElement value) => new OpaqueEnumerable();

        public override JsonElement Encode(object? value) =>
            throw new InvalidOperationException("Do not serialize resources.");
    }

    private sealed class OpaqueEnumerable : System.Collections.IEnumerable
    {
        internal string Id => "d1";
        public System.Collections.IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
    }
}

public sealed class AccessPayload
{
    private string id = "DEFAULT";

    [JsonPropertyName("documentId")]
    public string DocumentId
    {
        get => id;
        set => id = value.ToUpperInvariant();
    }

    [JsonPropertyName("count"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Count
    {
        get;
        set;
    }

    public string Dangerous => throw new InvalidOperationException("Unrelated getter must not run.");
}

public sealed class AccessEmpty
{
    public string Computed => "never-authorize";
}

public sealed class AccessWriteOnly
{
    private string id = "";

    [JsonPropertyName("documentId")]
    public string DocumentId
    {
        set => id = value;
    }

    public string ReadId() => id;
}

[JsonPolymorphic]
[JsonDerivedType(typeof(AccessDerived), "derived")]
public class AccessBase;

public sealed class AccessDerived : AccessBase
{
    [JsonPropertyName("documentId")]
    public string DocumentId
    {
        get;
        set;
    } = "";
}

[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(AccessPayload))]
[JsonSerializable(typeof(AccessEmpty))]
[JsonSerializable(typeof(AccessWriteOnly))]
[JsonSerializable(typeof(AccessBase))]
internal partial class AccessJson : JsonSerializerContext;
