using System.Collections.ObjectModel;
using System.Text.Json;

namespace Cordis.Composition;

public sealed partial class TypertGateway
{
    private static IReadOnlyDictionary<string, object?> ValidateArguments(
        TypertInvocationDescriptor descriptor,
        JsonElement arguments)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (descriptor.Invocation is { } selection)
            values.Add(selection.Wire, arguments.GetProperty(selection.Wire).Clone());
        foreach (var parameter in descriptor.Parameters)
        {
            if (!arguments.TryGetProperty(parameter.Wire, out var raw))
                continue;
            if (parameter.Lookup is not null)
            {
                values.Add(parameter.Wire, raw.Clone());
                continue;
            }

            try
            {
                AssertJsonValue(raw);
            }
            catch (JsonException error)
            {
                throw Fault(
                    "gateway/input-invalid",
                    descriptor.Endpoint,
                    "An argument is not a finite JSON value.",
                    error,
                    parameter.Wire);
            }

            values.Add(parameter.Wire, Decode(parameter.Codec, raw, descriptor.Endpoint, parameter.Wire));
        }

        return new ReadOnlyDictionary<string, object?>(values);
    }

    private static void AssertJsonValue(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new JsonException("A JSON number must be finite.");
                break;
            case JsonValueKind.Object:
                foreach (var field in value.EnumerateObject())
                    AssertJsonValue(field.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AssertJsonValue(item);
                break;
            case JsonValueKind.Undefined:
                throw new JsonException("A present argument must be JSON.");
        }
    }

    private void Authorize(
        TypertPeer peer,
        TypertInvocationDescriptor descriptor,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var resources = CallResources(descriptor, arguments, cancellationToken);
        TypertAccessDecision decision;
        if (resources is null)
            decision = new TypertAccessDecision.Deny("resource-invalid");
        else
        {
            try
            {
                decision = caller
                        .Get<TypertRemoteAccess>("remoteAccess", strict: false)
                        ?.Decide(new(peer.Principal, descriptor.Endpoint, resources, arguments)) ??
                    new TypertAccessDecision.Deny("no-allow");
            }
            catch (Exception)
            {
                decision = new TypertAccessDecision.Deny("policy-failed");
            }
        }

        if (decision is TypertAccessDecision.Deny denied)
            throw AccessDenied(descriptor.Endpoint, denied.Reason);
    }

    private static RemoteError AccessDenied(string endpoint, string reason)
    {
        using var details = JsonDocument.Parse(
            "{\"action\":" + JsonSerializer.Serialize(endpoint, TypertJson.Default.String) +
            ",\"reason\":" + JsonSerializer.Serialize(reason, TypertJson.Default.String) + "}");
        return new("remote-access/denied", "Remote access was denied: " + reason, details.RootElement.Clone());
    }

    private IReadOnlyList<TypertAccessResource>? CallResources(
        TypertInvocationDescriptor descriptor,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var sources = new OrderedDictionary<string, Func<IReadOnlyList<object?>>>(StringComparer.Ordinal);
            foreach (var resource in registry.ListResources())
                sources[resource.Kind] = () => DefaultResource(descriptor, arguments, resource.Field);

            var identities = new OrderedDictionary<string, List<object?>>(StringComparer.Ordinal);

            void AddIdentity(string kind, object? value)
            {
                if (!identities.TryGetValue(kind, out var values))
                    identities.Add(kind, values = []);
                values.Add(value);
            }

            if (descriptor.Invocation is { } receiver &&
                registry.GetHostContext(receiver.Context)?.Resource is { } contextKind)
                AddIdentity(contextKind, arguments[receiver.Wire]);
            foreach (var parameter in descriptor.Parameters)
                if (parameter.Lookup is { } lookup && registry.GetLookup(lookup)?.Resource is { } kind &&
                    arguments.TryGetValue(parameter.Wire, out var identity))
                    AddIdentity(kind, identity);
            foreach (var identity in identities)
                sources[identity.Key] = () => identity.Value;

            if (caller.Get<object>(descriptor.Service, strict: false) is ITypertRemoteResourceSource host &&
                host.ResourceReaders.TryGetValue(descriptor.Method, out var readers))
            {
                var positional = Array.AsReadOnly(
                    descriptor
                        .Parameters.Select(parameter =>
                            arguments.TryGetValue(parameter.Wire, out var value) ? value : Undefined.Value)
                        .ToArray());
                foreach (var reader in readers)
                    sources[reader.Key] = () =>
                    {
                        var ids = reader.Value(positional, cancellationToken);
                        if (ids is null)
                            return [];
                        if (ids.Count == 0)
                            throw new InvalidOperationException("An explicit resource list cannot be empty.");
                        return ids.Cast<object?>().ToArray();
                    };
            }

            var resources = new List<TypertAccessResource>();
            foreach (var source in sources)
            {
                if (string.IsNullOrEmpty(source.Key) || source.Key == "global" ||
                    source.Key.Contains('#', StringComparison.Ordinal))
                    return null;
                foreach (var value in source.Value())
                {
                    var id = value is JsonElement json && json.ValueKind == JsonValueKind.String
                        ? json.GetString()
                        : value as string;
                    if (string.IsNullOrEmpty(id))
                        return null;
                    resources.Add(TypertAccessResource.Create(source.Key, id));
                }
            }

            return resources.Count == 0 ? [TypertAccessResource.Global] : resources.AsReadOnly();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<object?> DefaultResource(
        TypertInvocationDescriptor descriptor,
        IReadOnlyDictionary<string, object?> arguments,
        string field)
    {
        var directParameter = descriptor.Parameters.FirstOrDefault(parameter => parameter.Wire == field);
        if (directParameter is not null)
            return arguments.TryGetValue(field, out var direct) ? [directParameter.Codec.ResourceIdentity(direct)] : [];
        foreach (var parameter in descriptor.Parameters)
        {
            if (!arguments.TryGetValue(parameter.Wire, out var value))
                continue;
            // Lookup identities have not been decoded; their wire object is the only available fact.
            var result = parameter.Codec.ReadResourceField(value, field);
            switch (result.Status)
            {
                case ResourceFieldStatus.NotObject:
                    continue;
                case ResourceFieldStatus.Absent:
                    return [];
                case ResourceFieldStatus.Present:
                    return [result.Value];
                default:
                    throw new InvalidOperationException("This decoded resource field needs an explicit reader.");
            }
        }

        return [];
    }
}
