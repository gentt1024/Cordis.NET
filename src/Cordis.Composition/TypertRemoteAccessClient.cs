using System.Text.Json;

namespace Cordis.Composition;

/// <summary>Client-owned access snapshot generations over a borrowed Connection.</summary>
public sealed class TypertRemoteAccessClient : IAsyncDisposable
{
    private readonly Context owner;
    private readonly TypertConnection connection;
    private readonly TypertCallLifetime lifetime = new();
    private int running;

    /// <summary>Create a fresh mount, optionally initialized from this page's baseline.</summary>
    public TypertRemoteAccessClient(
        Context owner,
        TypertConnection connection,
        TypertAccessFrame.Baseline? initial = null)
    {
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.connection = connection ?? throw new ArgumentNullException(nameof(connection));
        State = new(initial);
    }

    /// <summary>This mount's access state; never shared implicitly with a later mount.</summary>
    public TypertAccessState State
    {
        get;
    }

    /// <summary>Consume one physical generation, requiring exactly one opening baseline.</summary>
    /// <remarks>The surrounding Connection controller owns retries. Carrier loss retains the published snapshot;
    /// a business or protocol failure closes non-owner permissions. Each subsequent run requires a fresh baseline.</remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var call = lifetime.Enter() ?? throw new ObjectDisposedException(nameof(TypertRemoteAccessClient));
        using var carrier = connection.Enter();
        if (Interlocked.Exchange(ref running, 1) != 0)
            throw new InvalidOperationException("The Remote access Client already has an active generation.");
        try
        {
            using var signal = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                call.Token,
                carrier.Token);
            await owner.RunAsync(_ => PumpAsync(signal.Token));
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        using var lease = lifetime.Enter() ?? throw new ObjectDisposedException(nameof(TypertRemoteAccessClient));
        await using var stream = connection.Open(
            TypertRemoteAccess.FollowEndpoint,
            () => TypertAccessProtocol.Json(new System.Text.Json.Nodes.JsonObject()),
            token,
            lease);
        var seen = false;
        try
        {
            while (await stream.ReadAsync(token) is { } value)
            {
                token.ThrowIfCancellationRequested();
                var frame = TypertAccessFrame.FromJson(value);
                if (frame is TypertAccessFrame.Baseline baseline)
                {
                    if (seen)
                        throw new JsonException("Remote access emitted more than one opening baseline.");
                    State.Replace(baseline);
                    seen = true;
                }
                else
                {
                    if (!seen)
                        throw new JsonException("Remote access emitted an update before its opening baseline.");
                    State.Update(frame);
                }
            }

            if (!seen)
                throw new JsonException("Remote access ended before its opening baseline.");
            throw new RemoteStreamCarrierException("Remote access generation ended.");
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
        }
        catch (RemoteStreamCarrierException)
        {
            throw;
        }
        catch
        {
            State.Unavailable();
            throw;
        }
    }

    /// <summary>Cancel this mount's generation and wait until no callbacks can run.</summary>
    public ValueTask DisposeAsync() => lifetime.DisposeAsync();
}
