# 兼容性与证据

[English](compatibility.md)

## 0.2.1-alpha.1 的基线晋升

正式锁现选择 DSH `687f282e8625c6b8d11ff58211a90524be2e4317`（`0.2.1-rc.2`），并指向 `reference/candidate-adaptation.json` 记录的精确 Windows manifest 适配。验证重建派生源码 `fba488001a6c2d37787d800beeab54a770c826a1`，不会从上游获取这个本地提交。补丁与提交对象保持已审 S10 候选的字节。下方带日期的候选条目记录各原检查点的决定，其中“正式锁未变”描述的是当时状态。本次晋升改变源码选择及验证接线，不改变已成立的 S1–S9 native 合同或支持范围。历史 inventory、失败及既有包身份均保留。

浏览器来源记录也绑定到这一上游提交。39 份复制源码的摘要已逐项对照该提交的 Git blob；本地适配继续单列摘要和原因。随包 Loader 现按上游原样记录原始导入命名空间。受支持的浏览器包装仍拥有唯一的 release 准入 gate，并保留显式目标恢复修正；不增加第二个 gate 或自动回滚。

## 候选 Windows manifest 路径适配，2026-10-23

当前验证候选改为本地派生提交 `fba488001a6c2d37787d800beeab54a770c826a1`，唯一父提交为上游 `687f282e8625c6b8d11ff58211a90524be2e4317`。这是 Cordis.NET 的候选适配，不是上游发布，也不更新正式行为锁。[来源记录](../reference/candidate-adaptation.json)固定上游树、补丁 SHA-256、前后 blob 和可重建的提交对象。原始上游 checkout 与历史收据保留各自身份。

[补丁](../reference/candidate-patches/windows-manifest-scope.patch)只修改 `PackageManifests.hookBindingGetPackageScopeConfig` 中成功的原生 fallback：复制返回元组，再用现有 `toNamespacedPath` 规范其 manifest 路径，与 Windows 上已失效 manifest 的元组合同一致。字符串结果保持不变，不原地修改原生元组。.NET 公开 API 和原生运行时行为均不变。原始测试文件、断言、数量和平台跳过规则均不变。

候选准备从固定父提交重建这一干净 Git 提交，拒绝不同的补丁、树、文件集合或 blob。发现、差分 traces、全部源码分片及严格并集均使用派生身份，收据同时记录源码适配。派生提交通过不能改写成未修改上游提交通过，也不代表正式基线晋升。

## 候选 S10 验证范围，2026-10-23

S10 增加独立候选门禁，保留正式行为锁和历史测试清单，不引入应用公开 API。候选源码通过、原生断言结果及平台／包收据仍是分开的证据；候选能力索引不声称逐一移植了每条上游断言。

S10 还修复了包进程记录中的既有 Linux 竞态：短命根进程可能在读取启动时间前退出。由进程组或 Windows Job 持有的运行按组或 Job 身份恢复，记录的启动时间字段保留为零，不再读取已退出的根进程。无分组的 PID 恢复仍要求真实启动时间。子进程存活检查、输出排空和未解决记录的拒绝规则保持不变，根进程退出不代表可以删除运行记录。这是正式基线下既有进程合同的修复。

固定候选已确认存在 Windows 原始测试失败，位于 `package-manifest.spec.ts` 删除 manifest 后继续查找外层作用域的用例。作用域读取器返回普通盘符路径，原断言要求带命名空间前缀的路径。独立 Node 24.12.0 进程在有、无候选解析钩子两种情况下均复现失败；该 Node 版本处于上游声明的支持范围。见[诊断运行](https://github.com/gentt1024/Cordis.NET/actions/runs/39919928667)。另一次 [Windows 补丁对照](https://github.com/gentt1024/Cordis.NET/actions/runs/39920616943) 执行未修改的 39 项测试文件，原始实现有一项失败，隔离副本仅修正 fallback 路径后 39 项全过、零跳过。未适配候选直到 [771d69a 批次](https://github.com/gentt1024/Cordis.NET/actions/runs/39935870050)仍保留此失败。上文使用独立身份的适配现在应用已归因的修复，不改写这些失败收据，也不证明未修改的上游通过。

[3b6364a 验证批次](https://github.com/gentt1024/Cordis.NET/actions/runs/39922002150)还在私有仓库 SSH 回退用例中记录了一次五秒 Git 预检超时，发生在 pnpm 启动之前。[完整 composition 对照](https://github.com/gentt1024/Cordis.NET/actions/runs/39925752082)使用相同的原始用例、文件执行顺序、Git 版本和 runner 镜像，未复现该超时，根因仍未解决。失败批次继续保留，没有放宽超时、断言或跳过策略。

包展示元数据保留明确的原生边界。公开 `BundleConfigurationInfo` 提供包名、版本和描述，不提供 DSH 插件的本地化元数据或图标资源。候选的 package subpath 图标／locale 规则保留在源码清单和原始测试中，但不宣称原生展示支持。这类展示能力大部分在旧基线已存在；这里记录既有覆盖限制，不将其算成本次新增实现，也不据此省略包解析、元数据缓存失效或描述回归。新增原生本地化／图标输出需要单独的公开合同及消费者迁移。

候选 generator 中由上游禁用的 Cordis 文档 catalog 套件明确记录为 source-skipped，位于 Remote 元数据必需执行范围之外。其源码仍入清单，不计为通过。其他必需原始用例的失败和意外跳过仍会使候选门禁失败。DSH 产品专属服务及既有后端拒绝边界继续明确保留，不从清单中抹去。

## 候选浏览器安全与作者增量，2026-10-23

S9 依据固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317`，涵盖 factory 样式归属、Host release 准入、图片导入和 Slots 声明。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；历史清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。vendor 适配保留原始来源记录，并单独记录候选源码身份。本片不执行 S10 基线晋升。

factory 只认领自本次执行开始后插入的无标签 style 元素。页面原有样式仍归外壳所有，嵌套 factory 保留各自样式，抛错的 factory 先认领并删除本次新插入的样式，再报告失败。已有显式标签的归属规则继续有效。此规则覆盖同步 factory 求值；应用 effect 仍由其 Cordis 生命周期持有。

Host release 身份是可缺省的 `{version, commit?}`，由应用通过 `ClientModuleRelease` 提供，与继续仅绑定 graph 内容的 revision 分开。缺省 release 本身也是有效身份。受支持的浏览器包装层在解析收到的 graph 或改变目标 roster 前比较这两个字段。其他 release 的更新会暂停，包括采用未知 body 格式的 graph，页面保留现有模块。`releaseChange` 通知页面、前一个 Host 和当前 Host 的身份。`dismissRelease()` 仅隐藏该身份的提示，不允许其 graph 准入。Host 返回启动 release 后恢复协调。与候选一致，已接受的工作及对先前已接受 graph 的 retry 仍可继续。显式 disconnect 和 dispose 仍会撤销页面资源。包装层用同一门禁覆盖手动同步与管理重连，不另建一份门禁历史，也不内置重载界面。

原生应用提供稳定的应用版本和可选构建 commit，不从 Cordis 库版本、进程代或 graph hash 推导身份。`ClientModuleGraph.Release` 保留既有位置构造器；`ClientModuleCatalog.CaptureAsync` 增加末尾可选 release 参数，因此这些方法的二进制消费者必须重新构建。管理 graph 响应携带同一身份，缺省字段不输出。

作者构建把小写扩展名 SVG、PNG、WebP 导入内联为精确 MIME/base64 data URL，并观察实际图片输入。输入保护与不可变 generation 发布继续有效。受支持的制品仍是单个 factory 脚本；包内 chunks 和 Node 客户端格式不在此合同内。Slots 增加 `inline?: true` 及 keyed source 的可选 `member` 转发。包提供 registry 和 renderer 声明，应用仍需提供 renderer，包括 inline DOM 行为。

本地修正会在同步通知回调前保存当前消息的版本比较结果。回调中再次同步其他图，不能使外层异版本图获准，也不能拒绝外层已接受的图；通知仍反映最后观察到的 Host。通过公开包装器的回归测试复现候选缺陷，并验证两个方向。

## 候选元数据投影，2026-10-23

S8 依据固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317`，涵盖 RemoteInfo、命名输入视图及可选工具 Schema 投影。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；历史清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。S8 不选择新的发布基线。

`TypertRemoteInfo` 提供懒加载的描述符与直接方法索引。命名方法与该 Info 中对应的描述符是同一个对象。仅支持 Context 接收者的方法仍保留为描述符，不进入命名方法索引。原生注册保留既有的防御性快照边界；registry Info 使用实际存储的快照。与候选 registry 不同，原生不承诺跨注册边界的对象身份。通过 `with` 复制的描述符拥有独立的输入和结果元数据缓存。

normal 与 strict 输入视图分别缓存 parser 和投影。两者均拒绝未知的命名实参字段。元数据值 parser 拒绝非有限 JSON 数值，包括嵌套在未声明字段中的值。普通可选参数允许缺省，lookup 身份仍是必填。normal 使用既有 STJ codec，包括作者默认值与 setter；strict 额外关闭嵌套声明对象，同时保留显式字典与 JSON 的开放性。这是对候选 Zod 策略的原生适配：normal STJ 行为不承诺剥离未知属性。独立生成的载体 DTO 不会重放作者构造器或访问器的方法体。TypeScript 输入 parser 验证 JSON wire 值并保留其对象身份，不执行 CLR 转换、默认值或 setter。例如，字节数组输入在 Host 解码前仍为 base64 JSON 字符串。

工具 `JsonSchema` 与继续负责原生准入和输出的 `TypertCodec.Schema`、`ResultSchema` 分开。无法保留约束的工具投影不提供 Schema，并给出 `JsonSchemaError`；parser、文档及调用仍可用。投影展开无环本地引用，接受可表达的标量、数组、声明对象和互斥联合。递归类型、元组、受约束字典、重叠联合及不支持的约束会被拒绝。原生整数、范围和 format 约束不会被悄悄弱化到候选较小的工具子集。解码后的二进制结果和可能缺省的独立值也不提供工具投影，但其既有 parser 仍可用。普通可选参数通过命名外层对象中省略字段表达缺省，因此其内部 JSON 值仍可投影。非预期的 Schema 工厂异常向上传递，并允许后续重试。

生成的 Host 元数据与独立 .NET／TypeScript 客户端使用既有声明事实和序列化契约。原生命名调用接口直接转发到生成的类型化方法，不重复解析或另建传输层，返回原方法的任务；双向流调用者使用返回句柄既有的 send/end 操作。TypeScript 命名调用同样解析传入客户端当前的命名空间，并保留其返回值。消费者选择 strict 不会改变 Host dispatch 或结果解析。


## 候选事件与访问客户端，2026-10-23

S7 对照固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317` 的 Gateway 事件、RemoteAccess snapshot/follow 和客户端状态。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；历史清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。本节替代 S5/S6 的临时事件拒绝与快照延期说明；S8 元数据投影另行推进。

`Interactions=Answer` 允许订阅事件；只有 owner Peer 接收并回答 waterfall。非 owner 订阅者仅在 `event:<name>` 对每个资源都允许时收到完整通知。`TypertRemoteEventDefinition` 和 `TypertRemoteEventFrame` 新增可选 `ResourceReaders`，复用 `TypertResourceReader` 委托；位置参数是已显式编码、装箱的 `JsonElement`。每条通知对所有非 owner 接收者只解析一次资源；没有 reader 时使用 global。reader 返回 null、空列表、非法身份或抛错时，通知不发给非 owner。owner 交付绕过 reader 与策略。这个 wire JSON 边界对应候选的 JSON 事件参数；普通调用 reader 仍接收已解码 CLR 参数。

结果提交先检查存活、交互能力和 owner 资格，再解析参数，并要求 client 代际仍有效、提交方与订阅方是同一个 Peer 对象。另一 owner Peer 得到 `gateway/interactions-forbidden`，非 owner 回答得到 `gateway/invocation-unavailable`。断线移除当前代际交付，不视为 next 回答；待处理 waterfall 可交付给后续 owner 代际。Peer 销毁后丢弃缓冲事件。事件源关闭也丢弃缓冲并结束观察者，保留原生已有的正常结束方式。授权发生在入队时；策略失效通知影响后续事件，不重新检查已入队事件。

`TypertRemoteAccess.SnapshotFor` 返回封闭的 `TypertAccessFrame.Baseline`；`FollowAsync` 使用当前可信调用身份。`Contribution` 和 `RegisterRemote` 通过既有原生注册表暴露 `remoteAccess/follow`，没有 principal wire 参数。该端点不依赖策略授权。grants 保持资源首次绑定顺序，动作去重后排序并排除 `event:` 动作；条件不接收调用参数。`Invalidate(principal)` 按完整结构化 JSON 身份比较，忽略对象键顺序。失效通知合并处理；follow 先给基线，再给新增／变化项，最后给移除项。服务所有权结束旧代际；新服务使用新策略。与候选一致，直接调用 `FollowAsync` 可能先完成已计算的增量批次，再观察取消；Gateway 还会在每次远程迭代前后检查捕获的服务与 Peer。

`TypertAccessState` 与浏览器 `ClientAccessState` 在替换快照后保留权限 source 身份。没有基线时返回 false。每个物理 follow 代际必须且只能以一个基线开头，顺序错误是终止性失败。载体断线保留最后快照直到替换；终止性失败关闭非 owner 状态，owner 保持允许。原生 `TypertRemoteAccessClient.RunAsync` 沿用事件客户端的一次代际合同，由外层应用控制重试。浏览器 `mountAccess` 使用既有连接控制器，先取消 follow 再撤回 namespace，重新挂载使用全新状态。浏览器原生适配暴露可观察的 `source`／`global` API，不向冻结 slots 实现加入候选的 UI hook 注册。页面初始快照由 Host 显式选择并传入，不自动注入 owner 全局值。

S7 验证还暴露了既有原生执行上下文在 macOS arm64 上丢失唤醒的问题：执行器停止后，清理回调仍留在队列中。空闲状态切换现在通过完整内存屏障先于队列复查，使生产者启动新一轮执行，或由当前执行器发现新增任务。这修复了既有串行执行约定，不改变公开 API 或上游语义。


## 候选资源决策与调用准备，2026-10-23

S6 依据固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317` 的 Gateway access/preparation 与 RemoteAccess 决策、列表过滤。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`；历史清单保留原有 `ddefc45fbc7f8e46dd73185e68295696d1297887` 身份和处置记录。本节替代 S5 对非 owner 普通调用的临时全面拒绝。非 owner 事件订阅与回答仍拒绝，等待 S7；S5–S7 仍是非 owner 的最小发布闭包。本片不宣称已实现访问快照、follow、事件过滤、回答归属或事件吊销。

调用准备依次检查 Peer 活性、活动 descriptor 和精确字段，再对每个实际传入的普通 JSON 参数解码一次。资源提取与授权早于 Context 解析、Service/binding 准入和 lookup 解码解析。业务调用复用已经解码的普通值。因此非法普通 JSON 优先于 Service 缺失或无效、Context 和 lookup 错误，owner 也遵循此顺序。只有 lookup 的准备仍保留 Service 准入和并行首个失败语义。省略的普通参数保持缺省，显式 null 保持存在。这是相对正式基线有意改变的错误优先级。

`TypertRemoteAccess` 是 Host 持有的 `remoteAccess` Context 服务。`RegisterPolicy(owner, kind, policy)` 将非 owner kind 的策略归属于调用者 Fiber，并拒绝重复注册。`Decide` 每次读取一次 bindings，要求所有涉及的资源分别获准，同一资源的匹配 binding 互为备选。首个拒绝条件提供原因，除非后续 binding 允许；没有拒绝条件时使用 `Explain` 的原因或 `no-allow`。bindings、condition 和 explanation 异常返回 `policy-failed`；非法原生策略输出也拒绝。每个策略在 `Invalidate` 或注册变更前只报告一次诊断。原生 owner-only 宿主可以不安装该服务，非 owner 此时以 `no-allow` 拒绝。这是保留现有 owner 组合的明确适配，与上游必需注入不同。S7 前不会为 `remoteAccess/follow` 添加特殊许可。

`TypertAccessResource.Global` 没有身份，`Create(kind, id)` 创建 ID 为非空字符串的命名资源。role、binding、request 和封闭的 allow/deny 类型表达业务自有策略。`Allows(resource)` 读取当前 invocation 的 principal 和 endpoint，不传调用参数，每次重新读取当前 bindings。owner 和调用外允许，策略缺失或失败返回 false。应用在方法中用它过滤列表或流项目；一个资源的权限不会授予另一个资源。

`ITypertRemoteResourceSource.ResourceReaders` 在根服务上将导出方法名映射到按 kind 区分的 `TypertResourceReader`。每个 kind 的方法 reader 覆盖合并的 Context/lookup 身份，后者覆盖注册默认字段，并保持 kind 原有顺序。Context 身份早于 lookup 参数，重复 ID 保留且逐一检查。默认字段优先取同名顶层参数，否则只取第一个对象参数。reader 可返回多个 ID；null 移除本 kind，空列表、异常、非法 kind 或非字符串／空 ID 在策略求值前产生 `resource-invalid`。没有资源时使用 global。owner 不执行资源 reader 或策略决策。所有载体仍共享已关闭 Peer 和事件准入检查。

原生 reader 接收 `IReadOnlyList<object?>` 和单独的 `CancellationToken`，位置参数列表不会追加取消参数。policy/condition 的命名参数采用 `IReadOnlyDictionary<string, object?>`。普通项是之后传给业务的同一份已解码 CLR 值；lookup/Context 项是独立的原始 `JsonElement`。这是对上游已解码 JavaScript 视图的明确原生适配，不通过再次序列化或运行时反射构造授权视图。默认字段使用 codec 自有 JSON 元数据名称和生成的可读输入访问器，或已解码 JSON／字典字段。setter 规范化和构造默认值可见，仅供序列化的 getter 不提供输入字段。不可读字段、不透明对象或尚未选定元数据合同的运行时派生对象需要显式 reader，否则拒绝。可选顶层身份可解包，同时保持业务参数不变。嵌套 ID 使用包装类型的自定义 DTO 应声明显式 reader。

原生普通参数还会在 codec 解码前递归拒绝按 double 解释为非有限值的 JSON 数字，包括本可被 codec 丢弃的未声明字段。这有意严于候选在 parse 后检查 JSON 安全性的规则，避免构造第二份序列化视图，并覆盖无限制 JSON schema 路径；普通 CLR 数字宽度和转换仍由 codec 决定。直接调用、HTTP 和 mux 使用同一准备流程。业务 wire schema、生成客户端签名、正式锁和历史测试清单不变。参见[作者指南](authoring.zh.md)与[验证](validation.zh.md)。

## 候选 Remote 身份与资源分片，2026-10-23

S5 依据固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317` 的 `packages/typert/protocol/src/principal.ts`、`packages/client/connection/src/peer.ts`、注册表资源存储及 Gateway Peer 准入与寿命。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`，历史源码清单不变。这是范围明确的候选例外，不代表已完成候选 Remote 授权。

`TypertPrincipal` 保存独立的 JSON 身份，其中必须有且仅有一个非空、区分大小写的 `kind`。原生构造函数验证这一形状并克隆 JSON；各驱动的无凭据字段由宿主选择。`Owner` 是内置所有者身份。`PrincipalOf(invocation)` 返回原非 owner 身份，对 owner 或调用外部的归因返回 null。只有可信 Host 构造过程能选择 Peer 身份，HTTP 信封、mux 帧和方法参数都不能替换它。

`TypertPeer` 现在公开 `Principal`、`Interactions`、`CancellationToken` 和异步释放。`TypertPeerOptions` 在创建时固定身份与 `Answer`/`None` 交互资格。`TypertHostConnection` 新增显式选项构造函数，原构造函数创建具有 `Answer` 资格的 owner。Gateway 增加显式 Peer 的 unary 和 stream 重载。原有可信进程内便捷重载作为原生适配保留：准备前选择当前挂载的 Connection，或 Gateway 所有的一个回退 Peer，再进入相同的准入流程。流在清理结束前始终保留最初选择的 Peer。

已关闭 Peer 在描述符和参数准备前返回 `gateway/peer-disposed`，两个事件入口也相同。原生 HTTP 仍先解析外层信封取得关联 ID，并执行宿主端点授权；Peer 存活检查早于内层 payload 验证。mux 帧格式和宿主授权仍属于载体边界。Peer scope 清理会取消业务流和事件流，包括缓冲事件转发，但不取消已运行 unary 方法的载体 token。取消仍是协作式的；不结束的业务迭代器可能继续占有待完成清理。`None` 对事件订阅和回答返回 `gateway/interactions-forbidden`，普通 owner 业务调用仍可执行。

本中间分片在 provider 或业务准备前，以 `remote-access/denied` 和原因 `non-owner-unavailable` 拒绝所有非 owner Gateway 调用，包括事件。开放非 owner 调用前仍须完成 S6 资源决策，以及 S7 事件策略、回答归属和撤销。宿主 HTTP 授权仍然必需，不能替代后续这一完整范围。

`RegisterResource`、`GetResource`、`ListResources` 在现有 `TypertRegistry` 中增加归属 Fiber 的资源声明；变更通过现有 `Subscribe` 通知。一个 kind 只有一个默认 wire `Field`，`global` 为保留名称，重复或非法注册不改变注册表。列表是按插入顺序排列的独立快照。撤销只删除原注册，不会删除后来替换的注册。`TypertLookupProvider.Resource` 和 `TypertHostContextAdapter.Resource` 是可选的 provider 事实，resolver 覆盖会保留它们。保留的 lookup wire 定义不增加资源元数据。S5 尚不从参数解析资源，也不进行策略决策。

这些是原生 Host API 增量，生成的业务 DTO、方法 schema 和客户端 wire 制品不变。参见[作者指南](authoring.zh.md)和[验证记录](validation.zh.md)。

## 候选 Profile 与包热重装分片，2026-10-22

S4 依据固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317`，包括 app-boot profile-resolution 与 HMR package-reload 套件。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`，历史原始测试清单不变。这是另一项限定范围的候选例外。

原生 deployment 测试映射显式记录 S4 例外：历史用例 `U-dc7d7920be57ad34` 不再声明完整断言对应，因为 local 删除现在允许，Profile metadata 变化按 scope 判定。原始用例与源码清单保持完整，映射门禁仍执行其对应的原生后继测试。

`DeploymentPackageResolver.ValidateReplacement` 检查完整 successor 而不发布。`Replace` 原子校验并发布，仅成功时清除解析缓存。Profile mapping、local name 和 linked root 可以删除；保留的 Profile mapping 可以在相同规范目录与 scope 内变更 version 或 declarer。Installation mapping 保持固定，不允许新的 local override 覆盖已发布 mapping。保留项的目录移动要求重启。删除后的 linked root 可以在另一目标重新加入，但保留链接的目标变化仍是原生重启边界。

Profile 重组在旧解析代仍发布时停止已丢弃的最上层行和组，等待清理，再发布 successor 并重组新配置。发布失败时恢复原行结构与顺序，保留原始错误。清理未确认时不进行可能重叠的恢复。新选择 bundle 的行 ID 加入 required 集合，沿用既有审计规则忽略缺失和停用行；其他未改变的历史非活动条目仍作为 warning。依赖可用后，失败的 import 可以重试。

标准 CLR 在线升级路径是先移除再安装。移除仅检查已加载导出，不导入非活动模块。直接 Fiber 或被移除 bundle 之外的条目会在持久化修改前以 `unmanaged-plugin` 拒绝。所属行停止后，工具链注销导出并解除包映射，session 发布删除；重新安装取得新的模块身份与依赖闭包。停用安装会发布映射而不激活条目。激活失败保留已保存的安装和选择，供修复、移除与重试。

稳定制品保持不可变。同版本重装只有在 receipt 与身份校验通过，且准备结果中每个相对文件名与 hash 均一致时，才复用已有目录。有效但不同的文件树返回 `package-moved`；不完整或被篡改的 receipt 仍属于完整性失败。DLL、依赖、资源和生成的 scaffold 文件均不排除。新 SDK 准备关闭 scaffold 调试输出，使相同准备可以得到相同内容；历史产物存在差异时仍须换精确版本或显式离线删除。保留文件继续供已有 delegate、Worker 与其他进程使用。

直接更新已安装身份仍保存独立版本等待重启，当前路由不变。这保留原生不可变代际适配；候选管理器也要求先移除再进行 HMR 重装。不移植 Node 原地写包、私有 ESM/CJS 缓存清除和文件系统 marker clock。宿主显式拥有的开发模块仍经 S3 replacement callback 和宿主依赖图处理。Profile refresh 不推断 CLR 反向依赖图，也不热替换 installation-owned 宿主程序集。Native AOT 代码变化仍需重新发布。

公开 API 变化：`ProfileSession.RefreshAsync` 现在返回 `Task<ProfileReloadResult>`，包含非活动条目 warning、待处理模块身份和 Profile 级重启原因。`ConfigurationChange`、`PackageChange`、`PluginManagerChange` 增加可空 `RestartReason`，标准生成客户端从同一声明模型取得该字段。`DeploymentRestartRequiredException` 公开 `Reason`。CLR 与包工具链公开 `GetLoadedModulesAsync`；保留已加载模块的包装器必须转发。原因值为 `no-hmr`、`package-moved`、`host-module`、`unmanaged-plugin`、`module-comparison`，全局原因优先于待处理模块原因。当前原生 Profile 拒绝使用全局原因，不构造 Node 风格的待处理模块列表。`no-hmr` 表示没有协调生命周期的所有者，不表示关闭自动文件观测。调用者需为 refresh 返回类型及 wire schema 的变化重新编译。见[验证](validation.zh.md)和[制品所有权](clr-artifacts.zh.md)。

## 候选模块身份切片，2026-10-22

S3 依据固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317`，尤其是 [HMR dispatch 测试](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/boot/hmr/tests/package-manifest-dispatch.spec.ts)，与 S1/S2 一起作为限定范围的例外。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`，历史原始测试清单不变。包热重装、resolver 发布与 typed restart result 属于 S4。

Loader 在 `Entry.Module` 保留实际导入及其导出、运行代身份，不再从当前名称映射重建。即使别名共享插件导出，CLR 仍保留各导入身份；准备好的替换列表保留每个已加载请求。Loader 对每个旧 runtime 的 Fiber 只取一次快照，按 Entry 实际导入选择后继，使用最新 Entry raw 配置，并让禁用或未初始化的 Entry 保持不活跃。连续替换不会增加 Fiber。导入准备失败发生在拆卸前；激活失败沿用既有恢复规则，清理全部候选，再恢复全部旧导入和 Fiber。

原生适配保持明确：direct Fiber 存在歧义时在预检阶段拒绝，整个旧组保持活跃，无需先拆卸再恢复。清理失败仍阻止不安全的重叠恢复，并保留既有失败诊断。稳定发布目录、receipt、collectible 所有权与不可变产物规则不变。HMR 显式依赖图仍由宿主管理；仅 Entry 诊断优先使用已加载文件名，避免受已改变或缺失的 locator 影响。没有文件身份的导入沿用 fallback。

这是破坏性公开 API 迁移：`IModuleResolver.ResolveAsync` 及实现返回 `ResolvedModule`；`Entry.Module` 暴露该身份；`ModuleReplacement` 保存前后模块；`Loader.ReplacePluginsAsync` 和两个 CLR 替换回调接收 `IReadOnlyList<ModuleReplacement>`。直接调用方提取 `.Plugin`，自定义 resolver 按运行代缓存模块引用。通用一对一 `Loader.ReplacePluginAsync(IPlugin, IPlugin)` 保留，不保留旧字典回调。消费方需重新编译；管理 DTO、线协议和生成客户端不变。迁移见[作者指南](authoring.zh.md)，证据见[验证记录](validation.zh.md)。

## 候选管理持久化分片，2026-10-22

S2 依据固定候选 `687f282e8625c6b8d11ff58211a90524be2e4317` 的 [patch 编辑器](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/boot/plugin-manager/src/patch.ts)和 [bundle 移除](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/boot/plugin-manager/src/index.ts)。它与下述 S1 同属限定范围的候选例外。正式行为锁及历史源码测试清单保持不变；HMR、resolver 替换和其余候选分片仍是独立工作。

当其余选中 bundle 与 profile patch 已隐含请求的字面值时，`PluginConfigurationOperations.SetPluginEnabledAsync` 删除或不追加只有 `id`/`disabled` 的覆盖。原生 base 配置参与合成，以保留 bundle 向 base-owned group 插入成员的既有含义。home 和启动 overlays 不决定继承值。表达式按原始值比较、不执行；继承的表达式不能等同于字面开关。命名断言、额外设置、entry/key/value 上的 anchor 和值为 alias 的开关都会阻止删除。编辑 alias 值的开关会把该值改为请求的字面值，之后再次调用可删除这个已变成普通开关的条目。删除时保留周围独立注释，包括清空 sequence 的情况。原生实现通过源码拼接保留未改动文本，不采用上游 YAML 重新排版；附着在被删字段上的注释可能随之删除。

继承比较使用既有 profile 输入快照和已解析路径的 patches。完整候选准入及冲突检查仍先于原子保存。拒绝时 patch 不变；保存后激活失败则保留请求的 patch，包括已完成的删除。base 文件不被重写。只接受文件的 `ProfileMaintenance.WriteEnabledAsync` 没有组合上下文，保留显式覆盖行为；继承清理由 profile 管理入口负责。

`BundleConfigurationInfo.Removable` 现在也涵盖已选中、但不是 profile 依赖的名称。`RemovePackageAsync` 撤选并等待其贡献退出后直接返回，不调用 toolchain 卸载、不修改依赖、不将本地包标成已移除。仍存在的本地 bundle 可以再次选中；缺失的已选 bundle 也可撤选。安装层和管理必需 bundle 继续受保护；仍活跃的贡献继续要求先停机或返回 `bundle-in-use`。公开签名、DTO 形状、wire schema 和生成客户端均不变。既有移除结果分别描述 selection 与 installation。本片不增加热重装行为。

## DSH 0.2.1 候选准入分片，2026-10-22

第一片实现候选 DSH `687f282e8625c6b8d11ff58211a90524be2e4317`（0.2.1-rc.2）的插件 requirements 准入分片。正式行为锁仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`（0.2.0-rc.2）。这是明确限定范围的升级例外，不代表已全面兼容 0.2.1。HMR、Remote 授权等候选分片仍是独立工作。

候选的[判定实现](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/boot/app-boot/src/plugin-compatibility.ts)及[原始测试](https://github.com/deepseek-ai/deepseek-harness/blob/687f282e8625c6b8d11ff58211a90524be2e4317/packages/boot/app-boot/tests/plugin-compatibility.spec.ts)决定以下规则：

- 声明 `engines` 时必须是对象。其中存在 `dsh` 成员时，其值必须是字符串，并单独决定兼容性，即使 peers 格式错误或结论相反。其他 engine 成员不影响准入。
- 缺少 `engines.dsh` 时回退到既有 DSH peer 检查。过滤 DSH 包族前校验所有 peer 值均为字符串。Cordis peers 仍不属于 DSH peers。
- Engine 范围使用既有 npm semver 适配并包含预发布版本。空、无效及 `workspace:` engine 范围均不满足；只有 peer 的 `workspace:^`、`workspace:~`、`workspace:*` 按运行版本处理。
- 存在不匹配时，name/version 必须是非空白字符串，并保留原始拼写。精确豁免仍绑定包名、包版本及原始运行版本，包括构建元数据。大小写归一仍仅由既有显式 NuGet 包名适配器负责，不归一版本字符串。构建批准与版本豁免相互独立。

`DshPluginCompatibility.Peers` 改名为 `Requirements`，同时改名位置构造函数及解构参数。字典类型和参数顺序不变，结果包含 `engines.dsh` 或不匹配的 peer 包名。使用 `Peers`、命名参数或序列化属性名的消费者需要迁移并重新编译；不保留别名或第二套结果类型。`EvaluateCompatibility` 及准入方法签名不变。管理 Remote DTO 没有暴露此记录，因此 wire schema 和生成客户端签名不变。既有诊断字符串现在描述未满足的 requirements，包括 `engines.dsh`。

NuGet `cordis.plugin.json` 的 engines 和 peers 在检查元数据及部署的 `package.json` 中原样保留，包含无效值，以便准入拒绝。已知不兼容在 SDK 准备之前被拒绝，禁用安装也不能绕过；准备后的清单再次检查。命名 bundle、导入前入口检查、刷新／reconciliation、重启、精确豁免及撤销共享同一判定。通用 Loader 和未显式配置 DSH runtime 的 Profile 仍不应用此策略。本片保留既有 CLR 元数据定位与准备／发布适配，不增加 Node 包执行，也不改变安装／更新生命周期语义。

豁免文件锁在原有错误码之外，将 macOS `EAGAIN`（35）识别为锁竞争，修正同一精确豁免持久化路径的等待与取消；不扩大其他 macOS I/O 错误的重试范围，也不改变豁免文件格式。字典项对应 JSON 自有字段；JavaScript 原型及隐式安装版本发现没有直接 CLR 对应，宿主继续显式提供 `DshRuntimeIdentity`。原生诊断措辞与异常类型仍属 .NET 适配。

验证修复将既有包进程所有权扩展到 macOS。原生 `posix_spawn` 在独立进程组中创建暂停的主进程；记录组身份后才恢复执行，并回收直属子进程、排空两条输出管道。取消时终止整个组，等待仍存活的成员退出。SDK 成功退出但仍有后代进程时拒绝成功并清理，包括重定向输出或比主进程存活更久的后代。这是进程生命周期管理，不是不可信构建代码的隔离机制：主动逃离进程组不在合同内。与 Linux 一样，macOS 不承诺宿主崩溃后自动终止后代；存活进程组通过保留的运行记录阻止后继准备。缺少组身份的旧 macOS 记录需要显式恢复。不可读取或无法判定的成员状态仍按失败处理，内核 zombie 则已不能执行。不增加公开 API。平台常量和 ABI 对照 macOS SDK 头文件及 [Apple 启动属性文档](https://developer.apple.com/library/archive/documentation/System/Conceptual/ManPages_iPhoneOS/man3/posix_spawnattr_setflags.3.html)核对。

执行证据见[验证记录](validation.zh.md)，其中候选原始测试与正式基线清单分开记录。

## 可独立安装的浏览器作者工具，2026-10-18

作者命令在发布输出前保护本次实际输入闭包。入口在编译前检查，esbuild 输入信息及 CSS 插件的物理路径覆盖每次构建的打包依赖。源码子目录不能被退役后替换为输出目录，也不能混入 watch 代际。这是 .NET port 作者命令及输出发布的保护，不改变固定 DSH Loader 语义，不增加文件系统权威。比较前解析作者与输出路径中的目录别名，尚不存在的输出通过最近的已有父目录解析。这修正 macOS `/var` 与 `/private/var` 边界及等价目录别名。普通 `author/dist` 输出继续支持。

`@cordis-net/client-tools` 包交付既有单 factory 作者构建，不要求 Cordis 源码仓库或 .NET SDK。它沿用 esbuild，使用与固定 DSH 依赖一致的 Lightning CSS 1.32.0。[固定作者构建](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/client/tsdown.client.ts)定义精确 external 请求、CSS Modules、样式所有权标记和 inline CSS 文本。既有 Loader 负责 effect 清理及之后的样式移除。命令不引入另一套 Loader、资源服务器或动态 chunk 协议；相对 CSS URL 和 import 保持固定 transform 行为。参见[安装后用法](../clients/tools/README.zh.md)。

已安装归档构建了仓外 TSX 作者包，Windows 真实浏览器验证了实际样式、外部对象身份、撤销、重新加载和最终清理。首次测试清单不完整被拒绝，失败记录保留后修正夹具。此前 ce8c17b CI 的 Windows/Linux runtime 及同批作者消费通过，但 macOS 在作者目录保护断言处停止，尚未启动浏览器。修正候选 `83dad802` 随后在 [CI 39117111127](https://github.com/gentt1024/Cordis.NET/actions/runs/39117111127) 通过全部 15 项作业，包括 Windows、Ubuntu、macOS 消费同一 Linux 产出批次。实际 checkout 为 PR 临时合并 `3693949d`；后续发行版本仍须验证自己的 tag 源码。已发布 alpha.5.1 不包含此作者工具。

## 声明身份的跨平台一致性，2026-10-18

保存的 C# 声明模型属于已有 .NET 作者工具适配，不是 DSH wire 格式。其序列化器现在固定 LF，不再继承操作系统换行。此前 Linux 生成的模型即使作者事实完全相同，也可能被未修改的 Windows compiler 拒绝身份校验。本修正恢复跨平台制品消费，不改变固定 DSH 基线、Remote 协议、声明结构或严格身份校验。已有 LF 身份继续有效，使用 Windows 默认换行的开发模型需要重新提取。已发布 alpha.5.1 字节保持原样，其内置 compiler 不包含此修正。

## 数值枚举投影，2026-10-16

赋给可空枚举的非 null 常量保留底层枚举身份，null 与可空 `default` 仍为 null。这适用于作者绑定、参数默认值和普通／引用数据初始化器。独立发射器使用生成后的枚举类型，不携带作者程序集依赖。

固定 DSH 的 analyzer 保留枚举声明，但其 [Zod emitter](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator/src/emitter.ts)明确拒绝枚举投影。因此数值 CLR 枚举支持属于 .NET 平台适配，不能称为上游已有的 Schema 保证。现有共同模型已经保存底层类型及成员常量；独立 Remote、事件和普通数据投影复用这些事实，不加载提供者程序集，也不新增保存格式。

默认 [.NET 10 STJ 枚举转换器](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Converters/Value/EnumConverter.cs)读写底层整数，包括未命名值，并导出 integer Schema。保存模型 Web 输出通过共享客户端 emitter 使用同样的 Schema，不添加成员白名单。生成的 .NET 声明保留名称、常量、底层宽度及 Flags；原生反序列化检查范围。可空枚举保留 null 分支，枚举默认值引用生成后的类型。保存模型 Web 投影仍拒绝字符串枚举策略。独立 .NET 客户端支持下文所述的已知默认泛型 STJ 字符串枚举 converter；未知作者 converter 仍拒绝。JavaScript number 的既有精度限制不变，本次不引入无损 64 位传输。

## Linux 包进程完成边界，2026-10-16

固定 DSH 管理器等待被中断的工具退出后才报告取消。既有 .NET SDK 进程所有者还要求终止与管道排空。在 Linux 上，已终止的后代等待其他父进程回收时，进程组仍可能存在。所有者现在先核对 `/proc` 中的组状态，再判断这种存在是否代表执行尚未结束。仅排除已观测到、没有剩余线程的死亡成员；活进程、无法读取的状态及未确认的管道排空仍阻止成功。同一检查保护运行保留记录的接管。它不回收其他所有者的子进程，不把清理失败改成取消成功，也不新增进程监督器。参见 [Linux 进程状态](https://man7.org/linux/man-pages/man5/proc_pid_stat.5.html)与 [wait 语义](https://man7.org/linux/man-pages/man2/waitpid.2.html)。

## Group 撤回的持久化修正，2026-10-15

固定 DSH Loader 同样存在 group 撤回缺陷：空 base 加两个 bundle 补丁，撤下 group 并保留另一 bundle 的条目，会将有效条目写入 base。在 `639ed015397290b3745d163aafe02ffee4aa3f84` 上的定向执行也复现了 base 自有 host group 布局，重开后出现重复 retained 条目。这是共享缺陷，不能说成上游已经保证源文件不变。.NET 修正在将拥有的子插件清理判为自停前，检查条目及祖先已有的撤回意图。不重排 Fiber 清理，不抑制显式 Include 写入，不清空写队列，也不改变真正自停的持久化。标准 bundle 撤回持久化 Profile 选择，不将组合树复制到基础源文件。

Bundle 保护和激活要求对捕获的 base 与有序候选层调用现有补丁组合器，再选取来自该 bundle 补丁源的可达条目对象。外部容器保留原位置及后续 config 整体替换语义。这是对现有配置事实的脱离运行树审计，不新增来源注册表或第二套补丁算法。声明行仍是独立的目录视图。

## 输出合同复用，2026-10-14

固定 DSH 的公开 `InvocationDescriptor` 提供结果 codec，其 strict `create()` 提供可执行 parse 的 Schema（[protocol types](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/protocol/src/types.ts)）。这支持结果合同的组合复用，不直接规定 .NET 的 JSON Schema 属性。本库的平台适配以非 virtual 的 `TypertCodec.ResultSchema` 公开现有输出投影，内部方向分派仍为内部实现。STJ 的输入／输出差异要求它与 `Schema` 区分。消费者复用输出解码及客户端生成的同一投影，自行决定校验时机。不新增外部 override 扩展、Schema 服务、HTTP 端点或 Gateway 校验政策。投影限制见[作者指南](authoring.zh.md)。

输出 JSON 的字段存在性在原生读取之前由 ResultSchema 校验。输出 metadata modifier 只为存在 setter 的成员保留必填写入，只有 getter 的值遵循作者的原生 STJ 构造方式。构造参数的必填性仍由输出投影决定，条件省略的输出不会重新获得输入侧的必填要求。原生输入 metadata 和输出 Schema 生成不变。这是纠正此前 adapter 建立的 required 但不可反序列化 metadata，不是绕过 STJ 对只读 DTO 的固有限制。

生成的 TypeScript 模块公开只读 `resultCodecs[endpoint].parse(value)`，校验解码后的输出值。共享 emitter 将现有输出投影与实际序列化 metadata 编译成判定函数；运行时 descriptor 导出和受支持的保存模型投影使用同一个 emitter。只有实际的默认 `byte[]` metadata 映射为 `Uint8Array`，base64 注解或自定义 converter 不构成附件身份。`ResultSchema` 继续描述附件提取前的 JSON，与解码后的可执行合同各有明确表示。

解码值的表示检查与 Schema 布尔约束分别执行。固定 DSH 的[生成器](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator/src/emitter.ts) 用 `z.intersection` 组合类型合同，用 `z.instanceof(Uint8Array)` 表达字节；.NET emitter 在 metadata 适配后的值上表达既有 JSON Schema 约束。`allOf` 中冗余的 `not: { type: "null" }` 不得对 metadata 已确定的字节重新施加纯 JSON 限制，嵌套字节同样如此。JSON-only 位置仍拒绝 typed array，组合约束不扩大其表示范围。这修正了结果 emitter 的判定组合，不改变固定 DSH 的 strict decoder 政策或公开 codec 合同。

固定 DSH 的[客户端调用](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/client/index.ts) 在 Connection 还原和取消检查后，执行已提供的 strict 结果 decoder。本 .NET port 的生成式 TypeScript unary 调用同样只对带二进制 metadata 的结果自动执行解码值 codec。不匹配转为 `gateway/internal`，保留本地 `TypeError` cause。Remote 错误、取消与撤销维持既有顺序及结果。普通 JSON 结果保留既有自动校验政策，应用可显式调用导出的 codec；流条目政策不变。不新增附件还原前的分阶段接受 API，固定 DSH 也先等待 Connection 还原。接受、未知结果与回执恢复仍由应用负责。支持的投影和独立用法见[作者指南](authoring.zh.md)。

## scoped .NET caller 适配，2026-10-11

固定生成器仅在 direct 方法恰好一个 lookup 参数，且存在同 key、同 wire 类型的 `TypertContextMap` 条目时推导可选 scope（[analyzer](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator/src/analyzer.ts#L1177)）。接收方 Context 调用是另一种语义：身份在 Service 准入及 lookup 解析之前选择 Host Context。[Client Gateway](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/client/index.ts#L390) 经当前 Client adapter 取得 scoped 身份。

.NET 作者使用程序集级 `TypertContext(key, typeof(Wire))` 声明代替 TypeScript 声明合并。提取器把编译及其程序集引用中的声明保存在唯一当前模型内；Host binding 与 caller 发射消费同一事实。接收方 Context 生成使用声明的 wire 类型，替换此前硬编码的 string。adapter 仍拥有实际身份及 Context 解析职责，属性不注册运行对象。缺少这些声明事实的旧制品须重新提取，不保留并行 reader 或新增格式代际。

生成的 `ForContext(callerContext, registry)` 视图对应上下文关联的 Service 访问。它显式选择 scoped 签名；父客户端提供 direct 签名，不复刻 JavaScript 在参数列表之间的动态回退。接收方 scoped 方法不暴露公开的原始身份参数入口。direct lookup-scoped 方法保留普通身份参数，并获得省去该参数的视图方法。没有匹配 Context 声明或含多个 lookup 参数时仍为 direct。scoped 调用每次进入已有 Cordis 执行域读取当前 adapter 和身份，不缓存两者，不制造 Context，也不拥有借入的 Context／registry／HttpClient。父客户端释放及现有流取消继续沿用当前所有者。

上游缺失哨兵对应 `Undefined.Value`。引用类型身份的显式 null 保留在内部请求封套中，由 Host codec 校验，不合并为缺失，也不提前变成 STJ 序列化失败。值类型身份在进入强类型调用之前须匹配声明的 CLR 类型。Host lookup 对象类型仍保留在声明分析中；独立 caller 只投影其 wire 身份类型。本段说明原 .NET scoped 切片。后续 owned Context、Peer／uplink、事件与浏览器工作在[验证记录](validation.zh.md)中各有证据；当前支持与拒绝形状见[后端支持表](development.zh.md#alpha5-独立后端处置)，剩余具名集成见[剩余支持边界](development.zh.md#剩余支持边界)。[作者用法](authoring.zh.md)说明当前消费方式。

Cordis.NET 以[上游文档](upstream.zh.md)所述、DeepSeek Harness 锁定的 vendored Cordis 行为为目标。兼容性按可观察契约评估，不能只看包名或测试数量。

## 上游忠实性要求

保存模型后端按框架身份识别 `System.Guid`，将其作为 .NET 标量适配。固定 DSH [Schema emitter](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/generator/src/emitter.ts) 区分标准类型与作者声明，并没有定义 CLR Guid API。平台依据是默认 STJ [Guid converter](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Text.Json/src/System/Text/Json/Serialization/Converters/Value/GuidConverter.cs)，其读写 JSON string，Schema 为 string/uuid。共同事实保留真实程序集／类型身份；数据、方法和事件投影共享框架分类，独立 C# 保留 Guid，Web 使用 string。Guid? 及支持集合的值遵循既有 null／presence 规则。实际值准入继续由 Host STJ 负责，不增加 UUID 版本／变体校验器。独立后端仍拒绝自定义 converter，外部 DTO 仍需源事实；本项不扩入 Guid 字典键、ID wrapper 或其他 BCL 类型。

直接 Gateway 调用遵循固定 `packages/api/gateway/src/index.ts` 的 `operatorPeer()`：载体已准入的 Peer 优先；否则读取当前 `connection` 服务，没有 Connection 时才延迟创建并复用 Gateway Context 拥有的 Peer。原生宿主构造 `TypertHostConnection` 后，通过 `owner.Provide("connection", connection)` 使用普通 Cordis 服务所有权发布，并共同负责撤销注册和释放 Connection。流在开始时确定 Peer，在迭代和清理期间保持该身份；后续调用会观察替换或撤销。原先直接调用没有 Peer 是移植缺口，不是平台适配。这项修正不代表剩余丰富 Peer／scope-targeting 工作流完成。

普通数据后端通过既有共同声明组合与共享框架投影支持 `Dictionary<string,T>`、`IDictionary<string,T>` 和 `IReadOnlyDictionary<string,T>`。JSON 对象键保持字符串，不推断数值键转换。值保留声明的空性、默认值和引用声明身份。这是有界的静态 DTO／STJ 投影，不执行泛型作者代码，也不代表任意 CLR 集合均已支持。

Cordis.NET 是固定 DSH 基线及仓库已确认通用应用基础设施的忠实、地道的 .NET port。仓库历次试验设计不构成另一套独立框架规范，也不自动形成长期兼容义务。规范依据是固定上游行为、已确认的仓库范围，以及有具体平台需要支持的 .NET 适配。

已发布 alpha.5.2 是仓库维护的公共使用基线。组织整理保留其 API、生成合同和包布局。真实错误的修正必须说明受影响消费者及必要迁移，预览版身份本身不构成破坏用法的理由。

当前代码、API 快照、生成客户端和测试都应据此校正。签名曾进入 main、随旧版本公开或通过旧测试，不能单独成为保留错误移植的理由。确认错误后，应直接修正并迁移消费者；兼容路径必须有独立的消费职责或兼容承诺依据。持久数据与 Git 历史的保全应单独处理，不等同于保留被取代的运行接口。这一原则不要求逐行翻译 TypeScript，也不授权重写已经满足合同的机制。

上游忠实性是项目兼容性要求。评估改动时，应以 `upstream.lock.json` 固定的 DSH 修订、其真实 production caller 及可观察行为为依据。核对时序、所有权、生命周期、失败和恢复边界，包括调用方如何使用底层机制。任何差异都必须明确归类为必要的平台适配或显式项目决策，并提供针对性证据。仅证明 wrapper 内部自洽的绿测，不能单独作为上游忠实性的证明。

## 状态词汇

- **已实现：** .NET 行为已经存在。
- **已执行：** 已运行具名 .NET 测试、上游源码测试或配对场景。
- **断言已审阅：** 已逐项匹配上游 setup、helper 与断言，或记录明确适配。
- **不支持：** 行为位于产品或平台边界之外。

不可变的历史清单针对 DSH `ddefc45fbc7f8e46dd73185e68295696d1297887`，包含 631 个上游候选实例，不代表新基线的断言闭合状态。本次升级的范围、行为映射与平台适配见[升级矩阵](upgrade-0.2.0-rc.2.zh.md)。目前 322 项记录为实现完成，其中 35 项完成了断言审阅，287 项尚未完成全部断言审阅；其余 309 项明确为未完成，通常涉及 Cordis.NET 范围之外的 JavaScript 或 DeepSeek Harness 产品行为。这些数字不代表兼容率。

## 部署边界

可选浏览器模块 bootstrap 将外壳持有的 `staticModules` 和可选 `loadBundle` 传给固定 DSH 模块系统。原生目录捕获接受相同的额外精确请求名，SDK 默认模块继续可用。包根不会自动供应任意子路径。外壳持有对象与类型，Cordis 持有插件副作用；默认与自定义 transport 都保留既有同源 revision 边界。这是恢复上游 bootstrap 接线，不新增模块注册机制或 npm 解析器，见[开发说明](development.zh.md#外壳持有的客户端模块)。

浏览器启动复用固定 web 激活审计；后续 sync 保留诊断/重试语义。生成通知恢复复用固定连接控制器，以数值时序适配替代 Schemastery 运行时。暂时断线保留插件，显式 close 撤销插件。CLI 参数、就绪和有界退出复用 Composition、ProfileSession 与 Generic Host。这些修正恢复上游装配职责，不改变 Core Pending 语义。

浏览器 `ClientEntries` 对固定 DSH 的 `packages/client/modules/src/client/entries.ts` 作了有限的本地修正。固定源码在新代码启动成功后才记录替换 revision；新代码失败时仍保留旧 revision，调用者显式请求旧 graph 却可能重试新代码。修正后的记录随模块缓存失效，预取成功之前旧 Fiber 仍可用；新 Fiber 建立后、等待激活之前，仅为当前 generation 记录实际代码。显式 sync 和同目标 retry 因而选择对应代码，继续复用既有 generation、队列、依赖重挂与释放机制。不新增自动回滚、历史存储或业务副作用恢复。浏览器 provenance 分别记录原上游身份和本地文件摘要，正常构建检查这些身份；未修改文件继续保留固定摘要。

| 路径 | 支持的行为 | 边界 |
|---|---|---|
| 静态 Core 与 Composition | JIT 与 Native AOT；静态模块注册、生命周期、配置、profile 与部分扩展 | 新插件代码需要重新发布应用 |
| CLR 适配器 | 托管程序集的运行时加载、替换、回滚与协作式卸载 | 仅普通运行时；保留的 CLR 引用可能阻止回收 |
| JavaScript 适配器 | 通过 Jint 实际求值 `!!js`，并提供受控上下文桥接 | 只处理受信任配置；不承诺 Node 模块环境、沙箱或 Native AOT |

Cordis.NET 不执行任意服务端 TypeScript 插件。JavaScript 原型、抛出的 primitive、循环对象图和 Node 解析不属于通用平台兼容性承诺。应用专属的业务行为和领域政策由消费应用承担。可复用客户端/UI、诊断和安装机制仍在仓库范围内，其协议及 Node 实现需由 Core 之上的层明确适配。历史盘点记录当时的覆盖，不定义仓库永久排除项；范围内缺口不代表当前可用功能。

`ConfigObject<T>` 是 Composition 中组合既有 `ConfigSchema<T>` 的作者适配，不是第二套 validator。它将显式、完整的数据字段投影组合为 live 绑定、严格普通比较及完整保存转换。默认值、校验、raw 往返和完整字段覆盖仍由作者负责。此 API 需要 `0.2.0-alpha.3`或更高版本，未包含在已发布的 `0.2.0-alpha.1` 包中。

字段编辑复用固定 ConfigEditor 的职责：所属层定位、正常 hook/校验、完整 raw 用户覆盖、overlay 拒绝、持久写入和协作恢复。为保留无关注释，原生编辑可保留 YAML flow 根。revision 使用原生内容/激活哈希；不能无损往返的 `Undefined` SET 会被拒绝，不改变运行时 sentinel 语义。部署需重启时在保存前拒绝。Settings 选择已声明的 live 数据，包含嵌套对象和集合，并在提交时同样强制宿主选择／脱敏政策。用户来源 presence 纳入唯一的同源 inserted row，是明确的原生适配，不按 effective 值比较推断。有序 SET/reset 请求在写入前校验一个完整候选。显式元数据支持独立的 Schemastery 与 JSON Schema 投影；不能推断任意 validator 的变换，投影诊断仍是合同的一部分。Core 保存注解而不解释表单政策。独立审查与组合验证结果见[验证记录](validation.zh.md)。

客户端制品通过部署包路由复用 `dsh.client.platform=web` 与 `exports['./client']`，真实路径检查覆盖目录链接。原 Probes 消费者保留自包含 ESM 与轮询；当前源码另提供 `ClientModuleCatalog` 及可选 TypeScript 包，复用固定 Cordis/Loader、module entries 和 SlotCore 机制。显式声明外部包的模块编译成 lazy factory 注册，不承诺任意 npm 解析。生成式管理使用共享 Connection 的固定 unary 封套与文本 WebSocket mux carrier，具有宿主授权、代际约束及活动安装查询。该 carrier 不证明完整固定 Typert 线协议兼容。源码测试、浏览器交互、CLR 执行与包消费仍是分开的证据。这些新增能力不改变正式行为锁或已发布批次。

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


可选 NuGet 工具链将批准绑定到已检查的根归档，SDK 准备仅使用宿主选定的来源。批准覆盖整次 MSBuild 调用，包括依赖 targets；它不是逐依赖哈希批准系统，也不是沙箱。Windows 工具进程使用创建时绑定 Job（Windows 10 / Windows Server 2016 及以后），记录所有权后恢复执行，同时确认进程树及管道排空。Linux 使用独立进程组。取消会停止该组；宿主异常退出可能留下仍运行的进程组。后继操作拒绝未解决的运行记录，直到明确停止旧组并确认终止。保留或无法判定的记录需要明确恢复；诊断保留真实失败阶段和残留目录。更新已安装的 Profile 自有 NuGet 包时，工具链准备并准入新版本，将完整输出移动到独立版本目录，并保存下一次启动使用的版本及选择。操作返回 `restart-required`，当前 resolver 路由和插件 fiber 继续使用旧版本，与固定 DSH 的已安装包边界一致。已安装身份的同版本重复请求及安装目录自有包的替换仍被拒绝。已有目标目录绝不覆盖；准备、准入及保存前检测到的发布冲突保留原先保存的版本并报告残留输出。这不是文件系统事务，也不自动清理旧版本。已移除身份可通过上述 S4 工作流重装；静态 AOT 应用通过重新发布改变代码。

安装检查已捕获的包声明在 SDK 准备前准入，停用安装也不例外。`PackageInspection.ManifestJson` 是与 archive hash 同源的不可变快照；无法提前检查声明的适配器可以省略它，保留准备后 manifest 准入。.NET 工具链始终捕获根包声明，在准备前及 restore 后检查根包 hash，并保留准备后 manifest 准入。构建批准不授予版本豁免。

DSH-enabled NuGet 宿主应显式将 `ProfileLaunch.CompatibilityPackageName` 设置为 `DotnetPluginToolchain.NormalizeCompatibilityPackageName`，将 `ManifestLocator` 设置为该工具链的 `LocateManifest`。适配只改变 grant key 中的名称；包/runtime 精确版本、manifest 拼写和已保存选择不变。Scoped npm 名称及默认 DSH policy 保留原规则。管理入口在授权前只解析一次豁免身份，向持久化传递相同的规范值；直接调用者先使用 `ResolveVersionExemptionIdentity`，再调用 `SetVersionExemptionAsync`。授权变化参与既有 DSH session 刷新。授权数据不可读时不授权、不可覆写，也不阻止启动。

管理授权使用生成方法实际解析的资源：插件启停使用 `id`，bundle 启停/移除使用 `name`，包检查/安装使用 `request.name`，等待/取消使用 `requestId`，豁免使用规范的 `packageVersion`。原生配置使用 `entryId`，客户端制品使用路由包名。标准 Settings 与通知使用生成 Remote 路线及宿主 endpoint 授权。无关 query 字段不能替代目标。清单与通知流不指向单个资源。[管理迁移说明](management-remote-migration.zh.md)记录已退出的手写 HTTP 与 SSE 路线。

原生管理 `Changed` 事件逐个隔离同步观察者异常，记录日志，既不替换已完成操作的结果，也不阻止后续观察者。它不改变 Core 事件语义。启停与 bundle 选择先持久化、再协调；应用失败仍可能保留已保存文件。字段编辑恢复继续遵循其独立合同。

原生管理寻址先纳入绑定 Include 的基础文件声明，再应用 bundle 和 Profile 用户补丁。因此，基础文件定义的组可以接收 bundle 插入的子项，并以其现有 patch ID 执行启停。这是通用 .NET 宿主适配，不能宣称固定 DSH PluginManager 已有相同行为：其 `listPlugins()` 使用 `composeEntries([readProfilePatches(...)])` 从空根组合。固定上游 Include 本身会对解析后的基础数据应用补丁；.NET 管理沿用该补丁算法和实际基础源，不刷新运行时或求值表达式，也不对已经组合的运行树重复应用补丁。基础文件缺失或格式错误会使读取失败。既有树所有者、唯一 ID、模块名及管理保护检查继续保留，修改队列、持久化和协调流程不变。Home／启动 overlays 仍保持后置优先级，可能覆盖已保存的启停值，但不据此建立 Profile 管理地址。

管理读取复用捕获的 Profile 输入，并检查这些源未发生变化。Preview 将原始基础数据与全部有序层组合，包括 Home 和启动 overlays。Bundle 条目目录描述该 bundle 的插入声明，包括以基础文件中的组为目标的 insert。保护与必需条目审计则通过 `EntryPatches.Apply` 组合原始基础数据和全部有序候选层，再按该 bundle 补丁源的对象身份筛选最终可达条目。外部容器保留原位置及后续 config 整体替换语义；不会通过与初始声明取并集保留已移除子项，也不会把宿主声明归给 bundle。启用与新安装审计共用这一内部选择，详见顶部的 [group 撤回修正](#group-撤回的持久化修正2026-10-15)。这些视图保留仅组合补丁的 `Profiles.Compose`，不新增公共地址 API。

## 证据

保存模型的 DTO 和 client 生成也在 MSBuild 设计时构建的 `CoreCompile` 前执行，使用相同共同模型后端和显式引用模型，通过独立输出目录避免语言服务生成写入普通 build/pack 产物。这是 .NET 编译输入适配：STJ 必须在输入 compilation 中看到生成声明；不增加第二套模型权威或回退解析器。打包所需的源码模型提取仍属于普通构建，source generator 继续分析实时作者 compilation。MSBuild 语言服务输入检查与实际 IDE、UI 平台验收分别记录。

配置 watch 注册从最近存在的规范父目录开始，与固定 HMR watcher 一致。目标父目录已存在时，原生监听不包含子目录，对应 DSH depth 为零；无关 SDK 工作子目录不应进入该 watch 的事件缓冲或元数据解析。祖先缺失时，FileSystemWatcher 需要递归监听：布尔深度无法精确表达 DSH 的有限正整数深度，这一较宽的原生边界明确保留。原生缓冲溢出保留原 warning/error，再通过正常串行队列刷新，补读已丢通知。这是 FileSystemWatcher 的必要适配；固定 DSH 对运行期 watcher 错误只警告。其他原生错误保留诊断，已关闭 watch 不接受新刷新。

配置 watch 事件首先匹配规范化事件路径，与固定 HMR watcher 一致。匹配的修改、删除及祖先目录创建无需读取变化文件的属性，直接排队刷新。原生别名解析保留为后备路径；失败通过现有 warning/error 通道报告原异常，后续事件仍可恢复。rename 的两个路径分别处理。初始注册仍拒绝不可访问路径，已关闭的 watch 忽略排队事件。

Profile 安装现在对已失效的输入返回 `profile-conflict`，不再用 Prepare 前读取的 manifest 覆盖后续编辑。这是原生适配：固定 DSH 成功路径在 `selectBundle` 重读，但没有提供完整产品候选批准合同。两者都不保证能对忽略协作锁的任意编辑器执行条件替换。

`PluginConfigurationOperations.AdmitProfileAsync` 接收库持有的不可变 manifest 文本和原始根配置候选，组合仍使用现有 Profile/Include 逻辑。修改分离的 composition 视图不会改变保存候选。安装与 bundle 选择在发布/持久化前准入，并通过 `ProfileSession` 应用该候选；移除分别准入其持久步骤，其中删除依赖候选在工具解除运行时映射前准入。CLR 制品保留并单独报告，直至显式离线删除。拒绝第二步时，包保持已安装、已取消选择。重复选择仍在重新应用前准入。普通消费可以省略准入。仅使用旧 `ReconcileAsync` 的宿主在未启用产品准入时保留原回调；启用产品准入时必须支持候选 reconciliation，不能静默退回重新读盘组合。

单条启停将拟写入的 user patch 纳入完整候选，使用同一准入边界。拒绝时不保存补丁、不执行 reconciliation；批准后先核对捕获输入，再保存并应用该候选。既有补丁编辑器保留注释和表达式语法，manifest 与 base 不变。这是对既有 .NET 产品准入适配的覆盖补齐：固定 DSH 的 `PluginManager.setPluginEnabled` 先写入再 reload，并明确在激活失败后保留已保存修改。准入拒绝与后续激活失败分别处理；保存后的激活失败仍保留请求补丁。不提供批量启停或自动回滚合同。

即使 owner 配置比较相等，捕获的基础数据变化也会进入既有 Include 更新；未变化条目保留原 fiber。候选回调与赋值时的旧回调关联。宿主随后只替换 `ReconcileAsync` 时，未启用准入的操作仍执行该定制；启用准入则拒绝，直到宿主明确为此接线设置候选应用回调。

读集记录完整 Profile manifest、基础配置、选中 bundle 的 manifest/patch、Profile/Home patch、兼容输入、启动 overlays 及部署映射。Prepare 后记录准备目录中的相对文件名和内容哈希。声明的 `PublicationDirectory` 允许同一目录内容不变地移动，并增加新身份自己的本地映射。更新时保留已有运行时映射，候选则使用准备好的新版本组合；其他映射与来源保持不变。已批准 manifest 的替换是另一项计划内写入。发布后也区分这些变化与冲突。首次安装发布后但依赖尚未保存的冲突返回 installed=false、selected=false、failed 及保留的部署目录；更晚的应用失败可以保留已保存选择。不增加自动合并、重放或整个运行世界回滚，既有取消、安装等待和工具链归属继续有效。

在线 metadata 编辑使用同一所有者的 `ReadProfileAsync` 与 `SaveProfileMetadataAsync`。保存先等待已有 mutation，再核对原修订；不能修改管理入口拥有的 dependencies 或 `dsh` 政策/选择。宿主决定怎样呈现等待、草稿和冲突，不代替产品同意缩减作者体验。低层 `PackageManifest.Write` 和静态维护函数仍要求调用者排除并发，或在离线 Profile 使用。所有受支持的并发写者必须遵守同一 Profile 锁协议和队列顺序。指纹检查检测已观察到的变化，不能关闭最后比较到 rename 之间非协作写者的竞态。产品自身政策输入和后续运行时/插件副作用不构成新的全局事务。后续协作请求独立于本次安装提交，最终 Profile 可以合法地不同。

最近完成的运行汇总见[验证记录](validation.zh.md)。`docs/upstream-tests.json` 是不可变候选清单；`docs/test-map.json` 保存当前处置；`docs/scenario-map.json` 记录差分场景。上游源码执行、.NET 测试、配对 trace 与人工断言审阅仍是彼此独立的证据类别。

## 2026-10-09 模块导出与 Typert 续建

[范围续账](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/development.zh.md#2026-10-09-应用基础设施范围续账) 将起始 HEAD 的遗漏与本次实施分别记录。行为依据仍是 DSH `639ed015397290b3745d163aafe02ffee4aa3f84`。历史完成记录与固定基线保持不变。本次源码续建不声明已发布包批次。

### CLR 模块身份与所有权

固定 Loader 的模块导入和默认导出归一化独立于服务 `Provide` 选择插件入口。原生适配使用显式 CLR 入口类型：既有包根 `assembly`/`entryType` 元数据继续有效，可选 `exports` 将包子路径映射到同一程序集中的入口类型。不增加 Core 导出表，也不要求单入口插件声明空表。

归一化 bundle 目录相同的请求共享物理加载模式、程序集身份与可收集加载上下文。稳定加载保留原文件；显式 ShadowCopy 共享一个开发副本。同一已加载 bundle 的入口混用加载模式时，在导入新增入口之前拒绝。Resolver 租约按请求拥有，生命周期与配置仍按 Loader Fiber 拥有。最后一个租约移除时请求卸载；保留引用与 unload observer 失败可能延迟收集或 shadow 删除。Core、Clr、Composition 默认作为共享合同程序集；其他合同需要宿主显式选择。无关默认上下文程序集不能满足插件私有依赖。

`ClrModuleResolver.ReplaceAsync` 的整组重载要求提供 bundle 的每个已登记请求，包括别名及尚未加载的导出。它准备单一候选代际并共同提交路由。`Loader.ReplacePluginsAsync` 复用既有 Fiber 稳定与恢复语义，Pending 仍合法。回调拥有 teardown 与恢复。原子路由发布不表示候选副作用回滚或产品全局事务。合同 descriptor 不新增统一替换准入算法。

### 原生 Typert 合同与传输

本次续建提供 Roslyn 作者生成器、编译器无关 descriptor、System.Text.Json 元数据 codec 与 Schema、Fiber 拥有的 registry、显式 artifact resolver 的 Loader 集成、Gateway，以及生成的 TypeScript 模块/声明制品。生成器在 `Cordis.NET.Composition` 的 analyzer 目录内交付。这覆盖显式声明的原生 Remote 边界，不等同于固定 TypeScript 编译器的完整源类型图。显式 JSON 元数据及其命名、成员空性与必需字段选项定义原生数据合同。生成 codec 通过 `TypertCodec.CreateDeclared` 补入运行时丢失的声明空性，区分嵌套与递归使用位置。结果保留固定调用语义，不增加 Schema 准入。公开声明与包消费者见[作者指南](authoring.zh.md#2026-10-09-模块导出与生成式-remote-合同)。

`IClrTypertModule` 从插件工厂所在的同一已加载 CLR bundle 导出贡献。Typert 导入按 owner Fiber 激活期缓存。在动态模块代际边界，候选准备完成后、退役 provider 前，暂停受影响的精确 Loader 请求；仅在路由提交或确认旧 provider 恢复后恢复。暂停撤销相应登记、失效制品缓存并脱离旧导入；其他贡献者保持登记身份和 owner。旧导入晚到也不能登记进恢复后的新代。这样释放 loader 对旧生成 delegate 与 CLR 序列化类型的引用；仅有类型名相同，不能让旧贡献适配替换后的新程序集。登记有效性同时阻止撤销后的旧调用。Registry 元数据不代替 Cordis 服务权威或产品政策。

Gateway 支持直接调用、登记的 Context 选择/对象 lookup、Remote 错误、协作取消及下行流。ASP.NET 使用共享 Host Connection、固定 unary 封套、文本 WebSocket mux 和 unary multipart 结果。生成客户端借用共享 Connection；`mountRemote` 沿既有 owner 与共享 root namespace 登记互不重叠的方法。当前本地实现尚未闭环反向 Context、owned value 和完整事件语义。

Host unary 取消遵循固定调用边界：成功业务不会仅因传输信号已取消而被拒绝，已取消信号下的业务失败则成为 `gateway/cancelled`。Downlink 读取与信号竞争，完成清理后才暴露失败。原生枚举器必须先结束未完成的读取再释放；迟到读取错误会被观察，不替换取消结果，释放错误仍是清理失败。固定 Host 和此适配都不承诺强制终止始终不结束的业务或清理。提供者/定义撤销后的检查属于单独的原生代际适配，不是上游活动调用中止机制。

客户端安装串行修改 namespace，并在该队列之外等待同名 provider 退役，使依赖清理能继续。重复方法在发布前拒绝。同步 `internal/service` observer 在发布时抛错，保留固定 Cordis provider 的失败行为：即使挂载拒绝，仍可能留下已提供的 namespace。适配层不承诺任意 observer 的回滚；应先修复该 observer 并退役其 owner，再重试。

### 未闭合范围与证据边界

后续质量修复在共享 CLR bundle 内保留每个主程序集的依赖根并拒绝冲突的私有 binary，避免只依赖第一入口的 resolver 或 CLR 已加载程序集缓存。字节相同的副本可以共享身份。这项显式 .NET 规则不推导上游 ABI 兼容算法。原生 Schema 投影也保留位置 tuple 合同，不再丢弃 `prefixItems`；不支持的长度约束会拒绝生成。这些修复不增加完整源类型分析。

提供者代际检查独立于 definition 撤销验证：等待中的 unary Service、lookup、Context 和 downlink 操作保留活跃 descriptor，同时替换其提供者。撤销本身不会中止这些操作；旧成功结果由原生代际检查拒绝。JSON codec 输入校验、结果序列化与流清理限制分别在公开 API 和[作者指南](authoring.zh.md)中说明。修复后的精确源码与包检查点以最新验证记录为准；早期平台结果只适用于其记录的检查点。

Service 有效性使用既有 Core 注册身份及未经 tracing 的原始 provider 值。caller-bound view 是实际调用 receiver；`Service<TState>.CreateView` 每次新建视图不会使活跃 provider 失效。准入在 contextual tracing 前捕获注册及原始值。后续每次检查先执行正常 contextual 查询，再读取最终注册及原始值，因此同步重入不能掩盖 provider 变化。重新提供同一个对象，包括在同一个 Fiber 内重提供，都会形成不同注册，使此前准入失效。设置不同原始值也会使其失效，即使注册身份未变；同值 `Set`、普通 `Notify` 和无关隔离 realm 的变化保持准入。这些观察仅在 Core 与 Composition 内部使用，不新增公开 API、服务 registry 或持久代数。未完成调用可以保留其注册及 provider，直至结束，仍遵循既有 CLR 协作寿命边界。

共同声明分析、选定反向 Context/owned-value、类型化固定事件、Peer 路由及原生 Scope store/layer 现已有实现和有界消费证据。文本 mux、typed uplink 和 unary multipart 的实施边界见下方共享 Connection 记录。当前支持与拒绝形状见[后端支持表](development.zh.md#alpha5-独立后端处置)，剩余具名集成见[剩余支持边界](development.zh.md#剩余支持边界)，不据此声称任意类型图或所有 Peer/codec 组合均已完成。标准 Settings、PluginManager 和宿主选择的 emit 通知使用生成合同。原生 state、完整配置、制品路由及独立 `MapCordisService` 适配继续承担各自职责。固定线协议互通仅限[验证记录](validation.zh.md)中的具名用例。

[独立多入口包消费者](../scripts/verify-clr-multi-entry.py) 已具有本地普通运行时及生成 TypeScript/HTTP 证据，覆盖共享 bundle 身份、配置、HMR 恢复/替换、provider/合同撤销与旧调用失效。独立的[原生 Remote 包消费者](../scripts/verify-typert.py) 验证其他边界。Windows/Linux、JIT 与静态 Native AOT 结果必须按最新已完成[验证记录](validation.zh.md)分别读取；进行中的运行和已有源码测试不能作为正式平台验收。动态 CLR 加载不声明 Native AOT 支持。

产品替换接受、业务退休/排空、权限、Project authority、basis 与 receipt/outbox 规则仍由产品拥有。不引入应用 `ApiCatalog` 标准或竞争总规划。剩余通用缺口继续记在原应用基础设施范围中。

## 独立原生模型与 Settings 边界，2026-10-10

源编译工具于运行时 descriptor 发射前生成当前声明制品，保留方法／参数名、类型引用、成员／构造器关系、C# required 与 JSON-required 事实、嵌套可空标注、支持的常量、可选参数默认值、文档、JSON 策略及 STJ 使用的四个空状态注解，并记录有效编译设置。制品身份标识这些事实，不证明 activation、与任意 DLL 对应或普适替换准入。Roslyn Symbol 是提取输入；序列化模型没有 Symbol、CLR Type、delegate 或 JsonTypeInfo。引用 CLR 声明保留 metadata 来源；metadata 不能提供所有仅存在于源码的事实。

运行 descriptor 与 `TypertTypeModel` 是派生输出，不是反推声明的来源。当前合同只有一份公开制品 `cordis/typert/$(AssemblyName).cordis.model.json`，由同一 `extract-model` 入口合并普通根与 service 选择。所选 service 的漂移检查继续保留，独立 wire 制品及其 conformance 桥退出。内部投影必须可从声明事实和明确后端政策重新计算，不是第二份保存作者模型。所有者和退出路径见[阶段合同](development.zh.md#模型管线合同)。

.NET emitter 当前投影直接 unary 方法、downlink 流及选定普通数据 record/class。实际覆盖包含 bool、string、integer、array、只读 list 接口、JsonElement、可空成员、必需构造器/init 成员、命名/ignore 策略和选定空状态注解。独立 .NET Remote 投影还保留 struct／readonly record struct 及无约束泛型数据声明的闭合使用。只读输出通过数据构造器接收实际值，不重放作者 getter；来源合并区分 metadata 丢失的 record 语法与真正的 class／struct 矛盾。数据继承、带约束或开放的泛型根、任意自定义 converter，以及输入数据中不支持的默认表达式仍明确拒绝；不宣称完整 C# 或 TS 类型系统等价。JSON 策略不能省略必填 carrier 参数字段，包括显式 null 和默认值。

常量投影保留可空基本数值初始化值，包括 float/decimal 字面量后缀和显式 null，不改变模型格式。写入调用者源码前，emitter 检查目标 namespace 中全部投影 DTO、生成 client、JSON context、failure 和逐方法 envelope 的类型名。冲突明确标出双方来源后拒绝；拒绝时删除拥有的旧输出，不把冲突推迟到 C# 编译。

生成客户端复用原生 HTTP 结果信封和 RemoteError。DTO/抛错 API 是 .NET 适配；TS 保持独立输出，沿用既有结果 API 与 carrier。compiler 只使用所选 SDK Roslyn、STJ 和既有框架传输，本次没有选入新的 NuGet 依赖。

Settings describe 经既有 profile 队列、Include 和 schema/脱敏 helper 暴露选择的 live 配置。下文的合同修正通过同一配置权威提供脱敏 base/user 层与 owner 范围内的数值观察 revision。provider 诊断保留在原生侧，不加入固定 wire 响应。已准入的异步 snapshot 可以在可选普通 `settings` provider 退休后完成：固定上游 describe 是同步调用，没有嵌套 provider lease。这是显式异步适配，与 controller service 或 Remote definition 撤销保留的原生 Gateway generation fence 有区别；后续调用重新查找 provider。不推导任何产品准入、权限或排空规则。

Settings 自动失效通知在一个同步 Cordis 回合中观察与 `DescribeAsync` 相同的选定 live namespace 和 revision 指纹，不为发布通知读取 profile 文件或进入配置／HMR 事务。所有者关闭后，排队观察不再产生作用。显式 `DescribeAsync` 保留一致的 profile 分层读取及上述已准入 snapshot 边界。观察与协调分开，避免通知工作在所有者清理后仍占用 profile 文件。

[仅依赖包门禁](../scripts/verify-typert-dotnet.py)的 Settings 部分 验证源制品、模型独立合同包、pre-CoreCompile/STJ 时序、类型化调用及不同生命周期场景，不调用 Node。静态 Native AOT 适用于调用者，不适用于 collectible CLR Host。IDE/design-time 首次构建、任意引用模型组合、完整 Settings 操作及富图/协议范围继续开放。进行中运行不计作平台验收，正式冻结证据见[验证记录](validation.zh.md)。

Typert 自动发现将已知 Loader 内建入口视为无制品贡献者，对应固定上游对 `cordis:include` 的处理。这些插件由 Loader 自身导入，其请求不属于 CLR 制品 resolver。显式将内建入口配置为制品贡献者仍会失败；错误的已声明 CLR 制品与未知 resolver 请求继续报错。真实 Profile Settings 消费者验证了这一边界。


原生 Gateway 现在对应固定 prepareInvocation 顺序：参数名和 receiver Context 之后检查实际 Service／binding，再启动全部参数解析。解析保持参数顺序，按首先观察到的失败返回，捕获已启动工作的晚到错误而不取消它们。definition／provider／原始 Service 注册检查仍在分发前拒绝失效的成功准备结果。原生代际检查仍是明确 CLR 适配；未结束 resolver 可以保留提供者直到结束。调用取消不覆盖准备错误，也不强制终止 resolver。本修复有聚焦反例和生成包消费者，正式完成证据另行记录。零参数 Settings 切片不作为 lookup 顺序证明。

数值 Settings revision、独立模型事实和固定协议互通的原始决定见[历史续行合同](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/development.zh.md#已确认的续行合同与-r022026-10-11)。标准 Settings 的合同修正见下文；已有 base64 二进制结果不等价于 connection attachment。
## 普通声明分析，2026-10-11

普通根与 Remote 根共享声明提取及同一保存模型。模型在 Remote 投影前保留作者方法返回 wrapper 和取消参数。声明定义使用完整程序集身份及 CLR metadata name；构造引用保留实参、包含泛型类型和空性。泛型参数标明类型／方法 owner 和 ordinal。直接 base/interface 边、public/protected 成员、事件/delegate、accessor、required/default 事实、常量、未执行 initializer 和源码文档均为模型数据。结构化 attribute 类型和 `typeof` 值进入引用图；partial 声明、隐式成员／成员来源、构造器、显式非公开 Remote 标记及作者 JSON metadata 签名为准确解释及拒绝而保留。这些事实不注册运行能力，也不承诺可执行投影。

源码及保存输入对 service、method、DTO、构造器、serializer 命名与默认值使用同一声明事实解释。Host binding 使用作者实际实现及显式 STJ metadata；独立 caller 生成自己受支持的数据与 metadata。caller 限制不能静默成为分析或 Host 准入规则；保留事实也不等于后端已经支持。共享可达 DTO 图的命名策略冲突由受影响投影以 `TYPM113` 拒绝，包括嵌套与递归引用。外部签名保留 metadata 来源，不重建实现体。完整 metadata 签名与不透明 metadata 占位不同；任何后端都不能虚构缺失源码事实或加载实现 DLL 猜补。

普通数据投影可显式接收引用模型，解析可达外部 DTO 定义。每个输入均校验模型身份，声明必须按完整的 assembly-qualified 身份精确匹配。只有明确提供的源定义能补足 metadata 引用；未解析引用和冲突定义会被拒绝。根仍从主模型选择，跨模型继续使用既有有界数据 carrier 规则。源码位置不同不构成不同定义。生成源码记录所有显式输入身份。提取器为通过 `JsonInclude` 显式纳入 JSON 的非公开实例数据在所属类型上记录 `TYPM202`，避免公开数据投影静默省略 JSON 成员。这项源码制品组合适配复用既有 emitter，不加载实现程序集，也不新增依赖。它不证明任意 DLL 与源码制品对应，不重放作者行为，也不建立通用 Remote/Schema/TS 引用模型支持。

必需的结构化引用与来源事实属于唯一当前模型合同。不完整及旧输入被拒绝并提示重新提取，不保留历史格式 reader、转换或旧投影。身份校验检查已有事实，不能补回遗漏，也不证明完整作者 JSON 行为。受维护消费者重新提取和构建，不以假想的实验模型兼容承诺保留第二条公共路线。

重建必须让受支持的 unary、downlink 及标准 Settings／PluginManager／通知消费者接入共同事实。独立 caller 拒绝无法重建的 serializer 行为；Host 保留其已成立的实际 STJ binding 合同。分析可保留显式选择的非公开标记 service 及其 internal JSON context；运行生成继续要求 public／顶层／非泛型／partial。显式 private Remote 方法在收集后拒绝。只有被选中且有效、仅使用注解的 STJ context 可延后 pre-generator 抽象成员义务，不抑制无关错误。泛型 DTO、更广引用模型后端、丰富 Remote／owned-value 协议及其他原职责继续开放。当前后端拒绝不关闭要求支持的范围项。本合同不声称重建门禁已完成；[验证记录](validation.zh.md)将每个真实结果绑定到源码与包。

上游将标记为 secret 的整个 live 根脱敏为 JavaScript undefined。原生 JSON namespace 要求有值，因此省略该根并返回原生诊断，其他 namespace 仍可读取。hidden 根按宿主政策省略。这项有界 JSON 适配不公开 secret，也不伪造替代值。

标准 Settings 直接纠正为数值 revision 和可省略的 expected revision。旧字符串 Settings 合同明确拒绝；没有版本化 Remote、双格式解析或转换服务。具有独立用途的完整配置 hash 编辑继续单独保留。新公共用法与退出路径见[Settings 合同迁移](settings.zh.md)。R02 已由其修复所有者经 PR #15 合入 main；本候选以该主线为基点复验调用链。

### Remote Connection 修正，2026-10-11

当前本地实现以固定 DSH unary 封套、响应关联、文本 WebSocket mux 和 unary multipart 响应附件替换简化 HTTP 封套及每方法 NDJSON。生成包装借用共享 Connection。C# 普通方法返回 `Task<RemoteResult<T>>`，作者返回 `Task` 时对应 `Task<RemoteResult>`；本地装配错误仍抛异常。无值成功省略 `value`，不与显式 null 合并。流产出业务项，区分 Remote 错误、载体失败与取消。异步释放排空本地拥有的工作，不增加远端确认。

Host Connection 拥有默认 operator Peer，socket 在该作用域登记并借用它。客户端或逻辑流退出不关闭共享 Connection 和兄弟流。安装任务继续由管理服务拥有。STJ 元数据识别类型为 `byte[]` 的字段并提取 multipart 附件；其内部 base64 JSON 表示不发送到标准 unary 线上。附件路径保留对象键和数组索引，空数组仍是二进制分片。固定浏览器 Connection 代码在 Node 下执行，以及生成式 .NET/TS 包消费者，已通过这条有限路径。这些是本地实现结果，不是完整平台验收。

Remote 客户端 CLI/MSBuild 已通过既有声明 composition 接收显式引用模型。独立生成 DTO 必须具有默认值等源事实，仅 metadata 会被拒绝。包消费者检查正常 build/pack、引用变化、缺失拒绝和 Clean。Typed uplink 使用带标记的作者参数、共同声明模型和类型化发送／半关闭句柄。根空性与受支持的嵌套集合空性保留；显式 optional 值区分缺失与 null。反向 Context/owned-value 处理及完整 Peer/事件语义仍未完成。当前下行实现和通过的夹具不能反过来定义这些目标或成为另造协议的依据。不保留旧试验客户端的兼容路线。

### Remote 载体准入与二进制元数据，2026-10-12

owner 关闭上行时，按固定 `ClientUplinkQueue.close()` 清空待发送输入，并以正常完成唤醒读取者，不替换下行结局。远端 end/error 保留已缓冲的业务项；物理载体失败丢弃它们，但保留 carrier 错误。真实 null/undefined 拒绝原因仍是失败。委托结果编码的集合 codec 同时委托其序列化元数据，包括 nullable 包装。Optional codec 保留既有的纯 JSON 结果编码，不承诺附件。

Remote 内层 payload 仅包含 `args` 自有字段，其值必须为 JSON 对象。HTTP 外层封套保留固定上游允许额外字段的规则；mux 帧校验独立处理。无效内层 payload 在 lookup 或业务准备前产生 `gateway/internal`。JS 流先准备本地参数，再于取得载体前和就绪后分别检查取消，因此不会向 Host 发送 `open` 或补偿性 `cancel`。

Unary 附件提取与生成的 TypeScript 结果类型共同使用实际 STJ 元数据。内置 `byte[]` converter 提供二进制能力；schema 的 `contentEncoding: base64` 注解不提供这种能力。属性／类型 converter 保留作者定义的表示。编码字节上的 enum／const 约束投影为二进制结果类型，null 分支继续保留。输入与 mux item 继续使用既有 JSON codec。根字节直接快照为附件；嵌套对象仍先经 STJ 再提取，以保留作者 converter 和序列化规则，避免第二次读取属性。这项内部转换仍有分配成本，不是第二种 wire 格式。

Peer 关闭先中止自有物理 I/O 并取消寿命，再等待发送锁与自有泵结算。协议拒绝在没有挂起发送时可以发送关闭帧。物理请求释放资源前加入同一次停止操作。逻辑取消仍保留其他调用和借用的 Peer；没有引入远端释放确认或清理超时。

### 固定 Remote 事件与类型化声明投影，2026-10-12

事件路径使用固定 `$events`／`$events/result`、ready／emit／waterfall／cancel 帧及 `agent`／`agentId` 身份。公开委托在现有声明模型中保留事件事实，生成式 Host 描述与 .NET／TS 监听解释同一声明。C# 将 TS 的 receiver／request 形状表达为类型化 subject、request、同结果类型 next 及取消参数。Host 装配从 subject 得到身份，正常调用不传 wire 身份。客户端 `RemoteEventResult<T>` 将内部 continuation 标记与 undefined（`RemoteOptional<T>`）、null 分开。这是 CLR 结果类型适配，不改变 wire outcome。

反向 resolver 与 owned release 保持同步，每次调用读取当前 adapter。borrowed Context 保持借用，owned Context 即使遇到取消也存活至 handler 和回复结算。源撤销结束观察；清理失败仍归现有 Fiber，可拒绝替换。待决 waterfall 跨观察者断线保留；旧 client 身份不能结算当前交付，已完成回复保持幂等。标准通知消费者直接迁移到类型化事件监听，旧通知 Remote 退出。作者声明、选择与寿命边界见[Remote 事件](remote-events.zh.md)。本候选验证与此前队列／集合修正分别记录。

### 声明空性与事件未使用输入，2026-10-12

STJ 运行时元数据丢失泛型引用参数的空性。生成式 Host、事件及独立 .NET codec 使用 `TypertCodec.CreateDeclared(metadata, query)`，在导出的 Schema 路径上应用现有共同声明的 null 准入事实。Schema 仍懒加载；属性命名、converter 与序列化仍由 STJ 提供。query 是生成代码，不是保存格式或第二套 Schema 权威。显式 Schema 的 `Create` 继续服务真正的手写 codec。可空字典值不会使同一声明的另一处必填对象也变为可空；TS 发射保留联合类型与非空引用交集。

事件输出文件名采用委托的完整限定身份；不同 namespace 可以使用同名委托，生成公开类与事件 key 不变。固定 DSH 的 `openWireStream` 在观察事件前归还 `$events` 不使用的上行。Gateway 沿用这一所有权规则，mux 迭代器即使尚未读取也会关闭实际 inbox。归还失败与事件观察独立结算，不放宽普通业务上行的协议／溢出拒绝，也不关闭 sibling 流。

## 出站浏览器 Context 投影，2026-10-12

固定 `packages/api/gateway/src/client/index.ts` 的 `invokeMethod`、`prepareInvocation`、`RemoteNamespaceService` 决定 scoped 选择：通过 Cordis tracing 取得消费方 Context，查询当前 adapter，区分 undefined 与 null，仅在本来具有直接 descriptor 时允许直接 fallback。原生生成浏览器挂载现在从已有 invocation descriptor 取得接收方与 lookup 两种投影。类型化 `getRemote(ctx, namespace)` 只是 `ctx.get('remote.' + namespace)` 的声明便利入口；命名参数对象保留现有 .NET 生成 wire 命名和省略语义。不增加身份 registry、传输、模型格式或兼容分支。此前只挂载普通 endpoint 函数、没有消费方 Context 投影，是未完成切片，不是目标合同。

普通数据后端的框架集合接口现在复用共同声明中的框架投影。有序 JSON 数组表示、元素空性及引用事实与 Remote 后端共用；具体 .NET 接口选择属于 STJ 数据载体适配。两个后端也将框架 `ImmutableArray<T>` 投影为有序 JSON 数组，保留这个具体 .NET 类型及其声明的元素类型。这项有限映射不推定其他不可变集合也受支持。生成式 .NET 客户端现在向既有结果 codec 传入共同声明的空性查询，用于普通返回值与流元素，在保留 STJ 解码的同时拒绝声明不允许的 null 元素，包括可空的不可变集合。`DateTimeOffset` 保留 STJ 原有的字符串表示、offset 和精度。已知的默认 `JsonStringEnumConverter<TEnum>` 会投影到对应的生成枚举，保留 flags 和显式成员名称；任意 converter 执行仍被拒绝。枚举最终由 STJ 解析，其 Schema exporter 的已命名值列表不能完整描述大小写变体、flags 和整数回退。因此 codec 导出保守的 string/integer 形状，解码仍拒绝无效名称。独立 Web 后端仍明确拒绝带字符串转换器的枚举。闭合泛型数据载体采用下文说明的声明替换；执行任意 enumerable 或任意泛型作者行为仍不在该投影范围内。

### 选定 Client 调用的准备边界，2026-10-12

固定 Client 在进入 `invoke` 或 `invokeStream` 前选择变体，因此 adapter identity 自身的异常仍同步抛出。选定 unary 后，参数准备与缺失／关闭 Connection 的异常拒绝该 Promise，位于将 carrier 错误转为 `RemoteResult` 的 catch 之外。流仍在创建句柄时报告准备失败。撤销与取消沿用现有所有者；预取消不压掉准备错误。

命名参数对象对应上游的位置参数槽。生成元数据从同一 descriptor 列出必需 wire 名，包括 receiver 身份与 lookup 参数；默认值及其他 `AcceptsUndefined` 参数可以省略。选择／注入 scope 后，准备阶段在分发前要求这些 own 属性存在。仅有 direct 变体不能授权本次 fallback。值为 `undefined` 的 own 属性仍算已提供参数槽，对应上游显式位置 undefined；JSON 省略与 Host 必需字段校验继续生效。null 同样保留为已提供的值，交给 Host codec。这是形态准备，不复制 Schema 校验。现有生成客户端按当前元数据重新生成，不保留旧 tuple 读取器。

### 声明使用位置与 Schema 引用，2026-10-12

Chief 对 `9ae967c` 的审查发现，只给复用的 STJ `$ref` 根部增加空性约束，会丢失嵌套泛型的不同空性。生成查询现在返回 `TypertNullabilityInfo(Id, Nullable)`，标识在单个 codec 内区分完整声明使用位置。延迟导出按原始 Schema 位置与声明标识投影 STJ 图，重写本地引用并复用已经投影的组合，使递归图终止。这不增加保存模型版本、wire 字段或另一套序列化器。作者沿用现有 Remote 声明，消费者重新生成。

始终忽略的成员，以及不参与作者 STJ 合同的字段，不再贡献属性空性。条件忽略继续是序列化规则。存在歧义的属性映射保留序列化器规则，不再由最后遍历的声明覆盖。共同模型仍将这些成员保留为分析事实。STJ 负责属性命名、转换器、序列化和必填成员策略；声明泛型空性补充 CLR 元数据擦除的事实。这纠正了此前仅处理引用根部的适配，不表示支持任意泛型作者代码执行或任意转换器重建。

### Scope 身份与 Peer 路由，2026-10-12

固定 DSH 的 `packages/core/scope/src/index.ts` 与 `packages/client/connection/src/operator-peer.ts` 决定行为：子 Fiber 拥有注册；其 Context 继承不透明身份；事件按身份及路由父链准入，与 Fiber 祖先关系无关。Composition 中的 `Scope`、`Scoped<T>`、`ScopeParentBinding` 表达这些职责。Core 的 `IEventFilter` 在现有分发器中表达上游以 symbol 提供的 receiver 过滤器。弱引用身份表替代 symbol 标签与 WeakMap；锁保护父链写入及环检查。不增加依赖、模型格式或 wire 身份。

`Scope.Target(subject, key)` 捕获 subject 当前的过滤器，允许无标签监听者，以及 key 本身或其祖先标签的监听者。后代不准入；现有 Global 监听选项仍跳过过滤。null key 表示无路由标签，与 JSON null 或 Remote Undefined 无关。`IsCarrier` 区分无标签载体和普通 receiver。父链只绑定一次；只有原 binding 句柄可以重绑，拒绝成环，调用方仍须保证没有保留旧父级下产生的东西。

`DisposeRawAsync` 使用普通 Fiber disposer。`DisposeAsync` 还等待 `Fiber.WaitAsync`，使并发释放等待已由 raw 入口启动的清理。它复用原生生命周期工作，不增加所有者或调度器。Peer 以自身为 scope key，Connection／fallback 所有权及 socket 借用关系保持不变。生成的 waterfall 调用改用以 subject 为 key 的载体，保留 subject 的基础过滤器，并把 subject 留在参数中。按固定 `packages/api/remotes/src/index.ts`，转发先在载体没有 key 时调用 `next()`（包括普通 receiver）；有 key 时，必须在投影前确认 subject 类型正确且与 key 是同一对象。临时的原始 subject receiver 路径退出；消费者重新生成。

这完成上述原生路由机制，不代表所有反向 Context adapter 或全部 Peer 互操作完成。scope store／layer 导出见下文。具体候选的执行结果及平台限制见验证记录。

### Scoped 事件 subject 身份，2026-10-13

固定 agent dispatch 在 Cordis 对直接参数做 tracing 前，将 subject 放入事件 payload。原生生成式 waterfall 现在以一个值元组承载 subject、request 和取消令牌；选定转发器在投影前，用其中的原始 subject 与 key 比较。即使 `IContextualService` 的普通 caller view 是新对象，也保留该 subject 身份。Core tracing 不变。旧的三个直接参数布局已退出，事件 helper 需重新生成；公开 helper 签名、模型事实和 wire payload 不变。

## 保存模型 Web 制品适配，2026-10-13

固定 `packages/typert/generator/src/workspace.ts` 先执行分析，再调用 `FaceModelEmitter`，并检查包是否导出生成的运行文件和声明文件。.NET 保存模型 Web 路线对应这一职责：显式主模型及引用声明进入静态 JSON Schema 与共用 TS 发射器，再由正常 MSBuild build/pack 交付。客户端不加载 Provider 实现；Host 绑定继续执行实际注册的 Service。

选定事件声明现在使用同一保存模型 build/pack 路线。事件选择与 .NET 后端共用，运行时元数据和保存事实共用内部事件发射器。普通参数和 waterfall request 使用输出 Schema，reply 使用输入 Schema，continuation 结果类型使用输出 Schema。这保留了运行时发射器已有的方向合同，并从保存事实提供对应输入。固定的本地 next／Host continuation 边界、显式转发、反向 Context 及借用／拥有资源结算保持不变。`EventData` 仍是可重算投影，没有第二份作者模型或 Provider 加载步骤。

JSON Schema 2020-12 definitions 描述受支持的 STJ 线类型，不构成第二种保存声明模型。制品注释标识源模型，不承诺兼容旧试验输出。CLR 构造器/accessor 执行不属于该静态后端，所以初始化事实不作为运行默认值输出；真实 Host 仍负责执行。不支持的 policy/shape 投影会拒绝并删除所属旧输出；后端尚未完成不缩减已经成立的运行支持。公开输入及剩余拒绝边界见[作者指南](authoring.zh.md)。

### Web 输入与结果合同，2026-10-13

共同声明事实现在按方向生成 Web Schema。输入投影分别保留参数 DisallowNull 与省略／默认值、setter 的 AllowNull／DisallowNull，以及必填成员。非可选构造参数关联的成员始终必填，不取决于 RespectRequiredConstructorParameters；这与 .NET 10 JsonSchemaExporter 及 Host codec 先 Schema 准入、再反序列化的行为一致。没有放宽 Host 输入准入。

结果投影遵循 getter 的 MaybeNull／NotNull，以及成员 JsonIgnore 和全局 DefaultIgnoreCondition 的条件写出规则。JsonRequired 不承诺条件写出时一定出现该字段。运行 descriptor 路线从现有 STJ metadata 派生相应结果投影，输入 Schema 与 Encode 继续各司其职。不可静态解释的 ShouldSerialize 谓词只使对应输出字段的存在性不确定；生成时不会执行该谓词。显式提供的 codec Schema 仍是作者提供的合同。生成代码支撑记录 TypertNullabilityInfo 补充 GetterNullable 与 SetterNullable 显式标记。声明读取器已经保留 accessor 属性；投影统一处理属性上的写法与程序集中的 getter 返回值／setter 参数写法。CreateDeclared 通过原有 resolver 和私有 serializer options 副本应用差异，不修改作者 Context、不替换 converter、不关闭空性检查，也不建立第二个 Schema 权威。这修正了 .NET 10 源生成器只在 property 上读取标记所导致的引用程序集差异。保存模型版本不变；消费者从当前声明重新生成。

生成的 .NET 客户端按接收方输入合同编码参数，按发送方输出合同解码结果。私有序列化元数据保留 converter 和命名规则，移除 DTO 输入上的输出忽略规则，以及结果上的输入必填和 setter 规则。可选 JsonElement 成员对 Undefined 保留作者的省略规则，因为 Undefined 不是 JSON 值；显式 JSON null 仍然发送。参数封套继续保留可选参数省略规则。`TypertCodec.EncodeInput` 与 `DecodeOutput` 将这两个方向提供给生成式事件和上行消费；Host 的 `Decode` 与 `Encode` 继续承担读入／写出。这些是同一声明的用途投影，不增加线类型或兼容路径。

固定 DSH `packages/client/connection/src/client/rpc.ts` 将 fetch override 作为局部 callable 调用。Connection 采用相同方式，避免显式传入原生 fetch 时错误地绑定 Connection 接收者。现有构造时默认函数捕获与绑定、取消、错误和所有权保持。默认、原生、绑定和箭头函数都经过同一传输路径。

### Scope 注册存储，2026-10-13

固定 `packages/core/scope/src/store.ts` 提供 NamedEntries、AnonymousEntries 和 ScopedLayers。原生类型保持插入顺序、精确且幂等的撤销、借用值、非空表代际内的实时迭代，以及该代际清空后的分离。内部链表实现 JavaScript Map 迭代器的变更行为，普通 .NET 字典枚举器不能提供这种行为。Scope key 沿用现有 Scope 的引用身份。TryGetValue 区分名称缺失与存入的 null；Merge 返回平台 OrderedDictionary，作为独立的有序副本。

ScopedLayers 从传入的注册 Context 获取可见性和清理所有权。读取不创建覆盖层；Peek 只读精确 scope，ChainLayers 与 Merge 按路由父级从远到近处理。现有 Context effect 在 onChange 前收集撤销，遵循 notify:false，且仅在整个聚合为空时回收 scoped layer。它返回原有 EffectHandle，不增加寿命所有者或 RPC 身份参数。ScopeStoreTests 主责存储语义；共享生成包消费者验证这些能力在已准入 Peer 和子 scope 中的使用。

### Settings 文档交接，2026-10-13

固定 `packages/api/settings-controller/src/index.ts`、`packages/settings/settings/src/index.ts` 和 `packages/boot/config-editor/src/index.ts` 定义此流程。ISettingsProvider.PrepareDocumentAsync 返回提供者的文档；SettingsForms 返回现有 profile 用户补丁路径，不写入镜像，也不创建文件。SettingsController 在取消检查前解析当前 provider，准备后再次检查取消，将准备／打开失败分类为 gateway/internal 或 gateway/cancelled。注入的打开器接受文档后，并发取消不改写成功结果。结果始终为 opened:true；C# 没有布尔字面量类型，因此载体使用 bool。

可选构造器委托是宿主文本编辑器集成，对应上游 openTextFile。默认使用 ProcessStartInfo.ArgumentList，不经命令 shell：Windows 调用 Explorer，WSL 转换路径后调用 Explorer，macOS 使用 open -t，桌面 Linux 使用 xdg-open。Windows 目标保留非 ASCII 原文并转义 Explorer 分隔符；委派退出码 1 在未取消时视为接受。取消结束所拥有的启动进程，不关闭已经接收文档的编辑器。不新增依赖或第二个配置所有者。无桌面宿主可以提供自己的打开器；桌面不可用时继续返回可诊断失败。

## 候选准入与本地失败原因，2026-10-15

固定 DSH HMR 先准备实际 import 的替代插件，再改变注册；公开 protocol descriptor 支持复用 codec。CLR `ReplaceAsync` 在现有 mutation 边界内、切换回调之前提供 `admitCandidate`。`ClrReplacementCandidate` 暴露注册名及缓存贡献，包含未激活出口和别名。它是回调期借用视图，没有新增事务权威。拒绝会退出候选，旧合同尚未撤回。准入可能执行作者工厂和惰性 Schema，不能回滚任意作者副作用。关闭视图阻止继续读取，不能撤销已经返回的 CLR 引用。这是 CLR 组合适配，不是上游 ABI 兼容算法。

固定 `packages/api/gateway/src/client/index.ts` 对取消保留捕获的 cause，对普通 carrier 失败不保留。共享 JS 客户端现在对两者都保留实际捕获值。取消 cause 是移植纠正，普通 carrier cause 是明确增加的进程内观察能力。分类仍为 `gateway/cancelled` 或 `gateway/internal`。`RemoteError` 接受标准 `ErrorOptions`；`cause` 不可枚举，不放入 details，不从 wire 重建，也不增加重试或重放。原有准备、撤销和流边界保持不变。

## Metadata 数值方向，2026-10-15

运行描述符路线消费作者实际 `JsonTypeInfo`。现有 byte/sbyte、short/ushort、int/uint、long/ulong、float/double 和 decimal 在 Strict 下接受 JSON number；AllowReadingFromString 另接受原生数字字符串。现有 nullable、DTO、数组／集合、字符串键字典值组合使用同一投影。输出仍为数值。事件参数与 continuation 输出使用输出投影，传入回复使用输入准入。

.NET 10 exporter 合并读取与写出形状，提供的三个数字 pattern 比原生读取范围窄。输入投影保留有限数值的原生语法，包括有符号数的前导加号、前导零和 decimal 指数；无符号文本不带正负号。实现只识别这三个 exporter 模式及其有限原生模式投影，使用 BCL 非回溯匹配器；未知 pattern 继续拒绝。整数位宽、decimal 范围和转换由原生 STJ 负责。float/double 检查拒绝非有限结果，包括原生读取可能接受的字符串 NaN/Infinity。这是有界 STJ 适配，不是任意 JSON Schema 或 converter 执行。

WriteAsString、AllowNamedFloatingPointLiterals 和未支持组合在影响投影时明确拒绝。显式 reflection metadata Context 可以在 JIT Host 中保留 init-only 初始化值，并导出运行描述符；这不表示保存模型生成或 Native AOT 能执行任意作者 Context。Web 命名保留，但 Schema 准入仍要求声明的 wire 字段名；PropertyNameCaseInsensitive 不意味着当前 JSON Schema 接受原生读取的所有大小写形式。本次数值工作不证明全部 Web 默认行为等价。

## Remote 静态数据输入默认值

固定 DSH 生成器的公开 codec 工厂通过可执行 Schema 解码，依据为 `639ed015397290b3745d163aafe02ffee4aa3f84` 的 `packages/typert/generator/src/emitter.ts`。其对象 Schema emitter 保留类型和可选性，不把普通成员初始化器转换成默认值。analyzer 拒绝带默认值的 Remote 方法参数，这与 DTO 成员初始化是不同问题。.NET 适配继续让 codec 承担输入解析。把静态可证明的 C# 数据初始化器映射为省略成员的输入默认值，是本平台的明确判断，不是 DSH 的直接要求或 STJ 惯例的自动结果。

源码生成器和独立 .NET emitter 共用声明图及其参数／成员事实，为 `CreateDeclared` 提供静态 wire 默认值。输入 Schema 描述这些默认值，输入解码在 STJ 前应用它们；显式 null、必需字段和显式值保持各自含义。输出解码不使用输入默认值。编译器不会加载作者程序集执行构造器来完成投影。支持范围限于[作者说明](authoring.zh.md#remote-输入的静态数据默认值)中的被动 primary record 子集；未知行为继续作为分析事实保留，在独立输入投影确实需要时拒绝。

本适配纠正了已观察到的 STJ 源生成省略路径：record 的 init 成员丢失 `new(false, [])`，最终变成 null。它不改写任意应用序列化，不新增自动业务校验或第二个 Gateway。已有显式 reflection metadata 集成仍可用于其他作者行为的 JIT 场景，但这些静态默认值不要求它。重提取直接更新当前事实，不建立版本化模型兼容路线。

公共 `TypertInputDefault` 委托跨生成程序集边界提供稳定的声明 metadata，不承担请求政策。其寿命、JSON 所有权和工厂签名变更后的重建要求见[作者说明](authoring.zh.md#remote-输入的静态数据默认值)。提取阶段与生成查询不执行作者初始化器；手写委托仍是调用方代码。DateTimeOffset、有限枚举策略、ImmutableArray、闭合数据泛型、record struct 和方向性只读／空性投影补齐的是其他既有 .NET 后端缺口，继续在已说明的边界内支持。

空性／默认图现在只对递归遍历路径中同一泛型声明的活动使用点计预算，上限为 256。总图大小与兄弟实例不消耗该预算；已访问身份使普通递归终止。这项保护约束生成过程中的泛型递归展开，超过同一资源预算的有限路径仍会拒绝，不声称进行数学意义的终止判定。既有后端声明数量／深度预算和运行时默认遍历保护不变。
