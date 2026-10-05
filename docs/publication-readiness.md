# Publication readiness

[中文](publication-readiness.zh.md)

The current tree is prepared as a reviewable public source snapshot. Documentation, package metadata, package-specific readmes, community templates, public validation evidence, license material, and local release checks are present.

The repository identity and package metadata are fixed to `gentt1024/Cordis.NET`. The published `0.2.0-alpha.1` batch remains unchanged. Later application-infrastructure additions are source-only. A new version must be chosen before an authorized release; local verification packages using the source version do not authorize replacing published packages. Cordis.NET versions are independent of the pinned DSH tag. The final commit, version, tag and selected package hashes must agree.

The release whitelist contains Core, Composition, Extensions, Clr, Hosting, AspNetCore, JavaScript and Tool under `Cordis.NET.*`, with matching symbol packages. `Cordis.Example.Greeting` is validated but never selected for publication.

Before selecting a release batch, run the existing native, fixed-source, JIT/Native AOT and isolated package-consumer checks on the final source. Also inspect the actual nupkg/snupkg batch and execute its offline debug consumer:

```sh
python scripts/package_inspection.py --directory artifacts/release-packages --version 0.2.0-alpha.1 --symbols --debug-consumer
```

The optional `--dotnet` selects the SDK executable and `--source-root` selects the source checkout or source ZIP. The symbol check binds each DLL to its portable PDB, checks SourceLink's repository and commit against package metadata, and verifies document checksums against that Git commit. A source ZIP uses its matching `SOURCE_SHA256.json` commit and source hashes. Generated `obj` documents must have matching embedded source. Tracked source cannot bypass the commit check merely because it is embedded. Pack only after committing final source; a dirty source build can carry an old SourceLink commit and fail this check.

The isolated debug consumer installs Core from the exact local batch into a private package cache, loads the matching snupkg PDB, triggers a known library exception and reads its actual stack frame file and line. The inspector verifies the corresponding embedded source bytes and reports that line. `EmbedAllSources` makes this offline lookup possible. This checks offline source identity and stack symbol resolution; it does not claim an unpublished SourceLink URL was fetched from GitHub or that a debugger UI was exercised.

Reference tooling pins `js-yaml` 4.3.2. This is a targeted fix for the recorded advisories, including [empty merge-source CPU budget bypass](https://github.com/nodeca/js-yaml/security/advisories/GHSA-2883-xcg3-v3hh). Rerun `npm ci --prefix reference --ignore-scripts`, `npm audit --prefix reference` and the relevant fixed-source reference scenarios for the selected batch. It is not a runtime NuGet dependency.

Each future release requires these separately authorized external checks; local preparation does not establish their current remote status:

1. Run the checked-in GitHub Actions workflow on the public remote and require both platform checks on `main`.
2. Verify that GitHub private vulnerability reporting is enabled and the repository link in `SECURITY.md` accepts reports.
3. Protect the `nuget-production` environment and configure a NuGet Trusted Publishing policy for owner `gentt1024`, repository `Cordis.NET`, workflow `release.yml`, environment `nuget-production`, and package scope `Cordis.NET.*`.
4. After those gates pass, create the reviewed prerelease tag and publish the exact hashed package artifact produced by the release workflow.

A repository-external maintainer archive preserves the original handoff, raw evidence and private development history. Continue from the existing public Git history; this release preparation does not require a new root commit or history rewrite. Record formal baseline promotion separately from earlier stages in which the lock was unchanged. Historical delivery statements do not establish the final package or publication result.
