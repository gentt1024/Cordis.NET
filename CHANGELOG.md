# Changelog

## [Unreleased]

## [0.2.0-alpha.4] - Unreleased

- Added coordinated Profile/Session writes and immutable profile candidates. Installation refuses stale inputs rather than overwriting concurrent edits; publication permits only the planned package and mapping changes. Admission, persistence and application use the same candidate.
- Applied captured base configuration when Include patches compare equal, preserved legacy reconciliation customization without admission, and required explicit candidate-aware reconciliation when admission is enabled.
- Moved dependency-removal admission before physical package deletion. Rejection preserves the installed directory and dependency while retaining an already-approved deselection; failure results report actual installed, selected, application and residual state.
- Unified C# style under EditorConfig, SDK Roslyn analyzers and pinned repo-local JetBrains formatting. Added read-only formatting checks and a CI gate; mechanical formatting is separate from behavior fixes.
- Documented the coordinated-write contract and required fidelity to fixed upstream production callers and observable behavior. Direct filesystem writers are not covered by an atomic compare-and-swap guarantee.
- Migration: consumers using product admission must coordinate saves through the existing Profile/Session owner and configure the candidate-aware reconciliation callback after any legacy callback customization. Existing consumers without admission retain the legacy callback path.

## [0.2.0-alpha.3] - 2026-10-06

- Corrected the Windows host-death regression to wait for asynchronous child termination while retaining its bounded lifetime and no-further-writes checks. Runtime process ownership is unchanged.
- Reused the preceding solution test results within each verification job, binding them to the checkout, source hashes, SDK, RID and TRX bytes. Authoring contract checks remain required; standalone authoring verification still runs the solution tests.

- Added reusable typed configuration fields, ordered SET/unset editing, Settings selection, declaration export and discovery over the existing runtime contracts.
- Added application-level package coordination, explicit NuGet feeds and build authorization, ASP.NET Core management endpoints, CLI management commands and maintained client modules. These are optional layers above Core.
- Added independent managed plugin and application examples, including package installation, configuration, removal and development build/restart integration.
- Fixed nested Settings validation, raw expression preservation and falsey insert inheritance. Configuration notification failures no longer replace a completed operation's result.
- Aligned authorization with the resolved installation target, rejected known invalid declarations before package builds, and mapped NuGet package/version identities to exact DSH compatibility grants.
- Exposed shell-owned exact module requests and custom bundle loading through the public client bootstrap, catalog and build external selection.
- Preserved client plugin instances and drafts across transient disconnects; restored the fixed upstream startup activation audit, injectable management carriers and automatic/manual connection recovery without replaying uncertain writes.
- Connected application arguments, readiness and exit to the CLI using the existing Composition and .NET Host lifecycle. Application exit retains the upstream five-second cleanup grace.
- Strengthened release sealing to require all eight product packages and their symbols; examples remain excluded from publication.

## [0.2.0-alpha.2] - Not published

- Tagged release candidate, superseded by `0.2.0-alpha.3` before NuGet publication.

## [0.2.0-alpha.1] - 2026-10-03

- Fixed scalar configuration references retaining collectible effective objects and parent expression markers being compared as volatile child fields. Added declared host-framework manifests for self-contained ASP.NET CLR hosts, and run the deployment matrix through isolated NuGet consumers in the existing package gate.
- Updated the pinned DSH behavior baseline to `639ed015397290b3745d163aafe02ffee4aa3f84` (`dsh-v0.2.0-rc.2`). Cordis.NET keeps its own prerelease version.
- Added optional captured configuration descriptions, typed live projections, stable immutable references, and explicit ordinary equality and persistence adapters. Existing `IPlugin` and `Plugin<T>.Config` authoring remain supported.
- Added ordered profile patch files, explicit DSH profile admission, linked resolver generations, and HMR candidate settlement and recovery. Generic Loader does not enforce DSH product version policy.
- Preserved ordinary update/save/veto behavior. Compatible live updates retain the effective configuration and reference identities; invalid live input retains raw input without replacing running values. Restart and code replacement create new references and leave old references at their final values.
- Migration: descriptor defaults and field paths must match the validator; POCO configurations need typed ordinary equality and save projections. Multi-file profiles apply patches in declaration order. Static Native AOT code changes require republishing; runtime CLR replacement remains a separate deployment path.
- Lazy descriptions resolve only used input paths. Typed raw input can provide `WithDescriptionData`; lazy unions need a branch selector consistent with the validator. Descriptions with callbacks cannot be serialized as if those callbacks were data.
- Updated reference-only `js-yaml` to 4.3.2 to address its recorded security advisories. Runtime NuGet dependencies are unchanged.
- Prepared bilingual public documentation, package readmes, community templates, and documentation checks.
- Replaced private handoff material and machine-specific raw logs with public summaries.

This prerelease was published as seven `Cordis.NET.*` packages. The application-management additions above are not part of that released version.

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
