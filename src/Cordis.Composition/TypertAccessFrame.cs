using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Composition;

/// <summary>The endpoint actions allowed on one resource without call arguments.</summary>
public sealed record TypertAccessGrant(TypertAccessResource Resource, IReadOnlyList<string> Actions);

/// <summary>A closed frame of the access snapshot protocol.</summary>
public abstract class TypertAccessFrame
{
    private TypertAccessFrame()
    {
    }

    /// <summary>The opening frame of one physical stream generation.</summary>
    public abstract class Baseline : TypertAccessFrame
    {
        private Baseline()
        {
        }

        /// <summary>The owner's unrestricted access.</summary>
        public sealed class Owner : Baseline
        {
        }

        /// <summary>A complete non-owner snapshot.</summary>
        public sealed class Granted(IReadOnlyList<TypertAccessGrant> grants) : Baseline
        {
            /// <summary>The complete ordered grant snapshot.</summary>
            public IReadOnlyList<TypertAccessGrant> Grants
            {
                get;
            } = grants;
        }
    }

    /// <summary>Replace one resource's allowed actions.</summary>
    public sealed class Upsert(TypertAccessGrant grant) : TypertAccessFrame
    {
        /// <summary>The replacement grant.</summary>
        public TypertAccessGrant Grant
        {
            get;
        } = grant;
    }

    /// <summary>Remove every action for one resource.</summary>
    public sealed class Remove(TypertAccessResource resource) : TypertAccessFrame
    {
        /// <summary>The withdrawn resource.</summary>
        public TypertAccessResource Resource
        {
            get;
        } = resource;
    }

    /// <summary>Encode this frame using the Remote access wire protocol.</summary>
    public JsonElement ToJson() => TypertAccessProtocol.Encode(this);

    /// <summary>Decode one frame, refusing unknown variants and malformed fields.</summary>
    public static TypertAccessFrame FromJson(JsonElement value) => TypertAccessProtocol.Decode(value);
}

internal static class TypertAccessProtocol
{
    internal static JsonElement Encode(TypertAccessFrame frame)
    {
        JsonObject value = frame switch
        {
            TypertAccessFrame.Baseline.Owner => new()
            {
                ["type"] = "baseline",
                ["owner"] = true
            },
            TypertAccessFrame.Baseline.Granted baseline => new()
            {
                ["type"] = "baseline",
                ["owner"] = false,
                ["grants"] = new JsonArray(baseline.Grants.Select(grant => (JsonNode)Grant(grant)).ToArray())
            },
            TypertAccessFrame.Upsert upsert => new()
            {
                ["type"] = "upsert",
                ["grant"] = Grant(upsert.Grant)
            },
            TypertAccessFrame.Remove remove => new()
            {
                ["type"] = "remove",
                ["resource"] = Resource(remove.Resource)
            },
            _ => throw new JsonException("Invalid access frame.")
        };
        return Json(value);
    }

    internal static TypertAccessFrame Decode(JsonElement value)
    {
        var type = Text(value, "type");
        if (type == "baseline")
        {
            if (Exact(value, "type", "owner") && value.GetProperty("owner").ValueKind == JsonValueKind.True)
                return new TypertAccessFrame.Baseline.Owner();
            if (Exact(value, "type", "owner", "grants") &&
                value.GetProperty("owner").ValueKind == JsonValueKind.False &&
                value.GetProperty("grants").ValueKind == JsonValueKind.Array)
                return new TypertAccessFrame.Baseline.Granted(
                    Array.AsReadOnly(value.GetProperty("grants").EnumerateArray().Select(ReadGrant).ToArray()));
        }

        if (type == "upsert" && Exact(value, "type", "grant"))
            return new TypertAccessFrame.Upsert(ReadGrant(value.GetProperty("grant")));
        if (type == "remove" && Exact(value, "type", "resource"))
            return new TypertAccessFrame.Remove(ReadResource(value.GetProperty("resource")));
        throw new JsonException("Invalid access snapshot frame.");
    }

    internal static JsonElement Json(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject Resource(TypertAccessResource resource) => resource.Kind == "global"
        ? new()
        {
            ["kind"] = "global"
        }
        : new()
        {
            ["kind"] = resource.Kind,
            ["id"] = resource.Id
        };

    private static JsonObject Grant(TypertAccessGrant grant) => new()
    {
        ["resource"] = Resource(grant.Resource),
        ["actions"] = new JsonArray(grant.Actions.Select(action => (JsonNode?)JsonValue.Create(action)).ToArray())
    };

    private static TypertAccessResource ReadResource(JsonElement value)
    {
        var kind = Text(value, "kind");
        if (kind == "global" && Exact(value, "kind"))
            return TypertAccessResource.Global;
        if (kind != "global" && Exact(value, "kind", "id"))
            return TypertAccessResource.Create(kind, Text(value, "id"));
        throw new JsonException("Invalid access resource.");
    }

    private static TypertAccessGrant ReadGrant(JsonElement value)
    {
        if (!Exact(value, "resource", "actions") || value.GetProperty("actions").ValueKind != JsonValueKind.Array)
            throw new JsonException("Invalid access grant.");
        var actions = value
            .GetProperty("actions")
            .EnumerateArray()
            .Select(action =>
                action.ValueKind == JsonValueKind.String
                    ? action.GetString()!
                    : throw new JsonException("Access actions must be strings."))
            .ToArray();
        return new(ReadResource(value.GetProperty("resource")), Array.AsReadOnly(actions));
    }

    private static string Text(JsonElement value, string field) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(field, out var text) &&
        text.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(text.GetString())
            ? text.GetString()!
            : throw new JsonException("Missing access field " + field + ".");

    private static bool Exact(JsonElement value, params string[] fields) =>
        value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Count() == fields.Length &&
        fields.All(field => value.TryGetProperty(field, out _));
}
