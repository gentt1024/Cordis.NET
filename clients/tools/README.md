# Cordis.NET browser author tools

[中文](README.zh.md)

This Node package builds an independent author project into the existing Cordis.NET client factory and manifest. It needs no Cordis source checkout or .NET SDK. Version `0.2.0-alpha.5.2` is distributed as a separate installable archive alongside the browser runtime in the [matching release](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.0-alpha.5.2). Check that release for publication and exact artifact hashes. Published alpha.5.1 does not include this tool.

Install the selected tool archive as a development dependency, then use the installed command:

```console
npm install --save-dev <client-tools-tgz>
npx --no-install cordis-client-build . dist --entry src/client.tsx
```

The entry defaults to `client.ts`. `--watch` publishes complete immutable directories beneath `dist/generations`. Each successful build writes a JSON `client-built` frame containing `name`, `revision` and `directory`. A failed build writes `build-error` and keeps the previous complete generation. Changing package identity or external requests requires restarting the watcher. The host retains process and installation ownership.

Choose an output directory separate from the build inputs, such as `dist`. The command refuses the author directory, its ancestors, and directories containing the entry or a bundled JavaScript, TypeScript or CSS input. Author and output paths resolve existing directory aliases before comparison; a new output resolves through its nearest existing parent. The command checks each completed build's inputs before writing or moving output, including watch rebuilds. An output conflict leaves the input paths and bytes unchanged and emits no `client-built` frame. This prevents accidental input replacement; it is not a sandbox against concurrent filesystem changes.

The author package declares the same loader contract:

```json
{
  "name": "example-panel",
  "dsh": { "client": { "platform": "web", "external": ["example-provider"] } }
}
```

The output contains `client.js` and `package.json` with `exports["./client"]`. Load it through the existing ClientModuleCatalog and ClientModules workflow. Declared externals match complete import requests; declaring a package does not also externalize its subpaths. The existing Cordis runtime requests are implicit. Other dependencies are bundled. The tool does not install provider modules or replace host admission policy.

CSS follows the fixed DSH dynamic-factory build: `.module.css` exports hashed class names and injects a plugin-owned style at factory execution; ordinary `.css` injects a style; `.css?inline` exports compiled text for an author-owned effect. Styles carry `data-plugin` and `data-plugin-css`, and the existing Loader removes owned styles after plugin cleanup. Stylesheet changes participate in watch builds. Compilation uses pinned Lightning CSS. Relative CSS URLs and imports retain the fixed transform behavior; this tool does not invent a separate asset server or dynamic chunk protocol.

The native esbuild and Lightning CSS binaries are installed for the author's current operating system. Distribute the tool archive and its dependency declarations, not a copied Windows `node_modules` directory. Cross-platform acceptance must consume the same saved models and tool archive on the target systems; locally produced and consumed artifacts alone do not establish that evidence.
