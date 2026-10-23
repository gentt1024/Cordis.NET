using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertRemoteInfoTests
{
    [Fact]
    public void Nonfinite_numeric_literals_refuse_tool_projection()
    {
        foreach (var constraint in new[] { "\"const\":1e400", "\"enum\":[1e400]" })
        {
            var descriptor = Descriptor(JsonCodec("{\"type\":\"number\"," + constraint + "}"));
            Assert.Null(descriptor.Input().JsonSchema);
            Assert.Contains("finite JSON scalar", descriptor.Input().JsonSchemaError);
            Assert.Null(descriptor.ResultValue.JsonSchema);
            Assert.Throws<JsonException>(() => descriptor.Input().Parse(Json("{\"value\":1e400}")));
        }
    }

    [Fact]
    public void Combined_union_constraints_refuse_tool_projection_without_weakening_parsing()
    {
        var descriptor = Descriptor(
            JsonCodec(
                """
                {"oneOf":[{"type":"string"},{"type":"number"}],"anyOf":[{"type":"string"}]}
                """));
        Assert.Null(descriptor.Input().JsonSchema);
        Assert.Contains("combined union", descriptor.Input().JsonSchemaError);
        Assert.Null(descriptor.ResultValue.JsonSchema);
        Assert.IsType<JsonElement>(descriptor.Input().Parse(Json("{\"value\":\"accepted\"}"))["value"]);
        Assert.Throws<JsonException>(() => descriptor.Input().Parse(Json("{\"value\":1}")));
    }

    [Fact]
    public void Metadata_parsers_reject_nonfinite_numbers_in_unconstrained_json_and_ignored_fields()
    {
        var codec = JsonCodec("{}");
        var descriptor = Descriptor(codec) with
        {
            Uplink = codec
        };
        var value = Json("{\"nested\":[{\"overflow\":1e999}]}");
        var named = Json("{\"value\":{\"nested\":[{\"overflow\":1e999}]}}");
        Assert.Throws<JsonException>(() => descriptor.Input().Parse(named));
        Assert.Throws<JsonException>(() => descriptor.Input(true).Parse(named));
        Assert.Throws<JsonException>(() => descriptor.Parameters[0].Input().Parse(value));
        Assert.Throws<JsonException>(() => descriptor.Parameters[0].Input(true).Parse(value));
        Assert.Throws<JsonException>(() => descriptor.UplinkValue!.Parse(value));
        Assert.Throws<JsonException>(() => descriptor.ResultValue.Parse(value));
        var native = Descriptor(TypertCodec.Create(RemoteInfoJson.Default.RemoteInfoInput));
        Assert.Throws<JsonException>(() =>
            native.Input().Parse(Json("{\"value\":{\"name\":\"valid\",\"ignored\":[1e999]}}")));
        Assert.IsType<JsonElement>(codec.Decode(value));
        Assert.IsType<JsonElement>(codec.DecodeOutput(value));
        Assert.IsType<JsonElement>(
            descriptor.Input().Parse(Json("{\"value\":{\"zero\":-0,\"large\":1e308}}"))["value"]);
    }

    [Fact]
    public void Strict_tuple_prefix_objects_do_not_inherit_declared_fields_from_trailing_items()
    {
        var codec = JsonCodec(
            """
            {"type":"array","prefixItems":[{"type":"object","properties":{"prefix":{"type":"string"}}}],
             "items":{"type":"object","properties":{"tail":{"type":"string"}}}}
            """);
        var descriptor = Descriptor(codec);
        Assert.Null(descriptor.Input(true).JsonSchema);
        Assert.IsType<JsonElement>(
            descriptor.Input(true).Parse(Json("{\"value\":[{\"prefix\":\"one\"},{\"tail\":\"two\"}]}"))["value"]);
        Assert.Throws<JsonException>(() =>
            descriptor.Input(true).Parse(Json("{\"value\":[{\"prefix\":\"one\",\"tail\":\"extra\"}]}")));
    }

    [Fact]
    public void Context_only_parameters_expose_normal_and_strict_value_metadata_without_direct_input()
    {
        var codec = JsonCodec("{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}");
        var parameter = new TypertInvocationParameter("value", "value", codec)
        {
            Description = "Selected value"
        };
        var descriptor = Descriptor(codec) with
        {
            Parameters = [parameter],
            Invocation = new("scope", "id", codec)
        };
        Assert.Throws<InvalidOperationException>(() => descriptor.Input());
        var normal = descriptor.Parameters[0].Input();
        var strict = descriptor.Parameters[0].Input(true);
        Assert.Same(normal, parameter.Input());
        Assert.Same(strict, parameter.Input(true));
        Assert.NotSame(normal, strict);
        Assert.NotSame(normal.Create(), strict.Create());
        Assert.Equal("Selected value", normal.Description);
        Assert.Equal("Selected value", strict.JsonSchema!.Value.GetProperty("description").GetString());
        Assert.True(normal.JsonSchema!.Value.GetProperty("additionalProperties").GetBoolean());
        Assert.False(strict.JsonSchema!.Value.GetProperty("additionalProperties").GetBoolean());
        Assert.IsType<JsonElement>(normal.Parse(Json("{\"name\":\"hello\",\"extra\":true}")));
        Assert.Throws<JsonException>(() => strict.Parse(Json("{\"name\":\"hello\",\"extra\":true}")));
        Assert.IsType<JsonElement>(strict.Parse(Json("{\"name\":\"hello\"}")));
        var copy = parameter with
        {
            Description = "Copy"
        };
        Assert.NotSame(normal, copy.Input());
        Assert.Equal("Copy", copy.Input().Description);
    }

    [Fact]
    public void Named_input_reuses_parameter_value_caches_and_unexpected_projection_failures_retry()
    {
        var codec = new CountingCodec
        {
            Failures = 1
        };
        var descriptor = Descriptor(codec);
        var parameter = descriptor.Parameters[0].Input();
        Assert.Same(parameter.Create(), parameter.Create());
        Assert.Equal(0, codec.SchemaReads);
        Assert.Equal("decoded:x", parameter.Parse(Json("\"x\"")));
        Assert.Equal(0, codec.SchemaReads);
        Assert.Throws<InvalidOperationException>(() => _ = parameter.JsonSchema);
        Assert.NotNull(parameter.JsonSchema);
        Assert.Equal(2, codec.SchemaReads);
        Assert.NotNull(descriptor.Input().JsonSchema);
        Assert.Equal(2, codec.SchemaReads);
        Assert.NotNull(descriptor.Parameters[0].Input(true).JsonSchema);
        Assert.NotNull(descriptor.Input(true).JsonSchema);
        Assert.Equal(3, codec.SchemaReads);
    }

    [Fact]
    public void Uplink_value_metadata_uses_input_direction_and_preserves_parsers_when_projection_is_unavailable()
    {
        var codec = TypertCodec.Create(RemoteInfoJson.Default.RemoteInfoOutput);
        var descriptor = Descriptor(codec) with
        {
            IsStream = true,
            Uplink = codec
        };
        var uplink = descriptor.UplinkValue!;
        Assert.Same(uplink, descriptor.UplinkValue);
        Assert.Same(uplink.Create(), uplink.Create());
        Assert.IsType<RemoteInfoOutput>(uplink.Parse(Json("{}")));
        Assert.DoesNotContain(
            uplink.JsonSchema!.Value.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "label");
        Assert.Throws<JsonException>(() => descriptor.ResultValue.Parse(Json("{}")));
        var copy = descriptor with
        {
            Uplink = JsonCodec("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}")
        };
        Assert.NotSame(uplink, copy.UplinkValue);
        Assert.Null(copy.UplinkValue!.JsonSchema);
        Assert.Contains("dictionaries", copy.UplinkValue.JsonSchemaError);
        Assert.IsType<JsonElement>(copy.UplinkValue.Parse(Json("{\"arbitrary\":\"value\"}")));
        Assert.Null(
            (descriptor with
            {
                Uplink = null
            }).UplinkValue);
    }

    [Fact]
    public void Binary_result_metadata_refuses_tool_projection_without_disabling_native_parsing()
    {
        var bytes = Descriptor(TypertCodec.Create(RemoteInfoJson.Default.ByteArray)).ResultValue;
        Assert.Null(bytes.JsonSchema);
        Assert.Contains("binary results", bytes.JsonSchemaError);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<byte[]>(bytes.Parse(Json("\"AQID\""))));
        var nested = Descriptor(TypertCodec.Create(RemoteInfoJson.Default.RemoteInfoBinary)).ResultValue;
        Assert.Null(nested.JsonSchema);
        Assert.Contains("binary results", nested.JsonSchemaError);
        var parsed = Assert.IsType<RemoteInfoBinary>(nested.Parse(Json("{\"data\":\"AQID\",\"items\":[\"BA==\"]}")));
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Data);
        Assert.Equal(new byte[] { 4 }, Assert.Single(parsed.Items));
        var text = Descriptor(
                TypertCodec.Create(
                    RemoteInfoJson.Default.String,
                    schema: Json("{\"type\":\"string\",\"contentEncoding\":\"base64\"}")))
            .ResultValue;
        Assert.Equal("string", text.JsonSchema!.Value.GetProperty("type").GetString());
        Assert.Equal("AQID", text.Parse(Json("\"AQID\"")));
    }

    [Fact]
    public void Info_defers_index_enumeration_and_retries_failed_construction()
    {
        var descriptor = Descriptor(new CountingCodec());
        var source = new DeferredDescriptors(descriptor);
        var info = new TypertRemoteInfo("test", source);
        Assert.Equal("test", info.Package);
        Assert.Equal(0, source.Enumerations);
        Assert.Throws<InvalidOperationException>(() => _ = info.Descriptors);
        Assert.Equal(1, source.Enumerations);
        source.Fail = false;
        Assert.Same(descriptor, info.Methods["counter/add"]);
        Assert.Same(descriptor, Assert.Single(info.Descriptors));
        Assert.Equal(2, source.Enumerations);
    }

    [Fact]
    public void Result_metadata_uses_the_native_output_contract_and_requiredness()
    {
        var descriptor = Descriptor(TypertCodec.Create(RemoteInfoJson.Default.RemoteInfoOutput));
        Assert.IsType<RemoteInfoOutput>(descriptor.Input().Parse(Json("{\"value\":{}}"))["value"]);
        var schema = descriptor.ResultValue.JsonSchema!.Value;
        Assert.Equal("label", Assert.Single(schema.GetProperty("required").EnumerateArray()).GetString());
        Assert.Throws<JsonException>(() => descriptor.ResultValue.Parse(Json("{}")));
        Assert.IsType<RemoteInfoOutput>(descriptor.ResultValue.Parse(Json("{\"label\":\"remote\"}")));
        _ = descriptor.Input(true).JsonSchema;
        Assert.True(JsonElement.DeepEquals(schema, descriptor.ResultValue.JsonSchema!.Value));
    }

    [Fact]
    public void Index_input_parsers_and_projections_are_independent_and_share_descriptor_identity()
    {
        var codec = new CountingCodec();
        var direct = Descriptor(codec) with
        {
            Description = "Add",
            ResultDescription = "Answer"
        };
        var scoped = direct with
        {
            Id = "test#counter/scoped",
            Method = "scoped",
            Invocation = new("scope", "id", codec)
        };
        var info = new TypertRemoteInfo("test", [direct, scoped]);
        Assert.Same(direct, info.Descriptors[0]);
        Assert.Same(direct, info.Methods["counter/add"]);
        Assert.Same(info.Methods, info.Methods);
        Assert.Single(info.Methods);
        Assert.Equal(0, codec.SchemaReads);
        var normal = direct.Input();
        var strict = direct.Input(true);
        var result = direct.ResultValue;
        Assert.Same(normal, direct.Input());
        Assert.Same(strict, direct.Input(true));
        Assert.NotSame(normal, strict);
        Assert.Same(normal.Create(), normal.Create());
        Assert.NotSame(normal.Create(), strict.Create());
        Assert.Same(result.Create(), result.Create());
        Assert.Equal(0, codec.SchemaReads);
        Assert.Equal("decoded:value", normal.Parse(Json("{\"value\":\"value\"}"))["value"]);
        Assert.Equal(1, codec.Decodes);
        Assert.Equal(0, codec.SchemaReads);
        Assert.NotNull(normal.JsonSchema);
        Assert.Equal(1, codec.SchemaReads);
        Assert.Null(normal.JsonSchemaError);
        Assert.Equal(1, codec.SchemaReads);
        Assert.NotNull(strict.JsonSchema);
        Assert.Equal(2, codec.SchemaReads);
        Assert.Equal("Answer", result.JsonSchema!.Value.GetProperty("description").GetString());
        Assert.Equal(3, codec.SchemaReads);
        Assert.Equal("decoded:result", result.Parse(Json("\"result\"")));
        Assert.Throws<InvalidOperationException>(() => scoped.Input());
    }

    [Fact]
    public void With_copies_get_fresh_reference_keyed_metadata()
    {
        var original = Descriptor(new CountingCodec());
        _ = original.Input().JsonSchema;
        _ = original.ResultValue.JsonSchema;
        var changed = original with
        {
            Parameters = [new("other", "other", new CountingCodec())],
            ResultDescription = "changed",
        };
        Assert.NotSame(original.Input(), changed.Input());
        Assert.NotSame(original.ResultValue, changed.ResultValue);
        Assert.Equal("decoded:x", changed.Input().Parse(Json("{\"other\":\"x\"}"))["other"]);
        Assert.Equal("changed", changed.ResultValue.JsonSchema!.Value.GetProperty("description").GetString());
        Assert.False(changed.Input().JsonSchema!.Value.GetProperty("properties").TryGetProperty("value", out _));
    }

    [Fact]
    public void Unknown_fields_are_closed_and_only_ordinary_undefined_parameters_are_optional()
    {
        var codec = new CountingCodec();
        var descriptor = Descriptor(codec) with
        {
            Parameters =
            [
                new("value", "value", codec)
                {
                    AcceptsUndefined = true
                },
                new("lookup", "identity", codec)
                {
                    AcceptsUndefined = true,
                    Lookup = "lookup"
                }
            ],
        };
        var input = descriptor.Input();
        Assert.Throws<JsonException>(() => input.Parse(Json("{}")));
        Assert.Throws<JsonException>(() => input.Parse(Json("{\"identity\":\"id\",\"extra\":0}")));
        var parsed = input.Parse(Json("{\"identity\":\"id\"}"));
        Assert.False(parsed.ContainsKey("value"));
        Assert.Equal("decoded:id", parsed["identity"]);
        Assert.Equal(
            "identity",
            Assert.Single(input.JsonSchema!.Value.GetProperty("required").EnumerateArray()).GetString());
    }

    [Fact]
    public void Strict_declared_objects_reject_unknown_nested_fields_and_keep_native_setter_transforms()
    {
        var codec = TypertCodec.Create(RemoteInfoJson.Default.RemoteInfoInput);
        var descriptor = Descriptor(codec);
        var raw = Json("{\"value\":{\"name\":\"  hello  \",\"child\":{\"name\":\"world\",\"unknown\":true}}}");
        var parsed = Assert.IsType<RemoteInfoInput>(descriptor.Input().Parse(raw)["value"]);
        Assert.Equal("HELLO", parsed.Name);
        Assert.Equal("WORLD", parsed.Child!.Name);
        Assert.Throws<JsonException>(() => descriptor.Input(true).Parse(raw));
        Assert.Equal(
            "HELLO",
            Assert.IsType<RemoteInfoInput>(
                    descriptor
                        .Input(true)
                        .Parse(Json("{\"value\":{\"name\":\" hello \",\"child\":{\"name\":\"world\"}}}"))
                        ["value"])
                .Name);
        Assert.Equal("HELLO", Assert.IsType<RemoteInfoInput>(codec.Decode(raw.GetProperty("value"))).Name);
    }

    [Fact]
    public void Strict_preserves_dictionary_keys_but_closes_their_declared_values_and_preserves_explicit_json()
    {
        var schema =
            "{\"type\":\"object\",\"additionalProperties\":{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}}";
        var descriptor = Descriptor(JsonCodec(schema));
        Assert.IsType<JsonElement>(
            descriptor.Input(true).Parse(Json("{\"value\":{\"arbitrary\":{\"name\":\"ok\"}}}"))["value"]);
        Assert.Throws<JsonException>(() =>
            descriptor.Input(true).Parse(Json("{\"value\":{\"arbitrary\":{\"extra\":0}}}")));
        Assert.Null(descriptor.Input(true).JsonSchema);
        Assert.Contains("dictionaries", descriptor.Input(true).JsonSchemaError);
        var json = Descriptor(JsonCodec("{}"));
        Assert.IsType<JsonElement>(json.Input(true).Parse(Json("{\"value\":{\"any\":{\"extra\":true}}}"))["value"]);
        Assert.NotNull(json.Input(true).JsonSchema);
    }

    [Theory]
    [InlineData(
        "{\"allOf\":[{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}},{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"string\"}}}]}")]
    [InlineData(
        "{\"$defs\":{\"base\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}},\"$ref\":\"#/$defs/base\",\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"string\"}}}")]
    public void Strict_composed_objects_share_declared_fields_without_weakening_unknown_field_checks(string schema)
    {
        var descriptor = Descriptor(JsonCodec(schema));
        Assert.Null(descriptor.Input(true).JsonSchema);
        Assert.IsType<JsonElement>(
            descriptor.Input(true).Parse(Json("{\"value\":{\"a\":\"one\",\"b\":\"two\"}}"))["value"]);
        Assert.Throws<JsonException>(() =>
            descriptor.Input(true).Parse(Json("{\"value\":{\"a\":\"one\",\"b\":\"two\",\"extra\":true}}")));
    }

    [Theory]
    [InlineData("{\"type\":\"integer\",\"minimum\":0,\"maximum\":255}", "1")]
    [InlineData("{\"type\":\"string\",\"format\":\"date-time\"}", "\"2026-01-01T00:00:00Z\"")]
    [InlineData("{\"type\":\"array\",\"prefixItems\":[{\"type\":\"string\"}]}", "[\"item\"]")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"next\":{\"$ref\":\"#\"}}}", "{\"next\":{}}")]
    [InlineData(
        "{\"anyOf\":[{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}},{\"type\":\"object\",\"properties\":{\"b\":{\"type\":\"string\"}}}]}",
        "{}")]
    public void Unsupported_tool_shapes_retain_independent_working_parsers(string schema, string raw)
    {
        var descriptor = Descriptor(JsonCodec(schema));
        Assert.Null(descriptor.Input().JsonSchema);
        Assert.NotEmpty(descriptor.Input().JsonSchemaError!);
        Assert.IsType<JsonElement>(descriptor.Input().Parse(Json("{\"value\":" + raw + "}"))["value"]);
        Assert.Null(descriptor.ResultValue.JsonSchema);
        Assert.IsType<JsonElement>(descriptor.ResultValue.Parse(Json(raw)));
    }

    [Fact]
    public void Native_numeric_bounds_are_not_weakened_by_tool_projection()
    {
        var descriptor = Descriptor(TypertCodec.Create(RemoteInfoJson.Default.Byte));
        Assert.Null(descriptor.Input().JsonSchema);
        Assert.Null(descriptor.ResultValue.JsonSchema);
        Assert.Equal((byte)255, descriptor.Input().Parse(Json("{\"value\":255}"))["value"]);
        Assert.Throws<JsonException>(() => descriptor.Input().Parse(Json("{\"value\":256}")));
        Assert.Throws<JsonException>(() => descriptor.Input().Parse(Json("{\"value\":1.5}")));
    }

    [Fact]
    public void Optional_and_void_results_have_parsers_without_claiming_a_json_projection()
    {
        var optional = Descriptor(
            TypertCodec.CreateOptional<string>(TypertCodec.Create(RemoteInfoJson.Default.String)));
        Assert.Null(optional.ResultValue.JsonSchema);
        Assert.Contains("absent result", optional.ResultValue.JsonSchemaError);
        Assert.False(Assert.IsType<RemoteOptional<string>>(optional.ResultValue.Parse(default)).HasValue);
        var absent = Descriptor(TypertCodec.Void).ResultValue;
        Assert.Null(absent.JsonSchema);
        Assert.Same(Undefined.Value, absent.Parse(default));
        Assert.Throws<JsonException>(() => absent.Parse(Json("null")));
    }

    [Fact]
    public void Unexpected_schema_factory_failures_retry_instead_of_becoming_projection_errors()
    {
        var codec = new CountingCodec
        {
            Failures = 1
        };
        var input = Descriptor(codec).Input();
        Assert.Throws<InvalidOperationException>(() => _ = input.JsonSchema);
        Assert.NotNull(input.JsonSchema);
        Assert.Null(input.JsonSchemaError);
        Assert.Equal(2, codec.SchemaReads);
    }

    [Fact]
    public void Local_references_expand_and_disjoint_unions_keep_literals_descriptions_and_strict_objects()
    {
        var codec = JsonCodec(
            """
            {"$defs":{"left":{"type":"object","properties":{"kind":{"type":"string","const":"left"},"value":{"type":"string"}},"required":["kind"]}},
             "anyOf":[{"$ref":"#/$defs/left"},{"type":"object","properties":{"kind":{"type":"string","const":"right"}},"required":["kind"]}]}
            """);
        var descriptor = Descriptor(codec) with
        {
            Parameters =
            [
                new("value", "value", codec)
                {
                    Description = "Choice"
                }
            ]
        };
        var normal = descriptor.Input().JsonSchema!.Value.GetProperty("properties").GetProperty("value");
        var strict = descriptor.Input(true).JsonSchema!.Value.GetProperty("properties").GetProperty("value");
        Assert.Equal("Choice", strict.GetProperty("description").GetString());
        Assert.True(normal.GetProperty("oneOf")[0].GetProperty("additionalProperties").GetBoolean());
        Assert.False(strict.GetProperty("oneOf")[0].GetProperty("additionalProperties").GetBoolean());
        Assert.False(strict.GetProperty("oneOf")[0].TryGetProperty("$ref", out _));
        Assert.Throws<JsonException>(() =>
            descriptor.Input(true).Parse(Json("{\"value\":{\"kind\":\"left\",\"extra\":true}}")));
        Assert.IsType<JsonElement>(
            descriptor.Input().Parse(Json("{\"value\":{\"kind\":\"left\",\"extra\":true}}"))["value"]);
    }

    [Fact]
    public void Named_invocation_returns_the_exact_task_and_never_parses_arguments()
    {
        var codec = new CountingCodec
        {
            Failures = 10
        };
        var descriptor = Descriptor(codec) with
        {
            IsStream = true
        };
        var remote = new NamedRemote();
        var arguments = new Dictionary<string, object?>
        {
            ["unparsed"] = new object()
        };
        using var cancellation = new CancellationTokenSource();
        Assert.Same(remote.Result, descriptor.Invoke(remote, arguments, cancellation.Token));
        Assert.Same(arguments, remote.Arguments);
        Assert.Equal(cancellation.Token, remote.Token);
        Assert.Equal("counter/add", remote.Endpoint);
        Assert.Equal(0, codec.SchemaReads);
        Assert.Equal(0, codec.Decodes);
        Assert.Throws<InvalidOperationException>(() => (descriptor with
        {
            Invocation = new("scope", "id", codec)
        }).Invoke(remote, arguments));
    }

    [Fact]
    public async Task Registry_infos_keep_exact_snapshot_objects_without_preparing_schema_and_withdraw_with_owner()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var codec = new CountingCodec
        {
            Failures = 10
        };
        Fiber owner = null!;
        await root.RunAsync(context =>
        {
            owner = context.Plugin(
                new Plugin<object?>
                {
                    Apply = (ctx, _) =>
                    {
                        registry.Register(ctx, new TypertContribution("test", [Descriptor(codec)]));
                        registry.RegisterRemotes(ctx, new("test", [Descriptor(codec)]));
                    }
                });
            return Task.CompletedTask;
        });
        await owner.WaitAsync();
        await root.RunAsync(context =>
        {
            var local = registry.GetLocalInfo("test")!;
            Assert.Same(local, registry.GetLocalInfo("test"));
            Assert.Same(registry.GetLocal("counter/add"), local.Methods["counter/add"]);
            Assert.Same(local.Descriptors[0], local.Methods["counter/add"]);
            Assert.Same(registry.GetRemote("counter/add"), registry.GetRemoteInfo("test")!.Methods["counter/add"]);
            Assert.Equal(0, codec.SchemaReads);
            return Task.CompletedTask;
        });
        await owner.DisposeAsync();
        await root.RunAsync(context =>
        {
            Assert.Null(registry.GetLocalInfo("test"));
            Assert.Null(registry.GetRemoteInfo("test"));
            return Task.CompletedTask;
        });
    }

    private static TypertInvocationDescriptor Descriptor(TypertCodec codec) => new(
        "test#counter/add",
        "counter",
        "counter",
        "add",
        [new("value", "value", codec)],
        codec);

    private static TypertCodec JsonCodec(string schema) => TypertCodec.Create(
        RemoteInfoJson.Default.JsonElement,
        schema: Json(schema));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class DeferredDescriptors(TypertInvocationDescriptor descriptor)
        : IReadOnlyList<TypertInvocationDescriptor>
    {
        internal bool Fail = true;
        internal int Enumerations;
        public int Count => 1;

        public TypertInvocationDescriptor this[int index] =>
            index == 0 ? descriptor : throw new IndexOutOfRangeException();

        public IEnumerator<TypertInvocationDescriptor> GetEnumerator()
        {
            Enumerations++;
            if (Fail)
                throw new InvalidOperationException("transient index");
            return ((IEnumerable<TypertInvocationDescriptor>)new[] { descriptor }).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingCodec : TypertCodec
    {
        public int SchemaReads;
        public int Decodes;
        public int Failures;
        public override string TypeSymbol => "string";

        public override JsonElement Schema
        {
            get
            {
                SchemaReads++;
                if (Failures-- > 0)
                    throw new InvalidOperationException("transient factory");
                return Json("{\"type\":\"string\"}");
            }
        }

        public override object Decode(JsonElement value)
        {
            Decodes++;
            return "decoded:" + value.GetString();
        }

        public override JsonElement Encode(object? value) => Json("\"encoded\"");
    }

    private sealed class NamedRemote : ITypertNamedRemote
    {
        public readonly Task<object> Result = Task.FromResult<object>(new());
        public string? Endpoint;
        public IReadOnlyDictionary<string, object?>? Arguments;
        public CancellationToken Token;

        public object Invoke(
            string endpoint,
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken)
        {
            Endpoint = endpoint;
            Arguments = arguments;
            Token = cancellationToken;
            return Result;
        }
    }
}

public sealed class RemoteInfoInput
{
    private string name = "";

    public string Name
    {
        get => name;
        set => name = value.Trim().ToUpperInvariant();
    }

    public RemoteInfoInput? Child
    {
        get;
        set;
    }
}

public sealed record RemoteInfoBinary(byte[] Data, List<byte[]> Items);

public sealed class RemoteInfoOutput
{
    public string Label => "local";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteInfoInput))]
[JsonSerializable(typeof(RemoteInfoOutput))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(byte))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(RemoteInfoBinary))]
internal partial class RemoteInfoJson : JsonSerializerContext;
