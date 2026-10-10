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

HMR 等待候选生命周期完成；缺失服务时 fiber 可以停在 Pending。激活失败仅在候选清理成功后尝试恢复；后续清理、恢复或诊断观察者失败不会覆盖原始错误。保留的配置引用停留在该代最后提交的值，替换与恢复的 fiber 获得全新引用。若引用的泛型值类型属于可回收 bundle，宿主保留该引用也会保留 bundle，直到释放引用。CLR bundle 的私有托管依赖从稳定制品目录、显式请求的开发副本或显式共享的宿主契约解析；无关的默认上下文程序集不能补齐缺失私有依赖。框架程序集仍使用运行时，原生库查找保留 CLR/操作系统的加载规则。

2026-10-09 的 replacement 安全修补有意适配固定 DSH HMR 的“警告后继续”路径。普通 `Fiber.DisposeAsync` 仍报告异常并继续兄弟清理，保留既有 effect 局部分组语义。`Fiber.CleanupErrors` 记录清理失败，包括此前移除的 effect 及所拥有的子 fiber；重启后仍保留失败，因为逸出的工作可能继续存在。旧代清理出现任何失败时，replacement 拒绝激活候选。抛出的原异常携带 `PluginReplacementFailure`，包含阶段、清理异常和 `NotAttempted`/`Succeeded`/`Failed` 恢复结果。候选清理不能确认时不恢复 V1；V1 恢复失败时继续停止已部分恢复的 fiber。`Succeeded` 表示框架确认候选清理及原 fiber 激活为 Active，不证明任意业务副作用已逆转。诊断观察者抛错不能把替换失败变成成功。

Replacement 请求释放后，独立等待已捕获 fiber 的生命周期结束，对照固定 HMR 的 `registry.delete` 后调用 `fiber.await`。非根、具有所有权的 fiber 重复释放仍保持单次语义，不能证明先前清理已经完成。清理失败候选时，`WaitAsync` 在达到 Disposed 后仍可能重新抛出保留的启动错误；该已知启动错误与清理失败分开处理，对应 upstream 恢复路径的 `allSettled`。等待过程中记录的真实清理失败仍阻止提交候选或恢复 V1。

在线支持范围是契约兼容、所拥有工作能够合作式安全停止的插件。应用负责兼容准入、退休前关闭新业务入口、排空已接受工作，以及为保留回调、事件和迟到提交设置权限围栏。退休前拒绝可以保留 V1；退休失败可能已部分停止旧代，不得描述为工作图原状保留。除非真实 V1 业务就绪及失败 V2 的退休都已验证，否则应用必须保持受影响范围关闭，并使迟到业务权限失效。任意 resolver callback 的异常若没有结果信息，同样不提供恢复保证。单凭 `Context.DisposeAsync`、旧 resolver 映射、卸载请求或 ALC 存活／回收，均不能证明这些业务结果。完整包在线 Update 和更新并发事务不属于此次实现替换修补；下述独立包安装路径支持更新后重启生效；[验证记录](validation.zh.md) 给出真实 Host 夹具与剩余限制。

Host 夹具在受影响业务入口保持关闭时检查插件就绪，不持有准入锁调用插件代码。V2 只有在整个 resolver replacement 成功返回、映射完成提交后才开放入口。恢复 V1 时，先等待 resolver 完成失败候选回退，再核验业务并开放；仍重新抛出原替换异常。这些是应用操作，没有新增生产 readiness API。若 Loader 已成功切换后业务校验失败，resolver callback 失败会保留旧映射，但不能恢复旧运行图。夹具明确将该范围标记为需要人工处理，保持 HTTP／回调／事件业务关闭，并拒绝后续替换，包括已经排队的调用。它不声称 V1 已恢复，也不自动修复分叉。实际应用必须实现同样的关闭状态责任，或在恢复服务前另行证明恢复完成。

并发 Host 替换必须由应用协调，锁覆盖整个替换过程，包括 resolver 返回后的就绪核验、重新开放或失败关闭。夹具使用一把异步更新锁覆盖该范围。resolver 只串行化自身修改，不串行化应用随后作出的业务准入决定。此协调不实现完整包 Update 事务。

协作式卸载观察者错误通过 `ClrUnloadObservation.UnloadError` 的文本快照暴露，不保留异常实例或其可回收类型。`UnloadRequested` 记录一次尝试，`IsCollected` 观察托管加载上下文包装对象的回收。`Unloading` handler 抛错可能中止底层释放；即使包装对象已回收，DLL 仍可能被锁定。宿主必须修复或移除失败的 handler，在仍持有实际上下文时显式再次请求 `Unload()`；适配器不会自动重试。物理删除是独立结果，保留引用或中断的释放仍可能阻止删除。

[CLR 制品工作流](clr-artifacts.zh.md)默认使用稳定目录。工具链仅移动一次完成的工作输出，在已批准制品树之外保存完整性记录，启动时验证，并在逻辑移除后保留文件。创建 ALC 不复制制品，ALC 回收也不赋予删除权限。显式 `ShadowCopy` 仍用于完整静止、之后必须原地重建的开发输出。不承诺每个 ALC 独立 native 静态状态。没有记录的旧部署需显式重新安装，不会被静默重封。

patch 来源读取、字段编辑和 Settings 来源判定均将 falsey `insert`（包括 `null` 和 `false`）视为普通覆盖，遵循固定 Include 的应用规则。固定 ConfigEditor 使用 undefined 或字段存在性判断；此处恢复继承有意遵循 Include，确保 SET 回继承配置后移除有效覆盖，后续 base 更新可以继续继承。真实 insert、无关字段及源码注释沿用既有编辑合同。


可选 NuGet 工具链将批准绑定到已检查的根归档，SDK 准备仅使用宿主选定的来源。批准覆盖整次 MSBuild 调用，包括依赖 targets；它不是逐依赖哈希批准系统，也不是沙箱。Windows 工具进程使用创建时绑定 Job（Windows 10 / Windows Server 2016 及以后），记录所有权后恢复执行，同时确认进程树及管道排空。Linux 使用独立进程组。取消会停止该组；宿主异常退出可能留下仍运行的进程组。后继操作拒绝未解决的运行记录，直到明确停止旧组并确认终止。保留或无法判定的记录需要明确恢复；诊断保留真实失败阶段和残留目录。更新已安装的 Profile 自有 NuGet 包时，工具链准备并准入新版本，将完整输出移动到独立版本目录，并保存下一次启动使用的版本及选择。操作返回 `restart-required`，当前 resolver 路由和插件 fiber 继续使用旧版本，与固定 DSH 的已安装包边界一致。同版本重装及安装目录自有包的替换仍被拒绝。已有目标目录绝不覆盖；准备、准入及保存前检测到的发布冲突保留原先保存的版本并报告残留输出。这不是文件系统事务，也不自动清理旧版本。已退休身份仍需新 resolver 才可重装；静态 AOT 应用通过重新发布改变代码。

安装检查已捕获的包声明在 SDK 准备前准入，停用安装也不例外。`PackageInspection.ManifestJson` 是与 archive hash 同源的不可变快照；无法提前检查声明的适配器可以省略它，保留准备后 manifest 准入。.NET 工具链始终捕获根包声明，在准备前及 restore 后检查根包 hash，并保留准备后 manifest 准入。构建批准不授予版本豁免。

DSH-enabled NuGet 宿主应显式将 `ProfileLaunch.CompatibilityPackageName` 设置为 `DotnetPluginToolchain.NormalizeCompatibilityPackageName`，将 `ManifestLocator` 设置为该工具链的 `LocateManifest`。适配只改变 grant key 中的名称；包/runtime 精确版本、manifest 拼写和已保存选择不变。Scoped npm 名称及默认 DSH policy 保留原规则。管理入口在授权前只解析一次豁免身份，向持久化传递相同的规范值；直接调用者先使用 `ResolveVersionExemptionIdentity`，再调用 `SetVersionExemptionAsync`。授权变化参与既有 DSH session 刷新。授权数据不可读时不授权、不可覆写，也不阻止启动。

HTTP 管理授权与执行使用同一个端点资源。插件/bundle 启停使用 `target`，移除及包检查使用 `name`，活动安装等待/取消使用 `requestId`，豁免使用规范的 `packageVersion`。配置/Settings 使用 `entryId`，客户端制品使用路由中的包名。额外字段不能替代授权目标。清单与事件流要求对应操作权限，不指向单个资源。

原生管理 `Changed` 事件逐个隔离同步观察者异常，记录日志，既不替换已完成操作的结果，也不阻止后续观察者。它不改变 Core 事件语义。启停与 bundle 选择先持久化、再协调；应用失败仍可能保留已保存文件。字段编辑恢复继续遵循其独立合同。

## 证据

Profile 安装现在对已失效的输入返回 `profile-conflict`，不再用 Prepare 前读取的 manifest 覆盖后续编辑。这是原生适配：固定 DSH 成功路径在 `selectBundle` 重读，但没有提供完整产品候选批准合同。两者都不保证能对忽略协作锁的任意编辑器执行条件替换。

`PluginConfigurationOperations.AdmitProfileAsync` 接收库持有的不可变 manifest 文本和原始根配置候选，组合仍使用现有 Profile/Include 逻辑。修改分离的 composition 视图不会改变保存候选。安装与 bundle 选择在发布/持久化前准入，并通过 `ProfileSession` 应用该候选；移除分别准入其持久步骤，其中删除依赖候选在工具解除运行时映射前准入。CLR 制品保留并单独报告，直至显式离线删除。拒绝第二步时，包保持已安装、已取消选择。重复选择仍在重新应用前准入。普通消费可以省略准入。仅使用旧 `ReconcileAsync` 的宿主在未启用产品准入时保留原回调；启用产品准入时必须支持候选 reconciliation，不能静默退回重新读盘组合。

即使 owner 配置比较相等，捕获的基础数据变化也会进入既有 Include 更新；未变化条目保留原 fiber。候选回调与赋值时的旧回调关联。宿主随后只替换 `ReconcileAsync` 时，未启用准入的操作仍执行该定制；启用准入则拒绝，直到宿主明确为此接线设置候选应用回调。

读集记录完整 Profile manifest、基础配置、选中 bundle 的 manifest/patch、Profile/Home patch、兼容输入、启动 overlays 及部署映射。Prepare 后记录准备目录中的相对文件名和内容哈希。声明的 `PublicationDirectory` 允许同一目录内容不变地移动，并增加新身份自己的本地映射。更新时保留已有运行时映射，候选则使用准备好的新版本组合；其他映射与来源保持不变。已批准 manifest 的替换是另一项计划内写入。发布后也区分这些变化与冲突。首次安装发布后但依赖尚未保存的冲突返回 installed=false、selected=false、failed 及保留的部署目录；更晚的应用失败可以保留已保存选择。不增加自动合并、重放或整个运行世界回滚，既有取消、安装等待和工具链归属继续有效。

在线 metadata 编辑使用同一所有者的 `ReadProfileAsync` 与 `SaveProfileMetadataAsync`。保存先等待已有 mutation，再核对原修订；不能修改管理入口拥有的 dependencies 或 `dsh` 政策/选择。宿主决定怎样呈现等待、草稿和冲突，不代替产品同意缩减作者体验。低层 `PackageManifest.Write` 和静态维护函数仍要求调用者排除并发，或在离线 Profile 使用。所有受支持的并发写者必须遵守同一 Profile 锁协议和队列顺序。指纹检查检测已观察到的变化，不能关闭最后比较到 rename 之间非协作写者的竞态。产品自身政策输入和后续运行时/插件副作用不构成新的全局事务。后续协作请求独立于本次安装提交，最终 Profile 可以合法地不同。

最近完成的运行汇总见[验证记录](validation.zh.md)。`docs/upstream-tests.json` 是不可变候选清单；`docs/test-map.json` 保存当前处置；`docs/scenario-map.json` 记录差分场景。上游源码执行、.NET 测试、配对 trace 与人工断言审阅仍是彼此独立的证据类别。

## 2026-10-09 模块导出与 Typert 续建

[范围续账](development.zh.md#2026-10-09-应用基础设施范围续账) 将起始 HEAD 的遗漏与本次实施分别记录。行为依据仍是 DSH `639ed015397290b3745d163aafe02ffee4aa3f84`。历史完成记录与固定基线保持不变。本次源码续建不声明已发布包批次。

### CLR 模块身份与所有权

固定 Loader 的模块导入和默认导出归一化独立于服务 `Provide` 选择插件入口。原生适配使用显式 CLR 入口类型：既有包根 `assembly`/`entryType` 元数据继续有效，可选 `exports` 将包子路径映射到同一程序集中的入口类型。不增加 Core 导出表，也不要求单入口插件声明空表。

归一化 bundle 目录相同的请求共享物理加载模式、程序集身份与可收集加载上下文。稳定加载保留原文件；显式 ShadowCopy 共享一个开发副本。同一已加载 bundle 的入口混用加载模式时，在导入新增入口之前拒绝。Resolver 租约按请求拥有，生命周期与配置仍按 Loader Fiber 拥有。最后一个租约移除时请求卸载；保留引用与 unload observer 失败可能延迟收集或 shadow 删除。Core、Clr、Composition 默认作为共享合同程序集；其他合同需要宿主显式选择。无关默认上下文程序集不能满足插件私有依赖。

`ClrModuleResolver.ReplaceAsync` 的整组重载要求提供 bundle 的每个已登记请求，包括别名及尚未加载的导出。它准备单一候选代际并共同提交路由。`Loader.ReplacePluginsAsync` 复用既有 Fiber 稳定与恢复语义，Pending 仍合法。回调拥有 teardown 与恢复。原子路由发布不表示候选副作用回滚或产品全局事务。合同 descriptor 不新增统一替换准入算法。

### 原生 Typert 合同与传输

本次续建提供 Roslyn 作者生成器、编译器无关 descriptor、System.Text.Json 元数据 codec 与 Schema、Fiber 拥有的 registry、显式 artifact resolver 的 Loader 集成、Gateway，以及生成的 TypeScript 模块/声明制品。生成器在 `Cordis.NET.Composition` 的 analyzer 目录内交付。这覆盖显式声明的原生 Remote 边界，不等同于固定 TypeScript 编译器的完整源类型图。显式 JSON 元数据及其命名、成员空性与必需字段选项定义原生数据合同。Roslyn 通过 `TypertCodec.CreateNullable` 补入运行时丢失的根可空引用标注；其可空 Schema 投影保留非空递归子节点。结果保留固定调用语义，不增加 Schema 准入。公开声明与包消费者见[作者指南](authoring.zh.md#2026-10-09-模块导出与生成式-remote-合同)。

`IClrTypertModule` 从插件工厂所在的同一已加载 CLR bundle 导出贡献。Typert 导入按 owner Fiber 激活期缓存。在动态模块代际边界，候选准备完成后、退役 provider 前，暂停受影响的精确 Loader 请求；仅在路由提交或确认旧 provider 恢复后恢复。暂停撤销相应登记、失效制品缓存并脱离旧导入；其他贡献者保持登记身份和 owner。旧导入晚到也不能登记进恢复后的新代。这样释放 loader 对旧生成 delegate 与 CLR 序列化类型的引用；仅有类型名相同，不能让旧贡献适配替换后的新程序集。登记有效性同时阻止撤销后的旧调用。Registry 元数据不代替 Cordis 服务权威或产品政策。

原生 Gateway 支持直接调用、显式登记的 Context 选择/对象 lookup、Remote 错误、协作取消、downlink 流及 JSON base64 字节结果。ASP.NET 传输使用 HTTP JSON/NDJSON，由宿主提供 endpoint 授权。生成 TypeScript `createRemote` 消费该传输；异步 `mountRemote` 等待 owner 登记，将互不重叠的方法装入共享 root `remote.<namespace>` 服务，并按贡献撤销。这些是显式平台适配，不承诺完整固定 Typert wire protocol。

Host unary 取消遵循固定调用边界：成功业务不会仅因传输信号已取消而被拒绝，已取消信号下的业务失败则成为 `gateway/cancelled`。Downlink 读取与信号竞争，完成清理后才暴露失败。原生枚举器必须先结束未完成的读取再释放；迟到读取错误会被观察，不替换取消结果，释放错误仍是清理失败。固定 Host 和此适配都不承诺强制终止始终不结束的业务或清理。提供者/定义撤销后的检查属于单独的原生代际适配，不是上游活动调用中止机制。

客户端安装串行修改 namespace，并在该队列之外等待同名 provider 退役，使依赖清理能继续。重复方法在发布前拒绝。同步 `internal/service` observer 在发布时抛错，保留固定 Cordis provider 的失败行为：即使挂载拒绝，仍可能留下已提供的 namespace。适配层不承诺任意 observer 的回滚；应先修复该 observer 并退役其 owner，再重试。

### 未闭合范围与证据边界

后续质量修复在共享 CLR bundle 内保留每个主程序集的依赖根并拒绝冲突的私有 binary，避免只依赖第一入口的 resolver 或 CLR 已加载程序集缓存。字节相同的副本可以共享身份。这项显式 .NET 规则不推导上游 ABI 兼容算法。原生 Schema 投影也保留位置 tuple 合同，不再丢弃 `prefixItems`；不支持的长度约束会拒绝生成。这些修复不增加完整源类型分析。

提供者代际检查独立于 definition 撤销验证：等待中的 unary Service、lookup、Context 和 downlink 操作保留活跃 descriptor，同时替换其提供者。撤销本身不会中止这些操作；旧成功结果由原生代际检查拒绝。JSON codec 输入校验、结果序列化与流清理限制分别在公开 API 和[作者指南](authoring.zh.md)中说明。修复后的精确源码与包检查点以最新验证记录为准；早期平台结果只适用于其记录的检查点。

完整源类型分析、丰富 Context/owned-value 图、Peer/uplink/复用流与 event remotes 及完整二进制 attachment 协议仍未实现。PluginManager、Settings/配置与客户端管理生产消费者向生成合同的迁移仍未完成。既有配置 Schema 导出、手写 `MapCordisService` 与 HTTP/SSE 管理保留当前合同，不能据此计为已完成 Typert 消费者。

[独立多入口包消费者](../scripts/verify-clr-multi-entry.py) 已具有本地普通运行时及生成 TypeScript/HTTP 证据，覆盖共享 bundle 身份、配置、HMR 恢复/替换、provider/合同撤销与旧调用失效。独立的[原生 Remote 包消费者](../scripts/verify-typert.py) 验证其他边界。Windows/Linux、JIT 与静态 Native AOT 结果必须按最新已完成[验证记录](validation.zh.md)分别读取；进行中的运行和已有源码测试不能作为正式平台验收。动态 CLR 加载不声明 Native AOT 支持。

产品替换接受、业务退休/排空、权限、Project authority、basis 与 receipt/outbox 规则仍由产品拥有。不引入应用 `ApiCatalog` 标准或竞争总规划。剩余通用缺口继续记在原应用基础设施范围中。


## 独立原生模型与 Settings 边界，2026-10-10

源编译工具现于运行时 descriptor 发射前生成 version-1 原生声明制品，保留方法/参数名称和 wire 选择、类型引用、成员/构造器关系、C# required 与 JSON-required 事实、嵌套可空标注、支持的常量、可选参数默认值、文档、JSON 策略及 STJ 使用的四个空状态注解，并记录有效编译设置。制品身份标识这些事实，不是 activation 或普适替换准入。Roslyn Symbol 是提取输入；序列化模型没有 Symbol、CLR Type、delegate 或 JsonTypeInfo。引用 CLR 声明标记为 external；元数据不能提供所有仅存在于源码的事实。

现有由 descriptor 形成的 `TypertTypeModel` 仍是有界的旧摘要，新制品不从它反推。普通构建先将源事实与制品比较，再保留现有 descriptor generator。该 conformance 桥在独立提取与 .NET 投影继续建设时保留已成立的 runtime/TS 路径，不宣称所有 emitter 或运行时类型 registry 已消费完整新模型。

.NET emitter 当前投影直接普通 unary 方法及支持的数据 record/class。实际覆盖包含 bool、string、integer、array、只读 list 接口、JsonElement、可空成员、必需构造器/init 成员、命名/ignore 策略和选定空状态注解。只读数据成员、record struct、数据继承、多态/自定义 converter 和更丰富 Remote 形状保留事实或诊断后被该投影拒绝；不宣称完整 C# 或 TS 类型系统等价。JSON 策略不能省略必填 carrier 参数字段，包括显式 null 和默认值。

生成客户端复用原生 HTTP 结果信封和 RemoteError。DTO/抛错 API 是 .NET 适配；TS 保持独立输出，沿用既有结果 API 与 carrier。compiler 只使用所选 SDK Roslyn、STJ 和既有框架传输，本次没有选入新的 NuGet 依赖。

Settings describe 经既有 profile 队列、Include 和 schema/脱敏 helper 暴露选择的 live 配置。本适配仍未实现 base/user 层及数字单调 revision；provider 诊断保留在原生侧，不加入固定 wire 响应。已准入的异步 snapshot 可以在可选普通 `settings` provider 退休后完成：固定上游 describe 是同步调用，没有嵌套 provider lease。这是显式异步适配，与 controller service 或 Remote definition 撤销保留的原生 Gateway generation fence 有区别；后续调用重新查找 provider。不推导任何产品准入、权限或排空规则。

[仅依赖包的 Settings 门禁](../scripts/verify-typert-dotnet.py) 验证源制品、模型独立合同包、pre-CoreCompile/STJ 时序、类型化调用及不同生命周期场景，不调用 Node。静态 Native AOT 适用于调用者，不适用于 collectible CLR Host。IDE/design-time 首次构建、任意引用模型组合、完整 Settings 操作及富图/协议范围继续开放。进行中运行不计作平台验收，正式冻结证据见[验证记录](validation.zh.md)。

Typert 自动发现将已知 Loader 内建入口视为无制品贡献者，对应固定上游对 `cordis:include` 的处理。这些插件由 Loader 自身导入，其请求不属于 CLR 制品 resolver。显式将内建入口配置为制品贡献者仍会失败；错误的已声明 CLR 制品与未知 resolver 请求继续报错。真实 Profile Settings 消费者验证了这一边界。
