# Cordis.NET.Tool

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/tools/Cordis.Cli/README.zh.md)

Validate configuration, run an ordinary CLR application and manage plugins through its authorized Host. Local file commands and online management commands have different prerequisites.

## Install the tool

Use the .NET 10 SDK and matching .NET and ASP.NET Core runtimes. Install the published preview:

```console
dotnet tool install --global Cordis.NET.Tool --version 0.2.1-alpha.1
```

For source development, run `dotnet run --project tools/Cordis.Cli -- <command-and-arguments>` from the repository root with its pinned SDK. See [release and source status](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.md#current-support-and-release-status) for unpublished fixes.

## Validate or preview a configuration file

These local commands parse configuration without activating plugins:

```console
cordis validate cordis.yml
cordis preview cordis.yml --patch cordis.patch.yml --json
```

Use your actual file paths. A valid file does not prove that services, dependencies or JavaScript expressions will activate successfully.

## Run a CLR profile

First build the local feed in the [managed application example](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md). The commands below use its `artifacts/managed-feed` and package identity. Create a separate CLI profile; the CLI does not supply that example's browser mapping. Set the same authorization value in both terminals:

```powershell
$env:CORDIS_MANAGEMENT = 'Bearer local-development-token'
```

This is a loopback example value. A deployed Host supplies its own authentication and authorization policy. The named variable is read as the HTTP authorization header.

Start the Host in the first terminal:

```console
cordis run artifacts/cli-profile --source artifacts/managed-feed --url http://127.0.0.1:5080 --authorization-env CORDIS_MANAGEMENT --allow-build Example.ManagedPlugin@1.0.0-alpha.1 --settings example.managedplugin=limit,credential
```

Keep this Host running. A second process cannot own the same profile. Stop the Host before offline file operations. This route loads CLR code on an ordinary runtime; static AOT applications use the libraries and republish code changes.

## Inspect, install and remove plugins

Use the second terminal against the running Host:

```console
cordis inspect http://127.0.0.1:5080/cordis Example.ManagedPlugin 1.0.0-alpha.1 --source artifacts/managed-feed --authorization-env CORDIS_MANAGEMENT
cordis install http://127.0.0.1:5080/cordis Example.ManagedPlugin 1.0.0-alpha.1 --source artifacts/managed-feed --approve-build --authorization-env CORDIS_MANAGEMENT
cordis settings http://127.0.0.1:5080/cordis example.managedplugin --authorization-env CORDIS_MANAGEMENT
cordis remove http://127.0.0.1:5080/cordis Example.ManagedPlugin --authorization-env CORDIS_MANAGEMENT
```

Inspection does not run package code. Preparation requires both the caller's `--approve-build` and the Host's exact package allowance; approved build targets can include dependencies. `run` accepts repeated `--source` options; an online command selects its inspection source with `--source`. Installed upgrades save the selected version and require restart. Removal retains deployment files.

After a lost install response, query the operation instead of repeating it. `wait <endpoint> <request-id>` observes an active installation; exit code 3 means unknown. The CLI does not automatically retry an installation.

## Edit live Settings or full configuration

Use `settings <endpoint> <namespace>` for the generated live form. Its namespace is the profile entry ID and its revision is numeric. Use `configuration <endpoint> <entry>` for raw configuration with its separate string hash and runtime address. These operations require different permissions.

`schema <endpoint> configuration|settings <entry>` reads the selected declaration. `edit <endpoint> configuration|settings <entry> <revision> <operations.json>` submits one ordered JSON array, for example:

```json
[{"op":"set","path":["limit"],"value":7},{"op":"unset","path":["credential"]}]
```

Submit the revision from the original read. The CLI does not refresh it before writing. Settings accepts a numeric revision, or `-` to omit the expected revision; string hashes and null are refused. Omission retains authorization, complete candidate validation and write coordination. Full configuration can restart an activation; Settings exposes only Host-selected live fields. Add `--authorization-env` to online commands.

## Inspect sources and compatibility

`sources` and `versions <endpoint> <name> --source <feed>` read the selected feeds. `compatibility <endpoint>` reads exact DSH grants and corruption diagnostics. `grant <endpoint> <package@version> <runtime-version> <enabled> <accept-risk>` takes two positional Boolean values, spelled `true` or `false`. The first enables or revokes a grant; the second acknowledges risk. Generic profiles refuse this operation, and a compatibility grant does not approve build execution.

## Delete retained files offline

Stop every Host, Worker and other consumer before running `cordis delete-retained <profile> <package> <version>`. It deletes only an unreferenced version and its receipt. See [retained-file ownership](https://github.com/gentt1024/Cordis.NET/blob/main/docs/clr-artifacts.md).

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
