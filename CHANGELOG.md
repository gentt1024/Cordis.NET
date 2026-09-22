# Changelog

## [Unreleased]

- Prepared bilingual public documentation, package readmes, community templates, and documentation checks.
- Replaced private handoff material and machine-specific raw logs with public summaries.

## [0.1.0-alpha.3] - 2026-09-22

- Added optional typed event, configuration binding, owned external callback, and boot/resource helpers over the existing runtime.
- Added static and CLR Probes examples covering caller contributions, provider replacement, reactivation, cleanup, and unload observation.
- Documented asynchronous event result adaptation and verified raw/typed boundaries without changing synchronous event semantics.
- Extended Windows/Linux gates with authoring negative compilation, real JIT/Native AOT execution, and isolated consumers of the same validated package batch.
- Centralized existing third-party dependency versions without upgrades and isolated the CI SDK selected from `global.json`.

## [0.1.0-alpha.2] - 2026-09-21

- Changed the seven public NuGet package IDs to the unoccupied `Cordis.NET.*` prefix after the original `Cordis.*` IDs returned an ownership error.
- Kept CLR namespaces, assembly names, source paths, and runtime contracts unchanged.
- Updated package dependency checks, isolated consumers, release whitelists, and installation documentation for the new IDs.

## [0.1.0-alpha.1] - 2026-09-21

- Implemented the pinned DeepSeek Harness Cordis runtime and composition scope.
- Added locked inventories, differential scenarios, Native AOT validation, and independent package consumers.

This version has not been published to NuGet.
