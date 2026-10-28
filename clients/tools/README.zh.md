# Cordis.NET browser author tools

[English](https://github.com/gentt1024/Cordis.NET/blob/main/clients/tools/README.md) | 中文

将浏览器插件编译为 Cordis.NET 既有模块 Loader 消费的 factory 与 manifest。需要 Node 22 或更新版本及 npm；独立作者项目不需要 Cordis 源码检出或 .NET SDK。

## 创建作者项目

从对应 GitHub 发行安装已发布的工具归档：

```console
mkdir example-panel
cd example-panel
npm init -y
npm install --save-dev https://github.com/gentt1024/Cordis.NET/releases/download/v0.2.1-alpha.1/cordis-net-client-tools-0.2.1-alpha.1.tgz
```

将以下字段合入 `package.json`，保留 npm 写入的 `devDependencies`：

```json
{"name":"example-panel","dsh":{"client":{"platform":"web","external":[]}}}
```

创建 `client.js`：

```js
export function apply(ctx) {
  ctx.effect(() => {
    console.log('panel started')
    return () => console.log('panel stopped')
  })
}
```

## 构建与监听插件

```console
npx --no-install cordis-client-build . dist --entry client.js
```

输出包含 `client.js` 和带有 `exports["./client"]` 的 manifest。通过 Host 的 ClientModuleCatalog 与浏览器 ClientModules 流程加载该目录。插件激活时输出 `panel started`，effect 释放时输出 `panel stopped`；构建本身不激活插件。

不传 `--entry` 时入口默认为 `client.ts`，也可显式选择 JavaScript、TypeScript 或 TSX。添加 `--watch` 后，在 `dist/generations` 下发布完整不可变目录。成功输出含 `name`、`revision`、`directory` 的 JSON `client-built` 帧；失败输出 `build-error`，保留上一完整结果。包身份或 external 请求变化后重启 watch。

## 声明外部模块

在 `dsh.client.external` 列出完整 import 请求，例如 `example-provider/client`。声明包名不会同时 externalize 子路径。Cordis 运行时请求隐式提供，普通依赖打包进输出。Loader 要求的服务提供者包依赖在 `inject` 中声明。工具不安装提供者模块，也不提供 Host 准入策略。

## 包含样式与图片

`.module.css` 导出哈希类名，在 factory 执行时注入插件所属样式；普通 `.css` 注入样式，`.css?inline` 导出编译文本，供作者自有 effect 使用。样式带有 `data-plugin` 和 `data-plugin-css`，Loader 在插件清理后移除所属样式。CSS 变化参与 watch 构建。固定 Lightning CSS 保留既定相对 URL／import 转换，不增加资源服务器或动态分块协议。

SVG、PNG 和 WebP import 导出包含精确文件字节的 base64 data URL，图片变化参与 watch 构建。字节保留在 factory 制品中，不需要额外 manifest 设置。

## 将输出与输入分开

输出选择 `dist` 等独立目录。命令拒绝作者目录、祖先目录，以及包含入口或打包的 JavaScript、TypeScript、CSS、图片输入的目录。比较前解析已有目录别名，新输出通过最近的已有父目录解析。每次完成构建，包括 watch，都在移动输出前检查输入；冲突时保留输入字节，不发送 `client-built` 帧。这防止意外覆盖，不抵御恶意并发文件系统修改。

## 接入 Host 与浏览器

Host 管理进程启动、帧消费、catalog 发布与退出；浏览器运行时管理模块激活和 effect 清理。接线见[浏览器集成](https://github.com/gentt1024/Cordis.NET/blob/main/clients/modules/README.zh.md)及 [ManagedApplication](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md)。

交付归档及其依赖声明。esbuild 和 Lightning CSS 原生二进制按作者操作系统安装，不要把 Windows `node_modules` 复制到另一平台。[对应发行](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1)提供摘要与平台证据。本工具通过 TGZ 分发，不从 npm registry 的发行安装。
