using System.Text;

namespace Cordis.Composition;

public static partial class TypertArtifacts
{
    /// <summary>Emit typed listeners from the Host-selected common event declarations.</summary>
    public static TypertClientArtifact GenerateEvents(IReadOnlyList<TypertRemoteEventDefinition> events)
    {
        if (events.Select(value => value.Event).Distinct(StringComparer.Ordinal).Count() != events.Count)
            throw new ArgumentException("Selected event names must be unique.", nameof(events));
        var types = new StringBuilder(
            "import type { TypertConnection, RemoteEventContext, RemoteEventAdapters, RemoteEventsClient } from './cordis-remote.mjs';\n");
        var listeners = new StringBuilder("export interface EventListeners {\n");
        var index = 0;
        foreach (var item in events)
        {
            string Type(TypertCodec codec, bool output = false)
            {
                var name = "EventData" + index++;
                AppendType(types, name, output ? codec.ResultSchema : codec.Schema);
                return name + (codec.AcceptsUndefined ? " | undefined" : "");
            }

            listeners.Append(Quote(item.Event)).Append(": (");
            if (item.Request is { } request)
            {
                var data = Type(request, output: true);
                var result = Type(item.Result!);
                var next = Type(item.Result!, output: true);
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
                    .Append(
                        string.Join(", ", item.Args.Select((codec, i) => "arg" + i + ": " + Type(codec, output: true))))
                    .Append(") => void;\n");
        }

        listeners
            .Append("}\nexport type EventsClient = RemoteEventsClient<EventListeners>;\n")
            .Append(
                "export declare function createEvents(owner: RemoteEventContext, connection: TypertConnection, adapters?: RemoteEventAdapters): EventsClient;\n");
        return new(
            "import { createEventsClient } from './cordis-remote.mjs';\nexport function createEvents(owner, connection, adapters) { return createEventsClient(owner, connection, " +
            "[" + string.Join(",", events.Select(value => Quote(value.Event))) + "], adapters); }\n",
            types.Append(listeners).ToString());
    }
}
