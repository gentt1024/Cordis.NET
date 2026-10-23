using System.Text.Json;

namespace Cordis.Composition;

/// <summary>A Host-admitted caller identity and its owned registration scope.</summary>
public sealed class TypertPeer : IAsyncDisposable
{
    private readonly Scope scope;

    /// <summary>Create a Peer inside the owner's execution domain. Wire data cannot select these options.</summary>
    public TypertPeer(Context owner, TypertPeerOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Principal);
        if (!Enum.IsDefined(options.Interactions))
            throw new ArgumentOutOfRangeException(nameof(options));
        Principal = options.Principal;
        Interactions = options.Interactions;
        scope = Scope.Create(owner, this);
        Context = scope.Context;
        var lifetime = new CancellationTokenSource();
        CancellationToken = lifetime.Token;
        Context.Effect(
            () => (Action)(() =>
            {
                try
                {
                    lifetime.Cancel();
                }
                finally
                {
                    lifetime.Dispose();
                }
            }),
            "Typert Peer lifetime");
    }

    /// <summary>The immutable caller identity fixed by the Host at creation.</summary>
    public TypertPrincipal Principal
    {
        get;
    }

    /// <summary>Forwarded-event capacity; it does not grant business permissions.</summary>
    public TypertPeerInteractions Interactions
    {
        get;
    }

    /// <summary>Cancelled when this Peer scope ends, including owner withdrawal.</summary>
    public CancellationToken CancellationToken
    {
        get;
    }

    /// <summary>Opaque identity stable across this Connection's requests and physical sockets.</summary>
    public string Id
    {
        get;
    } = Guid.NewGuid().ToString();

    /// <summary>The child scope for Peer-owned registrations. Borrowers do not own this Context.</summary>
    public Context Context
    {
        get;
    }

    /// <summary>Release this scope and await its cleanup. Repeated disposal does not repeat cleanup.</summary>
    public ValueTask DisposeAsync() => scope.DisposeAsync();
}

/// <summary>Host ownership of the operator Peer. Sockets borrow it; the Gateway remains the dispatch authority.</summary>
public sealed class TypertHostConnection : IAsyncDisposable
{
    private readonly TypertGateway gateway;

    /// <summary>Create inside the owner's Cordis execution domain.</summary>
    /// <remarks>Publish as the Context service "connection" to let direct Gateway calls borrow this operator.
    /// The host owns service withdrawal and Connection disposal; direct calls do not dispose either.</remarks>
    public TypertHostConnection(Context owner, TypertGateway gateway)
        : this(owner, gateway, new(TypertPrincipal.Owner))
    {
    }

    /// <summary>Create a connection for an explicitly Host-admitted identity and interaction capacity.</summary>
    public TypertHostConnection(Context owner, TypertGateway gateway, TypertPeerOptions options)
    {
        ArgumentNullException.ThrowIfNull(owner);
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Peer = new(owner, options);
    }

    /// <summary>The default admitted operator, shared by requests and sockets.</summary>
    public TypertPeer Peer
    {
        get;
    }

    /// <summary>Dispatch a request as this Connection's operator.</summary>
    public Task<TypertRemoteResult> InvokeAsync(
        string endpoint,
        JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        return gateway.InvokeAsync(endpoint, arguments, Peer, cancellationToken);
    }

    /// <summary>Dispatch a logical stream as this Connection's operator.</summary>
    public IAsyncEnumerable<TypertRemoteResult> StreamAsync(
        string endpoint,
        JsonElement arguments,
        CancellationToken cancellationToken = default,
        IAsyncEnumerable<JsonElement>? uplink = null)
    {
        return gateway.StreamAsync(endpoint, arguments, Peer, cancellationToken, uplink);
    }

    /// <summary>Release the operator scope and its registrations. Borrowed Gateway and root remain owned by their providers.</summary>
    public ValueTask DisposeAsync() => Peer.DisposeAsync();
}
