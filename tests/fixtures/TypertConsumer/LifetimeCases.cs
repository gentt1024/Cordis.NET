using System.Text.Json;
using Cordis;
using Cordis.Composition;
using IndependentRemote;

internal static class LifetimeCases
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    public static async Task VerifyAsync()
    {
        await VerifyCallerViewsAsync();
        await using var root = new Context();
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(root, registry);
        var service = new ContextualEchoService();
        Fiber provider = null!;
        EffectHandle serviceRegistration = null!;

        Plugin<object?> ProviderPlugin() => new()
        {
            Apply = (ctx, _) => serviceRegistration = ctx.Provide("sample:remote", service)
        };

        var definitions = new Dictionary<string, TypertInvocationDescriptor>();
        await root.RunAsync(async ctx =>
        {
            registry.Register(ctx, AuthorModule.Contribution());
            foreach (var method in new[] { "Hold", "Lookup", "Scoped", "BlockedStream" })
                definitions.Add(method, registry.GetLocal("sample/" + method)!);
            provider = ctx.Plugin(ProviderPlugin());
            await provider.WaitAsync();
        });

        void DefinitionsStayActive()
        {
            foreach (var (method, descriptor) in definitions)
                Require(
                    ReferenceEquals(descriptor, registry.GetLocal("sample/" + method)),
                    "The stable definition changed during provider withdrawal: " + method);
        }

        async Task ReplaceServiceAsync(bool reuseObject = false)
        {
            await root.RunAsync(async ctx =>
            {
                DefinitionsStayActive();
                await provider.DisposeAsync();
                if (!reuseObject)
                    service = new ContextualEchoService();
                provider = ctx.Plugin(ProviderPlugin());
                await provider.WaitAsync();
                DefinitionsStayActive();
            });
        }

        var oldService = service;
        foreach (var mutation in new[]
                 {
                     "different-object", "same-object", "same-fiber", "notify", "contextual-read", "contextual-set"
                 })
        {
            await ReplaceServiceAsync();
            oldService = service;
            var oldProvider = provider;
            var reentered = false;
            var unary = gateway.InvokeAsync("sample/Hold", Json("{\"text\":\"old\"}"));
            await oldService.Entered.Task.WaitAsync(Limit);
            try
            {
                switch (mutation)
                {
                    case "different-object":
                    case "same-object":
                        await ReplaceServiceAsync(mutation == "same-object");
                        Require(!ReferenceEquals(oldProvider, provider), "The provider Fiber did not change.");
                        Require(
                            ReferenceEquals(oldService, service) == (mutation == "same-object"),
                            "The replacement did not exercise the selected object identity.");
                        break;
                    case "same-fiber":
                        await root.RunAsync(async _ =>
                        {
                            await serviceRegistration.DisposeAsync();
                            serviceRegistration = provider.Context.Provide("sample:remote", service);
                        });
                        break;
                    case "notify":
                        await root.RunAsync(ctx =>
                        {
                            provider.Context.Reflect.Set("sample:remote", service);
                            provider.Context.Reflect.Notify("sample:remote");
                            ctx.Isolate("sample:remote").Provide("sample:remote", new EchoService());
                            return Task.CompletedTask;
                        });
                        break;
                    case "contextual-read":
                    case "contextual-set":
                        await root.RunAsync(_ =>
                        {
                            var tracedService = service;
                            tracedService.ReadAction = () =>
                            {
                                tracedService.ReadAction = null;
                                if (mutation == "contextual-set")
                                {
                                    service = new ContextualEchoService();
                                    provider.Context.Reflect.Set("sample:remote", service);
                                }
                                else
                                {
                                    var cleanup = serviceRegistration.DisposeAsync();
                                    Require(
                                        cleanup.IsCompletedSuccessfully,
                                        "Reentrant provider cleanup did not settle.");
                                    cleanup.GetAwaiter().GetResult();
                                    serviceRegistration = provider.Context.Provide("sample:remote", service);
                                }

                                reentered = true;
                            };
                            return Task.CompletedTask;
                        });
                        break;
                }

                if (mutation is "same-fiber" or "notify" or "contextual-read" or "contextual-set")
                    Require(
                        ReferenceEquals(oldProvider, provider),
                        "An entry-only change replaced the provider Fiber.");
                await root.RunAsync(_ =>
                {
                    DefinitionsStayActive();
                    Require(!unary.IsCompleted, "Provider mutation actively completed a running unary call.");
                    return Task.CompletedTask;
                });
            }
            finally
            {
                oldService.Release.TrySetResult();
            }

            var completed = await unary.WaitAsync(Limit);
            if (mutation == "notify")
                Require(
                    completed.Ok && completed.Value!.Value.GetString() == "old",
                    "Notification retired a live registration.");
            else
                Error(completed, "gateway/service-unavailable");
            if (mutation is "contextual-read" or "contextual-set")
                Require(reentered, "The result check did not exercise contextual service reentry.");
            var current = await gateway.InvokeAsync("sample/NullableEcho", Json("{\"text\":\"fresh\"}"));
            Require(current.Ok && current.Value!.Value.GetString() == "fresh", "The current service is unavailable.");
            Console.WriteLine("PASS unary registration: " + mutation);
        }

        await ReplaceServiceAsync();
        oldService = service;
        var admissionReentered = false;
        await root.RunAsync(_ =>
        {
            oldService.ReadAction = () =>
            {
                oldService.ReadAction = null;
                service = new ContextualEchoService();
                provider.Context.Reflect.Set("sample:remote", service);
                admissionReentered = true;
            };
            return Task.CompletedTask;
        });
        var admitting = gateway.InvokeAsync("sample/Hold", Json("{\"text\":\"not admitted\"}"));
        try
        {
            var settled = await Task.WhenAny(admitting, oldService.Entered.Task).WaitAsync(Limit);
            Require(
                ReferenceEquals(settled, admitting) && !oldService.Entered.Task.IsCompleted,
                "A provider Set during the first contextual Get entered the old business method.");
            Require(admissionReentered, "Admission did not exercise the first contextual Get's provider Set.");
            Error(await admitting.WaitAsync(Limit), "gateway/service-unavailable");
        }
        finally
        {
            oldService.Release.TrySetResult();
            await admitting.WaitAsync(Limit);
        }

        await root.RunAsync(_ =>
        {
            DefinitionsStayActive();
            return Task.CompletedTask;
        });
        Console.WriteLine("PASS unary admission: contextual Set rejects the old view before business entry.");

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

    private static async Task VerifyCallerViewsAsync()
    {
        await using var root = new Context();
        var caller = root.Extend();
        var registry = new TypertRegistry(root);
        var gateway = new TypertGateway(caller, registry);
        var state = new CallerViewState(caller);
        CallerViewService provider = null!;
        var definitions = new Dictionary<string, TypertInvocationDescriptor>();
        await root.RunAsync(ctx =>
        {
            registry.Register(ctx, CallerViewServiceTypert.Contribution("IndependentCallerView"));
            provider = new CallerViewService(ctx, state);
            Require(
                !ReferenceEquals(provider.Provider, caller),
                "The fixture did not separate the service owner from its caller.");
            foreach (var method in new[] { "Ping", "Hold", "Count" })
                definitions.Add(method, registry.GetLocal("callerView/" + method)!);
            for (var index = 0;index < 3;index++)
            {
                var first = caller.Get<CallerViewService>("caller-view:remote", strict: false);
                var second = caller.Get<CallerViewService>("caller-view:remote", strict: false);
                Require(
                    first is not null && second is not null &&
                    !ReferenceEquals(first, second) && !ReferenceEquals(first, provider),
                    "The standard Service fixture did not create distinct caller views on repeated Get.");
            }

            return Task.CompletedTask;
        });

        void DefinitionsStayActive()
        {
            foreach (var (method, descriptor) in definitions)
                Require(
                    ReferenceEquals(descriptor, registry.GetLocal("callerView/" + method)),
                    "The caller-view definition changed during a provider value mutation: " + method);
        }

        for (var index = 0;index < 2;index++)
        {
            var result = await gateway.InvokeAsync("callerView/Ping", Json("{}"));
            Require(
                result.Ok && result.Value!.Value.GetString() == "caller-view" && state.Calls == index + 1,
                $"An unchanged provider with a fresh caller view was rejected before its business method: ok={result.Ok}; error={result.Error?.Code}; calls={state.Calls}.");
        }

        var items = new List<int>();
        await foreach (var item in gateway.StreamAsync("callerView/Count", Json("{\"count\":3}")))
        {
            Require(item.Ok, "An unchanged provider with fresh caller views rejected a downlink read.");
            items.Add(item.Value!.Value.GetInt32());
        }

        Require(
            items.SequenceEqual([0, 1, 2]) && state.StreamCalls == 1 && state.Calls == 2,
            "The caller-view stream lost its shared State or business values.");
        Console.WriteLine(
            "PASS caller views: distinct repeated Get views share State and use the actual unary/downlink caller.");

        foreach (var mutation in new[] { "same-value-set-notify", "different-provider-set" })
        {
            await root.RunAsync(ctx =>
            {
                state = new CallerViewState(caller);
                provider = new CallerViewService(ctx.Isolate("caller-view:remote"), state);
                ctx.Reflect.Set("caller-view:remote", provider);
                DefinitionsStayActive();
                return Task.CompletedTask;
            });
            var admittedProvider = provider;
            var admittedState = state;
            var pending = gateway.InvokeAsync("callerView/Hold", Json("{\"text\":\"held\"}"));
            await admittedState.Entered.Task.WaitAsync(Limit);
            try
            {
                await root.RunAsync(ctx =>
                {
                    if (mutation == "different-provider-set")
                    {
                        state = new CallerViewState(caller);
                        provider = new CallerViewService(ctx.Isolate("caller-view:remote"), state);
                        Require(
                            !ReferenceEquals(admittedProvider, provider) &&
                            ReferenceEquals(admittedProvider.Provider.Fiber, provider.Provider.Fiber),
                            "Raw provider replacement did not change the value within the same owner Fiber.");
                    }

                    ctx.Reflect.Set("caller-view:remote", provider);
                    ctx.Reflect.Notify("caller-view:remote");
                    DefinitionsStayActive();
                    Require(
                        !pending.IsCompleted,
                        "Provider Set/Notify actively completed a running caller-view method.");
                    return Task.CompletedTask;
                });
            }
            finally
            {
                admittedState.Release.TrySetResult();
            }

            var completed = await pending.WaitAsync(Limit);
            if (mutation == "same-value-set-notify")
                Require(
                    completed.Ok && completed.Value!.Value.GetString() == "held",
                    "Same-value Set/Notify retired an unchanged caller-view provider.");
            else
                Error(completed, "gateway/service-unavailable");
            Require(
                admittedState.Calls == 1,
                "The pending caller view did not use its provider's shared State exactly once.");
            var fresh = await gateway.InvokeAsync("callerView/Ping", Json("{}"));
            Require(
                fresh.Ok && fresh.Value!.Value.GetString() == "caller-view" &&
                state.Calls == (mutation == "same-value-set-notify" ? 2 : 1),
                "The current raw provider was not callable through a fresh caller view.");
            await root.RunAsync(_ =>
            {
                DefinitionsStayActive();
                return Task.CompletedTask;
            });
            Console.WriteLine("PASS caller-view provider mutation: " + mutation);
        }
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

    private sealed class ContextualEchoService : EchoService, IContextualService
    {
        public Action? ReadAction
        {
            get;
            set;
        }

        public object ForContext(Context context)
        {
            ReadAction?.Invoke();
            return this;
        }
    }
}
