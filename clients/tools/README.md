# Cordis.NET browser author tools

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/clients/tools/README.zh.md)

Build a browser plugin into the factory and manifest consumed by Cordis.NET's existing module Loader. You need Node 22 or newer and npm; an independent author project needs neither a Cordis source checkout nor the .NET SDK.

## Create an author project

Install the published tool archive from the matching GitHub release:

```console
mkdir example-panel
cd example-panel
npm init -y
npm install --save-dev https://github.com/gentt1024/Cordis.NET/releases/download/v0.2.1-alpha.1/cordis-net-client-tools-0.2.1-alpha.1.tgz
```

Merge the following fields into `package.json`, retaining the `devDependencies` written by npm:

```json
{"name":"example-panel","dsh":{"client":{"platform":"web","external":[]}}}
```

Create `client.js`:

```js
export function apply(ctx) {
  ctx.effect(() => {
    console.log('panel started')
    return () => console.log('panel stopped')
  })
}
```

## Build and watch the plugin

```console
npx --no-install cordis-client-build . dist --entry client.js
```

The output contains `client.js` and a manifest with `exports["./client"]`. Load that directory through the Host's ClientModuleCatalog and browser ClientModules workflow. The plugin logs `panel started` on activation and `panel stopped` when its effect is released. Building does not activate the plugin.

The default entry is `client.ts` when `--entry` is absent; JavaScript, TypeScript and TSX entries can be selected explicitly. Add `--watch` to publish complete immutable directories under `dist/generations`. Success writes a JSON `client-built` frame with `name`, `revision` and `directory`; failure writes `build-error` and preserves the previous complete generation. Restart watch after changing package identity or external requests.

## Declare external modules

List complete import requests in `dsh.client.external`, such as `example-provider/client`. Declaring a package does not also externalize its subpaths. The Cordis runtime requests are implicit; ordinary dependencies are bundled. Declare service-provider package dependencies in `inject` where the Loader requires them. The tool does not install provider modules or supply Host admission policy.

## Include styles and images

`.module.css` exports hashed class names and injects a plugin-owned style at factory execution. Ordinary `.css` injects a style; `.css?inline` exports compiled text for an author-owned effect. Styles carry `data-plugin` and `data-plugin-css`, and the Loader removes owned styles after plugin cleanup. CSS changes participate in watch builds. Pinned Lightning CSS retains the fixed relative URL/import transform; this does not add an asset server or dynamic chunk protocol.

SVG, PNG and WebP imports export base64 data URLs with the exact file bytes. Image changes participate in watch builds. These bytes stay inside the factory artifact and need no separate manifest setting.

## Keep output separate from inputs

Choose an output directory such as `dist`. The command refuses the author directory, its ancestors, and directories containing the entry or a bundled JavaScript, TypeScript, CSS or image input. Existing directory aliases are resolved before comparison; new output paths resolve through their nearest existing parent. Every completed build, including watch, checks its inputs before moving output. A conflict preserves input bytes and emits no `client-built` frame. This prevents accidental replacement, not hostile concurrent filesystem changes.

## Connect the Host and browser

The Host owns process startup, frame consumption, catalog publication and shutdown. The browser runtime owns module activation and effect cleanup. Follow [browser integration](https://github.com/gentt1024/Cordis.NET/blob/main/clients/modules/README.md) and [ManagedApplication](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md) for that setup.

Distribute this archive with its dependency declarations. Native esbuild and Lightning CSS binaries install for the author's OS; do not copy a Windows `node_modules` directory to another platform. The [matching release](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1) provides hashes and platform evidence. The tools are distributed as a TGZ, not installed from an npm registry release.
