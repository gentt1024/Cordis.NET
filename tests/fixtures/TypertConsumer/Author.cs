using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.Composition;

namespace IndependentRemote;

public sealed record EchoRequest(string Text, int Count);

public sealed record EchoReply(string Text, int Count);

public sealed record Document(string Title);

public sealed record TreeNode(string Value, TreeNode[] Children);

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoReply))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(byte[]))]
[JsonSerializable(typeof(TreeNode))]
[JsonSerializable(typeof(JsonElement))]
public partial class RemoteJson : JsonSerializerContext;

[RemoteService("sample:remote", typeof(RemoteJson), Namespace = "sample")]
public partial class EchoService
{
    public int LookupCalls
    {
        get;
        private set;
    }

    public int ScopedCalls
    {
        get;
        private set;
    }

    public TaskCompletionSource Entered
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StreamEntered
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource StreamRelease
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int StreamDisposed
    {
        get;
        private set;
    }

    public string? StreamDisposalContext
    {
        get;
        private set;
    }

    [RemoteMethod]
    public Task<EchoReply> Echo(EchoRequest request) => Task.FromResult(new EchoReply(request.Text, request.Count));

    [RemoteMethod]
    public Task<JsonElement> Tuple(JsonElement value) => Task.FromResult(value);

    [RemoteMethod]
    public Task<string?> NullableEcho(string? text) => Task.FromResult(text);

    [RemoteMethod]
    public Task<string?> ReturnsNull() => Task.FromResult<string?>(null);

    [RemoteMethod]
    public Task<string> UnrequestedCancellation() => Task.FromException<string>(new OperationCanceledException());

    [RemoteMethod]
    public Task<TreeNode?> NullableTree(TreeNode? tree) => Task.FromResult(tree);

    [RemoteMethod]
    public Task<string> Failure(string text)
    {
        using var details = JsonDocument.Parse("{\"reason\":\"example\"}");
        throw new RemoteError("sample/refused", text, details.RootElement);
    }

    [RemoteMethod]
    public async Task<string> Hold(string text, CancellationToken signal)
    {
        Entered.TrySetResult();
        await Release.Task.WaitAsync(signal);
        return text;
    }

    [RemoteMethod(Context = "@scope/example")]
    public Task<string> Scoped(string text)
    {
        ScopedCalls++;
        return Task.FromResult(TypertInvocation.Current!.Context.Metadata["prefix"] + ":" + text);
    }

    [RemoteMethod]
    public Task<string> Lookup([RemoteLookup("@scope/document", "documentId", typeof(string))] Document document)
    {
        LookupCalls++;
        return Task.FromResult(document.Title);
    }

    [RemoteMethod(Stream = true)]
    public async IAsyncEnumerable<int> Count(int count, [EnumeratorCancellation] CancellationToken signal)
    {
        try
        {
            for (var index = 0;index < count;index++)
            {
                await Task.Delay(1, signal);
                yield return index;
            }
        }
        finally
        {
            StreamDisposed++;
            StreamDisposalContext = TypertInvocation.Current?.Endpoint;
        }
    }

    [RemoteMethod]
    public Task<byte[]> Binary(string text) => Task.FromResult(System.Text.Encoding.UTF8.GetBytes(text));

    [RemoteMethod(Stream = true)]
    public async IAsyncEnumerable<string> BlockedStream([EnumeratorCancellation] CancellationToken signal)
    {
        try
        {
            StreamEntered.TrySetResult();
            await StreamRelease.Task;
            yield return "late";
        }
        finally
        {
            StreamDisposed++;
            StreamDisposalContext = TypertInvocation.Current?.Endpoint;
        }
    }
}

public static class AuthorModule
{
    public static IPlugin Create(EchoService service) => new Plugin<object?>
    {
        Apply = (context, _) => context.Provide("sample:remote", service)
    };

    public static TypertContribution Contribution() =>
        TupleContract.Apply(EchoServiceTypert.Contribution("IndependentRemote"));
}
