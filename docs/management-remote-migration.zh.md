# 标准管理与通知合同

[English](management-remote-migration.md)

当前源码使用唯一的生成式 `pluginManager` 合同及生成式 `remoteNotifications/watch` 流。
`PluginManager` 共用运行 Profile 的 `PluginConfigurationOperations`、工具链和安装任务表。
维护中的 CLI 与 ManagedApplication 消费这些合同。本次源码修改要求重建客户端，不改变已发布包或持久 Profile 格式。

## 公开操作

| 职责 | 生成方法 |
|---|---|
| 清单 | `listPlugins`、`listBundles`、`sources`、`versions` |
| 持久启停 | `setPluginEnabled`、`setBundleEnabled` |
| 包安装 | `inspect`、`installBundle`、`waitForInstall`、`cancelInstall`、`removeBundle` |
| 精确 runtime 豁免 | `listVersionExemptions`、`setVersionExemption` |
| 宿主选择的通知 | `remoteNotifications/watch` |

安装使用精确的 `PackageRequest { name, version, source }` 与
`PluginInstallOptions { requestId, inspectedHash, enabled? }`。
NuGet 来源及 SDK preparation 是固定包工作流的 .NET 适配。构建批准允许执行包代码；精确版本豁免只改变 runtime 兼容准入。

调用者先检查归档，再将返回的 `contentHash` 作为 `inspectedHash` 提交。
`installBundle` 在重新检查、hash 比较及宿主构建授权之前登记 request ID；这些步骤归既有安装所有者持有。
初始资源拒绝返回 `plugin-manager/forbidden`。受理后的 hash 不匹配返回失败 `PackageChange`，
其 `error` 为 `inspection-changed`；构建拒绝的 `error` 为 `build-not-approved`。两者都不进入 SDK preparation。
取消和所有者关闭会等待已受理工作，包括异步构建决策。因此，不结束的宿主决策可能延迟关闭。

`PackageChange` 区分安装、选择、应用、重启和残留文件。
`waitForInstall` 只观察查询时仍活动的任务；返回 null 不能确定结果。
`cancelInstall` 等待排空，其状态可以是 `cancelled`、`failed`、`too-late` 或 `not-running`。
响应丢失不授权重新提交写操作。已安装包的版本更新保存后返回 `restart-required`，运行代际继续使用原代码。

宿主提供资源与构建授权。`CordisManagement.MountPluginManagerAsync` 将 ASP.NET 请求身份及宿主代际约束接到生成服务。
写操作要求当前 `If-Cordis-Generation`，读取也可携带该值。宿主的 endpoint 授权仍控制物理 `/remote` carrier。
必需但可为空的 DTO 字段明确输出 null，使生成的 .NET 客户端验证服务实际发送的同一合同。

`PluginManagerChange.changed` 比较 profile manifest、patch 与兼容豁免文件的字节，
保持固定工作流的持久变化观察。新增精确版本豁免即使没有修改 manifest 或 patch，
也返回 true；重复同一豁免返回 false。运行应用仍由独立结果字段表达。
.NET 工作流没有 pnpm workspace 构建批准文件；每次准备的构建权限由宿主提供。

兼容文件不可读时，变化观察保留“内容未知”。既有兼容权威报告警告，拒绝授权和
不安全覆写。此适配保留已记录的 .NET 启动与数据保护政策，并返回失败的授权结果；
manifest 和 patch 仍要求成功读取其来源。

## 生成客户端用法

构建工具 `tools/Cordis.ClientAssets` 从实际运行贡献导出
`management.mjs`、`notifications.mjs`、`settings.mjs` 及其声明。
浏览器 SDK 通过 `/management`、`/notifications`、`/settings` 包导出供应这些制品，不保留手写 PluginManager DTO 或方法解码器。

```js
import { createRemote, TypertConnection } from '@cordis-net/client-modules/management'
const connection = new TypertConnection(new URL('/remote', location.href), authorizedFetch)
const manager = createRemote(connection)
const inspected = await manager['pluginManager/inspect']({ request })
if (!inspected.ok) throw inspected.error
const installed = await manager['pluginManager/installBundle']({
	request,
	options: { requestId, inspectedHash: inspected.value.contentHash },
})
```

`authorizedFetch` 提供当前宿主代际与应用请求身份。
生成 .NET 与 TypeScript API 都返回 Remote 结果。C# 的 `InspectAsync`、`InstallBundleAsync` 与
`WaitForInstallAsync` 返回 `Task<RemoteResult<T>>`，先判断 `Ok`，再读取 `Value` 或 `Error`。
装配失败仍抛异常；流句柄产出通知值，流失败通过异常呈现。等待客户端及 Connection 的异步释放，
客户端仅借用 Connection。浏览器 WebSocket 使用应用的准入策略，必要时通过 Connection 的 socket factory 配置；
fetch 请求头不会自动授权浏览器 socket。
Composition 在 `cordis/typert/Cordis.Composition.cordis.model.json` 供应选定服务模型；
既有 `emit-dotnet` 命令及 `CordisTypertClientModel` 构建属性消费该模型，不加载 provider。

## 通知所有权

宿主通过 `TypertRemoteEvents.FromCordis` 选择 emit 名称及参数 codec，再以
`TypertGateway.RegisterRemoteEvents` 注册唯一源。Gateway 运行一个 source pump，向各订阅者广播。
生成 watch 流先发送 `{ kind: "ready" }`，随后发送 `{ kind: "event", event, args }`。
先订阅，再读取当前 state 和 graph。

断线只移除一个观察者。撤销源立即关闭所有观察者，再取消并等待源结束。
通知不提供重放、持久交付或恰好一次保证。重连重新订阅并读取现有权威。
浏览器恢复客户端复用既有 connection controller，暂时断线时保留插件和草稿。
`createManagementClient` 在 `options.notifications` 接受生成通知客户端，可选 `transport.fetch`；
`transport.openEvents` 已退出。

迭代读取和释放异常传到所有者。C# 可能通过 `MoveNextAsync` 抛出 async iterator 的 finally 异常，
因此注册也将意外读取失败保留为清理失败。即使发生读取错误的源实际释放了资源，这一规则也可能阻止替换；
它避免将未确认清理报告为成功。取消是协作式的，不结束的源可能使所有者清理一直等待。

## 退出路径与剩余范围

手写 HTTP 插件、bundle、来源、检查、安装、豁免及 SSE 路由已退出。
浏览器管理 facade 中对应的方法和解码器一并退出，不保留 legacy 路由或版本化 Remote。
原生宿主状态、完整配置及客户端制品路由继续承担独立职责和授权。

Settings 保持唯一的[数值 revision 合同](settings-contract-migration.zh.md)。
Maker 适配属于其自己的会话：重新生成客户端、绑定当前宿主授权、订阅后读取快照、保留安装结果未知状态。
本仓库不修改 Maker，也不为其旧生成代码增加永久兼容路径。

共享 Connection 使用固定 unary 封套、unary multipart 附件及文本 WebSocket mux。
每方法 NDJSON 与生成器内嵌 carrier 已退出。宿主选择的 emit 通知尚未闭环 typed uplink、反向 Context 解析、
waterfall 或 owned-value 图。
原[范围账本](development.zh.md)与[验证记录](validation.zh.md)继续记录这些职责及精确平台证据。
