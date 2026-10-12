# Remote events

[中文](remote-events.zh.md)

The fixed DSH event path is `$events` plus `$events/result`. Cordis event declarations determine listener signatures; the Host selects which compatible declarations to forward. Plain void events preserve their argument list. Scoped asynchronous waterfalls preserve the same-result `next()` continuation and project the Host agent subject to the Client's resolved Context. The selection does not include DSH product events automatically.

## Declare and select

A public delegate marked with `RemoteEvent` is retained in the existing declaration model. The generator interprets these facts for the Host descriptors and typed listeners. No separate event model format is required.

```csharp
[assembly: TypertContext("agent", typeof(string))]

[RemoteEvent("app/decision", typeof(AppJson))]
public delegate Task<RemoteOptional<string?>> DecisionRequested(
    [RemoteLookup("agent", "agentId", typeof(string))] Agent agent,
    DecisionRequest request,
    Func<Task<RemoteOptional<string?>>> next,
    CancellationToken signal);

[RemoteEvent("app/changed", typeof(AppJson))]
public delegate void ApplicationChanged(Change change);
```

`AppJson` supplies source-generated metadata for the JSON request, result and notification arguments. The request contains the business fields; `agent` and `signal` are reserved projection fields, not serialized CLR resources. C# expresses TS's receiver/request/next shape as ordinary typed delegate parameters. The Host supplies its subject projection once when selecting the forwarding source:

```csharp
var selected = new[] {
    ApplicationChangedTypert.Definition,
    DecisionRequestedTypert.Forward(agent => new(agent.Context, agent.Id)),
};
gateway.RegisterRemoteEvents(owner,
    TypertRemoteEvents.FromCordis(owner, selected), new TypertRemoteEventHost(home));
var artifact = TypertArtifacts.GenerateEvents(selected);

var decision = await DecisionRequestedTypert.InvokeAsync(
    caller, agent, request, localContinuation, cancellationToken);
```

The business call supplies the subject, not a wire identity. The forwarding binding derives the Context and `agentId`, verifies the receiver is that subject, and sends only declared JSON data. Existing Cordis filtering, continuation and source ownership remain in use.

## Listen in C#

```csharp
await using var events = new TypertRemoteEventsClient(owner, registry, connection);
ApplicationChangedTypert.On(events, caller, change => Refresh(change));
DecisionRequestedTypert.On(events, caller, async (agent, request, next, signal) => {
    if (!CanHandle(request)) return await next();
    return new RemoteOptional<string?>(await AskAsync(agent, request, signal));
});
await events.RunAsync(host => OnReady(host.Home), generationCancellation);
```

The surrounding connection controller owns recovery. `RunAsync` represents one generation; it does not add retry policy. The Client borrows its Registry, Connection and Context. Listener registrations belong to the calling Fiber.

The typed waterfall listener returns `RemoteEventResult<T>`. Its continuation marker is separate from all `T` values. Returning `await next()` preserves that marker; a normal `T` converts to an answer. Use `RemoteOptional<T>` when the declaration permits undefined. Thus continuation, undefined and explicit null remain distinct without throwing an internal control exception through user code.

The existing `emit-dotnet` command also accepts a selected event name or delegate metadata name in `--service`. `CordisTypertClientModel`, `CordisTypertService`, `CordisTypertClientNamespace` and `CordisTypertClientName` generate the corresponding typed `On` helper and standalone data from the saved common model before compilation. Backend serializer restrictions still apply; a Host serializer that cannot be reproduced is not silently replaced with a different policy.


Collection element nullability comes from the event declaration, including `List<string?>?` and `Dictionary<string, T?>`. Authors register ordinary CLR metadata (`typeof(List<string>)`) in their JSON context; they do not hand-author a second schema. Generated source listeners, saved-model listeners and TS listeners use those same facts. Non-null elements still reject null. Namespace-qualified delegate identities permit unrelated same-name event delegates.

## Listen in TypeScript

```ts
import { createEvents } from './events.mjs'
const events = createEvents(owner, connection, adapters)
events.on(caller, 'app/decision', async (request, next) => {
  if (!canHandle(request)) return next()
  return ask(request.agent, request, request.signal)
})
await events.run(generationSignal, host => onReady(host.home))
```

`adapters.getClient('agent')` is consulted for every invocation. Its synchronous resolver returns a borrowed Cordis Context, an owned value from `typertOwnedValue`, or undefined. The shared owned-value symbol matches fixed DSH. No registry copy or permanent identity table is added. Generated emit listeners retain each declared argument and generated waterfall listeners project `agent` and `signal` into the request.

The browser module package exports standard platform events through `/events`. `createManagementClient({ modules, events })` uses the existing recovery controller, subscribes before querying full state and graph, and never replays writes. The old notification Remote and `/notifications` export are removed.

## Settlement and evidence boundaries

Resolvers and releases are synchronous. Missing or failing resolution delegates to the Host continuation. Borrowed values are not disposed; an owned value is released at most once after the handler and result reply settle. Cancellation reaches the handler but does not free its Context early. Client and Connection disposal join active waterfall work.

Disconnect removes that delivery without synthesizing `next`; pending invocations may reach the next connection generation. Source withdrawal closes its streams and cancels its pending work. An obsolete client identity cannot settle a current delivery; duplicate settled replies cannot revive it. One-way emits have no durable replay guarantee. Waterfall replies distinguish result, next and rejection, including rejection name, message, code and details.

The source correspondence is fixed DSH `packages/typert/protocol/src/types.ts`, `owned-value.ts`, `packages/api/gateway/src/client/remote-events.ts`, `stream-protocol.ts` and the forwarding selection in `packages/api/remotes/src/remote-events.ts`, at `639ed015397290b3745d163aafe02ffee4aa3f84`. This path does not by itself establish all browser scoped behavior, direct Gateway operatorPeer fallback, every model backend, or full DSH product/SDK interoperability. Validation records identify the actual candidate and consumers exercised.
