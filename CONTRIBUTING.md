# Contributing to Cordis.NET

[中文](CONTRIBUTING.zh.md)

Contributions to code, documentation, translation, compatibility counterexamples, and assertion review are welcome.

## Before changing code

Read [development](docs/development.md), [compatibility](docs/compatibility.md), and the relevant component map. Small, focused fixes do not need an RFC. Discuss changes to public APIs or intentional differences from the pinned behavior before implementation.

## Validate the change

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

Run `python scripts/verify.py` for runtime, packaging, compatibility, or release changes. Documentation-only changes need the documentation check and a focused review; they do not require every Windows, Linux, or AOT gate.

Keep English and Chinese partner documents synchronized when a promise, command, or code sample changes. Add third-party code only with its license and provenance. Never commit credentials, personal paths, raw local logs, or undisclosed vulnerability details.

Pull requests should explain the behavior change, tests, bilingual documentation impact, and licensing impact. By contributing, you agree that your contribution is provided under the repository's MIT license.
