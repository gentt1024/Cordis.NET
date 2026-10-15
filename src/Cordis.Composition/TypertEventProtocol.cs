using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Composition;

/// <summary>One application-selected notification or scoped waterfall sent to the Gateway.</summary>
public abstract record TypertRemoteEventDispatch(string Event);

/// <summary>A projected waterfall request. The application source owns its Context and continuation.</summary>
public sealed record TypertRemoteEventInvocation(
    string Event,
    Context Context,
    string AgentId,
    JsonElement Request,
    CancellationToken CancellationToken,
    Action<TypertRemoteEventOutcome> Resolve,
    Action<Exception> Reject) : TypertRemoteEventDispatch(Event);

/// <summary>A Client result, including absence, or delegation to the next Host listener.</summary>
public abstract record TypertRemoteEventOutcome
{
    private TypertRemoteEventOutcome()
    {
    }

    /// <summary>Continue the Host waterfall.</summary>
    public sealed record Next : TypertRemoteEventOutcome;

    /// <summary>Return the wire value. JsonValueKind.Undefined preserves an absent result.</summary>
    public sealed record Result(JsonElement Value) : TypertRemoteEventOutcome;
}

/// <summary>The stable Host facts published when an event generation opens.</summary>
public sealed record TypertRemoteEventHost(string Home);

internal static class TypertEventProtocol
{
    internal static JsonElement JsonNull
    {
        get;
    } = Null();

    private static JsonElement Null()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    internal const string Stream = "$events";
    internal const string Result = "$events/result";

    internal static JsonElement Json(JsonObject value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    internal static JsonNode? Node(JsonElement value) => JsonNode.Parse(value.GetRawText());

    internal static bool Exact(JsonElement value, params string[] fields) =>
        value.ValueKind == JsonValueKind.Object && fields.All(field => value.TryGetProperty(field, out _)) &&
        value.EnumerateObject().All(field => fields.Contains(field.Name, StringComparer.Ordinal));

    internal static string Text(JsonElement value, string name, bool empty = false)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var item) ||
            item.ValueKind != JsonValueKind.String ||
            (!empty && item.GetString()!.Length == 0))
            throw new JsonException("Invalid Remote event field: " + name);
        return item.GetString()!;
    }

    internal static void Request(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object || request.TryGetProperty("agent", out _) ||
            request.TryGetProperty("signal", out _))
            throw new JsonException("Remote event request must contain only projected JSON fields.");
        TypertCodec.ValidateUplinkItem(request);
    }

    internal static (string Client, string Event, TypertRemoteEventOutcome? Outcome, Exception? Error)
        ReadResult(JsonElement value)
    {
        if (!Exact(value, "clientId", "eventId", "outcome"))
            throw new JsonException("Invalid Remote event result.");
        var client = Text(value, "clientId");
        var id = Text(value, "eventId");
        var outcome = value.GetProperty("outcome");
        var kind = Text(outcome, "kind");
        if (kind == "next" && Exact(outcome, "kind"))
            return (client, id, new TypertRemoteEventOutcome.Next(), null);
        if (kind == "result" && (Exact(outcome, "kind") || Exact(outcome, "kind", "value")))
        {
            var result = outcome.TryGetProperty("value", out var returned) ? returned.Clone() : default;
            TypertCodec.ValidateUplinkItem(result);
            return (client, id, new TypertRemoteEventOutcome.Result(result), null);
        }

        if (kind == "rejected" && Exact(outcome, "kind", "error"))
        {
            var error = outcome.GetProperty("error");
            if (error.ValueKind != JsonValueKind.Object ||
                error.EnumerateObject().Any(field => field.Name is not ("name" or "message" or "code" or "details")))
                throw new JsonException("Invalid Remote event rejection.");
            var name = Text(error, "name");
            var message = Text(error, "message", empty: true);
            var code = error.TryGetProperty("code", out _) ? Text(error, "code", empty: true) : null;
            JsonElement? details = error.TryGetProperty("details", out var data) ? data.Clone() : null;
            if (details.HasValue)
                TypertCodec.ValidateUplinkItem(details.Value);
            return (client, id, null, new TypertRemoteEventException(name, message, code, details));
        }

        throw new JsonException("Invalid Remote event outcome.");
    }
}

/// <summary>A Client event rejection retaining the fixed wire error's name, code and JSON details.</summary>
public sealed class TypertRemoteEventException(string remoteName, string message, string? code, JsonElement? details)
    : Exception(message)
{
    /// <summary>The original error name.</summary>
    public string RemoteName
    {
        get;
    } = remoteName;

    /// <summary>The optional original error code.</summary>
    public string? Code
    {
        get;
    } = code;

    /// <summary>The optional JSON details.</summary>
    public JsonElement? Details
    {
        get;
    } = details;
}
