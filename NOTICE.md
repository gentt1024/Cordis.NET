# Attribution

Cordis.NET is an independently maintained .NET implementation targeting the Cordis sources vendored in
DeepSeek Harness at `ddefc45fbc7f8e46dd73185e68295696d1297887`. It is not an official
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
