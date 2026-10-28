# Cordis.NET browser client modules

[English](https://github.com/gentt1024/Cordis.NET/blob/main/clients/modules/README.md) | 中文

使用固定 DSH 模块注册表与 Loader 加载浏览器插件 factory、协调 Entry 并管理 Cordis effect。页面提供渲染器和认证。浏览器及普通 C# 插件运行不需要 Node，源码构建和作者工具需要。

## 获取浏览器运行时

安装已发布归档，供包 import 与声明使用：

```console
npm install https://github.com/gentt1024/Cordis.NET/releases/download/v0.2.1-alpha.1/cordis-net-client-modules-0.2.1-alpha.1.tgz
```

直接在浏览器加载时，将完整构建运行时目录放在 `/client-runtime` 等页面同源路径下，保留模块、声明与 `LICENSES.txt`。[ManagedApplication](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md)展示原生 Host 制品映射。运行时与作者工具归档职责不同，均位于[对应发行](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1)。

## 为 Loader 构建插件

按[作者工具教程](https://github.com/gentt1024/Cordis.NET/blob/main/clients/tools/README.zh.md)创建插件，生成 `client.js` 与 manifest。classic 注册脚本接收注册表的 `require`，其导出 `apply` 由 Loader 执行。DOM、监听与其他清理使用 `ctx.effect` 注册。此构建入口不支持包内动态分块；独立 `ClientArtifact` 消费者保留自身格式。

作者从 `@cordis-net/client-modules` 导入 `Context` 和 `Service`，共享页面的 Cordis 实例。external 请求按完整名称匹配，包括子路径；服务提供者依赖使用 `inject`。watch 时 Host 通过会话队列只消费成功 `client-built` 帧，捕获新 catalog 并发送模块变更通知，Host 管理 watcher 启动与退出。

## 将页面连接到 Host

Host 须在 `/remote` 提供生成式管理与通知贡献，在 `/cordis` 提供管理状态；运行时放在 `/client-runtime`。以下模块片段属于已有页面，页面提供 `renderConnection` 与 `renderProgress`，它不是完整页面外壳：

```js
import { bootClientModules, createManagementClient } from '/client-runtime/client.mjs'
import { createEvents } from '/client-runtime/events.mjs'
import { TypertConnection } from '/client-runtime/cordis-remote.mjs'

const modules = await bootClientModules({ graph: { rev: 'initial', entries: [], batches: [] } })
const connection = new TypertConnection(location.origin + '/remote')
const events = createEvents(modules.context, connection)
const management = createManagementClient({ baseUrl: '/cordis', modules, events })
management.connection.subscribe(() => renderConnection(management.connection.getSnapshot()))
management.subscribe(event => renderProgress(event))
```

`createManagementClient` 等待通知流就绪后读取完整状态与 graph，重连时也是如此。配置／模块通知使这些读取失效，安装状态／日志通知保留原始参数。短暂断线保留页面插件和草稿，通知不提供重放或序列缺口协议。

插件／包操作使用生成式 `/management` 模块。安装发送检查所得 `contentHash` 作为 `inspectedHash`，并带调用方自有 `requestId`。响应丢失后查询同 generation 的活动 `waitForInstall`，null 表示未知。fetch carrier 提供认证与 `If-Cordis-Generation`。原始配置修改要求已连接 generation，不会重放。`ManagementHttpError` 表示已知 HTTP 拒绝，`ManagementOutcomeUnknown` 表示修改结果未知。

实时表单使用生成式 Settings 模块，采用数值 revision、可省略 expected revision 和秘密存在 sidecar。完整配置保留独立哈希约束。见 [Settings](https://github.com/gentt1024/Cordis.NET/blob/main/docs/settings.zh.md)和[管理合同](https://github.com/gentt1024/Cordis.NET/blob/main/docs/management-remote-migration.zh.md)。

## 提供模块与 UI 贡献

`bootClientModules({ graph, staticModules, loadBundle })` 可使用外壳持有的模块对象。`staticModules` 按完整请求映射，包括显式声明的子路径；对象在插件撤销与重新加载间保持身份。SDK 模块默认提供，显式键优先。Cordis import 应使用 SDK 运行时实例。Host 通过 `ClientModuleCatalog.CaptureAsync(..., platformModules: names)` 声明匹配名称，catalog 记录名称，不序列化对象；未声明供应方会被拒绝。

可选 `loadBundle(url)` 必须在 resolve 前执行 classic-script 注册；默认使用上游 script-element 传输。可执行 URL 须同源并带有所声明 revision。见[外壳持有的模块](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.zh.md#外壳持有的客户端模块)。

`SlotCore` 提供通用贡献注册表，不附带 React 运行时；声明包含有许可记录的 React／CSS 类型依赖。通过 `@cordis-net/client-modules/slots` 导入并扩展 `SlotMap`。应用负责渲染与会话绑定，插件通过 `ctx.effect` 管理贡献 disposer。factory 所属样式在失败或撤销时释放，不接管外壳或嵌套 factory 样式。支持 SVG／PNG／WebP data URL 和 inline-slot 提示，提示不安装渲染器。

## 处理发行变化、恢复与退出

catalog 包含 `rev`、`entries`、`batches` 和可选 `release: { version, commit? }`。应用捕获 catalog 时提供 release 身份，仅改变该身份不改变制品 revision。`modules.releaseChange` 报告 `{ page, previous, host }`；`dismissRelease()` 只隐藏提示，不准入新 graph，外壳决定是否提供重载。解析或应用 graph 前，其版本和可选 commit 必须匹配启动 release。

初次启动拒绝 import 失败、激活错误和缺少服务的 Pending Entry，并释放 facade。后续同步报告失败并支持 retry。替换下载失败保留旧激活 Fiber；显式同步较早目标会加载该目标，retry 使用最新期望目标。这不自动回滚应用状态。成功替换提供者后会刷新导入它的消费者。

恢复管理握手时限、backoff、离线暂停和 `management.reconnect()`。失败握手释放自身尝试，读取响应取消，迟到回调不能发布就绪，恢复不重放写入。显式 `modules.disconnect()` 使待激活工作失效并撤销清单。页面退出时先关闭管理恢复，再在借用的模块 Context 释放前清理事件客户端和共享 Connection，同时释放 modules facade。

## 从源码构建

在仓库根目录使用 Node 和 `global.json` 固定的 SDK，构建会调用 .NET ClientAssets 生成器：

```sh
npm ci --prefix reference --ignore-scripts
npm ci --prefix clients/modules --ignore-scripts
node scripts/build-client-modules.mjs artifacts/client-modules
node scripts/verify-client-module-types.mjs artifacts/client-modules
```

源码与许可身份记录于 `vendor/provenance.json`；本地适配保留原始及本地摘要与原因。浏览器传输检查启动 `node scripts/client-modules-browser-host.mjs` 后，打开其打印地址的 `/shared`。注入传输检查为 `node scripts/verify-client-shared-modules.mjs`；Node 结果不代表浏览器结果，两者单独都不证明完整原生 Host 组合。
