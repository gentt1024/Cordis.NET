# Cordis.NET.Tool

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/tools/Cordis.Cli/README.zh.md)

Validate configuration, run an ordinary CLR application and manage plugins through its authorized Host. Local file commands and online management commands have different prerequisites.

## Install the tool

Use the .NET 10 SDK and matching .NET and ASP.NET Core runtimes. Install the published preview:

```console
dotnet tool install --global Cordis.NET.Tool --version 0.2.1-alpha.4
```

For source development, run `dotnet run --project tools/Cordis.Cli -- <command-and-arguments>` from the repository root with its pinned SDK. See [release and source status](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.md#current-support-and-release-status) for unpublished fixes.

## Validate or preview a configuration file

These local commands parse configuration without activating plugins:

```console
cordis validate cordis.yml
cordis preview cordis.yml --patch cordis.patch.yml --json
```

Use your actual file paths. A valid file does not prove that services, dependencies or JavaScript expressions will activate successfully.

## Manage profile dependencies offline

Use the development CLI for these commands. It can create a profile on the first successful add. Supply the source explicitly. Local paths resolve against your current directory.

```console
cordis plugin --profile dev add ./examples/ManagedPlugin
dotnet build examples/ManagedPlugin -c Debug
cordis plugin --profile dev list
cordis run dev --configuration Debug
```

For a feed package, pass `--source <feed>` and approve the exact package build with `--allow-build ID@version`. For example, `add Example.ManagedPlugin@1.0.0-alpha.1` pins the version. A versionless add selects the latest stable version. Quote NuGet ranges such as `"My.Plugin@[1.0,2.0)"`. An explicit `./file.nupkg` selects that archive. Each root has its own NuGet lock and private dependencies. The command accepts NuGet specifications and local roots. Use a separate Git checkout for Git sources, and pnpm for Web workspace, tag and script commands.

Use `install` to restore saved declarations, `update [ID]` to re-evaluate ranges, `why ID` to inspect the recorded NuGet graphs and `remove ID` to withdraw a source. Removal retains local source and deployed files. Installation of an ordinary library leaves the bundle list alone. The author project owns its compilation references. An existing online installation can still run from its verified exact-version deployment. Its first offline install needs a configured source and build approval to create the standard NuGet lock. Offline commands report whether the profile changed. Check the running Host separately for activation and notification.

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

Keep this Host running. A second process cannot own the same profile. Offline package edits use the profile manifest writer lock. Physical deletion of retained files requires all consumers to stop. This route loads CLR code on an ordinary runtime; static AOT applications use the libraries and republish code changes.

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

Stop every Host, Worker and other consumer before running `cordis delete-retained <profile> <package> <exact-version-or-graph>`. Pass the saved graph ID for an offline deployment. The command deletes an unreferenced deployment and its receipt. See [retained-file ownership](https://github.com/gentt1024/Cordis.NET/blob/main/docs/clr-artifacts.md).

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
