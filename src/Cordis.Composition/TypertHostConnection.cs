using System.Text.Json;

namespace Cordis.Composition;

/// <summary>The operator identity and registration scope owned by a Host Connection.</summary>
public sealed class TypertPeer
{
    private readonly Scope scope;

    internal TypertPeer(Context owner)
    {
        scope = Scope.Create(owner, this);
        Context = scope.Context;
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

    internal ValueTask DisposeAsync() => scope.DisposeAsync();

    internal bool Active => scope.Active;
}

/// <summary>Host ownership of the operator Peer. Sockets borrow it; the Gateway remains the dispatch authority.</summary>
public sealed class TypertHostConnection : IAsyncDisposable
{
    private readonly TypertGateway gateway;

    /// <summary>Create inside the owner's Cordis execution domain.</summary>
    /// <remarks>Publish as the Context service "connection" to let direct Gateway calls borrow this operator.
    /// The host owns service withdrawal and Connection disposal; direct calls do not dispose either.</remarks>
    public TypertHostConnection(Context owner, TypertGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(owner);
        this.gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        Peer = new(owner);
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
        ObjectDisposedException.ThrowIf(!Peer.Active, this);
        return gateway.InvokeAsync(endpoint, arguments, cancellationToken, Peer);
    }

    /// <summary>Dispatch a logical stream as this Connection's operator.</summary>
    public IAsyncEnumerable<TypertRemoteResult> StreamAsync(
        string endpoint,
        JsonElement arguments,
        CancellationToken cancellationToken = default,
        IAsyncEnumerable<JsonElement>? uplink = null)
    {
        ObjectDisposedException.ThrowIf(!Peer.Active, this);
        return gateway.StreamAsync(endpoint, arguments, cancellationToken, Peer, uplink);
    }

    /// <summary>Release the operator scope and its registrations. Borrowed Gateway and root remain owned by their providers.</summary>
    public ValueTask DisposeAsync() => Peer.DisposeAsync();
}
