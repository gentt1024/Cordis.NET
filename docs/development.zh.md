# 开发与验证

[English](development.md)

## 快速循环

源码构建的包版本统一定义在 [Directory.Build.props](../Directory.Build.props)。项目继承该值，包验证脚本也读取它来构建隔离消费者。需要精确的本地包版本时，查询同一个值：

```console
dotnet msbuild src/Cordis.Core/Cordis.Core.csproj -getProperty:Version
```

普通提交不需要提升包版本。源码版本号不代表 NuGet 上已存在对应实现；已发布包请按[安装指南](getting-started.zh.md)使用，作者 API 的版本要求与源码示例见[作者指南](authoring.zh.md)。

第三方依赖版本统一声明在 [Directory.Packages.props](../Directory.Packages.props)，各项目保留自己的 `PackageReference` 及其元数据。每个项目的 `packages.lock.json` 记录解析后的依赖图，验证继续使用 `--locked-mode`。未开启 transitive pinning，SDK 提供的隐式包仍由所选 SDK 控制。

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

## 完整门禁

```console
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --aot --package --package-output artifacts/upgrade-packages
python scripts/verify-authoring.py --aot --packages artifacts/upgrade-packages
```

完整门禁检查锁定的 SDK 与依赖、分析器、测试、上游源码执行、差分场景、Native AOT、API 形状、包和隔离消费者。发布候选应在 Windows x64 与真实 Linux x64 上运行。最近完成的证据见[验证记录](validation.zh.md)，证据含义见[兼容性文档](compatibility.zh.md)。

生成输出和原始日志可能包含本地路径或用户名。应将其保存在公开树之外；只提交脱敏摘要与稳定的机器可读证据。

## 外壳持有的客户端模块

可选[浏览器模块包](../clients/modules/README.md)通过公开 bootstrap 提供固定 DSH 的静态模块表与可选 transport 接入。外壳持有共享模块对象，插件副作用仍由 Cordis 持有。

```js
import { bootClientModules } from '/client-runtime/client.mjs'
const shell = { name: 'application shell' }
const tools = { format: value => String(value) }
const modules = await bootClientModules({
  graph: await fetch('/client/graph').then(response => response.json()),
  staticModules: { '@my-app/shell': shell, '@my-app/shell/tools': tools },
})
```

原生 Host 使用新增 `ClientModuleCatalog.CaptureAsync` 重载，以 `platformModules: ["@my-app/shell", "@my-app/shell/tools"]` 传入相同名称。宿主声明和浏览器供应必须一致。插件作者在 `dsh.client.external` 列出精确导入；根请求不会自动供应子路径。作者构建将未声明 external 的普通依赖打包，不会隐式将其子路径 external 化。类型与模块值由外壳供应，目录和 bootstrap 不发现 npm 包。

`staticModules` 扩展 SDK 默认模块，显式键优先。值不经克隆，插件撤销/重载后保持身份。提供 Cordis 相关服务时应使用 SDK 的运行时实例。可选 `loadBundle(url)` 必须先执行 factory 注册再完成 Promise；默认仍使用固定上游的 script 元素 transport。两种 transport 收到的都是经校验的同源、带 revision 的 URL。释放清理插件副作用与页面 facade，不清理外壳对象。

既有验证流程运行 `verify-client-shared-modules.mjs`，由独立插件消费构建后的公开包，使用注入的 Node transport。既有浏览器验证载体的 `/shared` 页面另行验证真实默认/自定义 script transport 和缺失供应者。这些结果不替代原生目录测试、上游断言或托管验证。


浏览器首次启动调用固定 web 激活审计；导入失败、激活失败和等待缺失服务的 Pending 条目会拒绝启动并清理。暂时断线保留插件和草稿。公开客户端接受 `transport.fetch`、`transport.openEvents`、`recovery` 及手动 `reconnect()`。固定连接控制器在原生 HTTP/SSE carrier 上负责握手截止、重试/退避和离线/上线处理。关闭所有者会拒绝新写入、取消读取并等待撤销。恢复不重放结果未知的写操作。

## 应用调用

```console
cordis run ./profile --resume abc --source app-owned-value
cordis run ./profile --source ./feed --url http://127.0.0.1:5080 --authorization-env CORDIS_TOKEN -- --help
```

启动器只解析 `--` 或首个不认识的 token 之前的自身选项；其余参数原样复制到 `cmdlineArgs` 的 `CommandLineArguments`。解析、帮助和错误属于应用。第一条命令的 `--source` 属于应用，第二条命令在分隔符之前选择包源。不传 `--url` 时 Generic Host 不打开 HTTP 监听。空来源白名单可以运行已安装包；获取新包仍须显式选择来源。

挂载条目前，CLI 还提供 `appReady` 的 `IApplicationReady` 与 `appExit` 的 `ApplicationExit`。就绪订阅应注册为 Context effect。插件树和宿主启动成功后提交就绪，晚订阅者立即回调。应用可在激活中（例如帮助）或就绪后请求退出。首个请求启动固定上游的五秒宽限，包括启动期间的请求；CLI 等待插件树有序释放，超过宽限则以该码强制退出进程。普通启动没有五秒时限。启动/就绪异常返回失败；Cordis 报告 effect 清理错误，不改写请求的退出码。既有 HMR 使用同一就绪结果。进程信号仍由 .NET Host 处理。

库宿主可通过 `ProfileSession.StartAsync(prepare: ...)` 提供这些 Composition 合同并决定自己的退出策略，不必使用 CLI 或另一套运行时。验证流程会编译独立 CLR 应用并执行真实 CLI，覆盖参数边界、帮助/错误、就绪与清理。受控 carrier、浏览器执行和原生管理测试分别记录。

## 初始应用基础设施实施片（历史验收）

沿用已完成的应用范围矩阵，参照 `upstream.lock.json` 中固定的 DSH 提交，不重新开展全仓审查。Core 保持领域无关；管理、作者能力和平台接入复用其上既有所有者。

| 实施片 | 状态 | 验收 |
|---|---|---|
| A：完整 typed 字段组合 | 已实现；Windows/Linux JIT/AOT 与独立包消费验证通过 | 实际手写/组合消费者、透明包装、live/普通更新、完整保存/默认往返、换代、collectible 正反例、JIT/AOT 与隔离包消费 |
| B：一个字段编辑动作 | 已实现；独立审查修正与 Windows/Linux 消费者验证通过 | 既有 operations/session 所有权、校验、revision、失败后的保存值/生效值 |
| C：一个设置视图 | 子集已实现；Windows/Linux 源码及隔离包的 JIT/AOT 消费通过 | 明确 live-only 合同、脱敏和 revision、实际 TypeScript 消费；不将 Core 描述图声称为 Schemastery 或 JSON Schema |
| D：一个客户端制品交付动作 | 子集已实现；Windows/Linux 不可变交付、路径及换代验证通过 | 明确清单/入口合同、宿主交付、实际 Node ESM 执行和代际失效 |

A 的有界规则是完整、显式的 plain-data 投影，保留 validator 权威，不推断默认值，不建全局回调/类型缓存。既有语义使用固定源码对照和原生回归验证；新增作者辅助通过实际消费者验证。作者门禁让未修改的断言对付移除 live 绑定、忽略普通 effective 变化、漏保存字段的 helper mutation。破坏版必须编译成功，并因指定运行时原因失败。不得为通过放宽断言或修改正式行为锁。

每片执行实现、引用规则/源码/反例的独立只读审查、修复和复验。规则修正独立审查后在批次间应用。断言退役值时保存转换前的值，原生 Fiber 本身会复用。提交、命令结果、未执行项和源码哈希沿用既有验证记录及私有证据输出，不代表新增发布授权。此工作方式借鉴[迁移文章](https://claude.com/blog/ai-code-migration)及[工具包 README/操作规则](https://github.com/anthropics/code-migration-kit-with-claude-code)，不导入其任务队列、工具禁令或逐阶段审批。

B 保留源所有权、raw 不透明性与既有 Session 队列。独立审查修正了 flow 行删除、用户 insert 继承、祖先 live 准入、重启结果和调用者可变 map。写入前检查 YAML 往返，拒绝不能持久化的完整候选。C 的读取/提交均受宿主政策约束，草稿 revision 保留原始宿主哈希，拒绝不支持的 reset/多字段请求。D 通过部署包路由捕获不可变 bytes，同时拒绝 lexical 和链接路径逃逸。这些规则在最终批次前校准，没有改变正式行为锁。既有作者门禁包含编译成功的生产/客户端破坏版和不变的端到端控制；正确源码及隔离包均运行实际原生宿主与 TypeScript 客户端。

最终批次发现 Node 的 data URL 错误输出被截断。消费者现输出原异常消息并保留非零退出；独立审查确认断言及验证器的指定拒绝条件未变，两平台作者门禁复跑通过。源码 ZIP 构建还发现独立的符号打包限制：缺少 Git 元数据时，SDK 没有生成 SourceLink 记录，严格检查器拒绝该包。Git checkout 的完整符号/包检查通过，无 Git 导出路径仍未关闭。以当前验证记录为准，创建归档不等于验收。

上述无 Git 导出限制随后已通过绑定真实源码提交修复。针对该构建修正的独立无 Git `verify.py --aot --package` 已通过，包含严格符号与独立消费。这项结果不验证后续应用基础设施改动，也不代表远端 SourceLink 已可取得。

当前源码在初始片之上扩展了有序配置编辑/reset、嵌套 Settings 与独立声明导出、NuGet/SDK 包操作、HTTP/SSE 管理、CLI 操作和浏览器模块交付。上文初始限制描述先前验收批次。新增路径分别进行独立审查与组合验证，不能把存在实现当作沿用旧批次证据的理由。

## 2026-10-09 应用基础设施范围续账

本轮承接原应用基础设施任务。工作树起点为干净的 `5a23dcf9816b7d97253741e59f06bdf734c411ed`，DSH 基线仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`。2026-10-03 原范围已把 Host/Client 请求、取消、二进制结果和连接恢复列为通用缺口，协议仍待决。原报告也明确 Typert、WebSocket Gateway 和浏览器依赖图未完整复审。其 2026-10-05 续账和上文验收记录仍只证明各自列明的历史路径。

原矩阵没有逐项列出 generator、protocol、registry 与 Loader 制品集成。这些是原范围盘点及依赖闭包缺少的细项，不是新的项目使命。[PR #5](https://github.com/gentt1024/Cordis.NET/pull/5) 明确将原生 HTTP/SSE carrier 与 Typert wire 兼容分开。其[范围补账评论](https://github.com/gentt1024/Cordis.NET/pull/5#issuecomment-6076018318)将 CLR 多 Entry 归为既有基础机制、作者交付待验证，将生成式 Remote 链归为未实现或未证明。本轮不改写两份历史验收。

有界核对沿实际模块导出、生成合同、运行时所有者、Gateway 及生产消费者展开。下表状态仅使用“已实现且有证据”“已有实现待验证”“未实现”“明确平台适配”和“真正产品专用”，每项只对应该行描述的子集。本轮新增的原生 Remote 链没有完成全部 Typert 职责。

| 职责及固定 DSH 入口/消费者 | 现有 .NET 公开能力 | 实际消费证据 | 适配与缺口归属 | 状态 |
|---|---|---|---|---|
| 模块 specifier、导出归一化与包子路径：[Loader Entry import](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/vendor/loader/src/config/entry.ts#L224)为组合入口调用[Loader.unwrapExports](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/vendor/loader/src/index.ts#L201) | 既有 `DeploymentModuleResolver.Register`、`ClrModuleResolver.Register`、`IClrPluginModule`；工具链 `exports` 元数据与共享 bundle 租约 | 独立 NuGet 作者及仅包引用消费者验证双 Entry、alias 身份、共享程序集/ALC、各自配置、次级子路径交付及最终包移除。两个生成的 TS 客户端实际 HTTP 调用并拒绝错误 DTO 类型 | CLR EntryType 与显式 NuGet 子路径元数据适配模块导出。单入口元数据继续有效，不要求空导出表。模块导出不等于 Cordis `Provide` | 已实现且有证据 |
| 原生生成式 Remote 子集：[Host tsdown 配置](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/tsdown.config.ts#L33)调用[typertPlugin 与 WorkspaceTypertGenerator](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator/src/tsdown-plugin.ts#L96)，Loader 与 Client Remote assembly 消费其输出 | SDK Roslyn `RemoteGenerator`、`[RemoteService]`、`[RemoteMethod]`、源生成 `JsonSerializerContext`、类型化调用委托与 `TypertArtifacts` | 独立作者包编译 descriptor 和 JSON 元数据；指定不支持的作者以 `CORDISREMOTE001` 拒绝。仅包引用 JIT 消费者调用生成的方法 | 原生生成接受 `net10.0` 的公开、顶层、非泛型 partial class 与显式 JSON context。`TypertCodec.CreateNullable` 保留 JSON 元数据擦除的根引用可空注解。该有界 Roslyn/JSON 适配不提供完整嵌套泛型类型图 | 明确平台适配 |
| 完整源类型图与反射：generator analyzer/model 供给[生产目录脚本的 projectCordisCatalog](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/scripts/gen-cordis-catalog.ts#L1161)、生成的子系统引用与运行时反射 | 原生方法/包模型覆盖已生成的 Remote 子集 | 没有完整原生类型图消费者 | 公开继承、泛型、声明链接、完整 event/object 反射、源码文档及全部上游目录投影仍属通用范围。存在 record 类型不能证明模型闭包 | 未实现 |
| 原生 Schema、descriptor、codec 与发布子集：`packages/typert/protocol/src/types.ts`、generator emitter；生成的 `./typert` 与 `./remote` 消费者 | `TypertContribution`、`TypertInvocationDescriptor`、`TypertCodec`、`TypertArtifacts.Contribution` / `GenerateClient` | 包 JIT 调用使用生成的 descriptor 与严格 JSON 元数据，错误参数被拒绝。多 Entry CLR module 从实际 bundle 提供 descriptor 和 Schema | JSON Schema/类型元数据适配可执行 Zod factory。不支持的 Schema keyword 拒绝；完整图 codec 与 owned-value 合同仍未完成 | 已实现且有证据 |
| 原子、Fiber 持有的原生 registry/provider 子集：`packages/typert/registry/src/service.ts`；Loader 与 Gateway | `TypertRegistry.Register`、`RegisterRemotes`、lookup/Context 注册及配置、schema/package 查询 | 定向原生测试覆盖原子重复拒绝、实际 Fiber 所有权、lookup wire 稳定性、override 与撤销。包消费者证明 provider 移除及 definition tombstone | 既有 Cordis effect 持有 contribution。身份/schema/invocation/endpoint 冲突在提交前拒绝；provider 声明在 resolver 撤销后保留 | 已实现且有证据 |
| 原生制品发现与撤销：`packages/typert/loader/src/index.ts`；live Loader Entry 与显式贡献包 | `TypertLoader.StartAsync`、`ITypertArtifactResolver`、`StaticTypertArtifactResolver`、CLR `IClrTypertModule` | 包消费者从实际 Entry 发现制品，最后一个 Entry 移除后撤销。原生测试覆盖共享 Entry 与 unmount 后 import 完成 | 静态 factory 支持 AOT，CLR 导出在 bundle ALC 中解析。精确原生 module request 可以贡献制品，固定 Node Loader 则发现包根导出。该差异明确记录 | 已实现且有证据 |
| 原生直接调用、对象 lookup 与字符串身份 Context receiver：`packages/api/gateway/src/index.ts`；选定的生成式 Remote contribution | `TypertGateway.InvokeAsync`、生成的 `ITypertRemoteService` binding、lookup 与 Host Context provider | 独立包 JIT 调用覆盖直接调用、对象 lookup、选定 receiver、对象缺失、provider 撤销、严格 wire 字段与 definition 不可用 | 每次路由解析真实 live Cordis 服务。descriptor 提供合同，不形成第二套服务目录。原生代际检查拒绝旧 definition/provider/service 的结果；固定 Host 不会因撤销立即 abort 调用。丰富的 Client consuming-Context 投影与 owned Context 值仍未完成 | 已实现且有证据 |
| 原生 Remote 错误、unary 取消与下行流：Gateway 和生成的 Client 方法 | `RemoteError`、`TypertRemoteResult`、`TypertGateway.StreamAsync`、`MapCordisRemote` | 包 JIT 检查所有者错误码/details、协作取消、流迭代、在调用 Context 中清理及服务撤销。独立 Windows TypeScript HTTP 检查也已通过。最新流读取竞争修复仍待最终包/平台复验 | Unary JSON 与下行 NDJSON 是明确 carrier 适配。固定 Host unary 在已调用方法失败且 signal 已 abort 时转换为 `gateway/cancelled`；流读取另行竞争 abort 并等待清理。强制终止永不结束的 body/cleanup 不在该保证内 | 已实现且有证据 |
| 原生 unary 二进制投影：[protocol codec encode/decode](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/protocol/src/types.ts#L271)由[Gateway 结果编码](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L1004)、[Connection attachment framing](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/connection/src/rpc-host.ts#L300)与[Client 结果解码](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/client/index.ts#L449)消费 | 显式 JSON 元数据把 `byte[]` 编码为 base64 | 包 JIT 往返 bytes 结果，生成的 Client 声明 JSON carrier 值 | Base64 JSON 是原生投影，不提供原生 binary attachment、零拷贝二进制所有权或任意 Typert wire 兼容 | 明确平台适配 |
| 原生类型化 Client 生成与 Fiber 挂载：generator Remote 输出、Gateway `$mount()`、`packages/api/remotes/src/client/index.ts` | `TypertArtifacts.GenerateClient`、生成的 `createRemote` 和 `mountRemote`、真实固定 Cordis Client Context | 两个独立多 Entry 客户端通过 HTTP 及 TypeScript 正反编译。更完整的 Windows 包消费者通过实际 HTTP、生成 TypeScript、固定 Cordis 挂载、撤销、替换、共享 namespace 及忽略 abort 的 carrier。后续 namespace 队列修复后再做最终源码复验 | Client effect 持有已挂载 namespace 方法、撤销和保留的回调。完整上游 Client Context/stream/event 合同仍未完成 | 已实现且有证据 |
| 完整 transport、图 codec 与事件闭包：[Client 流开启](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/client/index.ts#L220)消费[Connection Peer/carrier 合同](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/connection/src/rpc.ts#L263)，[Gateway 流 framing](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/stream-protocol.ts)、[owned-value 协议](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/protocol/src/owned-value.ts)及[API Remotes Host 事件注册](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/remotes/src/index.ts#L42)供给 Gateway | 这些路径没有完成的原生对应 | Unary 与下行证据不覆盖这些路径 | Client-to-Host 流 uplink、双向 Peer/request 服务、事件转发、丰富 Context/owned-value 图 codec、binary attachment 与完整 Typert wire 兼容仍需实现和消费者 | 未实现 |
| 既有原生管理消费者：PluginManager、ConfigEditor、Settings controller、客户端管理及包构建 | `PluginConfigurationOperations`、`ProfileSession`、Settings 导出、原生 HTTP/SSE endpoint、`DotnetPluginToolchain` | 原路径保留[验证记录](validation.zh.md)的历史证据。新增多 Entry 作者交付也使用实际工具链 | 原生 HTTP 管理仍是明确适配，保留既有状态和生命周期所有者 | 明确平台适配 |
| 管理消费者迁入生成合同：Settings controller/`ui-settings`、PluginManager/`ui-plugin-manager` 及 API Remotes Client assembly | 原生管理所有者已存在，生产合同仍使用先前 carrier | 本轮没有迁入的生产管理消费者 | 固定 Settings 消费者调用生成的 `describe`/`mutate`，PluginManager 消费者调用生成的管理动作与转发通知。原生生成式集成仍是通用缺口 | 未实现 |
| 共享 bundle HMR 与原生合同检查：[HMR watcher](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/boot/hmr/src/index.ts#L267)调用 `partialReload`，[替换/恢复](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/boot/hmr/src/index.ts#L406)导入并归一化 Loader 导出，用捕获的配置重注册受影响 Fiber，并等待 Loader 稳定 | 成组 `ClrModuleResolver.ReplaceAsync`、`Loader.ReplacePluginsAsync`、既有 `HmrManager`、live binding/provider 校验 | 独立多 Entry 包消费者覆盖不完整替换拒绝、坏候选、Pending 等待、双 Entry 回滚、保留兄弟入口、最后一次退役及 Remote 恢复。Provider 不匹配与 definition 撤销另有原生证据 | 共享 bundle 替换是既有 HMR 之上的 CLR 适配。这些检查不证明通用同契约替换准入算法 | 已实现且有证据 |
| 应用能力选择与替换准入：API Remotes 及领域持有的 provider | 宿主组合与应用政策 | 按固定生产所有权定责，没有读取 Maker | Agent/LLM、RSI、FSM Project 接受、权限、basis、receipt、CAS、outbox、Viewer 保护与业务退役仍属产品 | 真正产品专用 |

固定生产链已明确：[generator](https://github.com/deepseek-ai/deepseek-harness/tree/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator)生成制品，[Typert Loader](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/loader/src/index.ts)供给[registry](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/registry/src/service.ts)，[Gateway](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts)解析 live 服务。[Remote Client assembly](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/remotes/src/client/index.ts)选择生成的 contribution。[Settings mirror](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/ui-settings/src/client/settings-mirror.ts)和[form](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/ui-settings/src/client/config-form.ts)调用[Settings controller](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/settings-controller/src/index.ts)。[PluginManager Client store](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/ui-plugin-manager/src/client/manager-store.ts)调用[PluginManager](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/boot/plugin-manager/src/index.ts)。这些生产依赖说明原生管理适配为何不能关闭生成合同范围。

生成与发布是不同阶段。原生 Roslyn analyzer 在作者 C# 编译期运行，把类型化 binding 与 descriptor 生成到作者程序集。之后显式调用 `TypertArtifacts.GenerateClient(contribution)` 才产生 `.mjs` 与 `.d.mts` 内容，写入和交付由作者或宿主持有。固定 tsdown plugin 则在构建期生成并校验选择加入的 `./typert`、`./client/typert` 和 `./remote` 发布。独立包消费者证明了原生消费，没有关闭完整上游构建/发布链。实际步骤见[原生作者指南](authoring.zh.md#声明与生成原生-remote-合同)。

Roslyn 还通过 `TypertCodec.CreateNullable` 提供根引用可空注解，因为运行时 `JsonTypeInfo` 擦除了这些源码注解。原生 codec 添加 null 分支并重定位本地 Schema 引用，保留非 null 递归子节点；Client 生成据此投影可空边界类型。完整嵌套泛型可空性/类型图分析仍未完成。该源码适配及最新修复仍需最终包和平台复验。

固定 Host unary 生产路径从[Connection RPC handler](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/connection/src/rpc-host.ts#L261)经过[dispatchRpc](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L424)、[invokeRpc](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L677)与[prepareInvocation](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L700)，进入[invokePrepared](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L361)。事先已 abort 的 signal 不阻止业务调用，成功结果也不因之后 abort 被拒绝。已调用方法抛错且 invocation signal 已 abort 时转换为 `gateway/cancelled`。Definition/provider 撤销没有额外的立即 Host-abort 机制，原生代际检查是明确的有效性适配。

固定[流迭代器](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L1186)在每次读取前检查 abort，使 `next()` 与 abort 竞争，并在 `finally` 等待 invocation close 和 iterator return。原生流读取修复保留取消及清理顺序，在 `DisposeAsync` 前等待未完成的 `MoveNextAsync`，因为原生迭代器操作不能安全重叠。迟到的读取失败会被观察，清理完成后才 yield failure。永不结束的 body/cleanup 在两种运行时中都可能使释放持续等待。该修复仍待最终包/平台复验。

独立验证命令为 `python scripts/verify-clr-multi-entry.py --packages <local-feed> --output <private-evidence>` 与 `python scripts/verify-typert.py --packages <local-feed> --output <private-evidence> --aot`。其源码在项目引用树之外编译实际作者 NuGet 包与仅包引用消费者。多 Entry、生成合同、unary 调用、下行流与 base64 结果已有有界证据，其余行仍未关闭。错误模块/合同、重复注册、错误参数、provider 撤销、旧回调和不兼容 binding 需要分别对应失败，不能用测试总数代替。

独立 Windows Remote 包执行通过了所请求的作者拒绝、JIT、实际 HTTP/TypeScript/Client 所有权检查、Native AOT 发布与 native 执行。该批次早于后续 Client namespace 安装/清理队列修复。此次更新时，最终源码/包门禁与 Linux 执行仍待完成，先前成功不验证后续源码 bytes。Windows/Linux、静态 JIT/AOT、动态 CLR 和 TypeScript 证据分开记录；不要求 Native AOT 内动态加载 CLR。本任务不包含包发布、Release、版本升级或部署。
