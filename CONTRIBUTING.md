# Contributing to Cordis.NET

[中文](CONTRIBUTING.zh.md)

Contributions to code, documentation, translation, compatibility counterexamples, and assertion review are welcome.

## Before changing code

Read [development](docs/development.md), [compatibility](docs/compatibility.md), and the relevant component map. Small, focused fixes do not need an RFC. Discuss changes to public APIs or intentional differences from the pinned behavior before implementation.

## Validate the change

Create a branch from main, locate the owner and tests, and complete one concrete change with paired documentation. Describe the problem, change, risks and verification entry in the PR.

For documentation, run `python scripts/check-docs.py` and check changed anchors. For C#, run related tests, build and formatting checks. Runtime, package, compatibility and release changes require `python scripts/verify.py`. See [contributor tooling and CI](docs/development.md#contributor-tooling-and-ci) for SDK, formatter and CI shard details. Pure prose does not require manually repeating the full AOT/browser matrix.

Keep English and Chinese partner documents synchronized when a promise, command, or code sample changes. Add third-party code only with its license and provenance. Never commit credentials, personal paths, raw local logs, or undisclosed vulnerability details.

Pull requests should explain the behavior change, tests, bilingual documentation impact, and licensing impact. By contributing, you agree that your contribution is provided under the repository's MIT license.
