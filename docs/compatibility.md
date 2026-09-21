# Compatibility and evidence

[中文](compatibility.zh.md)

Cordis.NET targets the pinned DeepSeek Harness vendored Cordis behavior described in [upstream](upstream.md). Compatibility is assessed per observable contract, not by package name or test count alone.

## Status vocabulary

- **Implemented:** the .NET behavior exists.
- **Executed:** a named .NET test, upstream source test, or paired scenario ran.
- **Assertion reviewed:** the upstream setup, helpers, and assertions were individually matched or given an explicit adaptation.
- **Not supported:** the behavior is outside the product or platform boundary.

The inventory contains 631 upstream candidate instances. Current disposition records 323 as implementation complete. Of those, 36 have a complete assertion review and 287 have not received a complete assertion review. The remaining 308 are explicitly non-complete, usually because they concern JavaScript or DeepSeek Harness product behavior outside Cordis.NET. These figures do not claim a compatibility percentage.

## Deployment boundaries

| Path | Supported behavior | Boundary |
|---|---|---|
| Static Core and Composition | JIT and Native AOT; static module registration, lifecycle, configuration, profiles and selected extensions | New plugin code requires republishing the application |
| CLR adapter | Runtime loading, replacement, rollback, and cooperative unload of managed assemblies | Ordinary runtime only; retained CLR references can prevent collection |
| JavaScript adapter | Actual `!!js` evaluation and a controlled context bridge through Jint | Trusted configuration only; no Node module environment, sandbox, or Native AOT claim |

Cordis.NET does not execute arbitrary TypeScript plugins. JavaScript prototypes, thrown primitives, cyclic object graphs, Node resolution, agent/LLM features, telemetry, user interfaces, and package installation workflows are not general compatibility promises.

## Evidence

The latest completed run is summarized in [validation](validation.md). `docs/upstream-tests.json` is the immutable candidate inventory; `docs/test-map.json` contains current dispositions; `docs/scenario-map.json` records differential scenarios. Upstream source execution, .NET tests, paired traces, and manual assertion review remain separate evidence categories.
