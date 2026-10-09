# 兼容性与证据

[English](compatibility.md)

Cordis.NET 以[上游文档](upstream.zh.md)所述、DeepSeek Harness 锁定的 vendored Cordis 行为为目标。兼容性按可观察契约评估，不能只看包名或测试数量。

## 上游忠实性要求

上游忠实性是项目兼容性要求。评估改动时，应以 `upstream.lock.json` 固定的 DSH 修订、其真实 production caller 及可观察行为为依据。核对时序、所有权、生命周期、失败和恢复边界，包括调用方如何使用底层机制。任何差异都必须明确归类为必要的平台适配或显式项目决策，并提供针对性证据。仅证明 wrapper 内部自洽的绿测，不能单独作为上游忠实性的证明。

## 状态词汇

- **已实现：** .NET 行为已经存在。
- **已执行：** 已运行具名 .NET 测试、上游源码测试或配对场景。
- **断言已审阅：** 已逐项匹配上游 setup、helper 与断言，或记录明确适配。
- **不支持：** 行为位于产品或平台边界之外。

不可变的历史清单针对 DSH `ddefc45fbc7f8e46dd73185e68295696d1297887`，包含 631 个上游候选实例，不代表新基线的断言闭合状态。本次升级的范围、行为映射与平台适配见[升级矩阵](upgrade-0.2.0-rc.2.zh.md)。目前 323 项记录为实现完成，其中 36 项完成了断言审阅，287 项尚未完成全部断言审阅；其余 308 项明确为未完成，通常涉及 Cordis.NET 范围之外的 JavaScript 或 DeepSeek Harness 产品行为。这些数字不代表兼容率。

## 部署边界

可选浏览器模块 bootstrap 将外壳持有的 `staticModules` 和可选 `loadBundle` 传给固定 DSH 模块系统。原生目录捕获接受相同的额外精确请求名，SDK 默认模块继续可用。包根不会自动供应任意子路径。外壳持有对象与类型，Cordis 持有插件副作用；默认与自定义 transport 都保留既有同源 revision 边界。这是恢复上游 bootstrap 接线，不新增模块注册机制或 npm 解析器，见[开发说明](development.zh.md#外壳持有的客户端模块)。

浏览器启动复用固定 web 激活审计；后续 sync 保留诊断/重试语义。HTTP/SSE 恢复复用固定连接控制器，以数值时序适配替代 Schemastery 运行时。暂时断线保留插件，显式 close 撤销插件。CLI 参数、就绪和有界退出复用 Composition、ProfileSession 与 Generic Host。这些修正恢复上游装配职责，不改变 Core Pending 语义。

| 路径 | 支持的行为 | 边界 |
|---|---|---|
| 静态 Core 与 Composition | JIT 与 Native AOT；静态模块注册、生命周期、配置、profile 与部分扩展 | 新插件代码需要重新发布应用 |
| CLR 适配器 | 托管程序集的运行时加载、替换、回滚与协作式卸载 | 仅普通运行时；保留的 CLR 引用可能阻止回收 |
| JavaScript 适配器 | 通过 Jint 实际求值 `!!js`，并提供受控上下文桥接 | 只处理受信任配置；不承诺 Node 模块环境、沙箱或 Native AOT |

Cordis.NET 不执行任意服务端 TypeScript 插件。JavaScript 原型、抛出的 primitive、循环对象图和 Node 解析不属于通用平台兼容性承诺。Agent/LLM 业务、RSI 目标和 FSM 领域逻辑由产品承担。可复用客户端/UI、诊断和安装机制仍在仓库范围内，其协议及 Node 实现需由 Core 之上的层明确适配。历史盘点记录当时的覆盖，不定义仓库永久排除项；范围内缺口不代表当前可用功能。

`ConfigObject<T>` 是 Composition 中组合既有 `ConfigSchema<T>` 的作者适配，不是第二套 validator。它将显式、完整的数据字段投影组合为 live 绑定、严格普通比较及完整保存转换。默认值、校验、raw 往返和完整字段覆盖仍由作者负责。此 API 需要 `0.2.0-alpha.3`或更高版本，未包含在已发布的 `0.2.0-alpha.1` 包中。

字段编辑复用固定 ConfigEditor 的职责：所属层定位、正常 hook/校验、完整 raw 用户覆盖、overlay 拒绝、持久写入和协作恢复。为保留无关注释，原生编辑可保留 YAML flow 根。revision 使用原生内容/激活哈希；不能无损往返的 `Undefined` SET 会被拒绝，不改变运行时 sentinel 语义。部署需重启时在保存前拒绝。Settings 选择已声明的 live 数据，包含嵌套对象和集合，并在提交时同样强制宿主选择／脱敏政策。用户来源 presence 纳入唯一的同源 inserted row，是明确的原生适配，不按 effective 值比较推断。有序 SET/reset 请求在写入前校验一个完整候选。显式元数据支持独立的 Schemastery 与 JSON Schema 投影；不能推断任意 validator 的变换，投影诊断仍是合同的一部分。Core 保存注解而不解释表单政策。独立审查与组合验证结果见[验证记录](validation.zh.md)。

客户端制品通过部署包路由复用 `dsh.client.platform=web` 与 `exports['./client']`，真实路径检查覆盖目录链接。原 Probes 消费者保留自包含 ESM 与轮询；当前源码另提供 `ClientModuleCatalog` 及可选 TypeScript 包，复用固定 Cordis/Loader、module entries 和 SlotCore 机制。显式声明外部包的模块编译成 lazy factory 注册，不承诺任意 npm 解析。HTTP/SSE 管理是原生协议，使用宿主授权、代际约束与活动安装查询，不承诺与任意 Typert 服务线协议兼容。源码测试、浏览器交互、CLR 执行与包消费仍是分开的证据。这些新增能力不改变正式行为锁或已发布批次。

部署解析代在既有包映射和本地映射之外接受显式外部链接根。每一代捕获链接的真实目标；撤下根只停止其调用者的路由，不卸载保留的模块。同名链接改指另一目标需要重启，即使中间曾撤下该根。链接的 peer 声明从当前祖先 manifest 读取；私有依赖查找仍由宿主提供，CLR 导出仍使用显式映射。这不安装 Node loader，也不模拟 Node exports、包自引用、CommonJS/ESM 差异或 worker 传播。

HMR 等待候选生命周期完成；缺失服务时 fiber 可以停在 Pending。激活失败恢复原插件，后续清理、恢复或诊断观察者失败不会覆盖原始错误。保留的配置引用停留在该代最后提交的值，替换与恢复的 fiber 获得全新引用。若引用的泛型值类型属于可回收 bundle，宿主保留该引用也会保留 bundle，直到释放引用。CLR bundle 的私有托管依赖从影子副本或显式共享的宿主契约解析；无关的默认上下文程序集不能补齐缺失私有依赖。框架程序集仍使用运行时，原生库查找保留 CLR/操作系统的加载规则。

协作式卸载观察者错误通过 `ClrUnloadObservation.UnloadError` 的文本快照暴露，不保留异常实例或其可回收类型。`UnloadRequested` 记录一次尝试，`IsCollected` 观察托管加载上下文包装对象的回收。`Unloading` handler 抛错可能中止底层释放；即使包装对象已回收，影子 DLL 仍可能被锁定。宿主必须修复或移除失败的 handler，在仍持有实际上下文时显式再次请求 `Unload()`；适配器不会自动重试。影子目录删除是独立结果，保留引用或中断的释放仍可能阻止删除。

patch 来源读取、字段编辑和 Settings 来源判定均将 falsey `insert`（包括 `null` 和 `false`）视为普通覆盖，遵循固定 Include 的应用规则。固定 ConfigEditor 使用 undefined 或字段存在性判断；此处恢复继承有意遵循 Include，确保 SET 回继承配置后移除有效覆盖，后续 base 更新可以继续继承。真实 insert、无关字段及源码注释沿用既有编辑合同。


可选 NuGet 工具链将批准绑定到已检查的根归档，SDK 准备仅使用宿主选定的来源。批准覆盖整次 MSBuild 调用，包括依赖 targets；它不是逐依赖哈希批准系统，也不是沙箱。Windows 工具进程使用创建时绑定 Job（Windows 10 / Windows Server 2016 及以后），记录所有权后恢复执行，同时确认进程树及管道排空。Linux 使用独立进程组。取消会停止该组；宿主异常退出可能留下仍运行的进程组。后继操作拒绝未解决的运行记录，直到明确停止旧组并确认终止。保留或无法判定的记录需要明确恢复；诊断保留真实失败阶段和残留目录。动态包身份替换/重装可能需要新 resolver；静态 AOT 应用通过重新发布改变代码。

安装检查已捕获的包声明在 SDK 准备前准入，停用安装也不例外。`PackageInspection.ManifestJson` 是与 archive hash 同源的不可变快照；无法提前检查声明的适配器可以省略它，保留准备后 manifest 准入。.NET 工具链始终捕获根包声明，在准备前及 restore 后检查根包 hash，并保留准备后 manifest 准入。构建批准不授予版本豁免。

DSH-enabled NuGet 宿主应显式将 `ProfileLaunch.CompatibilityPackageName` 设置为 `DotnetPluginToolchain.NormalizeCompatibilityPackageName`，将 `ManifestLocator` 设置为该工具链的 `LocateManifest`。适配只改变 grant key 中的名称；包/runtime 精确版本、manifest 拼写和已保存选择不变。Scoped npm 名称及默认 DSH policy 保留原规则。管理入口在授权前只解析一次豁免身份，向持久化传递相同的规范值；直接调用者先使用 `ResolveVersionExemptionIdentity`，再调用 `SetVersionExemptionAsync`。授权变化参与既有 DSH session 刷新。授权数据不可读时不授权、不可覆写，也不阻止启动。

HTTP 管理授权与执行使用同一个端点资源。插件/bundle 启停使用 `target`，移除及包检查使用 `name`，活动安装等待/取消使用 `requestId`，豁免使用规范的 `packageVersion`。配置/Settings 使用 `entryId`，客户端制品使用路由中的包名。额外字段不能替代授权目标。清单与事件流要求对应操作权限，不指向单个资源。

原生管理 `Changed` 事件逐个隔离同步观察者异常，记录日志，既不替换已完成操作的结果，也不阻止后续观察者。它不改变 Core 事件语义。启停与 bundle 选择先持久化、再协调；应用失败仍可能保留已保存文件。字段编辑恢复继续遵循其独立合同。

## 证据

Profile 安装现在对已失效的输入返回 `profile-conflict`，不再用 Prepare 前读取的 manifest 覆盖后续编辑。这是原生适配：固定 DSH 成功路径在 `selectBundle` 重读，但没有提供完整产品候选批准合同。两者都不保证能对忽略协作锁的任意编辑器执行条件替换。

`PluginConfigurationOperations.AdmitProfileAsync` 接收库持有的不可变 manifest 文本和原始根配置候选，组合仍使用现有 Profile/Include 逻辑。修改分离的 composition 视图不会改变保存候选。安装与 bundle 选择在发布/持久化前准入，并通过 `ProfileSession` 应用该候选；移除分别准入其持久步骤，其中删除依赖候选在工具删除包目录前准入。拒绝第二步时，包保持已安装、已取消选择。重复选择仍在重新应用前准入。普通消费可以省略准入。仅使用旧 `ReconcileAsync` 的宿主在未启用产品准入时保留原回调；启用产品准入时必须支持候选 reconciliation，不能静默退回重新读盘组合。

即使 owner 配置比较相等，捕获的基础数据变化也会进入既有 Include 更新；未变化条目保留原 fiber。候选回调与赋值时的旧回调关联。宿主随后只替换 `ReconcileAsync` 时，未启用准入的操作仍执行该定制；启用准入则拒绝，直到宿主明确为此接线设置候选应用回调。

读集记录完整 Profile manifest、基础配置、选中 bundle 的 manifest/patch、Profile/Home patch、兼容输入、启动 overlays 及部署映射。Prepare 后记录准备目录中的相对文件名和内容哈希。声明的 `PublicationDirectory` 允许同一目录内容不变地移动，并增加该包自己的本地映射；其他映射与来源保持不变。已批准 manifest 的替换是另一项计划内写入。发布后也区分这些变化与冲突。发布后冲突可以返回 installed=true、selected=false、failed 及保留的部署目录；更晚的应用失败可以保留已保存选择。不增加自动合并、重放或整个运行世界回滚，既有取消、安装等待和工具链归属继续有效。

在线 metadata 编辑使用同一所有者的 `ReadProfileAsync` 与 `SaveProfileMetadataAsync`。保存先等待已有 mutation，再核对原修订；不能修改管理入口拥有的 dependencies 或 `dsh` 政策/选择。宿主决定怎样呈现等待、草稿和冲突，不代替产品同意缩减作者体验。低层 `PackageManifest.Write` 和静态维护函数仍要求调用者排除并发，或在离线 Profile 使用。所有受支持的并发写者必须遵守同一 Profile 锁协议和队列顺序。指纹检查检测已观察到的变化，不能关闭最后比较到 rename 之间非协作写者的竞态。产品自身政策输入和后续运行时/插件副作用不构成新的全局事务。后续协作请求独立于本次安装提交，最终 Profile 可以合法地不同。

最近完成的运行汇总见[验证记录](validation.zh.md)。`docs/upstream-tests.json` 是不可变候选清单；`docs/test-map.json` 保存当前处置；`docs/scenario-map.json` 记录差分场景。上游源码执行、.NET 测试、配对 trace 与人工断言审阅仍是彼此独立的证据类别。

## 2026-10-09 模块导出与 Typert 续建

[范围续账](development.zh.md#2026-10-09-应用基础设施范围续账) 将起始 HEAD 的遗漏与本次实施分别记录。行为依据仍是 DSH `639ed015397290b3745d163aafe02ffee4aa3f84`。历史完成记录与固定基线保持不变。本次源码续建不声明已发布包批次。

### CLR 模块身份与所有权

固定 Loader 的模块导入和默认导出归一化独立于服务 `Provide` 选择插件入口。原生适配使用显式 CLR 入口类型：既有包根 `assembly`/`entryType` 元数据继续有效，可选 `exports` 将包子路径映射到同一程序集中的入口类型。不增加 Core 导出表，也不要求单入口插件声明空表。

归一化 bundle 目录相同的请求共享 shadow copy、程序集身份与可收集加载上下文。Resolver 租约按请求拥有，生命周期与配置仍按 Loader Fiber 拥有。最后一个租约移除时请求卸载；保留引用与 unload observer 失败可能延迟收集或 shadow 删除。Core、Clr、Composition 默认作为共享合同程序集；其他合同需要宿主显式选择。无关默认上下文程序集不能满足插件私有依赖。

`ClrModuleResolver.ReplaceAsync` 的整组重载要求提供 bundle 的每个已登记请求，包括别名及尚未加载的导出。它准备单一候选代际并共同提交路由。`Loader.ReplacePluginsAsync` 复用既有 Fiber 稳定与恢复语义，Pending 仍合法。回调拥有 teardown 与恢复。原子路由发布不表示候选副作用回滚或产品全局事务。合同 descriptor 不新增统一替换准入算法。

### 原生 Typert 合同与传输

本次续建提供 Roslyn 作者生成器、编译器无关 descriptor、System.Text.Json 元数据 codec 与 Schema、Fiber 拥有的 registry、显式 artifact resolver 的 Loader 集成、Gateway，以及生成的 TypeScript 模块/声明制品。生成器在 `Cordis.NET.Composition` 的 analyzer 目录内交付。这覆盖显式声明的原生 Remote 边界，不等同于固定 TypeScript 编译器的完整源类型图。显式 JSON 元数据及其命名、nullable 与必需字段选项定义原生数据合同。公开声明与包消费者见[作者指南](authoring.zh.md#2026-10-09-模块导出与生成式-remote-合同)。

`IClrTypertModule` 从插件工厂所在的同一已加载 CLR bundle 导出贡献。Typert 导入按 owner Fiber 激活期缓存。动态 HMR 必须在 provider 替换前退役该 owner，在提交后或失败恢复旧 provider 后重启。这样释放旧生成 delegate 与 CLR 序列化类型；仅有类型名相同，不能让旧贡献适配替换后的新程序集。登记有效性同时阻止撤销后的旧调用。Registry 元数据不代替 Cordis 服务权威或产品政策。

原生 Gateway 支持直接调用、显式登记的 Context 选择/对象 lookup、Remote 错误、协作取消、downlink 流及 JSON base64 字节结果。ASP.NET 传输使用 HTTP JSON/NDJSON，由宿主提供 endpoint 授权。生成 TypeScript `createRemote` 消费该传输；异步 `mountRemote` 等待 owner 登记，将互不重叠的方法装入共享 root `remote.<namespace>` 服务，并按贡献撤销。这些是显式平台适配，不承诺完整固定 Typert wire protocol。

客户端安装串行修改 namespace，并在该队列之外等待同名 provider 退役，使依赖清理能继续。重复方法在发布前拒绝。同步 `internal/service` observer 在发布时抛错，保留固定 Cordis provider 的失败行为：即使挂载拒绝，仍可能留下已提供的 namespace。适配层不承诺任意 observer 的回滚；应先修复该 observer 并退役其 owner，再重试。

### 未闭合范围与证据边界

完整源类型分析、丰富 Context/owned-value 图、Peer/uplink/复用流与 event remotes、非协作取消及完整二进制 attachment 协议仍未实现。PluginManager、Settings/配置与客户端管理生产消费者向生成合同的迁移仍未完成。既有配置 Schema 导出、手写 `MapCordisService` 与 HTTP/SSE 管理保留当前合同，不能据此计为已完成 Typert 消费者。

[独立多入口包消费者](../scripts/verify-clr-multi-entry.py) 已具有本地普通运行时及生成 TypeScript/HTTP 证据，覆盖共享 bundle 身份、配置、HMR 恢复/替换、provider/合同撤销与旧调用失效。独立的[原生 Remote 包消费者](../scripts/verify-typert.py) 验证其他边界。Windows/Linux、JIT 与静态 Native AOT 结果必须按最新已完成[验证记录](validation.zh.md)分别读取；进行中的运行和已有源码测试不能作为正式平台验收。动态 CLR 加载不声明 Native AOT 支持。

产品替换接受、业务退休/排空、权限、Project authority、basis 与 receipt/outbox 规则仍由产品拥有。不引入应用 `ApiCatalog` 标准或竞争总规划。剩余通用缺口继续记在原应用基础设施范围中。
