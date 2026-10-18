# Cordis.NET 浏览器作者工具

[English](README.md)

这个 Node 包把仓外作者项目构建成现有 Cordis.NET 客户端 factory 和 manifest，不需要 Cordis 源码仓库或 .NET SDK。当前为源码候选，已发布 alpha.5.1 浏览器归档中不包含此工具。

将选定工具归档安装为开发依赖，再使用已安装命令：

```console
npm install --save-dev <client-tools-tgz>
npx --no-install cordis-client-build . dist --entry src/client.tsx
```

默认入口为 `client.ts`。`--watch` 在 `dist/generations` 下发布完整、不可变的目录。每次成功构建输出包含 `name`、`revision`、`directory` 的 JSON `client-built` 帧。失败输出 `build-error`，保留此前完整代际。包身份或 external 请求变化需要重启 watcher。宿主继续拥有进程和安装职责。

输出目录应与构建输入分开，例如使用 `dist`。命令拒绝作者目录、其祖先，以及包含入口或已打包 JavaScript、TypeScript、CSS 输入的目录。比较前会解析作者与输出路径中已有的目录别名；尚不存在的输出通过最近的已有父目录解析。每次构建完成后，包括 watch 重建，工具都会在写入或移动输出前检查本次输入。输出冲突不改变输入路径和字节，也不发送 `client-built` 帧。这项检查防止误选目录覆盖输入，不承诺抵御并发文件系统修改的沙箱隔离。

作者包声明同一 Loader 合同：

```json
{
  "name": "example-panel",
  "dsh": { "client": { "platform": "web", "external": ["example-provider"] } }
}
```

输出包含 `client.js` 和带 `exports["./client"]` 的 `package.json`，通过既有 ClientModuleCatalog 与 ClientModules 流程装载。external 按完整 import 请求精确匹配，声明包名不会同时把其子路径外置。既有 Cordis 运行时请求隐式提供，其余依赖打包进入制品。工具不安装 provider 模块，也不替代宿主准入政策。

CSS 沿固定 DSH 动态 factory 构建：`.module.css` 导出哈希类名，并在 factory 执行时注入插件所属样式；普通 `.css` 注入样式；`.css?inline` 导出编译文本，由作者自己的 effect 管理。样式带 `data-plugin` 和 `data-plugin-css`，现有 Loader 在插件清理后移除所属样式。样式文件变化参与 watch 构建。编译使用固定版本 Lightning CSS。相对 CSS URL 和 import 保持固定 transform 行为；本工具不另建资源服务器或动态 chunk 协议。

esbuild 与 Lightning CSS 的原生二进制安装到作者当前操作系统。交付工具归档及依赖声明，不复制 Windows `node_modules` 目录。跨平台验收需要在目标系统消费同一份保存模型和工具归档，不能仅用同平台自产自消代替。
