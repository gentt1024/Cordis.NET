using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
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

[RemoteService("sharedData", typeof(SharedRemoteJson))]
[ArrayLabels("null", null, Types = null)]
[ArrayLabels("empty", new string[] { }, Types = new Type[] { })]
[ArrayLabels("values", new[] { "label" }, Types = new[] { typeof(string) })]
public partial class SharedRemote
{
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
[JsonSerializable(typeof(int?))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(string))]
internal partial class SharedRemoteJson : JsonSerializerContext;

// This is a live Host object, not a client DTO or a JSON registration.
public sealed class SharedDocument(string title)
{
    public string ReadTitle() => title;
}
