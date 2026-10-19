# Settings guide

[中文](settings.zh.md)

Settings exposes live configuration forms through the existing configuration authority. Use numeric revisions and omit `expectedRevision` only when a prior-form comparison is not required.

## Host and consumer use

Mount the ordinary `SettingsForms` service over the running session's existing `ConfigurationOperations`. The optional `SettingsPolicy` limits host visibility; absent a policy, declared supported live fields are offered. Publish the standard controller independently of that optional provider and register its generated contribution:

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
    return Task.CompletedTask;
});
app.UseWebSockets();
app.MapCordisRemote("/remote", connection, authorize);
var clientArtifact = TypertArtifacts.GenerateClient(contribution);
```

The host supplies `authorize` and delivers the generated module and declaration together. The ordinary service remains usable in process; it does not require Remote. Remote resolves the current `ISettingsProvider` named `settings` on each call. Its absence is a `gateway/internal` refusal. See [authoring](authoring.md#native-authored-models-and-net-consumers-2026-10-10) for the model-only .NET client build.

```csharp
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri("http://localhost:5000/remote"));
await using var remote = new SettingsClient(connection);
var describe = await remote.DescribeAsync();
if (!describe.Ok) throw describe.Error;
var form = describe.Value.Namespaces.Single(row => row.Ns == "worker");
using var document = JsonDocument.Parse("{\"limit\":30}");
var updated = await remote.UpdateAsync(form.Ns, document.RootElement, form.Revision);
```

```ts
import { createRemote, TypertConnection } from './settings.mjs'

const connection = new TypertConnection(new URL('/remote', location.href))
const remote = createRemote(connection)
const read = await remote['settings/describe']({})
if (!read.ok) throw new Error(read.error.message)
const form = read.value.namespaces.find(row => row.ns === 'worker')!
const write = await remote['settings/mutate']({
  ns: form.ns,
  ops: [{ op: 'set', path: ['limit'], value: 30 }],
  expectedRevision: form.revision,
})
await remote.dispose()
await connection.dispose()
```

Normal editing submits the revision it read. All three Remote writes return a fresh redacted namespace. Native provider writes return Task; the controller then describes the same captured provider. An empty `ns` is refused as `gateway/bad-request` before provider lookup, whether or not the optional provider is present. `update` recursively merges objects and replaces arrays. `replace` resets the offered live fields against inheritance, then merges the submitted section while retaining ordinary configuration. A replace assembled from a redacted read can reset omitted secrets. Use `mutate` for incomplete views: ordered object unsets restore inheritance, and array unsets remove the element. Set requires a `value`, which may explicitly be JSON null; unset omits `value`.

The form schema preserves redacted public defaults. Secret defaults and required flags are removed, and host-hidden fields are omitted. Non-live object paths use fresh empty-object wrappers; metadata at a live boundary is retained.

## Revision and commit semantics

The .NET numeric carrier is `double`, matching JavaScript number. Revisions begin at zero and belong to one `SettingsForms` owner. They observe entry Fiber identity, the complete Config declaration and raw entry configuration. A presentation-only `Configure` change emits `settings/document-updated` with the same revision. Observed disappearance increments once; reappearance advances the observation again. A new owner starts new observations. Revisions are neither persisted nor derived from a content hash, and do not promise permanent cross-process monotonicity.

Supplying a stale expected revision refuses the write before persistence with `settings/conflict` and numeric `{ns, expected, actual}` details. Omitting it removes only that prior-form comparison. Host authorization, offered-path policy, complete candidate validation, the profile queue and captured-source consistency checks still apply. Generated clients omit the argument on the wire when the optional C# carrier has its default; explicit JSON null remains a different input and is rejected. Do not stringify a numeric revision, silently discard a stale argument, or send null to mean omission.

The existing full-configuration/file hash mechanism retains its independent source-editing duties. `ReadConfigurationAsync`, `MutateConfigurationAsync`, `ReadConfigurationSchemasAsync` and profile/package operations still use their own documented hash contracts. They share the existing profile authority and write flow with Settings. A full-configuration hash cannot serve as a Settings form revision.
