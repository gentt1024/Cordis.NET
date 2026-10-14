using System.Text;
using System.Text.Json;

namespace Cordis.Typert;

internal sealed record ClientEventWaterfall(ClientCodec Request, ClientCodec Reply, ClientCodec Next);

internal sealed record ClientEvent(string Name, ClientCodec[] Args, ClientEventWaterfall? Waterfall = null);

internal static class EventClientEmitter
{
    internal static ClientArtifact Generate(IReadOnlyList<ClientEvent> events)
    {
        if (events.Select(value => value.Name).Distinct(StringComparer.Ordinal).Count() != events.Count)
            throw new ArgumentException("Selected event names must be unique.", nameof(events));
        var types = new StringBuilder(
            "import type { TypertConnection, RemoteEventContext, RemoteEventAdapters, RemoteEventsClient } from './cordis-remote.mjs';\n");
        var listeners = new StringBuilder("export interface EventListeners {\n");
        var index = 0;
        foreach (var item in events)
        {
            string Type(ClientCodec codec)
            {
                var name = "EventData" + index++;
                ClientEmitter.AppendType(types, name, codec.Schema);
                return name + (codec.AcceptsUndefined ? " | undefined" : "");
            }

            listeners.Append(Quote(item.Name)).Append(": (");
            if (item.Waterfall is { } waterfall)
            {
                var data = Type(waterfall.Request);
                var result = Type(waterfall.Reply);
                var next = Type(waterfall.Next);
                listeners
                    .Append("this: RemoteEventContext, request: ")
                    .Append(data)
                    .Append(
                        " & {readonly agent: RemoteEventContext; readonly signal: AbortSignal}, next: () => Promise<")
                    .Append(next)
                    .Append(">) => Promise<")
                    .Append(result)
                    .Append(">;\n");
            }
            else
                listeners
                    .Append(string.Join(", ", item.Args.Select((codec, i) => "arg" + i + ": " + Type(codec))))
                    .Append(") => void;\n");
        }

        listeners
            .Append("}\nexport type EventsClient = RemoteEventsClient<EventListeners>;\n")
            .Append(
                "export declare function createEvents(owner: RemoteEventContext, connection: TypertConnection, adapters?: RemoteEventAdapters): EventsClient;\n");
        return new(
            "import { createEventsClient } from './cordis-remote.mjs';\nexport function createEvents(owner, connection, adapters) { return createEventsClient(owner, connection, " +
            "[" + string.Join(",", events.Select(value => Quote(value.Name))) + "], adapters); }\n",
            types.Append(listeners).ToString());
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value, ClientEmitterJson.Default.String);
}
