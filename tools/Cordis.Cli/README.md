# Cordis.NET.Tool

```console
dotnet tool install --global Cordis.NET.Tool --prerelease
```

The published tool validates and previews entry configuration with `cordis validate <path>` and `cordis preview <path>`.

Current source additionally runs an ordinary CLR profile and calls the formal management endpoint. It requires the matching .NET and ASP.NET Core runtimes. For source builds, prefix the following arguments with `dotnet run --project tools/Cordis.Cli --` instead of `cordis`:

```console
cordis run ./profile --source ./feed --url http://127.0.0.1:5080 --authorization-env CORDIS_MANAGEMENT --allow-build Example.ManagedPlugin@1.0.0-alpha.1 --settings root:example.managedplugin=limit,credential
cordis inspect http://127.0.0.1:5080/cordis Example.ManagedPlugin 1.0.0-alpha.1 --source ./feed --authorization-env CORDIS_MANAGEMENT
cordis install http://127.0.0.1:5080/cordis Example.ManagedPlugin 1.0.0-alpha.1 --source ./feed --approve-build --authorization-env CORDIS_MANAGEMENT
cordis settings http://127.0.0.1:5080/cordis root:example.managedplugin --authorization-env CORDIS_MANAGEMENT
cordis remove http://127.0.0.1:5080/cordis Example.ManagedPlugin --authorization-env CORDIS_MANAGEMENT
```

The named environment variable supplies the host's chosen authorization header; the CLI supplies no account system. Both the caller's build acknowledgement and the host's exact package allowance are required. Repeated `--source` options configure explicit feeds. Inspection does not run package code; approved preparation may run package/dependency build targets.

Online commands use the running host. A second CLI host cannot own the same profile. `wait <endpoint> <request-id>` observes only an active installation; exit code 3 means unknown, not successful. A lost install response triggers a query, never an automatic retry. Static Native AOT applications use the libraries with static modules and republish code changes; this dynamic CLI path does not load new managed code into AOT.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse, or Microsoft.

Full configuration and Settings are separate permissions. `configuration <endpoint> <entry>` reads raw configuration; `schema <endpoint> configuration|settings <entry>` exports declarations. `edit <endpoint> configuration|settings <entry> <revision> <operations.json>` submits one ordered JSON array, for example `[{"op":"set","path":["label"],"value":"worker"}]` or `[{"op":"unset","path":["label"]}]`. The revision must come from the original read; the CLI does not silently refresh it before writing. Full configuration can restart an activation; Settings permits only the host-selected live fields.

`sources` and `versions <endpoint> <name> --source <feed>` expose the host's selected feeds. `compatibility <endpoint>` reads exact DSH grants and corruption diagnostics. `grant <endpoint> <package@version> <runtime-version> <true|false> <accept-risk:true|false>` uses the existing DSH policy, and refuses generic profiles. A compatibility grant never approves build execution. Add `--authorization-env` to these online commands as required by the host.
