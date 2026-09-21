# Publication readiness

The current tree is prepared as a reviewable public source snapshot. Documentation, package metadata, package-specific readmes, community templates, public validation evidence, license material, and local release checks are present.

The repository identity and package metadata are fixed to `gentt1024/Cordis.NET`. The remaining external gates are:

1. Run the checked-in GitHub Actions workflow on the public remote and require both platform checks on `main`.
2. Enable GitHub private vulnerability reporting so the verified link in `SECURITY.md` accepts reports.
3. Protect the `nuget-production` environment and configure a NuGet Trusted Publishing policy for owner `gentt1024`, repository `Cordis.NET`, workflow `release.yml`, and environment `nuget-production`.
4. After those gates pass, create the reviewed prerelease tag and publish the exact hashed package artifact produced by the release workflow.

A repository-external maintainer archive preserves the original handoff, raw evidence, and private development history. The public repository starts from a clean root commit.
