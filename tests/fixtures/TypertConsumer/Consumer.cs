using System.Text.Json;
using Cordis;
using Cordis.AspNetCore;
using Cordis.Composition;
using IndependentRemote;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

await using var root = new Context();
var service = new EchoService();
var contribution = AuthorModule.Contribution();
var registry = new TypertRegistry(root);
var gateway = new TypertGateway(root, registry);
Fiber provider = null!;
EffectHandle lookup = null!;
EffectHandle scope = null!;
TypertLoader contracts = null!;
Loader loader = null!;
var artifactResolver = new StaticTypertArtifactResolver();
artifactResolver.Register("author", AuthorModule.Contribution);
await root.RunAsync(async ctx =>
{
    ctx.Provide("typert", registry);
    ctx.Metadata["prefix"] = "caller";
    var receiver = ctx.Extend();
    receiver.Metadata["prefix"] = "selected";
    lookup = registry.RegisterLookup(
        ctx,
        "@scope/document",
        new(
            "document",
            "documentId",
            "IndependentRemote.Document",
            "string",
            value => ValueTask.FromResult<object?>(Equals(value, "doc-1") ? new Document("loaded") : null)));
    scope = registry.RegisterHostContext(
        ctx,
        "@scope/example",
        new(
            "contextId",
            "string",
            value => ValueTask.FromResult<Context?>(Equals(value, "scope-1") ? receiver : null)));
    provider = ctx.Plugin(AuthorModule.Create(service));
    await provider.WaitAsync();
    loader = new Loader(
        ctx,
        new StaticModuleResolver().Register(
            "author",
            new Plugin<object?>
            {
                Apply = (_, _) =>
                {
                }
            }));
    await loader.CreateAsync(
        new()
        {
            Id = "one",
            Name = "author"
        });
    await loader.CreateAsync(
        new()
        {
            Id = "two",
            Name = "author"
        });
    await loader.WaitAsync();
    contracts = await TypertLoader.StartAsync(ctx, loader, registry, artifactResolver);
});
var artifact = TypertArtifacts.GenerateClient(contribution);
try
{
    TypertArtifacts.GenerateClient(
        contribution with
        {
            Invocations = contribution.Invocations.Concat([contribution.Invocations[0]]).ToArray()
        });
    throw new InvalidOperationException("Duplicate generated Client endpoint was accepted.");
}
catch (ArgumentException)
{
}

Directory.CreateDirectory(args[0]);
await File.WriteAllTextAsync(Path.Combine(args[0], "remote.mjs"), artifact.Module);
await File.WriteAllTextAsync(Path.Combine(args[0], "remote.d.mts"), artifact.Declaration);
foreach (var method in new[] { "Echo", "Failure" })
{
    var part = TypertArtifacts.GenerateClient(
        contribution with
        {
            Invocations = contribution.Invocations.Where(descriptor => descriptor.Method == method).ToArray()
        });
    await File.WriteAllTextAsync(Path.Combine(args[0], method + ".mjs"), part.Module);
    await File.WriteAllTextAsync(Path.Combine(args[0], method + ".d.mts"), part.Declaration);
}

var dependent = TypertArtifacts.GenerateClient(
    contribution with
    {
        Invocations =
        [
            contribution.Invocations.First(descriptor => descriptor.Method == "Echo") with
            {
                Namespace = "dependent"
            }
        ]
    });
await File.WriteAllTextAsync(Path.Combine(args[0], "dependent.mjs"), dependent.Module);

if (args.Contains("--serve"))
{
    var builder = WebApplication.CreateSlimBuilder();
    builder.Logging.ClearProviders();
    var app = builder.Build();
    app.Urls.Add("http://127.0.0.1:0");
    app.MapCordisRemote(
        "/remote",
        gateway,
        (_, endpoint) => Task.FromResult(endpoint.StartsWith("sample/", StringComparison.Ordinal)));
    await app.StartAsync();
    Console.WriteLine(app.Urls.Single());
    Console.Out.Flush();
    await Task.Delay(Timeout.Infinite);
}

static JsonElement Json(string value)
{
    using var document = JsonDocument.Parse(value);
    return document.RootElement.Clone();
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Error(TypertRemoteResult result, string code) => Require(
    !result.Ok && result.Error?.Code == code,
    "Expected " + code + ", got " + result.Error?.Code);

var echo = await gateway.InvokeAsync("sample/Echo", Json("{\"request\":{\"Text\":\"hello\",\"Count\":3}}"));
Require(echo.Ok && echo.Value!.Value.GetProperty("Text").GetString() == "hello", "Typed echo failed.");
Error(await gateway.InvokeAsync("sample/Echo", Json("{\"request\":{\"Text\":\"missing\"}}")), "gateway/input-invalid");
Error(
    await gateway.InvokeAsync("sample/Echo", Json("{\"request\":{\"Text\":\"wrong\",\"Count\":\"three\"}}")),
    "gateway/input-invalid");
Error(
    await gateway.InvokeAsync("sample/Echo", Json("{\"request\":{\"Text\":\"x\",\"Count\":1},\"extra\":1}")),
    "gateway/arguments-invalid");
Error(await gateway.InvokeAsync("sample/Echo", Json("{\"request\":{},\"request\":{}}")), "gateway/arguments-invalid");
var refusal = await gateway.InvokeAsync("sample/Failure", Json("{\"text\":\"refused\"}"));
Error(refusal, "sample/refused");
Require(refusal.Error!.Details!.Value.GetProperty("reason").GetString() == "example", "Owner error details were lost.");
var scoped = await gateway.InvokeAsync("sample/Scoped", Json("{\"text\":\"value\",\"contextId\":\"scope-1\"}"));
Require(scoped.Ok && scoped.Value!.Value.GetString() == "selected:value", "Scoped receiver failed.");
var loaded = await gateway.InvokeAsync("sample/Lookup", Json("{\"documentId\":\"doc-1\"}"));
Require(loaded.Ok && loaded.Value!.Value.GetString() == "loaded", "Object lookup failed.");
Error(await gateway.InvokeAsync("sample/Lookup", Json("{\"documentId\":\"absent\"}")), "gateway/lookup-not-found");
await root.RunAsync(async _ => await lookup.DisposeAsync());
Error(await gateway.InvokeAsync("sample/Lookup", Json("{\"documentId\":\"doc-1\"}")), "gateway/lookup-unavailable");
await root.RunAsync(async _ => await scope.DisposeAsync());
Error(
    await gateway.InvokeAsync("sample/Scoped", Json("{\"text\":\"value\",\"contextId\":\"scope-1\"}")),
    "gateway/context-unavailable");
Error(await gateway.InvokeAsync("sample/Count", Json("{\"count\":2}")), "gateway/signature-invalid");
var count = new List<int>();
await foreach (var item in gateway.StreamAsync("sample/Count", Json("{\"count\":5}")))
{
    Require(item.Ok, "Stream failed.");
    count.Add(item.Value!.Value.GetInt32());
    if (count.Count == 2)
        break;
}

Require(
    count.SequenceEqual([0, 1]) && service.StreamDisposed == 1 && service.StreamDisposalContext == "sample/Count",
    "Stream cleanup lost its invocation or did not run.");
var binary = await gateway.InvokeAsync("sample/Binary", Json("{\"text\":\"bytes\"}"));
Require(
    binary.Ok && System.Text.Encoding.UTF8.GetString(binary.Value!.Value.GetBytesFromBase64()) == "bytes",
    "Native binary projection failed.");
using (var cancellation = new CancellationTokenSource())
{
    var call = gateway.InvokeAsync("sample/Hold", Json("{\"text\":\"cancelled\"}"), cancellation.Token);
    await service.Entered.Task;
    await cancellation.CancelAsync();
    Error(await call, "gateway/cancelled");
}

await root.RunAsync(async _ =>
{
    await loader.RemoveAsync("one");
    await contracts.WaitForIdleAsync();
    Require(registry.GetLocal("sample/Echo") is not null, "One live entry lost its package contract.");
});
var pending = gateway.InvokeAsync("sample/Hold", Json("{\"text\":\"old\"}"));
await root.RunAsync(async _ =>
{
    await loader.RemoveAsync("two");
    await contracts.WaitForIdleAsync();
    Require(registry.GetLocal("sample/Echo") is null, "Final entry did not withdraw its contract.");
    service.Release.TrySetResult();
});
Error(await pending, "gateway/definition-unavailable");
Error(
    await gateway.InvokeAsync("sample/Echo", Json("{\"request\":{\"Text\":\"old\",\"Count\":1}}")),
    "gateway/definition-unavailable");
Console.WriteLine(
    "PASS independent generated package: typed calls, strict input, errors, scope, lookup, cancellation, stream disposal, binary projection, last-entry withdrawal and old callback.");
