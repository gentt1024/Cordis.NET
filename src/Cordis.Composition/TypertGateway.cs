using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Cordis.Composition;

/// <summary>Direct, generated invocation of a current service instance.</summary>
public delegate Task<object?> TypertUnaryInvoker(
    object service,
    IReadOnlyList<object?> arguments,
    CancellationToken cancellationToken);

/// <summary>Direct, generated iteration of a current service instance.</summary>
public delegate IAsyncEnumerable<object?> TypertStreamInvoker(
    object service,
    IReadOnlyList<object?> arguments,
    CancellationToken cancellationToken);

/// <summary>Visible Service-to-Gateway binding, independent of its generated descriptors.</summary>
public sealed record TypertRemoteBinding(
    string Service,
    string Namespace,
    IReadOnlyDictionary<string, TypertUnaryInvoker> Methods,
    IReadOnlyDictionary<string, TypertStreamInvoker> Streams);

/// <summary>A service explicitly exporting generated Remote methods.</summary>
public interface ITypertRemoteService
{
    /// <summary>Gets the service's explicit native binding.</summary>
    TypertRemoteBinding TypertRemote
    {
        get;
    }
}

/// <summary>Invocation information scoped to one asynchronous native method call.</summary>
public sealed record TypertInvocation(Context Context, string Endpoint, CancellationToken CancellationToken)
{
    private static readonly AsyncLocal<TypertInvocation?> CurrentCall = new();

    /// <summary>Gets the current call. Native methods use this instead of JavaScript's rebound this.ctx.</summary>
    public static TypertInvocation? Current => CurrentCall.Value;

    internal static IDisposable Enter(TypertInvocation invocation)
    {
        var previous = CurrentCall.Value;
        CurrentCall.Value = invocation;
        return new CallScope(previous);
    }

    private sealed class CallScope(TypertInvocation? previous) : IDisposable
    {
        public void Dispose() => CurrentCall.Value = previous;
    }
}

/// <summary>Remote error details use JSON data, without serializing arbitrary CLR exception objects.</summary>
public sealed record TypertRemoteFailure(string Code, string Message, JsonElement? Details);

/// <summary>The native carrier's success or error envelope.</summary>
public sealed record TypertRemoteResult(bool Ok, JsonElement? Value, TypertRemoteFailure? Error)
{
    /// <summary>Create a successful encoded result.</summary>
    public static TypertRemoteResult Success(JsonElement value) => new(true, value, null);

    /// <summary>Create an encoded failure preserving a Remote owner's code and JSON details.</summary>
    public static TypertRemoteResult Failure(Exception error) => error switch
    {
        RemoteError remote => new(false, null, new(remote.Code, remote.Message, remote.Details)),
        _ => new(false, null, new("gateway/internal", error.Message, null)),
    };
}

/// <summary>Carrier-independent Remote dispatch over live Cordis services and generated Typert contracts.</summary>
public sealed class TypertGateway(Context caller, TypertRegistry registry)
{
    /// <summary>Invoke one unary method with exact arguments and current service and provider registrations.</summary>
    /// <remarks>
    /// The native adapter checks registration generations after successful provider resolution and before
    /// encoding a successful business result. Withdrawal does not abort a resolver or business method already
    /// running. Cancellation is cooperative: an aborted signal alone does not change a successful business
    /// result, while a business failure under that signal becomes gateway/cancelled. Preparation failures
    /// retain their own error mapping. These generation fences are a native validity adaptation.
    /// </remarks>
    public async Task<TypertRemoteResult> InvokeAsync(
        string endpoint,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        TypertRemoteResult? result = null;
        try
        {
            await caller.RunAsync(async _ =>
            {
                var prepared = await PrepareAsync(endpoint, arguments);
                if (prepared.Descriptor.IsStream)
                    throw Fault("gateway/signature-invalid", endpoint, "A stream method requires the stream carrier.");
                if (!prepared.Binding.Methods.TryGetValue(prepared.Descriptor.Method, out var invoke))
                    throw Fault(
                        "gateway/method-unavailable",
                        endpoint,
                        "The active binding has no method for this descriptor.");
                object? value;
                using (TypertInvocation.Enter(new(prepared.Receiver, endpoint, cancellationToken)))
                {
                    try
                    {
                        value = await invoke(prepared.Service, prepared.Arguments, cancellationToken);
                    }
                    catch (Exception error) when (cancellationToken.IsCancellationRequested)
                    {
                        throw Fault("gateway/cancelled", endpoint, "The Remote invocation was cancelled.", error);
                    }
                    catch (OperationCanceledException error)
                    {
                        throw Fault(
                            "gateway/internal",
                            endpoint,
                            "The Remote invocation failed without cancellation.",
                            error);
                    }
                }

                prepared.Check();
                result = Encode(prepared.Descriptor, value);
            });
            return result!;
        }
        catch (Exception error)
        {
            return TypertRemoteResult.Failure(error);
        }
    }

    /// <summary>Iterate a downlink stream in its invocation Context, checking native validity before and after successful reads.</summary>
    /// <remarks>
    /// Early disposal sends cooperative cancellation. Cancellation races a pending read, but cleanup must
    /// first settle that read before calling DisposeAsync in the invocation Context. A business read or
    /// cleanup that never finishes can keep disposal pending; cancellation does not forcibly terminate it.
    /// Cleanup failures propagate from enumeration. The host must release and await or drain all active
    /// iterators before closing the Cordis root. Service and provider withdrawal do not actively abort
    /// an in-flight read; generation checks reject obsolete successful values before encoding them.
    /// </remarks>
    public async IAsyncEnumerable<TypertRemoteResult> StreamAsync(
        string endpoint,
        JsonElement arguments,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Prepared? prepared = null;
        IAsyncEnumerator<object?>? iterator = null;
        Task<bool>? pendingMove = null;
        Exception? failure = null;
        try
        {
            await caller.RunAsync(async _ =>
            {
                prepared = await PrepareAsync(endpoint, arguments);
                if (!prepared.Descriptor.IsStream ||
                    !prepared.Binding.Streams.TryGetValue(prepared.Descriptor.Method, out var invoke))
                    throw Fault("gateway/signature-invalid", endpoint, "This endpoint has no active stream binding.");
                using (TypertInvocation.Enter(new(prepared.Receiver, endpoint, lifetime.Token)))
                    iterator = invoke(prepared.Service, prepared.Arguments, lifetime.Token)
                        .GetAsyncEnumerator(lifetime.Token);
            });
        }
        catch (Exception error)
        {
            failure = error;
        }

        if (failure is not null)
        {
            yield return TypertRemoteResult.Failure(failure);
            yield break;
        }

        try
        {
            while (true)
            {
                var moved = false;
                TypertRemoteResult? item = null;
                try
                {
                    await caller.RunAsync(async _ =>
                    {
                        prepared!.Check();
                        lifetime.Token.ThrowIfCancellationRequested();
                        using (TypertInvocation.Enter(new(prepared.Receiver, endpoint, lifetime.Token)))
                        {
                            pendingMove = iterator!.MoveNextAsync().AsTask();
                            moved = await pendingMove.WaitAsync(lifetime.Token);
                            pendingMove = null;
                        }

                        prepared.Check();
                        if (moved)
                            item = Encode(prepared.Descriptor, iterator!.Current);
                    });
                }
                catch (Exception error)
                {
                    failure = error is OperationCanceledException && lifetime.IsCancellationRequested
                        ? Fault(
                            "gateway/cancelled",
                            endpoint,
                            "The Remote stream was cancelled.",
                            error)
                        : error;
                }

                if (failure is not null)
                    break;
                if (item is not null)
                    yield return item;
                if (!moved)
                    yield break;
            }
        }
        finally
        {
            try
            {
                await lifetime.CancelAsync();
            }
            finally
            {
                if (iterator is not null)
                    await caller.RunAsync(async _ =>
                    {
                        using (TypertInvocation.Enter(new(prepared!.Receiver, endpoint, lifetime.Token)))
                        {
                            if (pendingMove is not null)
                            {
                                try
                                {
                                    await pendingMove;
                                }
                                catch (Exception)
                                {
                                }
                            }

                            await iterator.DisposeAsync();
                        }
                    });
            }
        }

        if (failure is not null)
            yield return TypertRemoteResult.Failure(failure);
    }

    private async Task<Prepared> PrepareAsync(string endpoint, JsonElement arguments)
    {
        var descriptor = registry.GetLocal(endpoint) ?? throw Fault(
            registry.HasSeenLocal(endpoint) ? "gateway/definition-unavailable" : "gateway/invocation-unavailable",
            endpoint,
            "There is no active generated definition for this endpoint.");
        var checks = new List<Action>();
        var descriptorToken = registry.LocalToken(endpoint);
        checks.Add(() =>
        {
            if (!ReferenceEquals(descriptorToken, registry.LocalToken(endpoint)))
                throw Fault(
                    "gateway/definition-unavailable",
                    endpoint,
                    "The definition was withdrawn during this call.");
        });
        var expected = descriptor.Parameters.Select(parameter => parameter.Wire).ToHashSet(StringComparer.Ordinal);
        if (descriptor.Invocation is { } contextInvocation)
            expected.Add(contextInvocation.Wire);
        if (arguments.ValueKind != JsonValueKind.Object)
            throw Fault("gateway/arguments-invalid", endpoint, "Arguments must be a named JSON object.");
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
            if (!expected.Contains(field.Name) || !actual.Add(field.Name))
                throw Fault(
                    "gateway/arguments-invalid",
                    endpoint,
                    "Arguments contain an unexpected or repeated field.");
        foreach (var field in expected)
            if (!actual.Contains(field) && !descriptor.Parameters.Any(parameter =>
                    parameter.Wire == field && parameter.AcceptsUndefined && parameter.Lookup is null))
                throw Fault("gateway/arguments-invalid", endpoint, "A required wire field is absent.");
        Context receiver = caller;
        if (descriptor.Invocation is { } selection)
        {
            var provider = registry.GetHostContext(selection.Context) ?? throw Fault(
                "gateway/context-unavailable",
                endpoint,
                "The Context provider is unavailable.",
                field: selection.Wire);
            var providerToken = registry.HostContextToken(selection.Context);
            checks.Add(() =>
            {
                if (!ReferenceEquals(providerToken, registry.HostContextToken(selection.Context)))
                    throw Fault(
                        "gateway/context-unavailable",
                        endpoint,
                        "The Context provider was withdrawn during this call.",
                        field: selection.Wire);
            });
            Match(provider.Wire, provider.WireTypeSymbol, selection.Wire, selection.Codec, endpoint);
            var identity = Decode(selection.Codec, arguments.GetProperty(selection.Wire), endpoint, selection.Wire);
            try
            {
                receiver = await provider.Resolve(identity) ?? throw Fault(
                    "gateway/context-not-found",
                    endpoint,
                    "The identity resolved to no Context.",
                    field: selection.Wire);
            }
            catch (RemoteError)
            {
                throw;
            }
            catch (Exception error)
            {
                throw Fault("gateway/context-failed", endpoint, "The Context resolver failed.", error, selection.Wire);
            }

            if (!ReferenceEquals(receiver.Root, caller.Root))
                throw Fault(
                    "gateway/provider-mismatch",
                    endpoint,
                    "The receiver Context belongs to another Cordis execution domain.");
        }

        var decoded = new List<object?>();
        foreach (var parameter in descriptor.Parameters)
        {
            if (!arguments.TryGetProperty(parameter.Wire, out var raw))
            {
                decoded.Add(Undefined.Value);
                continue;
            }

            var value = Decode(parameter.Codec, raw, endpoint, parameter.Wire);
            if (parameter.Lookup is { } key)
            {
                var provider = registry.GetLookup(key) ?? throw Fault(
                    "gateway/lookup-unavailable",
                    endpoint,
                    "The lookup provider is unavailable.",
                    field: parameter.Wire);
                var providerToken = registry.LookupToken(key);
                checks.Add(() =>
                {
                    if (!ReferenceEquals(providerToken, registry.LookupToken(key)))
                        throw Fault(
                            "gateway/lookup-unavailable",
                            endpoint,
                            "The lookup provider was withdrawn during this call.",
                            field: parameter.Wire);
                });
                Match(provider.Wire, provider.WireTypeSymbol, parameter.Wire, parameter.Codec, endpoint);
                try
                {
                    value = await provider.Resolve(value) ?? throw Fault(
                        "gateway/lookup-not-found",
                        endpoint,
                        "The identity resolved to no object.",
                        field: parameter.Wire);
                }
                catch (RemoteError)
                {
                    throw;
                }
                catch (Exception error)
                {
                    throw Fault(
                        "gateway/lookup-failed",
                        endpoint,
                        "The lookup resolver failed.",
                        error,
                        parameter.Wire);
                }
            }

            decoded.Add(value);
        }

        var service = receiver.Get<object>(descriptor.Service, strict: false) ?? throw Fault(
            "gateway/service-unavailable",
            endpoint,
            "The live Cordis service is unavailable.");
        if (service is not ITypertRemoteService remote || remote.TypertRemote.Service != descriptor.Service ||
            remote.TypertRemote.Namespace != descriptor.Namespace)
            throw Fault("gateway/binding-invalid", endpoint, "The service has no matching explicit Remote binding.");
        checks.Add(() =>
        {
            if (!ReferenceEquals(service, receiver.Get<object>(descriptor.Service, strict: false)))
                throw Fault("gateway/service-unavailable", endpoint, "The service was withdrawn during this call.");
        });
        var prepared = new Prepared(descriptor, receiver, service, remote.TypertRemote, decoded, checks);
        prepared.Check();
        return prepared;
    }

    private static void Match(string wire, string symbol, string expectedWire, TypertCodec codec, string endpoint)
    {
        if (wire != expectedWire || symbol != codec.TypeSymbol)
            throw Fault(
                "gateway/provider-mismatch",
                endpoint,
                "The provider wire declaration differs from the generated contract.",
                field: expectedWire);
    }

    private static object? Decode(TypertCodec codec, JsonElement value, string endpoint, string field)
    {
        try
        {
            return codec.Decode(value);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw Fault(
                "gateway/input-invalid",
                endpoint,
                "An argument failed the generated boundary codec.",
                error,
                field);
        }
    }

    private static TypertRemoteResult Encode(TypertInvocationDescriptor descriptor, object? value)
    {
        try
        {
            return TypertRemoteResult.Success(descriptor.Result.Encode(value));
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidCastException)
        {
            throw Fault(
                "gateway/result-invalid",
                descriptor.Endpoint,
                "The result failed its generated boundary codec.",
                error);
        }
    }

    private static RemoteError Fault(
        string code,
        string endpoint,
        string message,
        Exception? cause = null,
        string? field = null)
    {
        using var details = JsonDocument.Parse(
            "{\"endpoint\":" + JsonSerializer.Serialize(endpoint, TypertJson.Default.String) +
            (field is null ? "" : ",\"field\":" + JsonSerializer.Serialize(field, TypertJson.Default.String)) + "}");
        return new(code, message, details.RootElement.Clone(), cause);
    }

    private sealed record Prepared(
        TypertInvocationDescriptor Descriptor,
        Context Receiver,
        object Service,
        TypertRemoteBinding Binding,
        IReadOnlyList<object?> Arguments,
        IReadOnlyList<Action> Checks)
    {
        public void Check()
        {
            foreach (var check in Checks)
                check();
        }
    }
}
