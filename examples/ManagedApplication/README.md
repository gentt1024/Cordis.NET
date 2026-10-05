# Managed application

This loopback development example composes the native profile session, package toolchain, ASP.NET management adapter and reusable browser module consumer. Its local policy authorizes loopback requests; applications supply their own identity and approval policy.

This example's feed argument is a local directory. Prepare its complete dependency set from the repository root in PowerShell:

```powershell
New-Item -ItemType Directory -Path artifacts/managed-feed -Force | Out-Null
dotnet restore examples/ManagedPlugin/ManagedPlugin.csproj
foreach ($managedExampleProject in @('Core','Composition','Clr')) {
  dotnet pack "src/Cordis.$managedExampleProject/Cordis.$managedExampleProject.csproj" -c Release -o artifacts/managed-feed -p:IsPackable=true
}
dotnet pack examples/ManagedPlugin/ManagedPlugin.csproj -c Release -o artifacts/managed-feed -p:IsPackable=true
$managedExampleCache = (dotnet nuget locals global-packages --list) -replace '^[^:]+:\s*', ''
$managedExampleLock = Get-Content examples/ManagedPlugin/packages.lock.json -Raw | ConvertFrom-Json
$managedExampleYaml = $managedExampleLock.dependencies.'net10.0'.YamlDotNet.resolved
Copy-Item -LiteralPath (Join-Path $managedExampleCache "yamldotnet/$managedExampleYaml/yamldotnet.$managedExampleYaml.nupkg") -Destination artifacts/managed-feed
```

Build the optional browser runtime and independent client packages:

```sh
npm ci --prefix clients/modules
node scripts/build-client-modules.mjs artifacts/client-runtime clients/modules/examples/provider artifacts/client-provider
node scripts/build-client-modules.mjs artifacts/client-runtime clients/modules/examples/panel artifacts/client-panel
dotnet run --project examples/ManagedApplication -- artifacts/managed-profile artifacts/managed-feed artifacts/client-runtime artifacts/client-provider artifacts/client-panel
```

The selected local feed must include the plugin's transitive dependencies, including its locked YamlDotNet package. The package library and CLI can separately configure an explicit NuGet v3 source; this example command accepts a complete local feed. The Host does not broaden its source mapping to hide an incomplete feed. A preparation failure remains a failed installation; after correcting the feed, repeat normal inspection and installation with a new request id.

Open `http://127.0.0.1:5187`. The first launch initializes the supplied client packages as selected profile bundles. Existing profile selection remains authoritative. Client-only bundles use `dsh.bundle.patch:[]`; they do not add a second selection store. Disabling a provider withdraws its dependent client contributions. Re-enabling restores the complete selected graph. Artifacts are captured bytes behind revisioned `/cordis/client/artifacts` URLs.

The supplied managed plugin package is `Example.ManagedPlugin` version `1.0.0-alpha.1`, matching its prerelease Cordis dependencies. Use that exact version in Inspect and install.

The page has one management connection owner. Reconnect closes its predecessor and subscriptions before constructing another connection. Connection loss withdraws module contributions; recovery reads full state and graph. Choose Settings for the filtered settings surface or Configuration for the independently authorized raw configuration and its schemas. Both use the last read revision and the same ordered edit operation contract. Compatibility grants have a separate read and explicit risk-accepting mutation. Installation uses an inspected archive hash and a unique request id. A lost installation response is queried through its active wait operation; an unknown result is not displayed as successful.

For an explicitly requested frontend development worker, append:

```sh
--watch=clients/modules/examples/provider --client-build-script=scripts/build-client-modules.mjs
```

`--node=` optionally names the approved Node executable. Node is only needed for this author toolchain. Each successful watch build supplies a completed immutable generation directory. The Host validates its package identity and captured SHA, then publishes a full catalog inside the profile session's existing queue and sends `client-modules`. Compiler errors retain the previous catalog. A failed output callback, invalid frame, oversized output or unconfirmed process cleanup is a failure, not completion.

Read development status from the page and use Stop compiler to cancel the selected worker. Stop waits for the existing package process owner's child-process termination and output settlement. Application shutdown and Context disposal close the same worker owner. Changing package identity or external requests requires restarting the author watch. Stopping a compiler retains the last published artifact; profile selection controls contribution withdrawal.

The example keeps prior build directories for inspection. The runtime can be distributed as precompiled bytes without author sources, Node, the local NuGet feed or these development commands. Validate published artifact consumption separately from a source checkout build.
