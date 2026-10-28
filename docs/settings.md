# Read and edit live Settings

English | [中文](settings.zh.md)

Settings exposes selected live configuration through the running profile's existing writer. Describe the current form, edit its offered paths and submit its numeric revision. Full raw configuration has a separate permission and hash contract.

## Prepare the Host and form

Use matching Composition and AspNetCore packages with a .NET 10 ASP.NET Core Host. Authors must declare live fields through their Config schema; a running entry must offer the form you want to edit. See [configuration descriptions](configuration-description.md) for those declarations.

The snippets below are Host and consumer integration fragments, not one combined program. The consumers assume a `worker` form with a live `limit` field; [ManagedApplication](../examples/ManagedApplication/README.md) is a complete example with namespace `example.managedplugin` instead.

## Mount the provider and controller

<a id="host-and-consumer-use"></a>

Use the running session's `ConfigurationOperations`. `app`, `session` and the authorization callback belong to your Host. Create the Connection inside the existing Context execution domain:

```csharp
var registry = new TypertRegistry(session.Context);
var contribution = SettingsControllerTypert.Contribution("Application.Settings");
TypertHostConnection connection = null!;
await session.Context.RunAsync(ctx =>
{
    new SettingsForms(ctx, session.ConfigurationOperations);
    new SettingsController(ctx);
    registry.Register(ctx, contribution);
    connection = new(ctx, new TypertGateway(ctx, registry));
    ctx.Provide("connection", connection);
    return Task.CompletedTask;
});
app.UseWebSockets();
app.MapCordisRemote("/remote", connection, authorize);
var clientArtifact = TypertArtifacts.GenerateClient(contribution);
```

The optional SettingsPolicy limits visibility; absent a policy, supported declared live fields are offered. The controller can be registered without a provider, but a call with no current `settings` provider fails with `gateway/internal`. Each call resolves the current provider. Ordinary in-process Settings use needs no Remote.

Deliver `clientArtifact.Module` and its declaration together, using the same module basename. The TS example below assumes `settings.mjs`. The Host owns authentication, authorization and shutdown. Await Connection disposal and withdraw its registration before disposing the owning Context; clients borrow these resources.

## Read and write from C#

Generate an independent client with service `settingsController`, client name `SettingsClient` and namespace `Example.Settings`, using Composition's saved model and the [client project settings](authoring.md#generate-an-independent-net-client). This example uses a Host at `http://localhost:5000`; replace that address with your mapped endpoint and supply its authorization through HttpClient as required:

```csharp
using Cordis.Composition;
using Example.Settings;
using System.Text.Json;

const string ns = "worker";
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri("http://localhost:5000/remote"));
await using var remote = new SettingsClient(connection);
var describe = await remote.DescribeAsync();
if (!describe.Ok) throw describe.Error;
var form = describe.Value.Namespaces?.SingleOrDefault(row => row.Ns == ns)
    ?? throw new InvalidOperationException("The Host does not offer the worker form.");
using var document = JsonDocument.Parse("""{"limit":30}""");
var updated = await remote.UpdateAsync(ns, document.RootElement, form.Revision);
if (!updated.Ok) throw updated.Error;
Console.WriteLine(updated.Value.Revision);
```

Check `Ok` before reading either successful value. A write returns a fresh redacted namespace. The `await using` order disposes the client before its borrowed Connection and HttpClient. A missing form means the Host did not offer that namespace; do not substitute an unrelated entry or revision.

## Read and mutate from TypeScript

Import the generated module and connect to the actual Remote address. Here the Host shares the page origin:

```ts
import { createRemote, TypertConnection } from './settings.mjs'

const connection = new TypertConnection(new URL('/remote', location.href))
const remote = createRemote(connection)
const ns = 'worker'
try {
  const read = await remote['settings/describe']({})
  if (!read.ok) throw new Error(read.error.message)
  const form = read.value.namespaces?.find(row => row.ns === ns)
  if (!form) throw new Error('The Host does not offer the worker form')
  const write = await remote['settings/mutate']({
    ns,
    ops: [{ op: 'set', path: ['limit'], value: 30 }],
    expectedRevision: form.revision,
  })
  if (!write.ok) throw new Error(write.error.message)
  console.log(write.value.revision)
} finally {
  await remote.dispose()
  await connection.dispose()
}
```

The generated output permits omitted fields: check the optional namespaces collection, then write to the application's selected namespace. Handle both read and write failures. A `settings/conflict` requires a fresh read and an application decision about the user's draft, not an automatic retry of the same edit. Native provider writes return Task; the Remote controller describes the same captured provider after completion.

## Choose an edit operation

- `update` recursively merges objects and replaces arrays.
- `replace` resets the offered live fields against inheritance, then merges the submitted section while retaining ordinary configuration. Replacing from a redacted read can reset omitted secrets.
- `mutate` applies ordered paths. Object unset restores inheritance; array unset removes the element. A set includes `value`, which may be explicit JSON null; unset omits `value`.

Use mutate for incomplete views. Empty namespace is refused with `gateway/bad-request` before provider lookup. Reads preserve public defaults, omit hidden properties and remove secret defaults and required flags. Secret presence is available separately, never the secret value. Non-live object paths are fresh wrappers; metadata at a live boundary is retained.

## Revision and commit semantics

The .NET numeric carrier is double, matching JavaScript number. Revisions start at zero for one SettingsForms owner and observe entry Fiber identity, the complete Config declaration and raw configuration. A presentation-only Configure change emits `settings/document-updated` with the same revision. Observed disappearance increments once; reappearance advances again. A new owner starts new observations. Revisions are not persisted content hashes or permanent cross-process counters.

A stale expected revision refuses before persistence with `settings/conflict` and numeric `{ns, expected, actual}` details. Omission skips only the prior-form comparison; authorization, offered paths, full candidate validation, profile queue and captured-source consistency still apply. Omit the optional argument to omit it on the wire. Explicit JSON null is different and refused; never stringify a revision.

Full configuration APIs retain their own file/source hash contract and share the same profile writer. A full-configuration hash is not a Settings form revision. Use [document opening](authoring.md#open-the-settings-document) only as a separately authorized Host action; it opens the Host's document, not a browser download.
