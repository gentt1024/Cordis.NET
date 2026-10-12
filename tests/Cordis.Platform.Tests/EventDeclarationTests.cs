using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Cordis.AspNetCore;
using Cordis.Composition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Xunit;
using Xunit.Abstractions;

namespace Cordis.Platform.Tests
{
    public sealed class EventDeclarationTests(ITestOutputHelper output)
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void Nested_collection_nullability_is_independent_of_export_order(bool strictFirst, bool invalid)
        {
            var codec = EventAlpha.NestedTypert.Definition.Args[strictFirst ? 1 : 0];
            output.WriteLine(codec.Schema.GetRawText());
            using var json = JsonDocument.Parse(
                invalid
                    ? """{"Loose":[[null]],"Strict":[[null]]}"""
                    : """{"Loose":[[null]],"Strict":[["required"]]}""");
            if (invalid)
                Assert.Throws<JsonException>(() => codec.Decode(json.RootElement));
            else
                Assert.NotNull(codec.Decode(json.RootElement));
        }

        [Fact]
        public void Ignored_member_does_not_override_the_active_json_member()
        {
            var ordinary = TypertCodec.Create(IgnoredDeclarationsJson.Default.IgnoredShadowsNullable);
            var generated = EventAlpha.IgnoredShadowTypert.Definition.Args[0];
            var encoded = generated.Encode(new IgnoredShadowsNullable());
            output.WriteLine(generated.Schema.GetRawText());
            Assert.Equal(JsonValueKind.Null, encoded.GetProperty("value").ValueKind);
            Assert.Null(Assert.IsType<IgnoredShadowsNullable>(ordinary.Decode(encoded)).AActual);
            Assert.Null(Assert.IsType<IgnoredShadowsNullable>(generated.Decode(encoded)).AActual);
        }

        [Fact]
        public async Task Generated_event_codecs_and_source_listener_preserve_root_and_element_nullability()
        {
            var definition = EventAlpha.ChangedTypert.Definition;
            Assert.Equal("alpha/changed", definition.Event);
            Assert.Equal("beta/changed", EventBeta.ChangedTypert.Definition.Event);
            var values = definition.Args[0];
            Assert.Equal(
                new string?[] { "text", null },
                Assert.IsType<List<string?>>(
                    values.Decode(
                        values.Encode(
                            new List<string?>
                            {
                                "text",
                                null
                            }))));
            Assert.Null(values.Decode(values.Encode(null)));
            var labels = definition.Args[1];
            Assert.Null(
                Assert.IsType<Dictionary<string, string?>>(
                    labels.Decode(
                        labels.Encode(
                            new Dictionary<string, string?>
                            {
                                ["empty"] = null
                            })))["empty"]);
            Assert.Throws<JsonException>(() =>
            {
                definition
                    .Args[2]
                    .Decode(
                        values.Encode(
                            new List<string?>
                            {
                                null
                            }));
            });

            await using var host = new Context();
            var gateway = new TypertGateway(host, new TypertRegistry(host));
            TypertHostConnection hostConnection = null!;
            await host.RunAsync(ctx =>
            {
                gateway.RegisterRemoteEvents(ctx, TypertRemoteEvents.FromCordis(ctx, [definition]), new("test-home"));
                hostConnection = new(ctx, gateway);
                return Task.CompletedTask;
            });
            await using var ownedConnection = hostConnection;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var app = builder.Build();
            app.UseWebSockets();
            app.MapCordisRemote("/remote", hostConnection, (_, _) => Task.FromResult(true));
            await app.StartAsync();
            using var http = new HttpClient();
            await using var carrier = new TypertConnection(http, new Uri(app.Urls.Single() + "/remote"));
            await using var owner = new Context();
            await using var events = new TypertRemoteEventsClient(owner, new TypertRegistry(owner), carrier);
            var received = Channel
                .CreateUnbounded<(List<string?>? Values, Dictionary<string, string?> Labels, List<string> Required)>();
            await owner.RunAsync(ctx =>
            {
                EventAlpha.ChangedTypert.On(
                    events,
                    ctx,
                    (items, map, required) => received.Writer.TryWrite((items, map, required)));
                return Task.CompletedTask;
            });
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var pump = events.RunAsync(_ => ready.TrySetResult());
            try
            {
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await host.RunAsync(ctx =>
                {
                    ctx.Emit(
                        "alpha/changed",
                        new List<string?>
                        {
                            null
                        },
                        new Dictionary<string, string?>
                        {
                            ["nil"] = null
                        },
                        new List<string>
                        {
                            "required"
                        });
                    return Task.CompletedTask;
                });
                var item = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Null(Assert.Single(item.Values!));
                Assert.Null(item.Labels["nil"]);
                Assert.Equal("required", Assert.Single(item.Required));
            }
            finally
            {
                await events.DisposeAsync();
                await pump.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    public sealed class IgnoredShadowsNullable
    {
        [JsonPropertyName("value")]
        public string? AActual
        {
            get;
            set;
        }

        [JsonIgnore, JsonPropertyName("value")]
        public string ZIgnored
        {
            get;
            set;
        } = "ignored";
    }

    [JsonSerializable(typeof(IgnoredShadowsNullable))]
    public partial class IgnoredDeclarationsJson : JsonSerializerContext;
}

namespace Cordis.Platform.Tests.EventAlpha
{
    [RemoteEvent("alpha/ignored-shadow", typeof(IgnoredDeclarationsJson))]
    public delegate void IgnoredShadow(IgnoredShadowsNullable value);
}
