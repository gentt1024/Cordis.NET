# Attribution

Cordis.NET is an independently maintained .NET implementation targeting the Cordis sources vendored in
DeepSeek Harness at `687f282e8625c6b8d11ff58211a90524be2e4317`. It is not an official
DeepSeek, Cordiverse or Microsoft project. The lock distinguishes the Harness release label,
vendored package version and source origins.

The Cordis lifecycle, registry, service, event and effect algorithms, Loader/Include patches,
and adapted tests derive from MIT-licensed Cordiverse Cordis and the DeepSeek Harness fork.
Retained licenses are in `LICENSES/`; baseline and provenance are documented in
`docs/upstream.md`. DeepSeek Harness local changes take precedence over origin code.

Historical handoff inputs are retained outside the public repository. Current implementation
status and validation evidence are documented independently in `docs/compatibility.md` and
`docs/validation.md`.

Third-party packages are resolved by exact versions in project files and packages.lock.json.
Their respective licenses remain applicable. YAML is parsed with YamlDotNet's low-level
parser/emitter; optional JS evaluation uses Jint. Reference-only TypeScript/Vitest/js-yaml
dependencies are development tools and are never runtime dependencies of C# consumers.

The Probes client's `client/vendor/form-model.ts` and `client/vendor/store/` retain
the unmodified MIT-licensed DSH form model and snapshot-store source at the same fixed
revision. `client/vendor/provenance.json` records the exact source identities. Their
browser build uses Zustand and Immer under their respective MIT licenses. This is
a bounded client example, not a translation of the DSH frontend or its wire protocol.

The browser SDK bundles the published DSH Cordis, Cosmokit, Loader, Connection and
Slots modules selected by the client npm lock. The runtime distribution includes
their license notices in `LICENSES.txt`. The subset of client-module source and
the browser stub retained in `clients/modules/vendor/` are identified in
`provenance.json`, with their upstream paths, upstream and adapted hashes, and
associated license files.

The module entries track which code was installed even when activation fails.
The release-change gate captures its decision before synchronous notifications.
These local adaptations preserve explicit recovery and the result of each
call during reentrant notification. See the browser SDK section of
`docs/compatibility.md` for the conditions for retiring the adaptations.

The shared Remote client in `src/Cordis.Composition/Client/` adapts the fixed DSH
Gateway client, mux and lossless-JSON input behavior. The .NET carrier and Gateway
also implement the fixed unary envelope, mux frames and unary attachment workflow.
The original MIT notices remain in `LICENSES/`. Bounded cross-end evidence is
recorded in `docs/validation.md`; this is not a whole-product wire compatibility claim.

`Cordis.Composition.Scope` and Peer registration routing adapt the fixed DSH
`packages/core/scope` and `packages/client/connection/src/operator-peer.ts`.
Their MIT notices remain in `LICENSES/deepseek-harness.LICENSE`.
