# Example.ManagedPlugin

An independently packed CLR plugin with live settings and an ordinary label. Its validator owns defaults and accepted values; metadata describes that same contract. `cordis.plugin.json` identifies the public CLR entry point. No npm manifest or Node installation is needed to build the C# package.

Follow the [managed application setup](../ManagedApplication/README.md) to prepare a complete local feed, including the Cordis libraries and locked YamlDotNet dependency. Then build this example separately from the repository root:

```console
dotnet pack examples/ManagedPlugin -c Release -p:IsPackable=true -o artifacts/example-feed
```

Supply both the Cordis package feed and this feed to the CLI host through repeated `--source` options. Inspect and install `Example.ManagedPlugin` through its management endpoint. The entry is `root:example.managedplugin`; changing `limit` keeps its activation, while an ordinary `label` update uses normal Loader replacement.

This example is excluded from the release package whitelist.
