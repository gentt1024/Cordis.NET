# Attribution

Cordis.NET is an independently maintained .NET implementation targeting the Cordis sources vendored in
DeepSeek Harness at `639ed015397290b3745d163aafe02ffee4aa3f84`. It is not an official
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

The reusable browser modules in `clients/modules/vendor/` retain DSH's module loader,
contracts, snapshot stores and SlotCore at the same fixed revision. Their provenance
manifest records source hashes; the original MIT licenses are retained in
the corresponding `clients/modules/vendor/*/LICENSE` files. The .NET integration keeps application event selection separate from fixed
carrier behavior.

`clients/modules/vendor/modules/entries.ts` contains a local correction to the fixed
source: replacement records identify installed code independently of successful
activation, so an explicit sync back to an earlier target cannot retry failed newer
code. The provenance manifest preserves the upstream path and hash and records
the adapted file hash separately. This correction is not present in the fixed DSH
source and does not add automatic rollback.

The shared Remote client in `src/Cordis.Composition/Client/` adapts the fixed DSH
Gateway client, mux and lossless-JSON input behavior. The .NET carrier and Gateway
also implement the fixed unary envelope, mux frames and unary attachment workflow.
The original MIT notices remain in `LICENSES/`. Bounded cross-end evidence is
recorded in `docs/validation.md`; this is not a whole-product wire compatibility claim.

`Cordis.Composition.Scope` and Peer registration routing adapt the fixed DSH
`packages/core/scope` and `packages/client/connection/src/operator-peer.ts`.
Their MIT notices remain in `LICENSES/deepseek-harness.LICENSE`.
