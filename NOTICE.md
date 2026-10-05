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
the corresponding `clients/modules/vendor/*/LICENSE` files. The .NET management transport is an explicit adapter,
not a claim of DSH wire-protocol compatibility.
