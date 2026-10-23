using System.Text.Json;

namespace Cordis.Composition;

/// <summary>A credential-free caller identity supplied by trusted Host code.</summary>
/// <remarks>The Host chooses the JSON fields for each kind. Request payloads never supply this value.</remarks>
public sealed class TypertPrincipal
{
    /// <summary>Snapshot one JSON object with a nonempty, case-sensitive kind.</summary>
    public TypertPrincipal(JsonElement identity)
    {
        if (identity.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A principal must be a JSON object.", nameof(identity));
        var kinds = identity.EnumerateObject().Where(property => property.NameEquals("kind")).ToArray();
        if (kinds.Length != 1 || kinds[0].Value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(kinds[0].Value.GetString()))
            throw new ArgumentException("A principal must have one nonempty string kind.", nameof(identity));
        Identity = identity.Clone();
        Kind = kinds[0].Value.GetString()!;
    }

    /// <summary>The local user who owns this Host.</summary>
    public static TypertPrincipal Owner
    {
        get;
    } = CreateOwner();

    /// <summary>The exact kind declared by the Host identity driver.</summary>
    public string Kind
    {
        get;
    }

    /// <summary>A detached, immutable JSON identity suitable for durable attribution.</summary>
    public JsonElement Identity
    {
        get;
    }

    /// <summary>Return the non-owner caller, or null for the owner and outside a Remote call.</summary>
    public static TypertPrincipal? PrincipalOf(TypertInvocation? invocation) =>
        invocation?.Peer?.Principal is { Kind: not "owner" } principal ? principal : null;

    private static TypertPrincipal CreateOwner()
    {
        using var document = JsonDocument.Parse("{\"kind\":\"owner\"}");
        return new(document.RootElement);
    }
}

/// <summary>Whether a Peer may participate in forwarded Remote events.</summary>
public enum TypertPeerInteractions
{
    /// <summary>Permit forwarded event subscription and owner answers.</summary>
    Answer,

    /// <summary>Disable forwarded event subscription and answers.</summary>
    None,
}

/// <summary>Host-selected identity and interaction capacity fixed for one Peer lifetime.</summary>
public sealed record TypertPeerOptions(
    TypertPrincipal Principal,
    TypertPeerInteractions Interactions = TypertPeerInteractions.Answer);
