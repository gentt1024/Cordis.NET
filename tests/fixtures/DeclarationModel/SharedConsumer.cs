using System.Text;
using System.Text.Json;
using Cordis;
using Cordis.Composition;
using IndependentDeclarations.Wire;

if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new Exception("Shared declaration consumer enabled reflection fallback.");
var host = new Uri(args.Single());
var transport = new CountingHandler();
using var http = new HttpClient(transport)
{
    BaseAddress = host,
    Timeout = TimeSpan.FromSeconds(20)
};
http.DefaultRequestHeaders.Authorization = new("consumer-policy");
await using var connection = new TypertConnection(http, new Uri(host, "/remote"));
await using var client = new SharedClient(connection);
await using (var text = await client.InputTextAsync("prefix:"))
{
    try
    {
        text.Send(null!);
        throw new Exception("Non-null input accepted null.");
    }
    catch (JsonException)
    {
    }

    text.Send("first");
    text.Send("second");
    text.End();
    text.End();
    try
    {
        text.Send("late");
        throw new Exception("Input after End was accepted.");
    }
    catch (InvalidOperationException)
    {
    }

    Check(
        (await Collect(text)).SequenceEqual(new[] { "prefix:first", "prefix:second", "ended" }),
        "pre-read sends are ordered and half-close preserves the downlink's final item");
}

await using (var nullable = await client.InputNullableAsync())
{
    nullable.Send(null);
    nullable.Send("text");
    nullable.End();
    Check((await Collect(nullable)).SequenceEqual(new[] { "null", "text" }), "nullable reference uplink");
}

await using (var number = await client.InputNumberAsync())
{
    number.Send(null);
    number.Send(17);
    number.End();
    Check((await Collect(number)).SequenceEqual(new[] { "null", "17" }), "nullable value uplink");
}

await using (var list = await client.InputListAsync())
{
    list.Send(null);
    list.Send(["text", null]);
    list.End();
    Check(
        (await Collect(list)).SequenceEqual(new[] { "null-list", "text,null-item" }),
        "root and nested collection nullability");
}

await using (var optional = await client.InputOptionalAsync())
{
    optional.Send(RemoteOptional<string?>.Undefined);
    optional.Send(new(null));
    optional.Send(new("text"));
    optional.End();
    Check(
        (await Collect(optional)).SequenceEqual(new[] { "undefined", "null", "text" }),
        "absent item and explicit null remain distinct");
}

static async Task<List<string>> Collect(IAsyncEnumerable<string> values)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var result = new List<string>();
    await foreach (var value in values.WithCancellation(timeout.Token))
        result.Add(value);
    return result;
}

Check((await client.AcknowledgeAsync()).Ok, "Task method returns no-value success");
Check((await client.NullableAsync()).Value is null, "explicit null remains a nullable success value");
Check(
    (await client.BytesAsync()).Value.SequenceEqual(new byte[] { 0, 13, 10, 255 }),
    "root multipart bytes round-trip");
var attachments = (await client.AttachmentsAsync()).Value;
Check(
    attachments.Items.Length == 2 && attachments.Items[0].SequenceEqual(new byte[] { 0, 13, 10, 255 }) &&
    attachments.Items[1].Length == 0 && attachments.Files["part/name"].SequenceEqual(new byte[] { 1, 2 }) &&
    attachments.Files["empty"].Length == 0 && attachments.Text == "AA0K/w==",
    "nested, empty and named binary paths preserve ordinary strings");
foreach (var malformed in new[]
         {
             "missing",
             "extra",
             "duplicate-field",
             "duplicate-part",
             "wrong-path",
             "wrong-placeholder",
             "metadata-file",
             "failed-result"
         })
{
    using var invalidHttp = new HttpClient(new MultipartReply(malformed));
    await using var invalidConnection = new TypertConnection(invalidHttp, new Uri("http://invalid.test/remote"));
    await using var invalidClient = new SharedClient(invalidConnection);
    var rejected = await invalidClient.BytesAsync();
    Check(!rejected.Ok && rejected.Error.Code == "gateway/internal", "invalid multipart refused: " + malformed);
}

var peer = (await client.PeerAsync()).Value;
Check(
    (await client.PeerRoutingAsync()).Value == "global:peer,peer:peer,global:child,peer:child,child:child,global:none",
    "operator Peer scope routes only matching and ancestor listeners");
await using (var first = await client.HoldAsync())
await using (var second = await client.HoldAsync())
{
    await using var firstReader = first.GetAsyncEnumerator();
    await using var secondReader = second.GetAsyncEnumerator();
    Check(
        await firstReader.MoveNextAsync() && firstReader.Current == peer,
        "unary and stream admit the same operator Peer");
    await first.DisposeAsync();
    Check(
        await secondReader.MoveNextAsync() && secondReader.Current == peer,
        "one stream cancellation preserves its sibling");
}

await using (var anotherConnection = new TypertConnection(http, new Uri(host, "/remote")))
await using (var anotherClient = new SharedClient(anotherConnection))
await using (var held = await anotherClient.HoldAsync())
{
    await using var reader = held.GetAsyncEnumerator();
    Check(
        await reader.MoveNextAsync() && reader.Current == peer,
        "another physical socket borrows the same operator Peer");
}

Check((await client.PeerAsync()).Value == peer, "socket disposal does not dispose the Host Peer");
using var lifetime = new CancellationTokenSource();
var request = new SharedData
{
    Name = "remote",
    Note = null,
    Count = 29,
    Child = new SharedData
    {
        Name = "nested",
        Count = 17
    }
};
var response = (await client.EchoAsync(request, cancellationToken: lifetime.Token)).Value;
Check(
    response.Name == "remote" && response.Note is null && response.Count == 32 &&
    response.Child is { Name: "nested", Count: 17 },
    "saved Remote model maps serializer names, recursion, omitted nullable optional and constant default over HTTP");
var changed =
    (await client.EchoAsync(request, adjustment: -2, replacementCount: 101, cancellationToken: lifetime.Token)).Value;
Check(changed.Count == 99, "explicit optional values reach the real author method");

await using var caller = new Context();
var registry = new TypertRegistry(caller);
EffectHandle clientAdapter = null!;
await caller.RunAsync(owner =>
{
    owner.Metadata["document"] = "doc-1";
    owner.Metadata["receiver"] = 7;
    clientAdapter = registry.RegisterClientContext(
        owner,
        "shared:document",
        new(ctx => ctx.Metadata["document"], _ => null));
    registry.RegisterClientContext(owner, "shared:receiver", new(ctx => ctx.Metadata["receiver"], _ => null));
    return Task.CompletedTask;
});
var scoped = client.ForContext(caller, registry);
Check(
    (await scoped.ScopedReadAsync()).Value == "selected:default",
    "Context invocation selects the Host receiver with a typed identity");
Check((await client.ReadAsync("doc-2")).Value == "root:doc-2!", "direct lookup remains an explicit wire-identity call");
Check(
    (await scoped.ReadAsync()).Value == "root:doc-1!",
    "lookup scope supplies the object identity without changing the receiver");
await caller.RunAsync(owner =>
{
    owner.Metadata["document"] = "doc-2";
    return Task.CompletedTask;
});
Check((await scoped.ReadAsync("?")).Value == "root:doc-2?", "a retained view re-reads caller identity");
var streamed = new List<string>();
await using (var stream = await scoped.WatchAsync(cancellationToken: lifetime.Token))
    await foreach (var value in stream)
        streamed.Add(value);
Check(streamed.SequenceEqual(["root:doc-2", "complete"]), "scoped downlink uses the same identity and stream carrier");
await caller.RunAsync(owner =>
{
    owner.Metadata["document"] = Undefined.Value;
    return Task.CompletedTask;
});
await LocalFailure(() => scoped.ReadAsync(), "does not represent Context");
await caller.RunAsync(owner =>
{
    owner.Metadata["document"] = null;
    return Task.CompletedTask;
});
var beforeNull = transport.Requests;
await RemoteFailure(() => scoped.ReadAsync(), "gateway/input-invalid");
Check(transport.Requests == beforeNull + 1, "explicit null reached Host input validation");
await caller.RunAsync(async owner =>
{
    await clientAdapter.DisposeAsync();
    owner.Metadata["document"] = "doc-1";
});
await LocalFailure(() => scoped.ReadAsync(), "No Client Context adapter");
await caller.RunAsync(owner =>
{
    registry.RegisterClientContext(owner, "shared:document", new(_ => "doc-2", _ => null));
    return Task.CompletedTask;
});
Check((await scoped.ReadAsync()).Value == "root:doc-2!", "a retained view observes the replacement adapter");
using (var withdrawn = await http.PostAsync("/control/withdraw-lookup", null)) withdrawn.EnsureSuccessStatusCode();
await RemoteFailure(() => scoped.ReadAsync(), "gateway/lookup-unavailable");
using (var withdrawn = await http.PostAsync("/control/withdraw-context", null)) withdrawn.EnsureSuccessStatusCode();
await RemoteFailure(() => scoped.ScopedReadAsync(), "gateway/context-unavailable");

{
    using var body = new StringContent(
        """{"type":"client-request","rpcId":"explicit-null","method":"sharedData/echo","payload":{"args":{"value":{"display_name":"invalid"},"replacementCount":null}}}""",
        Encoding.UTF8,
        "application/json");
    using var rejected = await http.PostAsync("/remote/sharedData/echo", body);
    rejected.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
    Check(
        !document.RootElement.GetProperty("result").GetProperty("ok").GetBoolean() &&
        document.RootElement.GetProperty("result").GetProperty("error").GetProperty("code").GetString() ==
        "gateway/input-invalid",
        "nullable optional CLR parameter distinguishes omission from an explicit wire null");
}

await client.DisposeAsync();
await RemoteFailure(() => scoped.ReadAsync(), "gateway/internal");
using (var stopped = await http.PostAsync("/control/stop", null))
    stopped.EnsureSuccessStatusCode();
Console.WriteLine("saved Remote declaration HTTP consumer passed");

async Task LocalFailure(Func<Task<RemoteResult<string>>> call, string message)
{
    var before = transport.Requests;
    try
    {
        await call();
    }
    catch (InvalidOperationException error) when (error.Message.Contains(message, StringComparison.Ordinal))
    {
        Check(transport.Requests == before, "Client identity refusal sent a request");
        return;
    }

    throw new Exception("Expected Client refusal: " + message);
}

static async Task RemoteFailure(Func<Task<RemoteResult<string>>> call, string code)
{
    var result = await call();
    Check(!result.Ok && result.Error.Code == code, "Expected Host refusal: " + code);
}

static void Check(bool value, string message)
{
    if (!value)
        throw new Exception(message);
}

sealed class CountingHandler() : DelegatingHandler(new HttpClientHandler())
{
    private int requests;
    public int Requests => Volatile.Read(ref requests);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        return base.SendAsync(request, cancellationToken);
    }
}

// Only the response carrier is controlled; the public generated caller parses it.
sealed class MultipartReply(string mode) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        var envelope = System.Text.Json.Nodes.JsonNode.Parse(
                """{"type":"server-response","rpcId":"","result":{"ok":true,"value":null},"attachments":[{"codec":"bytes","part":"bytes-0","path":[]}]}""")
            !;
        envelope["rpcId"] = body.RootElement.GetProperty("rpcId").GetString();
        if (mode == "wrong-path")
            envelope["attachments"]![0]!["path"]!.AsArray().Add((System.Text.Json.Nodes.JsonNode)"absent");
        if (mode == "wrong-placeholder")
            envelope["result"]!["value"] = 7;
        if (mode == "duplicate-part")
            envelope["attachments"]!.AsArray().Add(envelope["attachments"]![0]!.DeepClone());
        if (mode == "failed-result")
            envelope["result"]!["ok"] = false;
        var multipart = new MultipartFormDataContent("fixture-boundary");
        var metadata = new StringContent(envelope.ToJsonString());
        if (mode == "metadata-file")
            multipart.Add(metadata, "metadata", "blob");
        else
            multipart.Add(metadata, "metadata");
        if (mode != "missing")
            multipart.Add(new ByteArrayContent([0, 255]), "bytes-0", "blob");
        if (mode == "extra")
            multipart.Add(new StringContent("unused"), "extra");
        if (mode == "duplicate-field")
            multipart.Add(new ByteArrayContent([]), "bytes-0", "blob");
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = multipart
        };
    }
}
