using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cordis;
using Cordis.Composition;

namespace IndependentRemote;

public sealed record EchoRequest(string Text, int Count);

public sealed record EchoReply(string Text, int Count);

public sealed record Document(string Title);

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoReply))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(byte[]))]
public partial class RemoteJson : JsonSerializerContext;

[RemoteService("sample:remote", typeof(RemoteJson), Namespace = "sample")]
public partial class EchoService
{
    public TaskCompletionSource Entered
    {
        get;
    } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release
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
    public Task<string> Scoped(string text) =>
        Task.FromResult(TypertInvocation.Current!.Context.Metadata["prefix"] + ":" + text);

    [RemoteMethod]
    public Task<string> Lookup([RemoteLookup("@scope/document", "documentId", typeof(string))] Document document) =>
        Task.FromResult(document.Title);

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
}

public static class AuthorModule
{
    public static IPlugin Create(EchoService service) => new Plugin<object?>
    {
        Apply = (context, _) => context.Provide("sample:remote", service)
    };

    public static TypertContribution Contribution() => EchoServiceTypert.Contribution("IndependentRemote");
}
