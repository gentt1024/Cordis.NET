# Contributing to Cordis.NET

[中文](CONTRIBUTING.zh.md)

Contributions to code, documentation, translation, compatibility counterexamples, and assertion review are welcome.

## Before changing code

Read [development](docs/development.md), [compatibility](docs/compatibility.md), and the relevant component map. Small, focused fixes do not need an RFC. Discuss changes to public APIs or intentional differences from the pinned behavior before implementation.

## Validate the change

### C# style and lint

The root `.editorconfig` is the style authority. The pinned SDK's compiler and Roslyn analyzers own correctness, nullable, async, lifetime, interop, performance, and API-usage diagnostics. Build warnings are errors. Formatting does not apply semantic fixes.

```console
python scripts/format.py
python scripts/format.py --check
```

The script restores the repository-local JetBrains ReSharper GlobalTools version from `dotnet-tools.json` and the solution's locked dependencies. It runs `jb cleanupcode` with `Built-in: Reformat Code` on repository C# files, then `dotnet format whitespace` in folder and solution modes, then CleanupCode again. Solution mode covers conditional compilation branches. Every stage uses the same explicit C# file list, including fixtures and tools outside the solution. Each later stage must leave the previous output unchanged.

`--check` copies current tracked and non-ignored new files, including configuration and uncommitted edits, to a temporary directory. Restore and formatting run there; nothing is written back to the working tree. Any stage changing the copied C# files fails the check. Files resolving outside the repository are rejected. CI runs this check on Windows and Linux. Git and Python are required.

Roslyn defines braces, newlines, spacing, and indentation; single-line blocks and embedded statements are expanded. JetBrains adds wrapping for signatures, arguments, call chains, and initializers at a target width of 120 columns. Unbreakable tokens and string contents may exceed that width. GlobalTools is a development tool, not a dependency of any `Cordis.NET.*` package. CSharpier is not used.

To keep both formatters stable, nested loops are indented, `for` semicolons have no padding, and explicit line breaks are preserved, including before a call after a multiline constructor. Existing lifecycle exceptions have narrow, commented analyzer suppressions; formatting must not change cleanup or cancellation timing.

Rider and Visual Studio read `.editorconfig` automatically. In Rider or Visual Studio with ReSharper, use **Reformat Code**, with Roslyn analyzers enabled. Visual Studio's built-in formatter handles the Roslyn rules; run the script before submitting changes to apply width-aware wrapping too. The pinned CLI output is canonical. Avoid Full Cleanup for formatting-only changes because it can rewrite code. See the [CleanupCode reference](https://www.jetbrains.com/help/resharper/CleanupCode.html) and [wrapping settings](https://www.jetbrains.com/help/resharper/EditorConfig_CSHARP_LineBreaksPageSchema.html).

### Build and tests

Install the exact SDK in `global.json` and select that installation in Rider/Visual Studio and your CLI. SDK patch roll-forward is disabled to keep SDK-supplied dependencies, including ILLink, aligned with CI and the lock files. Regenerate dependency locks only with that SDK; normal validation uses locked restore.

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

Run `python scripts/verify.py` for runtime, packaging, compatibility, or release changes. Documentation-only changes need the documentation check and a focused review; they do not require every Windows, Linux, or AOT gate.

Keep English and Chinese partner documents synchronized when a promise, command, or code sample changes. Add third-party code only with its license and provenance. Never commit credentials, personal paths, raw local logs, or undisclosed vulnerability details.

Pull requests should explain the behavior change, tests, bilingual documentation impact, and licensing impact. By contributing, you agree that your contribution is provided under the repository's MIT license.
