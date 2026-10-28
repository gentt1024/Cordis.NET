# Forward selected events to Remote listeners

English | [中文](remote-events.zh.md)

Declare the events you want to expose, select them at the Host, and register listeners under a Client owner. The fixed DSH workflow uses `$events` and `$events/result`; it does not publish every application or DSH product event automatically.

## Prepare both sides

Use matching Composition packages, a generated event declaration and explicit STJ metadata. A network Host also needs its existing Registry, Gateway, authorized Connection and ASP.NET adapter. A Client needs a Cordis Context, Registry and borrowed Connection. Follow [Remote Host setup](authoring.md#register-call-and-withdraw-contracts) first.

The following snippets are parts of an application. `Agent`, its Id/Context, request and notification DTOs, the JSON context, business handlers and `localContinuation` are application-owned. Register ordinary JSON boundary types in AppJson. The subject's scoped lifetime must be registered using the [scope routing](authoring.md#registration-scopes-and-peer-event-routing) API before expecting a remote waterfall.

## Declare and select

Mark public delegates with RemoteEvent. Plain notifications keep their argument list; asynchronous scoped waterfalls retain their `next()` result contract:

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

The request holds business data; projected agent and signal are not serialized CLR resources. Select forwarding under the Host owner's execution domain and supply subject projection once:

Host forwarding and invocation:

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

The business call supplies the subject, not an explicit wire identity. The helper uses `Scope.Target(agent, agent)` and retains the original subject for forwarding. A receiver without a scope key continues locally through next; a keyed carrier with a missing or different subject is refused before projection. Keep normal filtering, continuation and source ownership.

## Listen in C#

Register Client listeners inside the owner execution domain. Caller and Client owner must share one root. For the agent receiver, register `registry.RegisterClientContext(owner, "agent", adapter)` first: its synchronous Resolve maps the incoming identity to a borrowed Context or `TypertResolvedContext.Owned`. Reuse the same adapter's Identity operation for outbound scoped calls. The snippets assume that application adapter is already installed:

```csharp
TypertRemoteEventsClient events = null!;
await owner.RunAsync(ctx =>
{
    events = new TypertRemoteEventsClient(ctx, registry, connection);
    ApplicationChangedTypert.On(events, caller, change => Refresh(change));
    DecisionRequestedTypert.On(events, caller, async (agent, request, next, signal) =>
    {
        if (!CanHandle(request)) return await next();
        return new RemoteOptional<string?>(await AskAsync(agent, request, signal));
    });
    return Task.CompletedTask;
});
try
{
    await events.RunAsync(host => OnReady(host.Home), generationCancellation);
}
finally
{
    await events.DisposeAsync();
}
```

RunAsync owns one generation and stays active until cancellation or disconnection. The surrounding connection controller owns retry. Listener registrations belong to caller's Fiber; the event Client borrows Context, Registry and Connection. Dispose the event Client before its borrowed resources.

The listener returns RemoteEventResult<T>. `await next()` preserves a continuation marker separately from any T value. RemoteOptional<T> represents allowed undefined independently from present null. The Client callback's agent is the resolved Client Context, not a reconstructed Host Agent object.

## Generate independent listeners

Use `emit-dotnet --service <event-name-or-delegate-name>` with the existing model/client project properties to generate typed On helpers and data before compilation. The model-only Web route uses CordisTypertWebModel and explicit reference models, producing `createEvents` in `cordis/typert/web/remote.mjs` with declarations and shared Connection assets. No live Host or provider DLL is needed for generation; forwarding still requires Host selection.

Ordinary arguments, requests and continuation values are Host output; handler replies are Host input. Collection-element nullability comes from the declaration, including nullable lists and dictionary values. Register normal CLR types in STJ; do not hand-author another schema. Independent backends refuse serializer behavior they cannot reproduce. See [generation support](development.md#generation-backend-support).

## Listen in TypeScript

The generated module below is served as events.mjs. Supply the existing owner, Connection and adapter source; application callbacks provide the UI and business action:

```ts
import { createEvents } from './events.mjs'
const events = createEvents(owner, connection, adapters)
events.on(caller, 'app/decision', async (request, next) => {
  if (!canHandle(request)) return next()
  return ask(request.agent, request, request.signal)
})
await events.run(generationSignal, host => onReady(host.home))
```

Every invocation reads `adapters.getClient('agent')` again. Its resolver returns a borrowed Cordis Context, an owned value from typertOwnedValue, or undefined. `next()` runs the remaining Client listener chain; when that chain has no answer its continuation delegates to the Host. It does not expose the Host's later return value as a browser round trip.

The browser package's standard events are exported through `/events`. Supply them to createManagementClient to reuse its subscribe-before-read recovery. Use the same generation cancellation signal and close/dispose the event Client when the page owner ends.

## Settlement and evidence boundaries

Resolvers and release callbacks are synchronous. An owned cleanup must not throw. Missing or failed resolution delegates to Host continuation. Borrowed values are not disposed; owned values release at most once after the handler and result reply settle. Cancellation reaches the handler but does not free its Context early. Client and Connection disposal wait for active waterfall work.

Disconnect removes a delivery without synthesizing next; pending calls can reach a later connection generation. Source withdrawal closes streams and cancels pending work. Obsolete identities and duplicate replies cannot settle or revive a current delivery. Replies distinguish result, next and rejection, including name/message/code/details; notifications have no durable replay guarantee.

The protocol and owned-value sources are under fixed DSH `packages/typert/protocol` and `packages/api/gateway`, with Host forwarding in `packages/api/remotes`. [upstream.lock.json](../upstream.lock.json) selects the current baseline; [compatibility](compatibility.md) records adaptations and [validation](validation.md) identifies completed consumers. Selected event forwarding does not establish complete DSH product/SDK interoperability.
