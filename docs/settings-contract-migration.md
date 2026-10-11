# Standard Settings contract migration

[中文](settings-contract-migration.zh.md)

The standard Settings contract is corrected directly against DSH `639ed015397290b3745d163aafe02ffee4aa3f84`. Rebuild the generated .NET and TypeScript consumers. There is one recommended contract, with numeric `revision` and optional numeric `expectedRevision`; old string revisions and explicit null expected revisions are refused. No versioned Remote, legacy endpoint or revision conversion is provided. The [port policy](compatibility.md#upstream-fidelity-requirement) governs this correction.

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

## Removed paths and consumer changes

The Settings-only hash DTOs and operations are removed: `SettingsView`, `SettingsField`, `SettingsSecret`, `SettingsNamespaceSelection`, `ISettingsDescribeProvider`, `ProfileSettingsDescribeProvider`, `ReadSettingsAsync`, `EditSettingsFieldAsync`, `MutateSettingsAsync`, `ReadSettingsSchemasAsync` and the former selection-based `DescribeSettingsAsync`. Use `SettingsForms`, `ISettingsProvider` and the one generated controller instead.

`CordisManagement` no longer publishes `/settings`, `/settings/schema` or the handwritten Settings mutation route. The browser management client no longer supplies Settings hash methods. Its raw configuration methods retain their independent purpose. The maintained application, CLI, probes and package consumers use the generated Settings contract. CLI Settings namespaces are profile entry ids such as `worker`, rather than runtime addresses such as `root:worker`; full-configuration commands retain runtime addresses. CLI Settings reads/schema/edits call the same generated controller.

Maker is outside this change. Its owning session must regenerate its client, preserve numeric revisions in drafts, send the observed value on ordinary edits, use path mutations for redacted forms, handle conflict without discarding drafts, and stop calling the removed handwritten routes. This library adds no compatibility path on Maker's behalf.

## Data and publication

Persisted YAML/JSON configuration and profile formats are unchanged. No user document migration, file deletion or Git-history rewriting is required for this RPC correction. Historical validation checkpoints retain their original meaning; current use follows this contract.

These are local source changes. The source package version and upstream lock are unchanged. Publishing this contract requires a new package version identity; it must not replace an already published alpha.4 package. Regenerating a Remote contract and choosing a future package version are separate operations.
