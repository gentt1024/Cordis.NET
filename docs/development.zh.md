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
| 原生 Remote 错误、unary 取消与下行流：Gateway 和生成的 Client 方法 | `RemoteError`、`TypertRemoteResult`、`TypertGateway.StreamAsync`、`MapCordisRemote` | 包 JIT 检查所有者错误码/details、协作取消、流迭代、在调用 Context 中清理及服务撤销。独立 TypeScript HTTP 检查与流读取竞争修复已在 `16440e3e0adaac65abf510038495dc7115f1cc6e` 完成 Windows/Linux 复验 | Unary JSON 与下行 NDJSON 是明确 carrier 适配。固定 Host unary 在已调用方法失败且 signal 已 abort 时转换为 `gateway/cancelled`；流读取另行竞争 abort 并等待清理。强制终止永不结束的 body/cleanup 不在该保证内 | 已实现且有证据 |
| 原生 unary 二进制投影：[protocol codec encode/decode](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/protocol/src/types.ts#L271)由[Gateway 结果编码](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L1004)、[Connection attachment framing](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/connection/src/rpc-host.ts#L300)与[Client 结果解码](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/client/index.ts#L449)消费 | 显式 JSON 元数据把 `byte[]` 编码为 base64 | 包 JIT 往返 bytes 结果，生成的 Client 声明 JSON carrier 值 | Base64 JSON 是原生投影，不提供原生 binary attachment、零拷贝二进制所有权或任意 Typert wire 兼容 | 明确平台适配 |
| 原生类型化 Client 生成与 Fiber 挂载：generator Remote 输出、Gateway `$mount()`、`packages/api/remotes/src/client/index.ts` | `TypertArtifacts.GenerateClient`、生成的 `createRemote` 和 `mountRemote`、真实固定 Cordis Client Context | 两个独立多 Entry 客户端通过 HTTP 及 TypeScript 正反编译。`16440e3e0adaac65abf510038495dc7115f1cc6e` 的 Windows/Linux 包消费者通过实际 HTTP、生成 TypeScript、固定 Cordis 挂载、撤销、替换、共享 namespace 及忽略 abort 的 carrier，包含 namespace 队列修复 | Client effect 持有已挂载 namespace 方法、撤销和保留的回调。完整上游 Client Context/stream/event 合同仍未完成 | 已实现且有证据 |
| 完整 transport、图 codec 与事件闭包：[Client 流开启](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/client/index.ts#L220)消费[Connection Peer/carrier 合同](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/connection/src/rpc.ts#L263)，[Gateway 流 framing](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/stream-protocol.ts)、[owned-value 协议](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/protocol/src/owned-value.ts)及[API Remotes Host 事件注册](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/remotes/src/index.ts#L42)供给 Gateway | 这些路径没有完成的原生对应 | Unary 与下行证据不覆盖这些路径 | Client-to-Host 流 uplink、双向 Peer/request 服务、事件转发、丰富 Context/owned-value 图 codec、binary attachment 与完整 Typert wire 兼容仍需实现和消费者 | 未实现 |
| 既有原生管理消费者：PluginManager、ConfigEditor、Settings controller、客户端管理及包构建 | `PluginConfigurationOperations`、`ProfileSession`、Settings 导出、原生 HTTP/SSE endpoint、`DotnetPluginToolchain` | 原路径保留[验证记录](validation.zh.md)的历史证据。新增多 Entry 作者交付也使用实际工具链 | 原生 HTTP 管理仍是明确适配，保留既有状态和生命周期所有者 | 明确平台适配 |
| 管理消费者迁入生成合同：Settings controller/`ui-settings`、PluginManager/`ui-plugin-manager` 及 API Remotes Client assembly | 原生管理所有者已存在，生产合同仍使用先前 carrier | 本轮没有迁入的生产管理消费者 | 固定 Settings 消费者调用生成的 `describe`/`mutate`，PluginManager 消费者调用生成的管理动作与转发通知。原生生成式集成仍是通用缺口 | 未实现 |
| 共享 bundle HMR 与原生合同检查：[HMR watcher](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/boot/hmr/src/index.ts#L267)调用 `partialReload`，[替换/恢复](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/boot/hmr/src/index.ts#L406)导入并归一化 Loader 导出，用捕获的配置重注册受影响 Fiber，并等待 Loader 稳定 | 成组 `ClrModuleResolver.ReplaceAsync`、`Loader.ReplacePluginsAsync`、既有 `HmrManager`、live binding/provider 校验 | 独立多 Entry 包消费者覆盖不完整替换拒绝、坏候选、Pending 等待、双 Entry 回滚、保留兄弟入口、最后一次退役及 Remote 恢复。Provider 不匹配与 definition 撤销另有原生证据 | 共享 bundle 替换是既有 HMR 之上的 CLR 适配。这些检查不证明通用同契约替换准入算法 | 已实现且有证据 |
| 应用能力选择与替换准入：API Remotes 及领域持有的 provider | 宿主组合与应用政策 | 按固定生产所有权定责，没有读取 Maker | Agent/LLM、RSI、FSM Project 接受、权限、basis、receipt、CAS、outbox、Viewer 保护与业务退役仍属产品 | 真正产品专用 |

固定生产链已明确：[generator](https://github.com/deepseek-ai/deepseek-harness/tree/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator)生成制品，[Typert Loader](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/loader/src/index.ts)供给[registry](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/registry/src/service.ts)，[Gateway](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts)解析 live 服务。[Remote Client assembly](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/remotes/src/client/index.ts)选择生成的 contribution。[Settings mirror](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/ui-settings/src/client/settings-mirror.ts)和[form](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/ui-settings/src/client/config-form.ts)调用[Settings controller](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/settings-controller/src/index.ts)。[PluginManager Client store](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/ui-plugin-manager/src/client/manager-store.ts)调用[PluginManager](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/boot/plugin-manager/src/index.ts)。这些生产依赖说明原生管理适配为何不能关闭生成合同范围。

生成与发布是不同阶段。原生 Roslyn analyzer 在作者 C# 编译期运行，把类型化 binding 与 descriptor 生成到作者程序集。之后显式调用 `TypertArtifacts.GenerateClient(contribution)` 才产生 `.mjs` 与 `.d.mts` 内容，写入和交付由作者或宿主持有。固定 tsdown plugin 则在构建期生成并校验选择加入的 `./typert`、`./client/typert` 和 `./remote` 发布。独立包消费者证明了原生消费，没有关闭完整上游构建/发布链。实际步骤见[原生作者指南](authoring.zh.md#声明与生成原生-remote-合同)。

Roslyn 还通过 `TypertCodec.CreateNullable` 提供根引用可空注解，因为运行时 `JsonTypeInfo` 擦除了这些源码注解。原生 codec 添加 null 分支并重定位本地 Schema 引用，保留非 null 递归子节点；Client 生成据此投影可空边界类型。完整嵌套泛型可空性/类型图分析仍未完成。修复后的投影已在 `16440e3e0adaac65abf510038495dc7115f1cc6e` 通过 Windows/Linux 独立包 JIT/AOT 与严格 TypeScript 检查。

固定 Host unary 生产路径从[Connection RPC handler](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/connection/src/rpc-host.ts#L261)经过[dispatchRpc](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L424)、[invokeRpc](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L677)与[prepareInvocation](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L700)，进入[invokePrepared](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L361)。事先已 abort 的 signal 不阻止业务调用，成功结果也不因之后 abort 被拒绝。已调用方法抛错且 invocation signal 已 abort 时转换为 `gateway/cancelled`。Definition/provider 撤销没有额外的立即 Host-abort 机制，原生代际检查是明确的有效性适配。

固定[流迭代器](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L1186)在每次读取前检查 abort，使 `next()` 与 abort 竞争，并在 `finally` 等待 invocation close 和 iterator return。原生流读取修复保留取消及清理顺序，在 `DisposeAsync` 前等待未完成的 `MoveNextAsync`，因为原生迭代器操作不能安全重叠。迟到的读取失败会被观察，清理完成后才 yield failure。永不结束的 body/cleanup 在两种运行时中都可能使释放持续等待。取消/读取的串行清理已在 `16440e3e0adaac65abf510038495dc7115f1cc6e` 通过 Windows/Linux 包 JIT/AOT 检查。

独立验证命令为 `python scripts/verify-clr-multi-entry.py --packages <local-feed> --output <private-evidence>` 与 `python scripts/verify-typert.py --packages <local-feed> --output <private-evidence> --aot`。其源码在项目引用树之外编译实际作者 NuGet 包与仅包引用消费者。多 Entry、生成合同、unary 调用、下行流与 base64 结果已有有界证据，其余行仍未关闭。错误模块/合同、重复注册、错误参数、provider 撤销、旧回调和不兼容 binding 需要分别对应失败，不能用测试总数代替。

最终复验覆盖源码检查点 `16440e3e0adaac65abf510038495dc7115f1cc6e` 的模块导出与原生 Remote 子集，包括 Client namespace 队列、根可空引用投影及流读取修复。Windows x64 与 WSL2 下 Ubuntu 24.04 x64 均通过 `verify.py --aot --package` 和 `verify-authoring.py --verification ... --aot --packages ...`，使用 SDK 10.0.111、Node 24.12.0。Windows 为 766 通过、0 失败/跳过；Linux 为 763 通过、0 失败、3 项预期的 Windows 专用 Platform 跳过。四份报告均记录 `sourceUnchanged=true`，467 个文件的源码哈希清单键和值完全一致。

两平台独立 Remote NuGet 作者/消费者均通过七步：作者打包、指定编译拒绝、仅包引用 JIT、Client 运行时构建、实际 HTTP 与严格 TypeScript 和固定 Cordis 挂载、静态 Native AOT 发布及 native 执行。独立 CLR 包通过十个阶段，覆盖标准工具链多 Entry 交付、共享程序集/ALC 身份、分别配置、组级失败恢复、带 DTO 变更的成功替换、生成 HTTP 客户端、旧调用拒绝及撤销。八个 Cordis.NET 包与 Greeting 示例的检查/符号/离线消费者及既有 Client 源码路径通过；验证脚本自测在两平台各通过 38 项。详细结果与限制见[验收记录](validation.zh.md#2026-10-09-模块导出与原生-remote-续建)。

后续改动仅添加包括本范围续账在内的文档验收记录；报告验证上述检查点，不证明之后的运行时变更。未关闭行继续保持未关闭，本次验收不宣称整个 Typert 子系统完成。Windows/Linux、静态 JIT/AOT、动态 CLR 和 TypeScript 证据分开记录；不宣称 Native AOT 内动态加载 CLR。未执行 Hosted CI、远端 SourceLink 获取或 Maker 运行。未执行 NuGet 发布、GitHub Release、版本升级或部署。

### 既有验收之后的质量续作

后续有界质量审查发现，共享 CLR bundle 只使用第一主程序集的依赖根，客户端 Schema 投影则丢弃了 tuple 的 `prefixItems`。两者是既有公开能力中的实现缺陷。修复增加按入口登记的依赖根与确定性的私有 binary 冲突拒绝，并保留位置 tuple 类型，对不支持的长度明确拒绝。独立包 fixture 保留修复前对照，检查两个 CLR 入口顺序、实际 native 依赖及类型化 tuple 消费。

审查还发现公开 XML 缺少生命周期限制，提供者代际检查的证据也不完整。Codec 文档现区分延迟输入 Schema 校验与结果序列化；Gateway 文档说明撤销、协作取消、串行流清理与 root 关闭边界。独立的仅包引用用例保留活跃 definition，分别替换 unary Service、等待中的 lookup、Context 和 downlink 提供者。最新[验证记录](validation.zh.md)将已完成检查绑定到精确源码及包批次。这些修正承接原范围；上述通用未完成职责仍未实现，不改写历史验收。

## 2026-10-10 .NET First Typert 架构审查

本审查承接原范围续账。目标是忠实对应固定 DSH 的设计与可观察语义，优先服务 .NET 作者和消费者。完整 Typert 仍属于通用基础设施范围；短期交付限制不使其缺失职责成为产品专用或永久排除项。以下建议不是新的实现验收。

### 源码检查点与集成边界

本次审查的 Typert 候选仍为 `2d048e3c101c12abf4a757b9eeacebbf9dcd3538`。已获取的远端 main 为 `a018f34681d834f485217343db6a39dc609ee8ec`，包含安全退休/恢复及已合入的稳定 CLR 制品工作流。较早 CLR 候选 `3036bcb1500148296fa5218c9f057903eea94d60` 已不再是集成依据。DSH 仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`。

不修改 index 的合并模拟报告 `ClrModuleResolver.cs`、`Loader.cs` 与 `docs/public-api.txt` 存在冲突，未合并分支。Resolver/toolchain 集成现可基于已接受的 main SHA 推进，须保留默认稳定目录、显式开发影子副本、部署文件保留、receipt 及安装包升级要求重启的行为。开发实现 HMR 仍是独立路径。旧候选的 Windows/Linux、包、AOT 和 TypeScript 结果不能证明集成源码。

### 架构判断与作者体验

固定[官方作者 Skill](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/preset/agent-preset/skills/cordis-plugin-development/SKILL.md)及其 [host-plugin 指南](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/preset/agent-preset/skills/cordis-plugin-development/references/host-plugin.md)暴露普通 Plugin、Service 与 Config 职责。Cordis.NET 应保留这些普通 C# 路径。同进程消费者直接解析当前 Service。作者在跨环境时选择 Remote；类型提取、制品发布和注册逐步由既有工具链承担。

建议方向：作者 C# 声明经过 Roslyn 提取，形成可序列化、编译器无关的模型；模型分别投影为原生 binding、类型化 .NET 合同/客户端、JSON 合同及按需 TypeScript 制品。运行贡献仍由既有 Loader/Registry 机制持有。这是在现有 Typert 中补齐职责，不替换 Core、Composition、HMR 或管理 API。

固定[生成器模型](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator/src/model.ts)区分 `FaceModel`/`TypeGraph`、调用元数据及作者类型与 codec 类型，包含非 Remote Service、Event 与引用对象；其[发射器](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator/src/emitter.ts)消费该模型。原生提取须保留对应职责，明确 C#/CLR 适配，不能承诺任意 C# 或整个 TypeScript 编译器等价。

| 信息来源 | 合适的事实责任 | 限制 |
|---|---|---|
| Roslyn Symbol 与语义分析 | 作者声明、选定公共成员、类型关系、可空性、Remote 标记、身份及源码诊断 | 编译器对象仅作提取输入，不进入运行模型状态 |
| 编译器无关模型 | 稳定声明关系及分开的序列化/调用投影 | 不存 Service 实例、CLR `Type`、delegate、`JsonTypeInfo`、Expression Tree 或通用活对象图 |
| STJ 元数据、Schema 与显式 codec 策略 | 实际 JSON 名称、converter、必填性及支持的 wire 形状 | 不能恢复全部作者类型、方法 scope 或借用/拥有 Context 的语义 |
| 运行 descriptor 与 binding | 活跃贡献的调用准入及原生分派 | 不能成为完整源码模型或持久合同缓存 |
| 显式 Reflection adapter | 为选定程序集提供可选 JIT 提取/校验 | 不作作者必经路径、全局程序集扫描或 Native AOT 回退 |

Schema/OpenAPI 优先的组织方式会简化部分 DTO/HTTP 生成，但会丢失声明关系与生命周期含义，最终仍须另建模型补回。Reflection 优先则缺少源码事实，并增加运行加载/AOT 约束。两者均不适合作为完整 Typert 的唯一事实源。本决策无需引入 Quantum 或新的对象图框架。

### 现有候选：保留与调整

| 现有代码 | 判断 | 剩余工作 |
|---|---|---|
| 直接 Service 解析；Fiber 拥有的原子贡献；分开的 lookup/Context 声明与 provider；definition history | 保留为运行基础，使用已有有界证据 | 将每个新消费者的可观察顺序与失败/恢复同固定生产调用者对照 |
| Roslyn 类型化调用 delegate、STJ 元数据与输入校验、carrier 无关 Gateway、HTTP/NDJSON、现有 TS 输出 | 保留 | 增加类型化 .NET 消费，不能强制 TS 或引入另一套 RPC 协议 |
| `RemoteGenerator` 直接发射 descriptor；`TypertArtifacts.Contribution` 回填方法签名而类型/event/object 集合为空 | 保留为有界兼容投影，不能扩为全类型事实源 | 插入独立提取模型，再逐步让现有发射器消费该模型 |
| Schema 到 TS 边界投影与根可空性修复 | 保留已经证明的 wire 行为 | 在投影之前保留作者名称/关系；嵌套泛型可空性及完整源类型图仍未完成 |
| `TypertLoader` 将 contribution task 缓存至整个 activation 结束 | 保留已记录的当前行为，重新核对 CLR 代际集成 | 退休共享 owner 同时撤销无关包；仅重启整个 owner 不能证明 A/B 隔离替换。须明确新代路由且不保留旧 codec/factory |

编译模型缓存可只包含不可变数据，以精确构建输入与生成器版本为键。运行 codec、delegate 和元数据则具有 owner/bundle 代际寿命。Registry 的持久 history 不能保留退休 binding。调用方生成 DTO 或显式共享且有版本的合同程序集，优于类型化客户端引用 collectible provider 实现。Managed 卸载仍是协作行为；保留的调用、DTO 或元数据可以继续持有 ALC。

一项分派差异须单独判定：固定 Host 在并发解析参数 lookup 之前检查当前 Service/binding；候选先串行解析 lookup，再检查当前 Service。Provider 副作用与撤销可能使该顺序可观察。零参数 Settings 切片不能验证这项差异。宣称 lookup 对等之前，须建立对照场景并修复顺序，或记录有依据的平台适配。

当前显式 `JsonSerializerContext` 继续是受支持的作者路径。普通源生成器不能消费彼此的普通输出。Roslyn 新文档的 pre-compilation API 不能读取 Compilation/Syntax 输入，不能据此声称固定 SDK 可从发现的 C# 方法自动生成 STJ 元数据。应比较显式 context、构建前步骤与分阶段合同编译，再决定自动化。Attribute 形态、公开模型/客户端 API、合同共享、目标框架与构建组织需要在实现前另行进行 API 讨论。

### 成熟库复用

继续使用 Roslyn 提取/生成，STJ 提供静态 JSON 元数据与支持的 Schema 投影，ASP.NET Core/HttpClient 提供传输机制。DSH 特有的身份、所有权、Context/lookup、撤销、错误与流生命周期仍由 Cordis 负责。[库调研附录](typert-dotnet-library-research.zh.md)记录官方依据、AOT 限制、依赖、许可及待核查项。

NJsonSchema 是构建期 DTO 生成候选，需实际 Schema dialect 与生成 serializer 验证。NSwag 适用于明确选择的 OpenAPI 投影，不能成为新的 Remote 权威。gRPC 与 StreamJsonRpc 可作为未来 carrier，但需要大量语义转换；目前引入不能补上模型或 .NET 客户端职责，反而增加工作。本审查未选定或安装任何新库。

### 有界生产消费者与实施优先级

选择固定 DSH [Settings controller](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/settings-controller/src/index.ts)的 `settings/describe` 及真实 [Settings mirror](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/ui-settings/src/client/settings-mirror.ts)。该零参数 direct unary 没有 lookup、Context receiver 或取消参数；它读取当前 optional provider，并显式投影/脱敏返回值。Mirror 在读取失败时保留旧视图，保留期间发生的 invalidation，并允许重试。这些观察构成消费者对照依据，无需新造 Echo 示例。

原生 `ReadSettingsAsync` 与 `ReadSettingsSchemasAsync` 可复用真实 ProfileSession/配置所有权，但其形状和 revision 不同，且分别运行在不同事务。拼接两个读取不能证明处于同一 DSH `DescribeValue` 检查点。公开 Settings 投影与 revision 适配需先确定，再暴露生成 controller。固定 Host 撤销不强制中断已开始的 unary，也不对成功结果执行代际检查；固定 Client 挂载在撤销后拒绝晚到结果。现有原生成功值代际检查是明确适配。客户端 HttpClient 取消不能改变该 Host 合同。

1. 基于已接受 main 集成，同时保留稳定 CLR 工作流及已证明的多 Entry 身份/依赖隔离。验证两个入口顺序、配置、移除、失败替换/恢复及无关 owner 不受影响。不增加 `IModulePlugin.Exports`，不将导出混为 `Provide`。
2. 扩大 descriptor 之前，先提取有界独立模型。包含选定非 Remote 声明、递归/外部类型引用、作者类型与 wire 类型及可空性。分析可以成功而 codec 投影拒绝，须分别记录。完整类型图未支持职责继续保持未完成。
3. 证明一条仅包引用 .NET 链：C# 作者声明 → 模型 → 发布合同/客户端制品 → 注册 → 实际 carrier 上类型化调用 → provider/definition 撤销与重试。消费者使用生成方法，不能手写 endpoint 字符串/JSON，不能引用 provider 实现。JIT 与静态 AOT 门禁不依赖 Node/TypeScript；覆盖脱敏、额外字段/参数、provider 缺失/故障、并发 invalidation 及保留回调的晚到结果。
4. 从同一模型独立验证按需 TS/Web 输出。Scope/lookup、流、binary 和取消各用固定生产场景闭环；`describe` 成功不能关闭这些行。在精确集成源码上执行适用的 Windows/Linux/包/平台门禁。

该顺序承接既有任务表，不另建总规划。完整模型/元数据发布、丰富 Context/owned-value 图、Peer/uplink/events、binary attachment 及 PluginManager/Settings/客户端管理生成合同消费仍是原使命缺口。产品替换接受与 FSM 权限/basis/receipt 继续属于产品。WPF、Avalonia、Godot .NET 与 Blazor 是目标消费者，但目标框架、analyzer host、浏览器及 export 兼容性仍需具名证据。

本审查仅修改文档，未合并、修改运行/API、安装依赖或产生新的平台验收。原范围只追加记录，既有验收保持不变。文档检查不能证明建议中的无 Node .NET 消费链或合并后的 CLR/Typert 行为。


## 实施续建，2026-10-10

固定 main 整合与局部生命周期修正保留为独立检查点。多入口消费者现在验证路由撤销后的稳定制品保留，不再要求与 main 冲突的删除；无关 B 的断言也能在 V2 调用成功后拒绝实际旧的全 owner 重建。历史验收记录不改写。

后续实现新增 SDK 提取的独立原生模型、随包 compiler/build target、源事实 conformance 桥和仅消费模型的类型化 .NET 调用者。生产 Settings describe 在一个既有 profile 检查点读取全部选择的 namespace；普通 provider 仍可直接使用，无须 RPC。调用者生成源码在 CoreCompile 前进入编译，STJ 能看到完整 DTO/context；独立消费者关闭 reflection fallback。[新门禁](../scripts/verify-typert-dotnet.py) 并入 `verify.py --package`，TS/Web 仍分开验证。

确切冻结包与平台证据通过后，只闭环具名的原生源模型/Settings 范围，不闭环完整类型图、引用模型组合、全部 generator 后端或全部 Settings 操作。PluginManager/客户端管理迁移和作用域/lookup、流、取消、二进制及更丰富协议范围仍各自需要证据。WPF、Avalonia、Godot .NET 和 Blazor 保留为预期消费者，框架/host 限制需另行验证。本次不引入竞争总规划，不代表发布或新增依赖选型获验收。


### 2026-10-10 有界执行完成记录

运行时/包检查点 `89a63edb0ba51ea63d5fb6ffa96e14134178286a` 已完成 Windows/Linux 全部门禁及独立构建的无 Git 源码压缩包验收。见[验证记录](validation.zh.md#2026-10-10-clr-整合与原生-settings-消费链验证)及[绑定证据回执](../verification/typert-dotnet-2026-10-10/evidence.json)。固定 main 整合、精确生命周期修正和原生模型/客户端实现仍为独立检查点。原私有范围追加状态续账，保留历史验收及失败运行。

实际流水线为：C# 源声明与有效编译输入 → 独立可序列化模型 → 仅模型合同包 → CoreCompile 前 DTO/client/context → STJ 元数据 → 真实 Settings HTTP 调用。普通 `ISettingsDescribeProvider` 仍直接作为 Service 消费。既有运行时绑定和 TS emitter 经源/制品 conformance 桥保留，尚未全部迁移到独立模型。JSON Schema 和 RPC descriptor 是投影，不是 authored model 的事实来源。

已证明的模型切片保留作者成员/构造器关系、C#/JSON required、空状态注解、支持的常量与可选默认值、XML 作者说明及编译选项。分析事实与调用方投影支持分开；不支持的可选调用、readonly 数据、record struct 和规范化方法名称冲突明确拒绝，避免静默丢失语义。完整图组合、全部后端及原范围剩余基础设施项仍开放；不新建总规划或选定第三方依赖。


### 已确认审查缺陷修复，2026-10-10

原范围仅追加 `a9b53d2fce5e1e611599098d87b46fa6300c9618` 的 R1/R2/R3 修复状态。Gateway 内部身份复用已有注册 entry，Core 不新增公共 API、registry 或 RPC 依赖。可空数值事实继续来自作者模型，仅修正字面量发射。DTO/辅助类型共用名称预检，不另建命名系统。Windows 全门禁还暴露配置事件属性读取失败；事件匹配按固定 HMR 的词法路径投递，原生别名失败沿用现有诊断并可恢复。

见[已完成的修复验证](validation.zh.md#typert-修复验证2026-10-10)。真实 Settings 消费、独立 TS/Web、CLR 身份及原错误/寿命边界继续分别验证。早先有界验收和失败不改写。类型图、后端、协议、Settings、lookup 时序及生产消费者开放项仍开放；本次不构成完整 Typert 或新增依赖验收。


### Caller view 修复与保留的平台缺口，2026-10-10

`e277a7eafcdad5e7047cf73729afd7f51558819d` 的 F1 修正既有 Gateway 对普通 `Service<TState>` 视图的 provider 检查。注册身份、原始 provider 值和 caller view 分别承担职责。作者可以返回新视图，无需自行缓存；Gateway 仍使用原 caller 调用该视图。原始快照同时保留 R1 在重登记或 Set 重入后的失效语义。本轮修正有界 Remote 基础，不新增模型、模块导出机制或完整 Typert 验收。

原范围追加续账。[验证记录](validation.zh.md#caller-view-修复验证2026-10-10) 与[回执](../verification/typert-caller-view-2026-10-10/evidence.json) 保留旧包实际失败、新 Linux 全量及 Windows 无 Git 包消费证据，以及两次 Windows 全量失败。未改动的定向用例通过不能关闭这些失败。Windows 全量验收仍开放，需要解释刷新等待并通过必需门禁；该平台事项不改变既有通用能力缺口的范围或归属。本轮未扩大类型系统调研、选入新依赖、放宽超时或重写无关运行时。
