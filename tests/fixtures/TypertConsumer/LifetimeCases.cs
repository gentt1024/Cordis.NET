using System.Text.Json;
using Cordis;
using Cordis.Composition;
using IndependentRemote;

internal static class LifetimeCases
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static async Task VerifyAsync()
    {
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var service = new EchoService();
        Fiber provider = null!;
        var definitions = new Dictionary<string, TypertInvocationDescriptor>();
        await root.RunAsync(async ctx =>
        {
            registry.Register(ctx, AuthorModule.Contribution());
            foreach (var method in new[] { "Hold", "Lookup", "Scoped", "BlockedStream" })
                definitions.Add(method, registry.GetLocal("sample/" + method)!);
            provider = ctx.Plugin(AuthorModule.Create(service));
            await provider.WaitAsync();
        });

        void DefinitionsStayActive()
        {
            foreach (var (method, descriptor) in definitions)
                Require(
                    ReferenceEquals(descriptor, registry.GetLocal("sample/" + method)),
                    "The stable definition changed during provider withdrawal: " + method);
        }

        async Task ReplaceServiceAsync()
        {
            await root.RunAsync(async ctx =>
            {
                DefinitionsStayActive();
                await provider.DisposeAsync();
                service = new EchoService();
                provider = ctx.Plugin(AuthorModule.Create(service));
                await provider.WaitAsync();
                DefinitionsStayActive();
            });
        }

        var oldService = service;
        var unary = gateway.InvokeAsync("sample/Hold", Json("{\"text\":\"old\"}"));
        await oldService.Entered.Task.WaitAsync(Limit);
        try
        {
            await ReplaceServiceAsync();
            Require(!unary.IsCompleted, "Service withdrawal actively completed a running unary call.");
        }
        finally
        {
            oldService.Release.TrySetResult();
        }

        Error(await unary.WaitAsync(Limit), "gateway/service-unavailable");
        var fresh = await gateway.InvokeAsync("sample/NullableEcho", Json("{\"text\":\"fresh\"}"));
        Require(fresh.Ok && fresh.Value!.Value.GetString() == "fresh", "The replacement service is unavailable.");

        var lookupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lookupResult = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectHandle lookup = null!;
        await root.RunAsync(ctx =>
        {
            lookup = registry.RegisterLookup(
                ctx,
                "@scope/document",
                new(
                    "document",
                    "documentId",
                    "IndependentRemote.Document",
                    "string",
                    _ =>
                    {
                        lookupEntered.TrySetResult();
                        return new ValueTask<object?>(lookupResult.Task);
                    }));
            return Task.CompletedTask;
        });
        var resolving = gateway.InvokeAsync("sample/Lookup", Json("{\"documentId\":\"doc\"}"));
        await lookupEntered.Task.WaitAsync(Limit);
        try
        {
            await root.RunAsync(async ctx =>
            {
                await lookup.DisposeAsync();
                registry.RegisterLookup(
                    ctx,
                    "@scope/document",
                    new(
                        "document",
                        "documentId",
                        "IndependentRemote.Document",
                        "string",
                        _ => ValueTask.FromResult<object?>(new Document("fresh document"))));
                DefinitionsStayActive();
            });
            Require(!resolving.IsCompleted, "Lookup withdrawal actively completed a running resolver.");
        }
        finally
        {
            lookupResult.TrySetResult(new Document("old document"));
        }

        Error(await resolving.WaitAsync(Limit), "gateway/lookup-unavailable");
        Require(service.LookupCalls == 0, "A withdrawn lookup result entered the business method.");
        var document = await gateway.InvokeAsync("sample/Lookup", Json("{\"documentId\":\"doc\"}"));
        Require(
            document.Ok && document.Value!.Value.GetString() == "fresh document" && service.LookupCalls == 1,
            "The replacement lookup did not enter the current service.");

        var contextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contextResult = new TaskCompletionSource<Context?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EffectHandle contextProvider = null!;
        Context oldReceiver = null!;
        await root.RunAsync(ctx =>
        {
            oldReceiver = ctx.Extend();
            oldReceiver.Metadata["prefix"] = "old";
            contextProvider = registry.RegisterHostContext(
                ctx,
                "@scope/example",
                new(
                    "contextId",
                    "string",
                    _ =>
                    {
                        contextEntered.TrySetResult();
                        return new ValueTask<Context?>(contextResult.Task);
                    }));
            return Task.CompletedTask;
        });
        var selecting = gateway.InvokeAsync("sample/Scoped", Json("{\"contextId\":\"scope\",\"text\":\"value\"}"));
        await contextEntered.Task.WaitAsync(Limit);
        try
        {
            await root.RunAsync(async ctx =>
            {
                await contextProvider.DisposeAsync();
                var receiver = ctx.Extend();
                receiver.Metadata["prefix"] = "fresh";
                registry.RegisterHostContext(
                    ctx,
                    "@scope/example",
                    new("contextId", "string", _ => ValueTask.FromResult<Context?>(receiver)));
                DefinitionsStayActive();
            });
            Require(!selecting.IsCompleted, "Context withdrawal actively completed a running resolver.");
        }
        finally
        {
            contextResult.TrySetResult(oldReceiver);
        }

        Error(await selecting.WaitAsync(Limit), "gateway/context-unavailable");
        Require(service.ScopedCalls == 0, "A withdrawn Context entered the business method.");
        var scoped = await gateway.InvokeAsync("sample/Scoped", Json("{\"contextId\":\"scope\",\"text\":\"value\"}"));
        Require(
            scoped.Ok && scoped.Value!.Value.GetString() == "fresh:value" && service.ScopedCalls == 1,
            "The replacement Context did not select the current receiver.");

        oldService = service;
        await using var stream = gateway.StreamAsync("sample/BlockedStream", Json("{}")).GetAsyncEnumerator();
        var reading = stream.MoveNextAsync().AsTask();
        await oldService.StreamEntered.Task.WaitAsync(Limit);
        try
        {
            await ReplaceServiceAsync();
            Require(!reading.IsCompleted, "Service withdrawal actively completed a running stream read.");
        }
        finally
        {
            oldService.StreamRelease.TrySetResult();
        }

        Require(await reading.WaitAsync(Limit), "The old stream did not report its generation failure.");
        Error(stream.Current, "gateway/service-unavailable");
        Require(
            oldService.StreamDisposed == 1 && oldService.StreamDisposalContext == "sample/BlockedStream",
            "The old stream did not finish disposal in its original invocation Context.");
        Require(!await stream.MoveNextAsync().AsTask().WaitAsync(Limit), "The old stream produced another item.");
        fresh = await gateway.InvokeAsync("sample/NullableEcho", Json("{\"text\":\"fresh stream service\"}"));
        Require(
            fresh.Ok && fresh.Value!.Value.GetString() == "fresh stream service",
            "The stream replacement is unavailable.");
        await root.RunAsync(_ =>
        {
            DefinitionsStayActive();
            return Task.CompletedTask;
        });
        Console.WriteLine(
            "PASS stable definitions: withdrawn unary Service, pending lookup, pending Context and pending downlink read reject old success; replacements remain callable.");
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void Error(TypertRemoteResult result, string code) => Require(
        !result.Ok && result.Error?.Code == code,
        "Expected " + code + ", got " + result.Error?.Code);
}
