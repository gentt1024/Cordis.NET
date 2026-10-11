using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cordis.Composition;

/// <summary>One host-selected Cordis notification with explicitly encoded JSON arguments.</summary>
public sealed record TypertRemoteEventFrame(string Event, IReadOnlyList<JsonElement> Args);

/// <summary>Open the sole host-selected notification source under its registration's cancellation.</summary>
/// <remarks>
/// Attach the selected host listeners before returning the iterable. The source must cooperate with
/// cancellation and dispose its listeners when enumeration ends. It is consumed once by the Gateway,
/// which broadcasts each item to its currently connected clients.
/// </remarks>
public delegate IAsyncEnumerable<TypertRemoteEventFrame> TypertRemoteEventSource(CancellationToken cancellationToken);

/// <summary>A subscription-ready item or one forwarded host event.</summary>
/// <param name="Kind">ready confirms subscription; event carries the original event and JSON arguments.</param>
/// <param name="Event">The original host event name, present for event items.</param>
/// <param name="Args">The original encoded argument list, present for event items.</param>
public sealed record TypertRemoteNotification(
    string Kind,
    string? Event = null,
    IReadOnlyList<JsonElement>? Args = null);

/// <summary>The generated notification Remote over the Gateway-owned host event source.</summary>
[RemoteService("remoteNotifications", typeof(RemoteNotificationsJson), Namespace = "remoteNotifications")]
public partial class RemoteNotifications
{
    private readonly TypertGateway gateway;

    /// <summary>Publish the notification Remote independently of the selected event source.</summary>
    public RemoteNotifications(Context context, TypertGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(gateway);
        gateway.EnsureNotificationContext(context, nameof(context));
        this.gateway = gateway;
        context.Provide("remoteNotifications", this);
    }

    /// <summary>Subscribe before querying a snapshot; disconnect removes only this observer.</summary>
    /// <remarks>
    /// The first ready item confirms registration of this observer. Event items have no replay or
    /// durable delivery guarantee. Reconnect by subscribing again and querying current state.
    /// Source removal or failure terminates every observer with a Remote failure.
    /// </remarks>
    [RemoteMethod("watch", Stream = true)]
    public IAsyncEnumerable<TypertRemoteNotification> WatchAsync(CancellationToken cancellationToken = default) =>
        gateway.WatchRemoteEventsAsync(cancellationToken);
}

/// <summary>Static serialization metadata for generated notification frames.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TypertRemoteNotification))]
public partial class RemoteNotificationsJson : JsonSerializerContext;
