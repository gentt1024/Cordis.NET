# Release procedure

English | [中文](publication-readiness.zh.md)

Use this guide to prepare and publish a new Cordis.NET preview. For what is already published, see [release evidence](validation.md#released-version-evidence); for unpublished main changes, see [current support](development.md#current-support-and-release-status). Completing local preparation does not publish a release.

## Select the release identity

Choose a new Cordis.NET version and commit its metadata, internal package references, locks and release notes. Cordis.NET versions are independent of the DSH version pinned in `upstream.lock.json`. Record any baseline promotion separately from earlier validation that used another lock.

The repository and package metadata identify `gentt1024/Cordis.NET`. The final version commit, tag and validated package/symbol hashes must agree. Keep published tags, packages and archives unchanged. A same-version CI artifact cannot replace a published package, and renaming an older artifact does not make it a new release.

Describe affected public usage in [the changelog](../CHANGELOG.md) and the relevant guide. Keep English and Chinese documentation synchronized. Preserve licenses and third-party provenance.

## Validate the final source and packages

Run the existing CI on the final candidate and require its configured checks. CI and release share the locked source discovery, shards and strict per-platform union. Windows and Ubuntu also run native JIT/AOT, authoring and package validation; three platforms consume the same Linux-produced authoring batch. A PR result validates that candidate, while a release tag must produce its own formal evidence.

The publication whitelist contains Core, Composition, Extensions, Clr, Hosting, AspNetCore, JavaScript and Tool under `Cordis.NET.*`, with matching symbol packages. `Cordis.Example.Greeting` is validated but excluded from publication.

Inspect the actual nupkg/snupkg batch and execute its offline debug consumer:

```sh
python scripts/package_inspection.py --directory artifacts/release-packages --version "$RELEASE_VERSION" --symbols --debug-consumer
```

Set `RELEASE_VERSION` to the selected new version. The optional `--dotnet` selects the SDK executable; `--source-root` selects the source checkout or source ZIP. Pack committed source: a dirty build can retain an older SourceLink commit and fail inspection.

The symbol check binds each DLL to its portable PDB, verifies SourceLink's repository and commit against package metadata, and checks document hashes against that commit. A source ZIP uses its matching `SOURCE_SHA256.json`. Generated `obj` documents require matching embedded source; embedding a tracked file does not bypass the commit check.

The debug consumer installs Core from the exact local batch into a private cache, loads the matching symbol package and triggers a known library exception. It checks the stack frame's file and line against the embedded source. This proves offline source and symbol correspondence; it does not prove that GitHub SourceLink retrieval or a debugger UI was exercised.

For the selected release batch, verify the locked reference dependencies and relevant source scenarios. The pinned `js-yaml` dependency addresses the recorded [merge-source CPU budget advisory](https://github.com/nodeca/js-yaml/security/advisories/GHSA-2883-xcg3-v3hh); it is not a runtime NuGet dependency. Use `npm ci --prefix reference --ignore-scripts` and `npm audit --prefix reference`, and retain the actual result for the selected source.

## Check repository and publishing access

Before publishing, confirm the remote state:

1. The checked-in GitHub Actions workflow has completed the required checks for the reviewed source.
2. GitHub private vulnerability reporting is enabled, and the repository link in `SECURITY.md` accepts reports.
3. The `nuget-production` environment is protected. NuGet Trusted Publishing selects owner `gentt1024`, repository `Cordis.NET`, workflow `release.yml`, environment `nuget-production` and package scope `Cordis.NET.*`.

Keep the existing protection and approval requirements. Local checks do not establish their current remote status.

## Tag, seal and publish

Create the reviewed new prerelease tag without moving an existing tag. In the `release.yml` workflow, select that same tag as both the workflow ref and the `tag` input.

For an authorized publication, run the workflow once with `publish=true`. Validation runs before sealing; the Linux package batch is sealed, and publication waits for `nuget-production` approval. The publishing job downloads and verifies those exact artifacts instead of rebuilding them. Check the formal run's source identity, validation receipts, package hashes and symbols before approval.

Use `publish=false` for validation-only work. A separate preliminary validation-only run is not required before an authorized publishing run. Preserve failed runs and their evidence; a later pass does not change the earlier result.

## Verify public distribution

After the publish job succeeds, confirm that all eight NuGet packages can be downloaded publicly and that their payloads match the sealed batch. Record NuGet's added signature separately. A propagation-time 404 is not a successful download and is not a reason to push the package again.

Publish the source archive, browser runtime and author-tool archives with their matching manifest, evidence and checksums. The author-tool archive must be the one consumed by the formal platform checks. Check public attachment downloads against their recorded hashes before declaring distribution complete. Earlier package batches and historical PR results cannot substitute for this evidence.

State validation limits in the release notes: macOS authoring/browser consumption does not establish full runtime, application or AOT support. Keep local supplements, failed complete gates and formal release results distinguishable; see [validation](validation.md).
