using System.Text.Json;

namespace Cordis.Composition;

/// <summary>One selected Cordis notification with its original, explicitly encoded argument list.</summary>
public sealed record TypertRemoteEventFrame(string Event, IReadOnlyList<JsonElement> Args)
    : TypertRemoteEventDispatch(Event);

/// <summary>Attach application-selected listeners before returning the Gateway's sole event source.</summary>
/// <remarks>The source retains the original event names and owns its listener cleanup. It must
/// reject buffered waterfall invocations when it exits, and cooperate with registration cancellation.</remarks>
public delegate IAsyncEnumerable<TypertRemoteEventDispatch>
    TypertRemoteEventSource(CancellationToken cancellationToken);
