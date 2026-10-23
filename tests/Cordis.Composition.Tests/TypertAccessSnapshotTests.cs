using System.Text.Json;
using Xunit;

namespace Cordis.Composition.Tests;

public sealed class TypertAccessSnapshotTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly TypertAccessResource Room = TypertAccessResource.Create("room", "one");
    private static readonly TypertAccessResource Other = TypertAccessResource.Create("room", "two");

    [Fact]
    public async Task Snapshot_reads_bindings_once_preserves_resource_order_and_sorts_non_event_actions()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var reads = 0;
        var conditions = 0;
        await root.RunAsync(ctx =>
        {
            access.RegisterPolicy(
                ctx,
                "guest",
                new(
                    _ =>
                    {
                        reads++;
                        return
                        [
                            Bound(Other, "notes/z", "notes/a", "event:changed", "notes/a"),
                            Bound(Room, "notes/b"),
                            new(
                                new("condition", "room", ["notes/c", "notes/d"]),
                                Room,
                                question =>
                                {
                                    conditions++;
                                    Assert.Null(question.Arguments);
                                    return question.Action == "notes/c"
                                        ? TypertAccessDecision.Allowed
                                        : new TypertAccessDecision.Deny("no");
                                })
                        ];
                    },
                    _ => throw new Exception("Snapshots must not explain")));
            var frame = Assert.IsType<TypertAccessFrame.Baseline.Granted>(
                access.SnapshotFor(Principal("{\"kind\":\"guest\"}")));
            Assert.Equal([Other, Room], frame.Grants.Select(grant => grant.Resource));
            Assert.Equal(["notes/a", "notes/z"], frame.Grants[0].Actions);
            Assert.Equal(["notes/b", "notes/c"], frame.Grants[1].Actions);
            Assert.Equal(1, reads);
            Assert.Equal(2, conditions);
            Assert.IsType<TypertAccessFrame.Baseline.Owner>(access.SnapshotFor(TypertPrincipal.Owner));
            Assert.Equal(1, reads);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Registered_follow_is_callable_by_guest_and_targets_full_json_identity_with_coalesced_deltas()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        TypertPeer peer = null!;
        IReadOnlyList<TypertAccessBinding> held = [Bound(Room, "notes/a"), Bound(Other, "notes/b")];
        var reads = 0;
        await root.RunAsync(ctx =>
        {
            ctx.Provide("remoteAccess", access);
            access.RegisterRemote(ctx, registry);
            access.RegisterPolicy(
                ctx,
                "guest",
                new(_ =>
                {
                    reads++;
                    return held;
                }));
            peer = new(
                ctx,
                new(Principal("{\"kind\":\"guest\",\"id\":\"one\",\"nested\":{\"a\":[1,true],\"b\":null}}")));
            return Task.CompletedTask;
        });
        using var timeout = new CancellationTokenSource(Limit);
        using var args = JsonDocument.Parse("{}");
        await using var iterator = gateway
            .StreamAsync("remoteAccess/follow", args.RootElement, peer, timeout.Token)
            .GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.True(iterator.Current.Ok, iterator.Current.Error?.Message);
        Assert.IsType<TypertAccessFrame.Baseline.Granted>(TypertAccessFrame.FromJson(iterator.Current.Value!.Value));
        await root.RunAsync(_ =>
        {
            held = [Bound(Other, "notes/c")];
            access.Invalidate(
                Principal("{\"kind\":\"guest\",\"id\":\"other\",\"nested\":{\"a\":[1,true],\"b\":null}}"));
            return Task.CompletedTask;
        });
        var changed = iterator.MoveNextAsync().AsTask();
        await Task.Delay(25);
        Assert.False(changed.IsCompleted);
        Assert.Equal(1, reads);
        await root.RunAsync(_ =>
        {
            access.Invalidate(Principal("{\"nested\":{\"b\":null,\"a\":[1,true]},\"id\":\"one\",\"kind\":\"guest\"}"));
            access.Invalidate(peer.Principal);
            return Task.CompletedTask;
        });
        Assert.True(await changed.WaitAsync(Limit));
        var upsert = Assert.IsType<TypertAccessFrame.Upsert>(TypertAccessFrame.FromJson(iterator.Current.Value!.Value));
        Assert.Equal(Other, upsert.Grant.Resource);
        Assert.Equal(["notes/c"], upsert.Grant.Actions);
        Assert.True(await iterator.MoveNextAsync());
        Assert.Equal(
            Room,
            Assert.IsType<TypertAccessFrame.Remove>(TypertAccessFrame.FromJson(iterator.Current.Value!.Value))
                .Resource);
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Policy_registration_and_withdrawal_update_open_guest_followers()
    {
        await using var root = new Context();
        var access = new TypertRemoteAccess(root);
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        TypertPeer peer = null!;
        EffectHandle policy = null!;
        await root.RunAsync(ctx =>
        {
            ctx.Provide("remoteAccess", access);
            access.RegisterRemote(ctx, registry);
            peer = new(ctx, new(Principal("{\"kind\":\"guest\"}")));
            return Task.CompletedTask;
        });
        using var timeout = new CancellationTokenSource(Limit);
        using var args = JsonDocument.Parse("{}");
        await using var iterator = gateway
            .StreamAsync("remoteAccess/follow", args.RootElement, peer, timeout.Token)
            .GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        Assert.Empty(
            Assert.IsType<TypertAccessFrame.Baseline.Granted>(TypertAccessFrame.FromJson(iterator.Current.Value!.Value))
                .Grants);
        await root.RunAsync(ctx =>
        {
            policy = access.RegisterPolicy(ctx, "guest", new(_ => [Bound(Room, "notes/read")]));
            return Task.CompletedTask;
        });
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<TypertAccessFrame.Upsert>(TypertAccessFrame.FromJson(iterator.Current.Value!.Value));
        await policy.DisposeAsync();
        Assert.True(await iterator.MoveNextAsync());
        Assert.IsType<TypertAccessFrame.Remove>(TypertAccessFrame.FromJson(iterator.Current.Value!.Value));
    }

    [Fact]
    public async Task Withdrawing_service_ends_owner_generation_and_remount_starts_with_new_baseline()
    {
        await using var root = new Context();
        Fiber mounted = null!;
        TypertRemoteAccess access = null!;
        await root.RunAsync(ctx =>
        {
            mounted = ctx.Plugin(
                new Plugin<object?>
                {
                    Apply = (child, _) =>
                    {
                        access = new(child);
                        child.Provide("remoteAccess", access);
                        access.SnapshotFor(TypertPrincipal.Owner);
                    }
                });
            return Task.CompletedTask;
        });
        await mounted.WaitAsync();
        IAsyncEnumerator<TypertAccessFrame> iterator = null!;
        await root.RunAsync(async _ =>
        {
            iterator = access.FollowAsync().GetAsyncEnumerator();
            Assert.True(await iterator.MoveNextAsync());
            Assert.IsType<TypertAccessFrame.Baseline.Owner>(iterator.Current);
        });
        var pending = root.RunAsync(async _ => Assert.False(await iterator.MoveNextAsync()));
        await mounted.DisposeAsync();
        await pending.WaitAsync(Limit);
        await iterator.DisposeAsync();
        await root.RunAsync(async _ =>
        {
            await using var stale = access.FollowAsync().GetAsyncEnumerator();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.MoveNextAsync().AsTask());
        });
    }

    [Fact]
    public void Client_sources_are_stable_replace_atomically_and_fail_closed_except_owner()
    {
        var state = new TypertAccessState();
        var source = state.Resource("room", "one", "notes/read");
        Assert.Same(source, state.Resource("room", "one", "notes/read"));
        Assert.False(source.Value);
        var changes = 0;
        using var subscription = source.Subscribe(() => changes++);
        state.Replace(new TypertAccessFrame.Baseline.Granted([new(Room, ["notes/read"])]));
        Assert.True(source.Value);
        Assert.False(state.Global("notes/read").Value);
        state.Update(new TypertAccessFrame.Remove(Room));
        Assert.False(source.Value);
        state.Update(new TypertAccessFrame.Upsert(new(Room, ["notes/read"])));
        Assert.True(source.Value);
        state.Unavailable();
        Assert.False(source.Value);
        state.Replace(new TypertAccessFrame.Baseline.Owner());
        state.Unavailable();
        Assert.True(source.Value);
        Assert.Equal(5, changes);
    }

    [Fact]
    public void Duplicate_baseline_resources_use_the_last_grant_and_ignored_updates_do_not_notify()
    {
        var state = new TypertAccessState();
        var source = state.Resource("room", "one", "notes/read");
        var changes = 0;
        using var subscription = source.Subscribe(() => changes++);
        state.Update(new TypertAccessFrame.Upsert(new(Room, ["notes/read"])));
        Assert.Equal(0, changes);
        state.Replace(
            new TypertAccessFrame.Baseline.Granted(
            [
                new(Room, ["notes/read"]), new(Room, ["notes/write"])
            ]));
        Assert.False(source.Value);
        Assert.True(state.Resource("room", "one", "notes/write").Value);
        state.Replace(new TypertAccessFrame.Baseline.Owner());
        state.Update(new TypertAccessFrame.Remove(Room));
        state.Unavailable();
        Assert.Equal(2, changes);
        Assert.True(source.Value);
    }

    [Theory]
    [InlineData("{\"type\":\"baseline\",\"owner\":true,\"grants\":[]}")]
    [InlineData("{\"type\":\"baseline\",\"owner\":false}")]
    [InlineData("{\"type\":\"upsert\",\"grant\":{\"resource\":{\"kind\":\"global\",\"id\":\"x\"},\"actions\":[]}}")]
    [InlineData("{\"type\":\"remove\",\"resource\":{\"kind\":\"room\",\"id\":\"\"}}")]
    [InlineData("{\"type\":\"unknown\"}")]
    public void Frame_decoder_refuses_malformed_closed_protocol(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => TypertAccessFrame.FromJson(document.RootElement));
    }

    [Fact]
    public void Frames_round_trip_exact_wire_shapes()
    {
        TypertAccessFrame[] frames =
        [
            new TypertAccessFrame.Baseline.Owner(),
            new TypertAccessFrame.Baseline.Granted([new(Room, ["notes/read"])]),
            new TypertAccessFrame.Upsert(new(TypertAccessResource.Global, ["notes/list"])),
            new TypertAccessFrame.Remove(Room)
        ];
        foreach (var frame in frames)
        {
            var value = frame.ToJson();
            Assert.True(JsonElement.DeepEquals(value, TypertAccessFrame.FromJson(value).ToJson()));
        }

        Assert.Equal("{\"type\":\"baseline\",\"owner\":true}", frames[0].ToJson().GetRawText());
    }

    private static TypertAccessBinding Bound(TypertAccessResource resource, params string[] actions) =>
        new(new("role", resource.Kind, actions), resource);

    private static TypertPrincipal Principal(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new(document.RootElement);
    }
}
