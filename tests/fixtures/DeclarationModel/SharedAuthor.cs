using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.Composition;

[assembly: TypertContext("shared:receiver", typeof(int))]
[assembly: TypertContext("shared:document", typeof(string))]

namespace IndependentDeclarations;

// One authored DTO is selected as an ordinary root and used at the Remote boundary.
public sealed class SharedData
{
    [JsonPropertyName("display_name")]
    public required string Name
    {
        get;
        set;
    }

    public string? Note
    {
        get;
        set;
    } = "initial";

    public int Count
    {
        get;
        set;
    } = 7;

    public SharedData? Child
    {
        get;
        set;
    }
}

public sealed class SharedEnvelope
{
    [JsonPropertyName("child")]
    public SharedData? Child
    {
        get;
        set;
    }
}

public sealed record SharedAttachments(byte[][] Items, Dictionary<string, byte[]> Files, string Text)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public byte[]? Optional
    {
        get;
        init;
    }

    public SharedAttachmentNode? Child
    {
        get;
        init;
    }
}

public sealed record SharedAttachmentNode(byte[] Payload, SharedAttachmentNode? Next);

[RemoteService("sharedData", typeof(SharedRemoteJson))]
[ArrayLabels("null", null, Types = null)]
[ArrayLabels("empty", new string[] { }, Types = new Type[] { })]
[ArrayLabels("values", new[] { "label" }, Types = new[] { typeof(string) })]
public partial class SharedRemote
{
    [RemoteMethod("inputText", Stream = true)]
    public async IAsyncEnumerable<string> InputTextAsync(
        string prefix,
        [RemoteUplink] IAsyncEnumerable<string> input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in input.WithCancellation(cancellationToken))
            yield return prefix + item;
        yield return "ended";
    }

    [RemoteMethod("inputNullable", Stream = true)]
    public async IAsyncEnumerable<string> InputNullableAsync(
        [RemoteUplink] IAsyncEnumerable<string?> input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in input.WithCancellation(cancellationToken))
            yield return item ?? "null";
    }

    [RemoteMethod("inputCaught", Stream = true)]
    public async IAsyncEnumerable<string> InputCaughtAsync([RemoteUplink] IAsyncEnumerable<string> input)
    {
        await using var reader = input.GetAsyncEnumerator();
        try
        {
            await reader.MoveNextAsync();
        }
        catch (Exception)
        {
        }

        yield return "must-not-escape";
    }

    [RemoteMethod("inputNumber", Stream = true)]
    public async IAsyncEnumerable<string> InputNumberAsync(
        [RemoteUplink] IAsyncEnumerable<int?> input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in input.WithCancellation(cancellationToken))
            yield return item?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }

    [RemoteMethod("inputList", Stream = true)]
    public async IAsyncEnumerable<string> InputListAsync(
        [RemoteUplink] IAsyncEnumerable<List<string?>?> input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in input.WithCancellation(cancellationToken))
            yield return item is null ? "null-list" : string.Join(",", item.Select(value => value ?? "null-item"));
    }

    [RemoteMethod("inputOptional", Stream = true)]
    public async IAsyncEnumerable<string> InputOptionalAsync(
        [RemoteUplink] IAsyncEnumerable<RemoteOptional<string?>> input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in input.WithCancellation(cancellationToken))
            yield return item.HasValue ? item.Value ?? "null" : "undefined";
    }

    [RemoteMethod("bytes")]
    public Task<byte[]> BytesAsync() => Task.FromResult(new byte[] { 0, 13, 10, 255 });

    [RemoteMethod("attachments")]
    public Task<SharedAttachments> AttachmentsAsync() => Task.FromResult(
        new SharedAttachments(
            [new byte[] { 0, 13, 10, 255 }, []],
            new()
            {
                ["part/name"] = [1, 2],
                ["empty"] = []
            },
            "AA0K/w==")
        {
            Child = new([4, 5], new([], null))
        });

    [RemoteMethod("acknowledge")]
    public Task AcknowledgeAsync() => Task.CompletedTask;

    [RemoteMethod("nullable")]
    public Task<string?> NullableAsync() => Task.FromResult<string?>(null);

    [RemoteMethod("peer")]
    public Task<string> PeerAsync() => Task.FromResult(TypertInvocation.Current!.Peer!.Id);

    [RemoteMethod("peerRouting")]
    public async Task<string> PeerRoutingAsync()
    {
        var invocation = TypertInvocation.Current!;
        var owner = invocation.Context;
        var peer = invocation.Peer!;
        await using var child = Scope.Create(owner, new object(), peer);
        await using var other = Scope.Create(owner, new object());
        var seen = new List<string>();
        await using var global = Listen(owner, "global");
        await using var selected = Listen(peer.Context, "peer");
        await using var nested = Listen(child.Context, "child");
        await using var sibling = Listen(other.Context, "other");
        owner.Events.EmitWith(Scope.Target(owner, peer), "consumer/peer-routing", "peer");
        owner.Events.EmitWith(Scope.Target(owner, Scope.Of(child.Context)), "consumer/peer-routing", "child");
        owner.Events.EmitWith(Scope.Target(owner, null), "consumer/peer-routing", "none");
        return string.Join(",", seen);

        EffectHandle Listen(Context context, string name) => context.On(
            "consumer/peer-routing",
            (_, args) =>
            {
                seen.Add(name + ":" + args[0]);
                return Undefined.Value;
            });
    }

    [RemoteMethod("scopeLayers")]
    public async Task<string> ScopeLayersAsync()
    {
        var invocation = TypertInvocation.Current!;
        var owner = invocation.Context;
        var peer = invocation.Peer!;
        var layers = new ScopedLayers<ChoiceLayer>(
            _ => new(),
            () =>
            {
            });
        await using var global = layers.Effect(
            owner,
            layer => layer.Choices.Insert("theme", "global"),
            "global-choice");
        await using var selected = layers.Effect(
            peer.Context,
            layer => layer.Choices.Insert("theme", "peer"),
            "peer-choice");
        var childKey = new object();
        await using var child = Scope.Create(owner, childKey, peer);
        layers.Effect(child.Context, layer => layer.Choices.Insert("theme", "child"), "child-choice");
        var nested = layers.Merge(childKey, layer => layer.Choices)["theme"];
        await child.DisposeAsync();
        if (layers.Peek(childKey) is not null)
            throw new InvalidOperationException("The child contribution outlived its owner.");
        var inherited = layers.Merge(childKey, layer => layer.Choices)["theme"];
        await selected.DisposeAsync();
        return nested + ">" + inherited + ">" + layers.Merge(childKey, layer => layer.Choices)["theme"];
    }

    private sealed class ChoiceLayer : IScopeLayer
    {
        public NamedEntries<string> Choices
        {
            get;
        } = new(name => new InvalidOperationException("Duplicate choice: " + name));

        public bool IsEmpty => Choices.IsEmpty;
    }

    [RemoteMethod("hold", Stream = true)]
    public async IAsyncEnumerable<string> HoldAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return TypertInvocation.Current!.Peer!.Id;
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    [RemoteMethod("scopedRead", Context = "shared:receiver", Wire = "receiverId")]
    public Task<string> ScopedReadAsync(string text = "default") =>
        Task.FromResult(TypertInvocation.Current!.Context.Metadata["prefix"] + ":" + text);

    [RemoteMethod("read")]
    public Task<string> ReadAsync(
        [RemoteLookup("shared:document", "documentId", typeof(string))]
        SharedDocument document,
        string suffix = "!") => Task.FromResult(
        TypertInvocation.Current!.Context.Metadata["prefix"] + ":" + document.ReadTitle() + suffix);

    [RemoteMethod("watch", Stream = true)]
    public async IAsyncEnumerable<string> WatchAsync(
        [RemoteLookup("shared:document", "documentId", typeof(string))]
        SharedDocument document,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return TypertInvocation.Current!.Context.Metadata["prefix"] + ":" + document.ReadTitle();
        yield return "complete";
    }

    [RemoteMethod("echo")]
    public Task<SharedData> EchoAsync(
        SharedData value,
        int adjustment = 3,
        [DisallowNull] int? replacementCount = null!,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new SharedData
            {
                Name = value.Name,
                Note = value.Note,
                Count = (replacementCount ?? value.Count) + adjustment,
                Child = value.Child
            });
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(SharedData))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(SharedAttachments))]
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(List<string?>))]
internal partial class SharedRemoteJson : JsonSerializerContext;

// This is a live Host object, not a client DTO or a JSON registration.
public sealed class SharedDocument(string title)
{
    public string ReadTitle() => title;
}
