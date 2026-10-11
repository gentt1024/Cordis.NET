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
using var client = new SharedClient(http, new Uri(host, "/remote"));
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
var response = await client.EchoAsync(request, cancellationToken: lifetime.Token);
Check(
    response.Name == "remote" && response.Note is null && response.Count == 32 &&
    response.Child is { Name: "nested", Count: 17 },
    "saved Remote model maps serializer names, recursion, omitted nullable optional and constant default over HTTP");
var changed = await client.EchoAsync(request, adjustment: -2, replacementCount: 101, cancellationToken: lifetime.Token);
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
    await scoped.ScopedReadAsync() == "selected:default",
    "Context invocation selects the Host receiver with a typed identity");
Check(await client.ReadAsync("doc-2") == "root:doc-2!", "direct lookup remains an explicit wire-identity call");
Check(
    await scoped.ReadAsync() == "root:doc-1!",
    "lookup scope supplies the object identity without changing the receiver");
await caller.RunAsync(owner =>
{
    owner.Metadata["document"] = "doc-2";
    return Task.CompletedTask;
});
Check(await scoped.ReadAsync("?") == "root:doc-2?", "a retained view re-reads caller identity");
var streamed = new List<string>();
await foreach (var value in scoped.WatchAsync(cancellationToken: lifetime.Token)) streamed.Add(value);
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
Check(await scoped.ReadAsync() == "root:doc-2!", "a retained view observes the replacement adapter");
using (var withdrawn = await http.PostAsync("/control/withdraw-lookup", null)) withdrawn.EnsureSuccessStatusCode();
await RemoteFailure(() => scoped.ReadAsync(), "gateway/lookup-unavailable");
using (var withdrawn = await http.PostAsync("/control/withdraw-context", null)) withdrawn.EnsureSuccessStatusCode();
await RemoteFailure(() => scoped.ScopedReadAsync(), "gateway/context-unavailable");

{
    using var body = new StringContent(
        """{"args":{"value":{"display_name":"invalid"},"replacementCount":null}}""",
        Encoding.UTF8,
        "application/json");
    using var rejected = await http.PostAsync("/remote/sharedData/echo", body);
    rejected.EnsureSuccessStatusCode();
    using var document = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
    Check(
        !document.RootElement.GetProperty("ok").GetBoolean() &&
        document.RootElement.GetProperty("error").GetProperty("code").GetString() == "gateway/input-invalid",
        "nullable optional CLR parameter distinguishes omission from an explicit wire null");
}

client.Dispose();
await RemoteFailure(() => scoped.ReadAsync(), "gateway/internal");
using (var stopped = await http.PostAsync("/control/stop", null))
    stopped.EnsureSuccessStatusCode();
Console.WriteLine("saved Remote declaration HTTP consumer passed");

async Task LocalFailure(Func<Task<string>> call, string message)
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

static async Task RemoteFailure(Func<Task<string>> call, string code)
{
    try
    {
        await call();
    }
    catch (RemoteError error) when (error.Code == code)
    {
        return;
    }

    throw new Exception("Expected Host refusal: " + code);
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
