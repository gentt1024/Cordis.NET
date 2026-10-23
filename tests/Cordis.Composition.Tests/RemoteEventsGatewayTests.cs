using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class RemoteEventsGatewayTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("ordinary", false)]
    [InlineData("unkeyed", false)]
    [InlineData("different-key", true)]
    [InlineData("different-subject-type", true)]
    public async Task Scoped_forwarding_checks_the_key_before_projecting(string kind, bool rejected)
    {
        await using var root = new Context();
        await root.RunAsync(async ctx =>
        {
            var projected = 0;
            var codec = TypertCodec.Create(NotificationTestsJson.Default.JsonElement);
            var definition = TypertRemoteEvents.Waterfall<Context, JsonElement, JsonElement>(
                "selected",
                codec,
                codec,
                subject =>
                {
                    projected++;
                    return new(subject, "agent");
                });
            var gateway = new TypertGateway(ctx, new TypertRegistry(ctx));
            await using var source = gateway.RegisterRemoteEvents(
                ctx,
                TypertRemoteEvents.FromCordis(ctx, [definition]),
                new("home"));
            object receiver = kind switch
            {
                "ordinary" => ctx,
                "unkeyed" => Scope.Target(ctx, null),
                "different-key" => Scope.Target(ctx, new object()),
                _ => Scope.Target(ctx, ctx)
            };
            object payload = kind == "different-subject-type"
                ? (new object(), Json("{}"), CancellationToken.None)
                : (ctx, Json("{}"), CancellationToken.None);
            var pending = TypertRemoteEvents.AwaitResult<JsonElement>(
                ctx.Events.WaterfallWith(
                    receiver,
                    "selected",
                    () => Task.FromResult<object?>(Json("\"local\"")),
                    payload));
            if (rejected)
                await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Limit));
            else
                Assert.Equal("local", (await pending.WaitAsync(Limit)).GetString());
            Assert.Equal(0, projected);
        });
    }

    [Fact]
    public async Task Withdrawing_selected_source_settles_every_queued_waterfall()
    {
        await using var root = new Context();
        Task<JsonElement>[] pending = [];
        await root.RunAsync(async ctx =>
        {
            var gateway = new TypertGateway(ctx, new TypertRegistry(ctx));
            var codec = TypertCodec.Create(NotificationTestsJson.Default.JsonElement);
            var definition = TypertRemoteEvents.Waterfall<Context, JsonElement, JsonElement>(
                "selected",
                codec,
                codec,
                subject => new(subject, "agent"));
            var source = gateway.RegisterRemoteEvents(
                ctx,
                TypertRemoteEvents.FromCordis(ctx, [definition]),
                new("home"));
            pending = Enumerable
                .Range(0, 3)
                .Select(_ => TypertRemoteEvents.AwaitResult<JsonElement>(
                    ctx.Events.WaterfallWith(
                        Scope.Target(ctx, ctx),
                        "selected",
                        () => Task.FromResult<object?>(Json("{}")),
                        (ctx, Json("{}"), CancellationToken.None))))
                .ToArray();
            await source.DisposeAsync();
        });
        foreach (var invocation in pending)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => invocation.WaitAsync(Limit));
    }

    [Fact]
    public async Task Event_source_cannot_cross_the_Gateway_root()
    {
        await using var root = new Context();
        await using var foreign = new Context();
        var gateway = new TypertGateway(root, new TypertRegistry(root));
        var source = new ControlledSource();
        await foreign.RunAsync(ctx =>
        {
            Assert.Throws<ArgumentException>(() =>
                gateway.RegisterRemoteEvents(ctx, source.Open, new TypertRemoteEventHost("")));
            Assert.Equal(0, source.Opened);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Selected_Cordis_events_reach_two_fixed_event_streams_after_subscription_readiness()
    {
        await using var root = new Context();
        var opened = 0;
        var (gateway, owner) = await StartAsync(
            root,
            host =>
            {
                var source = TypertRemoteEvents.FromCordis(
                    host,
                    [
                        new(
                            "selected",
                            [
                                TypertCodec.Create(NotificationTestsJson.Default.JsonElement),
                                TypertCodec.Create(NotificationTestsJson.Default.Int32)
                            ])
                    ]);
                return token =>
                {
                    opened++;
                    return source(token);
                };
            });
        await using var first = Open(gateway);
        await using var second = Open(gateway);
        await ReadyAsync(first);
        await ReadyAsync(second);
        Assert.Equal(1, opened);
        await root.RunAsync(ctx =>
        {
            ctx.Emit("unselected", new object());
            ctx.Emit("selected", Json("{\"value\":null,\"enabled\":false}"), 4);
            return Task.CompletedTask;
        });
        foreach (var client in new[] { first, second })
        {
            var frame = await EventAsync(client);
            Assert.Equal("selected", frame.GetProperty("event").GetString());
            var arguments = frame.GetProperty("args");
            Assert.Equal(2, arguments.GetArrayLength());
            Assert.Equal(JsonValueKind.Null, arguments[0].GetProperty("value").ValueKind);
            Assert.False(arguments[0].GetProperty("enabled").GetBoolean());
            Assert.Equal(4, arguments[1].GetInt32());
        }

        await first.DisposeAsync();
        await second.DisposeAsync();
        await owner.DisposeAsync();
        await root.RunAsync(ctx =>
        {
            Assert.False(ctx.Events.ListenerCounts.ContainsKey("selected"));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Disconnect_removes_only_its_observer_and_reconnection_does_not_replay_events()
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        using var cancellation = new CancellationTokenSource();
        await using var first = Open(gateway, cancellation.Token);
        await using var second = Open(gateway);
        await ReadyAsync(first);
        await ReadyAsync(second);
        var disconnectedRead = first.MoveNextAsync().AsTask();
        var retainedRead = second.MoveNextAsync().AsTask();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disconnectedRead.WaitAsync(Limit));
        Assert.False(await first.MoveNextAsync().AsTask().WaitAsync(Limit));
        Assert.False(source.Ended.Task.IsCompleted);
        source.Publish(new("selected", [Json("1")]));
        Assert.True(await retainedRead.WaitAsync(Limit));
        Assert.Equal(1, second.Current.Value!.Value.GetProperty("args")[0].GetInt32());
        await second.DisposeAsync();
        Assert.False(source.Ended.Task.IsCompleted);
        await using var reconnected = Open(gateway);
        await ReadyAsync(reconnected);
        source.Publish(new("selected", [Json("2")]));
        Assert.Equal(2, (await EventAsync(reconnected)).GetProperty("args")[0].GetInt32());
        Assert.Equal(1, source.Opened);
        await reconnected.DisposeAsync();
        await owner.DisposeAsync();
        await source.Ended.Task.WaitAsync(Limit);
    }

    [Fact]
    public async Task Source_owner_withdrawal_closes_all_pending_observers_and_joins_source_cleanup()
    {
        await using var root = new Context();
        var source = new ControlledSource(pauseCleanup: true);
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        await using var first = Open(gateway);
        await using var second = Open(gateway);
        await ReadyAsync(first);
        await ReadyAsync(second);
        var firstRead = first.MoveNextAsync().AsTask();
        var secondRead = second.MoveNextAsync().AsTask();
        var retirement = owner.DisposeAsync().AsTask();
        try
        {
            await source.CleanupEntered.Task.WaitAsync(Limit);
            foreach (var pending in new[] { firstRead, secondRead })
                Assert.False(await pending.WaitAsync(Limit));
            Assert.False(retirement.IsCompleted);
            Assert.False(source.Ended.Task.IsCompleted);
            await using var unavailable = Open(gateway);
            var unavailableError =
                await Assert.ThrowsAnyAsync<RemoteError>(() => unavailable.MoveNextAsync().AsTask().WaitAsync(Limit));
            Assert.Equal("gateway/service-unavailable", unavailableError.Code);

            var replacement = new ControlledSource();
            var replacementOwner = await RegisterSourceAsync(root, gateway, _ => replacement.Open);
            await using var later = Open(gateway);
            await ReadyAsync(later);
            source.AllowCleanup();
            await retirement.WaitAsync(Limit);
            Assert.True(source.Ended.Task.IsCompletedSuccessfully);
            replacement.Publish(new("replacement", [Json("8")]));
            Assert.Equal("replacement", (await EventAsync(later)).GetProperty("event").GetString());
            Assert.False(replacement.Ended.Task.IsCompleted);
            await later.DisposeAsync();
            await replacementOwner.DisposeAsync().AsTask().WaitAsync(Limit);
        }
        finally
        {
            source.AllowCleanup();
            await retirement.WaitAsync(Limit);
        }
    }

    [Theory]
    [InlineData("dispose")]
    [InlineData("cancelled-finally")]
    [InlineData("natural-finally")]
    public async Task Source_disposal_failure_is_retained_by_its_Fiber_and_blocks_plugin_replacement(
        string cleanupMode)
    {
        var reported = new List<Exception>();
        await using var root = new Context(reported.Add);
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var cleanupFailure = new IOException("The event source could not release its listener.");
        var source = new FailingCleanupSource(cleanupFailure, cleanupMode);
        var previous = new Plugin<object?>
        {
            Apply = (host, _) => gateway.RegisterRemoteEvents(host, source.Open, new TypertRemoteEventHost("")),
        };
        var candidateStarts = 0;
        var candidate = new Plugin<object?>
        {
            Apply = (_, _) => candidateStarts++,
        };
        Loader loader = null!;
        await root.RunAsync(ctx =>
        {
            loader = new(ctx, new StaticModuleResolver().Register("source", previous));
            return Task.CompletedTask;
        });
        await loader.Root.UpdateAsync(
        [
            new()
            {
                Id = "events",
                Name = "source"
            }
        ]);
        await loader.WaitAsync();
        var owner = loader.Resolve("events").Fiber!;
        await source.ReadStarted.Task.WaitAsync(Limit);
        await using var client = Open(gateway);
        await ReadyAsync(client);
        var pending = client.MoveNextAsync().AsTask();
        if (cleanupMode == "natural-finally")
        {
            source.EndNaturally();
            Assert.False(await pending.WaitAsync(Limit));
            Assert.False(source.CancelledAtCleanup);
        }

        var failure = await Assert.ThrowsAsync<IOException>(() =>
            loader.ReplacePluginAsync(previous, candidate).WaitAsync(Limit));

        Assert.Same(cleanupFailure, failure);
        Assert.False(await pending.WaitAsync(Limit));
        Assert.False(await client.MoveNextAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(0, candidateStarts);
        Assert.Equal(1, source.DisposeCalls);
        var outcome = PluginReplacementFailure.FromException(failure)!;
        Assert.Equal(PluginReplacementPhase.Retirement, outcome.Phase);
        Assert.Equal(PluginRecoveryState.NotAttempted, outcome.Recovery);
        Assert.Same(cleanupFailure, Assert.Single(outcome.CleanupErrors));
        await root.RunAsync(_ =>
        {
            Assert.Same(cleanupFailure, Assert.Single(owner.CleanupErrors));
            Assert.Contains(cleanupFailure, reported);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_failure_or_unexpected_completion_closes_observers_and_retains_the_owner_failure(bool fail)
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        await using var first = Open(gateway);
        await using var second = Open(gateway);
        await ReadyAsync(first);
        await ReadyAsync(second);
        var readFailure = fail ? new RemoteError("source/rejected", "source failure") : null;
        source.Complete(readFailure);
        foreach (var client in new[] { first, second })
        {
            Assert.False(await client.MoveNextAsync().AsTask().WaitAsync(Limit));
        }

        await owner.DisposeAsync().AsTask().WaitAsync(Limit);
        await root.RunAsync(_ =>
        {
            if (fail)
                Assert.Same(readFailure, Assert.Single(owner.CleanupErrors));
            else
                Assert.Contains("ended unexpectedly", Assert.Single(owner.CleanupErrors).Message);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Typed_event_encoding_failure_ends_delivery_without_failing_the_triggering_emit()
    {
        await using var root = new Context();
        var (gateway, owner) = await StartAsync(
            root,
            host => TypertRemoteEvents.FromCordis(
                host,
                [
                    new("selected", [TypertCodec.Create(NotificationTestsJson.Default.Int32)])
                ]));
        await using var client = Open(gateway);
        await ReadyAsync(client);
        var completed = false;
        await root.RunAsync(ctx =>
        {
            ctx.Emit("selected", "not-an-int");
            completed = true;
            return Task.CompletedTask;
        });
        Assert.True(completed);
        Assert.False(await client.MoveNextAsync().AsTask().WaitAsync(Limit));
        await owner.DisposeAsync().AsTask().WaitAsync(Limit);
        await root.RunAsync(ctx =>
        {
            Assert.False(ctx.Events.ListenerCounts.ContainsKey("selected"));
            Assert.Equal("gateway/event-invalid", Assert.IsType<RemoteError>(Assert.Single(owner.CleanupErrors)).Code);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Gateway_owns_json_snapshots_before_broadcasting_to_later_readers()
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        await using var first = Open(gateway);
        await using var second = Open(gateway);
        await ReadyAsync(first);
        await ReadyAsync(second);
        using var document = JsonDocument.Parse("{\"value\":7}");
        var arguments = new[] { document.RootElement };
        source.Publish(new("borrowed-json", arguments));
        Assert.Equal(7, (await EventAsync(first)).GetProperty("args")[0].GetProperty("value").GetInt32());
        document.Dispose();
        arguments[0] = Json("null");
        Assert.Equal(7, (await EventAsync(second)).GetProperty("args")[0].GetProperty("value").GetInt32());
        await first.DisposeAsync();
        await second.DisposeAsync();
        await owner.DisposeAsync().AsTask().WaitAsync(Limit);
    }

    [Fact]
    public async Task Undefined_source_arguments_are_rejected_instead_of_serialized_or_dropped()
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        await using var client = Open(gateway);
        await ReadyAsync(client);
        source.Publish(new("invalid", [default]));
        Assert.False(await client.MoveNextAsync().AsTask().WaitAsync(Limit));
        await owner.DisposeAsync().AsTask().WaitAsync(Limit);
        await root.RunAsync(_ =>
        {
            Assert.Contains(
                "must be JSON values",
                Assert.IsType<JsonException>(Assert.Single(owner.CleanupErrors)).Message);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("""{"kind":"next"}""", "next")]
    [InlineData("""{"kind":"result"}""", "undefined")]
    [InlineData("""{"kind":"result","value":null}""", "null")]
    [InlineData(
        """{"kind":"rejected","error":{"name":"BusinessRefusal","message":"Denied","code":"approval/denied","details":{"rule":7}}}""",
        "rejected")]
    public async Task Pending_waterfall_survives_disconnect_and_settles_once_from_current_delivery(
        string reply,
        string expected)
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        await using var first = Open(gateway);
        await ReadyAsync(first);
        var firstId = first.Current.Value!.Value.GetProperty("clientId").GetString()!;
        var outcome =
            new TaskCompletionSource<TypertRemoteEventOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var settlements = 0;
        source.Publish(
            new TypertRemoteEventInvocation(
                "approval/request",
                root,
                "agent-1",
                Json("{\"question\":\"allow?\"}"),
                CancellationToken.None,
                value =>
                {
                    settlements++;
                    outcome.TrySetResult(value);
                },
                error =>
                {
                    settlements++;
                    outcome.TrySetException(error);
                }));
        Assert.True(await first.MoveNextAsync().AsTask().WaitAsync(Limit));
        var original = first.Current.Value!.Value;
        Assert.Equal("waterfall", original.GetProperty("type").GetString());
        var eventId = original.GetProperty("eventId").GetString()!;
        await first.DisposeAsync();
        Assert.False(outcome.Task.IsCompleted);

        await using var current = Open(gateway);
        await ReadyAsync(current);
        var currentId = current.Current.Value!.Value.GetProperty("clientId").GetString()!;
        Assert.NotEqual(firstId, currentId);
        Assert.True(await current.MoveNextAsync().AsTask().WaitAsync(Limit));
        Assert.Equal(original.GetRawText(), current.Current.Value!.Value.GetRawText());
        var stale = await Reply(firstId);
        Assert.False(stale.Ok);
        Assert.False(outcome.Task.IsCompleted);
        Assert.True((await Reply(currentId)).Ok);
        if (expected == "rejected")
        {
            var failure = await Assert.ThrowsAsync<TypertRemoteEventException>(() => outcome.Task.WaitAsync(Limit));
            Assert.Equal("BusinessRefusal", failure.RemoteName);
            Assert.Equal("Denied", failure.Message);
            Assert.Equal("approval/denied", failure.Code);
            Assert.Equal(7, failure.Details!.Value.GetProperty("rule").GetInt32());
        }
        else if (expected == "next")
            Assert.IsType<TypertRemoteEventOutcome.Next>(await outcome.Task.WaitAsync(Limit));
        else
            Assert.Equal(
                expected == "null" ? JsonValueKind.Null : JsonValueKind.Undefined,
                Assert.IsType<TypertRemoteEventOutcome.Result>(await outcome.Task.WaitAsync(Limit)).Value.ValueKind);

        Assert.True((await Reply(currentId)).Ok);
        Assert.Equal(1, settlements);
        await current.DisposeAsync();
        await owner.DisposeAsync();

        Task<TypertRemoteResult> Reply(string clientId) => gateway.InvokeAsync(
            "$events/result",
            Json("{\"clientId\":\"" + clientId + "\",\"eventId\":\"" + eventId + "\",\"outcome\":" + reply + "}"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_closure_discards_buffered_notifications(bool fail)
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        await using var observer = Open(gateway);
        await using var barrier = Open(gateway);
        await ReadyAsync(observer);
        await ReadyAsync(barrier);
        source.Publish(new("selected", [Json("1")]));
        source.Publish(new("selected", [Json("2")]));
        await EventAsync(barrier);
        await EventAsync(barrier);
        if (fail)
        {
            source.Complete(new InvalidOperationException("source failed"));
            await source.Ended.Task.WaitAsync(Limit);
            Assert.False(await barrier.MoveNextAsync().AsTask().WaitAsync(Limit));
        }
        else
            await owner.DisposeAsync();

        Assert.False(await observer.MoveNextAsync().AsTask().WaitAsync(Limit));
        await owner.DisposeAsync();
    }

    [Fact]
    public async Task Owner_notifications_bypass_resource_readers()
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, owner) = await StartAsync(root, _ => source.Open);
        await using var observer = Open(gateway);
        await ReadyAsync(observer);
        var reads = 0;
        source.Publish(
            new TypertRemoteEventFrame("selected", [])
            {
                ResourceReaders = new Dictionary<string, TypertResourceReader>
                {
                    ["doc"] = (_, _) =>
                    {
                        reads++;
                        throw new InvalidOperationException("Owner must not resolve resources.");
                    }
                }
            });
        Assert.Equal("selected", (await EventAsync(observer)).GetProperty("event").GetString());
        Assert.Equal(0, reads);
        await observer.DisposeAsync();
        await owner.DisposeAsync();
    }

    [Theory]
    [InlineData("allowed")]
    [InlineData("denied")]
    [InlineData("empty")]
    [InlineData("missing")]
    [InlineData("throws")]
    public async Task Emit_resources_are_resolved_once_and_filter_whole_notifications(string mode)
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, sourceOwner) = await StartAsync(root, _ => source.Open);
        TypertPeer firstPeer = null!, secondPeer = null!;
        var reads = 0;
        var decisions = 0;
        await root.RunAsync(ctx =>
        {
            var access = new TypertRemoteAccess(ctx);
            ctx.Provide("remoteAccess", access);
            access.RegisterPolicy(
                ctx,
                "guest",
                new(_ =>
                {
                    decisions++;
                    return
                    [
                        new(new("observe", "doc", ["event:selected"]), TypertAccessResource.Create("doc", "a")),
                        new(new("marker", "global", ["event:marker"]), TypertAccessResource.Global)
                    ];
                }));
            firstPeer = new(ctx, new(new(Json("{\"kind\":\"guest\"}"))));
            secondPeer = new(ctx, new(new(Json("{\"kind\":\"guest\"}"))));
            return Task.CompletedTask;
        });
        await using var firstLifetime = firstPeer;
        await using var secondLifetime = secondPeer;
        await using var owner = Open(gateway);
        await using var first = gateway.StreamAsync("$events", Json("{}"), firstPeer).GetAsyncEnumerator();
        await using var second = gateway.StreamAsync("$events", Json("{}"), secondPeer).GetAsyncEnumerator();
        await ReadyAsync(owner);
        await ReadyAsync(first);
        await ReadyAsync(second);
        source.Publish(
            new TypertRemoteEventFrame("selected", [Json("{\"secret\":42}")])
            {
                ResourceReaders = new Dictionary<string, TypertResourceReader>
                {
                    ["doc"] = (arguments, _) =>
                    {
                        reads++;
                        Assert.Equal(42, Assert.IsType<JsonElement>(arguments[0]).GetProperty("secret").GetInt32());
                        return mode switch
                        {
                            "allowed" => ["a"],
                            "denied" => ["a", "b"],
                            "empty" => [],
                            "missing" => null,
                            _ => throw new InvalidOperationException("reader failed")
                        };
                    }
                }
            });
        Assert.Equal("selected", (await EventAsync(owner)).GetProperty("event").GetString());
        source.Publish(new("marker", []));
        foreach (var observer in new[] { first, second })
        {
            if (mode == "allowed")
                Assert.Equal(42, (await EventAsync(observer)).GetProperty("args")[0].GetProperty("secret").GetInt32());
            Assert.Equal("marker", (await EventAsync(observer)).GetProperty("event").GetString());
        }

        Assert.Equal(1, reads);
        Assert.Equal(mode is "allowed" or "denied" ? 4 : 2, decisions);
        await first.DisposeAsync();
        await second.DisposeAsync();
        await owner.DisposeAsync();
        await sourceOwner.DisposeAsync();
    }

    [Fact]
    public async Task Only_the_delivered_owner_peer_can_answer_and_guest_never_receives_waterfalls()
    {
        await using var root = new Context();
        var source = new ControlledSource();
        var (gateway, sourceOwner) = await StartAsync(root, _ => source.Open);
        TypertPeer owner = null!, other = null!, guest = null!;
        await root.RunAsync(ctx =>
        {
            owner = new(ctx, new(TypertPrincipal.Owner));
            other = new(ctx, new(TypertPrincipal.Owner));
            guest = new(ctx, new(new(Json("{\"kind\":\"guest\"}"))));
            var access = new TypertRemoteAccess(ctx);
            ctx.Provide("remoteAccess", access);
            access.RegisterPolicy(
                ctx,
                "guest",
                new(_ => [new(new("observe", "global", ["event:marker"]), TypertAccessResource.Global)]));
            return Task.CompletedTask;
        });
        await using var ownerLifetime = owner;
        await using var otherLifetime = other;
        await using var guestLifetime = guest;
        await using var ownerStream = gateway.StreamAsync("$events", Json("{}"), owner).GetAsyncEnumerator();
        await using var guestStream = gateway.StreamAsync("$events", Json("{}"), guest).GetAsyncEnumerator();
        await ReadyAsync(ownerStream);
        var clientId = ownerStream.Current.Value!.Value.GetProperty("clientId").GetString();
        await ReadyAsync(guestStream);
        var completion =
            new TaskCompletionSource<TypertRemoteEventOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Publish(
            new TypertRemoteEventInvocation(
                "approve",
                root,
                "agent",
                Json("{}"),
                default,
                value => completion.TrySetResult(value),
                error => completion.TrySetException(error)));
        Assert.True(await ownerStream.MoveNextAsync().AsTask().WaitAsync(Limit));
        var eventId = ownerStream.Current.Value!.Value.GetProperty("eventId").GetString();
        var result = Json(
            "{\"clientId\":\"" + clientId + "\",\"eventId\":\"" + eventId + "\",\"outcome\":{\"kind\":\"next\"}}");
        Assert.Equal(
            "gateway/interactions-forbidden",
            (await gateway.InvokeAsync("$events/result", result, other)).Error?.Code);
        Assert.Equal(
            "gateway/invocation-unavailable",
            (await gateway.InvokeAsync("$events/result", Json("null"), guest)).Error?.Code);
        Assert.False(completion.Task.IsCompleted);
        Assert.True((await gateway.InvokeAsync("$events/result", result, owner)).Ok);
        Assert.IsType<TypertRemoteEventOutcome.Next>(await completion.Task.WaitAsync(Limit));
        source.Publish(new("marker", []));
        Assert.Equal("marker", (await EventAsync(guestStream)).GetProperty("event").GetString());
        Assert.True((await gateway.InvokeAsync("$events/result", result, owner)).Ok);
        await ownerStream.DisposeAsync();
        await guestStream.DisposeAsync();
        await sourceOwner.DisposeAsync();
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("guest")]
    public async Task Passive_peers_cannot_subscribe_or_answer_before_payload_validation(string kind)
    {
        await using var root = new Context();
        var gateway = new TypertGateway(root, new TypertRegistry(root));
        TypertPeer peer = null!;
        await root.RunAsync(ctx =>
        {
            peer = new(ctx, new(new(Json("{\"kind\":\"" + kind + "\"}")), TypertPeerInteractions.None));
            return Task.CompletedTask;
        });
        await using var lifetime = peer;
        await using var stream = gateway.StreamAsync("$events", Json("null"), peer).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("gateway/interactions-forbidden", stream.Current.Error?.Code);
        Assert.Equal(
            "gateway/interactions-forbidden",
            (await gateway.InvokeAsync("$events/result", Json("null"), peer)).Error?.Code);
    }

    private static async Task<(TypertGateway Gateway, Fiber Owner)> StartAsync(
        Context root,
        Func<Context, TypertRemoteEventSource> select)
    {
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var owner = await RegisterSourceAsync(root, gateway, select);
        return (gateway, owner);
    }

    private static async Task<Fiber> RegisterSourceAsync(
        Context root,
        TypertGateway gateway,
        Func<Context, TypertRemoteEventSource> select)
    {
        Fiber owner = null!;
        await root.RunAsync(ctx =>
        {
            owner = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (host, _) =>
                        gateway.RegisterRemoteEvents(host, select(host), new TypertRemoteEventHost("")),
                });
            return Task.CompletedTask;
        });
        await owner.WaitAsync();
        return owner;
    }

    private static IAsyncEnumerator<TypertRemoteResult> Open(
        TypertGateway gateway,
        CancellationToken cancellationToken = default) =>
        gateway
            .StreamAsync("$events", Json("{}"), cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

    private static async Task ReadyAsync(IAsyncEnumerator<TypertRemoteResult> client)
    {
        Assert.True(await client.MoveNextAsync().AsTask().WaitAsync(Limit));
        Assert.True(client.Current.Ok, client.Current.Error?.Message);
        var frame = client.Current.Value!.Value;
        Assert.Equal("ready", frame.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(frame.GetProperty("clientId").GetString()));
        Assert.Equal("", frame.GetProperty("host").GetProperty("home").GetString());
        Assert.False(frame.TryGetProperty("event", out _));
        Assert.False(frame.TryGetProperty("args", out _));
    }

    private static async Task<JsonElement> EventAsync(IAsyncEnumerator<TypertRemoteResult> client)
    {
        Assert.True(await client.MoveNextAsync().AsTask().WaitAsync(Limit));
        Assert.True(client.Current.Ok, client.Current.Error?.Message);
        var frame = client.Current.Value!.Value;
        Assert.Equal("emit", frame.GetProperty("type").GetString());
        return frame;
    }

    private static JsonElement Json(string source)
    {
        using var document = JsonDocument.Parse(source);
        return document.RootElement.Clone();
    }

    private sealed class FailingCleanupSource(Exception failure, string cleanupMode)
        : IAsyncEnumerable<TypertRemoteEventFrame>, IAsyncEnumerator<TypertRemoteEventFrame>
    {
        private CancellationToken lifetime;
        private readonly TaskCompletionSource naturalEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int DisposeCalls
        {
            get;
            private set;
        }

        public bool CancelledAtCleanup
        {
            get;
            private set;
        }

        public TypertRemoteEventFrame Current => throw new InvalidOperationException("The source has no ready event.");

        public IAsyncEnumerable<TypertRemoteEventFrame> Open(CancellationToken cancellationToken)
        {
            lifetime = cancellationToken;
            return cleanupMode == "dispose" ? this : Iterate(cancellationToken);
        }

        public IAsyncEnumerator<TypertRemoteEventFrame> GetAsyncEnumerator(
            CancellationToken cancellationToken = default) => this;

        public async ValueTask<bool> MoveNextAsync()
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, lifetime);
            return false;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            CancelledAtCleanup = lifetime.IsCancellationRequested;
            return ValueTask.FromException(failure);
        }

        public void EndNaturally() => naturalEnd.TrySetResult();

        private async IAsyncEnumerable<TypertRemoteEventFrame> Iterate(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                ReadStarted.TrySetResult();
                if (cleanupMode == "natural-finally")
                    await naturalEnd.Task.WaitAsync(cancellationToken);
                else
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                yield break;
            }
            finally
            {
                DisposeCalls++;
                CancelledAtCleanup = cancellationToken.IsCancellationRequested;
                throw failure;
            }
        }
    }

    private sealed class ControlledSource
    {
        private readonly Channel<TypertRemoteEventDispatch>
            queue = Channel.CreateUnbounded<TypertRemoteEventDispatch>();

        private readonly TaskCompletionSource cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledSource(bool pauseCleanup = false)
        {
            if (!pauseCleanup)
                cleanup.TrySetResult();
        }

        public int Opened
        {
            get;
            private set;
        }

        public TaskCompletionSource Ended
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CleanupEntered
        {
            get;
        } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAsyncEnumerable<TypertRemoteEventDispatch> Open(CancellationToken cancellationToken)
        {
            Opened++;
            return Iterate(cancellationToken);
        }

        public void Publish(TypertRemoteEventFrame frame) => Publish((TypertRemoteEventDispatch)frame);

        public void Publish(TypertRemoteEventDispatch frame) => Assert.True(queue.Writer.TryWrite(frame));

        public void Complete(Exception? failure) => Assert.True(queue.Writer.TryComplete(failure));

        public void AllowCleanup() => cleanup.TrySetResult();

        private async IAsyncEnumerable<TypertRemoteEventDispatch> Iterate(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var frame in queue.Reader.ReadAllAsync(cancellationToken))
                    yield return frame;
            }
            finally
            {
                CleanupEntered.TrySetResult();
                await cleanup.Task;
                Ended.TrySetResult();
            }
        }
    }
}

[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(int))]
internal partial class NotificationTestsJson : JsonSerializerContext;
