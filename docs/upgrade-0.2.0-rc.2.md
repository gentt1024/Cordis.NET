# DSH 0.2.0-rc.2 upgrade

[中文](upgrade-0.2.0-rc.2.zh.md)

Follow-up fixes separate reference snapshots from the Fiber's complete effective object, keep parent raw expressions opaque, and capture SDK-declared host frameworks for self-contained ASP.NET deployments. CLR classification now names its three sources explicitly: the adapter's core manifest, the executable's framework-reference manifest, and framework-dependent host deps. The package gate runs five actual CLR deployment modes as isolated NuGet consumers; hosted CI execution remains separate evidence.

For multi-file authoring, use `PatchLayers` rather than mutating the legacy `Patches` view:

```csharp
var changed = bundle with
{
    PatchLayers = [
        new ConfigurationLayer("first.yml", [new EntryOptions { Id = "row", Config = "first" }]),
        new ConfigurationLayer("second.yml", [new EntryOptions { Id = "row", Config = "second" }])
    ]
};
```

`Patches` returns the original mutable list for one file and a flattened copy for multiple files. Assigning it replaces the entire bundle with one primary layer. `PatchLayers` preserves file provenance and declaration order.

Cordis.NET `0.2.0-alpha.1` targets fixed DSH commit `639ed015397290b3745d163aafe02ffee4aa3f84`. This candidate includes the bounded public-review repairs below. Earlier upgrade checks do not substitute for regression checks on this candidate; final-source results belong to its delivery evidence. Historical test dispositions remain historical evidence.

| Behavior | Native owner | Relevant tests | Adaptation or limit |
|---|---|---|---|
| Captured descriptions and stable references | `Configuration.cs`, `Plugin.cs`, `Fiber.cs` | `ConfigurationTests`, static authoring checks | One bundled validator; explicit typed projections, equality and persistence; no reflection-based second validator |
| Volatile raw / effective / noSave, mixed updates and code generations | Existing Entry/Fiber and HMR chain | `VolatileEntryTests`, V01/V02, HMR tests | Retired references freeze; generic Loader keeps independent policy; activation waiting and failure recovery retained |
| Ordered multi-patch, provenance, atomic skip and diagnostics | `Profiles.cs`, Profile composition/session/maintenance | `ProfileUpgradeTests`, `ProfileSessionTests`, P01 | Multi-patch does not add all-bundle watchers |
| Profile admission and resolver generations | DSH policy/admission, `DeploymentResolution.cs` | Policy, grants, linked-resolution and HMR tests | Damaged grants cannot be overwritten; no Node loader or DSH product runtime |
| R1: expression raw equality | `JsExpression`, existing Entry diff | `PublicConfigurationReviewTests`, V03 | YAML nodes expose JSON `__jsExpr`; comparison never evaluates, unknown CLR values retain identity |
| R2: finite lazy resolution | `ConfigDescriptor`, `Fiber.ResolveConfig` | Finite leaf/tree, optional, raw-map-to-POCO, typed shape and union tests; V04 | Traverse validator input after successful validation. Opaque typed input uses `WithDescriptionData`; lazy unions declare their selected branch. These callbacks never run in raw diff; selectors cannot be serialized as data |
| R3: Bundle record updates | `Bundle` | Legacy `with`, provenance, empty/multi-file views | Ordered layers are the sole authority; assigning legacy `Patches` replaces the bundle with one primary layer |
| R4: no-reference default equivalence | Existing Entry commit path | Equivalent explicit default and changed ordinary value; V05 | Retain raw and skip revalidation on the equivalent no-reference path; force still processes patch-context |
| R5: host framework loading | `ClrModuleResolver` | Actual folder/single-file/ASP.NET deployments and private-dependency negative control | SDK core-framework names plus host-declared shared-framework manifests; exclude application/private dependencies |
| Primitive reference lifetime | `Configuration.References.cs`, `Fiber.cs` | Collectible projector, scalar reference to retired `CollectibleSettings`, and typed-reference retention control | Scalar references retain path/cell and field snapshots, without retaining the full effective POCO or projector binding. Actively retaining the full effective object or a reference whose generic argument is a plugin type can still keep its assembly alive |
| R6–R8: policy and release metadata | DSH policy, props, reference lock, package inspection | Fixed four-bundle policy, targeted audit, portable PDB/checksum and isolated NuGet debug consumer | Own version `0.2.0-alpha.1`; embedded sources support offline debugging; no remote publication claim |

The established trace set remains 35 scenarios; the upgrade set has six, including V03–V05 from this review. Actual fixed-source execution, .NET tests, source navigation, independent probes and paired traces remain separate evidence categories. Configuration API responsibilities and migration are explained in [configuration descriptions](configuration-description.md).

Final reproduction uses the existing verification and authoring flows, static Native AOT, actual CLR deployment fixtures and independent package consumers. Whole-DSH peripheral tests and the retired whole-repository S0 gate are not prerequisites. Native AOT covers static Core/Composition; runtime CLR replacement and JavaScript evaluation retain their existing platform boundaries.

Hosted CI and publication require separate authorization. Historical assertion-review debt remains separate. Reference-only `js-yaml` is fixed at 4.3.2 after targeted advisory repair; runtime NuGet dependencies were not changed. Offline symbol validation does not claim that an unpublished commit can be fetched through its remote SourceLink URL. See [validation](validation.md) and the final delivery evidence for executed commands and limitations.
