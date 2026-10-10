# 插件与应用作者指南

[English](authoring.md)

这些可选的 .NET 作者辅助接口复用现有 Cordis 运行时。包使代码可用；模块注册把名称映射到代码；patch/profile 条目选择实例；`Inject` 决定插件何时能够激活。服务身份仍由名称与 realm 决定。这些辅助接口不增加第二套注册表或生命周期。

## 从可运行示例开始

本页的作者辅助 API 要求使用[发布说明](../CHANGELOG.md)中列出的作者接口版本；旧 NuGet 包不包含这些 API。在对应版本上架 NuGet 前，请使用当前源码检出。下面的命令通过项目引用运行仓库示例，并使用 `global.json` 指定的 SDK；仅安装包不会提供示例项目。

[共享契约](../examples/Probes.Contracts/Probes.cs)、[提供者与配置](../examples/Probes.Plugin/ProbeModule.cs)和[包含两个消费者的静态宿主](../examples/Probes/Program.cs)是示例的权威源码。它们展示普通格式化能力、caller-bound 状态探针注册服务、类型化事件、提前撤销、提供者消失和消费者重新激活。

```console
dotnet run --project examples/Probes/Probes.csproj
dotnet publish examples/Probes/Probes.csproj -c Release -r win-x64 -p:PublishAot=true
```

请使用实际构建并运行可执行文件的机器所对应的运行时标识。发布命令本身不是执行成功的证据；已完成结果记录在[验证记录](validation.zh.md)。[V1](../tests/fixtures/ProbeV1/ProbeV1.csproj) 与 [V2](../tests/fixtures/ProbeV2/ProbeV2.csproj) DLL 夹具通过[显式 CLR 入口](../tests/fixtures/ProbeEntry.cs)编译同一份提供者源码。

### 通过 CLR DLL 替换运行同一份提供者

[CLR 控制台宿主](../examples/Probes.Clr/Program.cs)仅引用共享契约和 `Cordis.Clr`（Composition 通过传递引用可用）。它从两个夹具 bundle 加载同一份提供者源码编译出的实现，没有静态引用 `Probes.Plugin` 或任何夹具项目。在仓库根目录执行：

```console
dotnet build tests/fixtures/ProbeV1/ProbeV1.csproj -c Release
dotnet build tests/fixtures/ProbeV2/ProbeV2.csproj -c Release
dotnet run --project examples/Probes.Clr/Probes.Clr.csproj -c Release -- tests/fixtures/ProbeV1/bin/Release/net10.0 tests/fixtures/ProbeV2/bin/Release/net10.0
```

两个参数都是包含 `ProbePlugin.dll` 及其依赖的 bundle 目录。宿主将自身实际使用的契约程序集传给 `ClrModuleResolver`，加载 V1、激活两个消费者、用 V2 替换 V1，并验证两个消费者均重新取得 caller-bound 视图。释放一个消费者只移除它自己的贡献；释放另一个后，根停止前注册表为空。显式 `ClrModuleDefinition` 使用 `Cordis.ProbeFixture.Entry`；夹具中的其他入口类型用于负向测试。

宿主直接加载两个完整稳定目录，在生命周期清理和卸载请求后仍保留它们，不强制 GC，也不删除代码目录。退出码零表示贡献断言、生命周期清理和卸载请求成功，回收可以仍在等待。普通发布宿主并传入两个准备好的目录即可，运行时不还原包，也不依赖源码仓库。这条 CLR 路线要求普通运行时，不支持 Native AOT。[制品工作流](clr-artifacts.zh.md)说明库管理的安装与离线删除。[部署测试](../tests/Cordis.Platform.Tests/ProbeDeploymentTests.cs)另外保留显式影子复制覆盖及仅限测试的 GC 观察。

## 选择最小而有用的作者写法

| 任务 | 现有写法 | 可选便利入口 | 何时更简单的写法已经够用 |
|---|---|---|---|
| 读取已声明能力 | `(IProbeRegistry)ctx.Reflect.Read("probes")!` | `ctx.Reflect.Read<IProbeRegistry>("probes")`；契约的 C# 扩展属性提供 `ctx.Probes` | 单次读取可能只需强制转换。泛型重载集中转换，共享扩展集中名称。 |
| 发布或监听单项 payload | 字符串名称和 `object?[]` 参数 | `EventKey<ProbeChanged>` 与类型化 `On`/分发重载 | 现有多参数协议保留原始布局。发布者与监听者共用契约时，key 更有价值。 |
| 校验配置 | `Plugin<T>.Config = raw => ...` | `ConfigBinding.FromJsonTypeInfo(metadata, validate: rules)` | 标量或非数据契约通常只需简短直接校验器；嵌套数据契约更适合元数据。 |
| 接受外部通知 | effect 拥有订阅、注册有效标记、`RunAsync` 与错误观察 | `ctx.SubscribeExternal(subscribe, callback, reportError)` | 已接入 Cordis 的来源可能无需额外包装；辅助接口消除重复的所有权和迟到回调检查。 |
| 启动应用 | 显式 `Context` + `Loader` + `MountAsync` | `ApplicationBoot.BootGenericAsync` | 应用已拥有这些对象时可保留显式装配；`BootAsync` 仍是 DSH 兼容入口。 |
| 读取嵌入 patch | 打开 manifest 流、读取文本、解析条目 | `PatchResources.Read(assembly, resourceName)` | `ReadText` 适合仍需文本的调用方；两者都不激活插件。 |

Attribute 与自定义生成器仍是设计选项，并非一概禁止。当前实现采用普通 C# 扩展属性和手写 `CreateView`，因为剩余服务视图代码很小，所有权也直接可见。更大的契约面可以通过 Attribute/生成器减少重复声明，但会增加生成器包、诊断、生成代码调试、版本管理与兼容检查成本。重复代码或实际错误足以证明收益时，可以采用。现有 System.Text.Json 生成器已经提供有用且受限的元数据契约；它不生成 Cordis 生命周期，也不保证 CLR 可卸载性。

## 消费者插件

在 `Inject` 中声明服务名称，然后在 `Apply` 中取得能力。示例的 `ctx.Probes` 属性委托给 `Reflect.Read<IProbeRegistry>`，保留依赖继承、拦截、realm 查找和 caller tracing。它不会增加依赖声明。`ctx.Get<T>(name)` 仍是沿现有查找规则进行可选访问的入口；不能用它代替属性式依赖检查。

泛型读取与手写强制转换具有相同行为：对象类型不兼容会失败，引用值即使带非空标注也仍可能为 null。每次激活都重新取得视图。保存的旧视图不会自动路由到替换后的提供者，而且可能持有可卸载的插件程序集。

`EventKey<T>` 只描述一个 payload 槽位。同名 key 共用原始监听表，原始发布者与类型化监听者互通。不会把现有多参数事件隐式转换成 tuple/DTO。原始参数数量或 payload 类型错误会明确失败。可空引用标注不能在运行时强制 payload 非空。

`On` 和 `Once` 保留 `EventOptions`、过滤、receiver、顺序及 effect 所有权。分发参数中 `receiver` 与 payload 分开。Action 观察者返回 `Undefined.Value`。对象结果监听器保留 `null`、`false`、`Undefined.Value`、零和空字符串。Task 重载沿用现有原始分发器的结果规则。`ParallelAsync` 等待任务但不返回其结果；`SerialAsync` 提取 `Task<object?>` 的结果，却将其他 `Task<T>` 作为观察者等待，并以 `Undefined.Value` 代替结果。返回 `Task<int>`、`Task<string>` 或 `Task<bool>` 的方法组可以直接传入类型化 `On` 或 `Once` 并通过编译；其结果在串行分发时会被丢弃，与原始路径相同。这是继承的原始限制，并非类型化接口独有的回归。

| 监听器写法，其中 `Answer` 返回 `Task<int>` | `SerialAsync` 行为 |
|---|---|
| `ctx.On(key, Answer)`（或 `Once`） | 等待任务，以 `Undefined.Value` 代替结果，然后调用下一个监听器。 |
| `ctx.On(key, (evt, value) => (object?)Answer(evt, value))` | 强制转换任务对象并未适配其结果，行为与上一行相同。 |
| `ctx.On(key, async (evt, value) => (object?)await Answer(evt, value))` | 产生 `Task<object?>`；保留等待得到的整数并停止串行分发，包括零。 |

`Once` 以及返回 `Task<string>` 或 `Task<bool>` 的方法使用相同的显式适配：`async (evt, value) => (object?)await Answer(evt, value)`。原始监听器应返回等价异步辅助函数产生的 `Task<object?>`。适配后，`null`、`false` 和 `Undefined.Value` 让串行分发继续；零、空字符串和 `true` 则停止分发。分发器不会通过反射读取任意 `Task.Result` 属性。null 任务引用仍保留原始 null 结果。

同步的 `Emit`、`Bail` 和 `Waterfall` 不等待任务。`Emit` 立即调用后续监听器；`Bail` 和 `Waterfall` 中未调用 `next` 就返回的监听器会返回原任务对象，不包装任务或提取结果。`Waterfall` 仍要求显式调用 `next`，观察者不会自动继续。需要观察异步错误时使用可等待的分发方式。

## 服务提供者与配置

示例的 `IProbeFormatter` 是通过 `ctx.Provide` 提供的普通共享对象，不需要服务基类、代理或状态容器。`IProbeRegistry.Register` 创建调用方拥有的资源，因此实现采用现有 `Service<TState>` 模型：提供者和视图共享 `State`，每个视图携带其调用方 `Context`，`CreateView` 调用视图构造函数，不重复注册提供者。

自动清理来自 `Register` 中的 `Context.Effect`，而不是接口返回类型。返回的 disposer 也允许提前撤销。释放一个消费者只撤销其自己的贡献，另一个消费者与提供者继续可用。普通共享对象也可以显式接收 owner，只要这样更符合其 API 含义。

`ConfigBinding.FromJsonTypeInfo` 返回供 `Plugin<T>.Config` 使用的委托。传入显式 `JsonTypeInfo<T>`，通常由 System.Text.Json 生成，并可提供返回问题字符串的领域规则。绑定失败包含数据路径和 `binding` 前缀；业务规则返回的问题使用 `validation` 前缀。业务规则抛出的异常保留原始身份。反序列化成功本身不等于领域校验成功。

支持的输入域是数据：字符串、布尔、标准整数、decimal、有限浮点数、string/object 字典、`IList` 序列与 `JsonElement`，支持嵌套 null，深度上限为 64。`JsonElement` 数值必须能表示为有限 double。任意对象、delegate、循环、嵌套 undefined 和尚未求值的表达式会被拒绝。允许共享但无环的子数据。Loader 表达式先求值，然后绑定；只有适配器支持的数据结果才能进入该路线。适配器不会回写原始条目或 `Fiber.RawConfig`。

根配置缺失（`Undefined.Value`）和根级显式 null 都被拒绝，但错误信息不同。需要默认值的插件必须显式包装 binder，或直接提供 `Config` 委托；辅助接口不会悄悄替换成 `{}`。成员默认值、required 成员、构造函数、枚举转换、命名与未知字段行为由元数据决定。尤其要注意：生成元数据可能把缺失的 init-only 属性初始化值替换为 CLR 默认值。示例用 record 构造函数的可选参数表达字段缺失时的默认值。缺失字段、null、零、false 与空字符串仍是不同输入，由领域规则决定是否允许。

返回的 binder 持有元数据和 validator。可卸载插件应让委托随插件存活，并释放外部保存的 binder、converter 和错误对象。没有全局元数据缓存。非数据配置仍适合直接使用 `Plugin<T>.Config`。

具有 live 字段的完整 typed 数据对象推荐使用 Composition 的 `ConfigObject<T>.Create(validator).Field(...).Build()`。它将显式键、描述和投影组合为既有配置合同，不推断 POCO 成员，不改变校验或默认值。生成元数据适用时，可将 `ConfigBinding.FromJsonTypeInfo` 作为该 validator，并保持其命名/默认规则与字段声明一致。全部普通字段和保存字段都须声明。参见[配置示例](configuration-description.zh.md)及[实际手写/组合消费者](../examples/Probes/ConfigurationScenario.cs)。特殊转换和嵌套 live 路径继续使用 `ConfigSchema<T>`。此推荐用法需要 `0.2.0-alpha.3`或更高版本，未包含在已发布的 `0.2.0-alpha.1` 包批次中。

## 外部回调与所有权

[`SubscribeExternal`](../src/Cordis.Extensions/ExternalCallbacks.cs) 适配接受 `Action<T>` 并返回 `IDisposable` 的来源；应在 Cordis 回调或 `RunAsync` 内调用。它先登记 effect 所有权，再订阅，因此覆盖订阅期间的同步通知和重入释放。每次注册都有独立的有效标记。清理先关闭准入再退订，进入 `RunAsync` 后执行时再次检查标记。同一 Fiber 重新激活不会使旧的排队回调重新有效。

回调返回 `Task`；最终失败会到达必填的 `reportError` 出口，即使根已关闭也是如此。取消独立处理，仅进入可选的取消出口。错误出口可能运行在外部线程，且不应抛出异常。已经开始的工作可能在退订后完成：辅助接口既不取消也不 drain。订阅期间抛错的来源应释放自己已经创建的注册。外部长期保存传入的回调可能持有插件对象。

进入执行域、停止新调用、请求取消和等待在途工作结束，是不同职责。带执行租约的应用注册表可以与 Cordis 服务共存：Cordis 控制可见性与激活，租约注册表控制准入和已开始调用的完成。没有明确 owner 和顺序规则时，应避免维护两套重复贡献事实。领域事务、远程权限、UI scope 与 lease/drain 政策属于应用 SDK。不能仅因为两者返回同一接口，就把 lease borrow 换成普通服务查找。

## 宿主装配、资源与诊断

`BootGenericAsync` 准备 context、挂载配置、等待当前工作、审计失败，并在启动失败时清理。它不提供 DSH home 服务，也不设置默认 required 集合。依赖 Pending 默认合法，除非应用明确要求当前已出现的条目必须激活。required 名称不安装缺失模块、不要求不存在的条目出现，也不无限等待未来提供者。应用 readiness 保持显式。

`BootAsync` 保留既有签名、`DshRequiredEntries` 默认值和 `dshHomePath` 服务。两个入口共享机制。调用方拥有返回的 context，resolver 也仍由调用方管理。已有 `ProfileSession`、HMR 和 [Generic Host 集成](usage.md)继续可用；借用的容器服务由原容器释放。

向 `PatchResources.Read` 传入显式程序集与 manifest 资源名称，并在项目中用 `EmbeddedResource LogicalName` 固定该名称。每次调用都打开部署程序集中的资源、关闭流并通过 `ConfigurationFile` 解析。它不搜索源码、包缓存或程序集清单，不缓存程序集或解析结果。资源缺失会标明程序集和名称，格式错误保留原始 parser 异常。把条目传给 `EntryPatches.Apply`、boot 或既有 reconciliation 路径即可。读取资源不重定向模块名称，也不隐式激活；patch 应用保留既有替换/合并规则。

`AuditAsync` 报告模块解析失败、缺失依赖和激活错误。`Fiber.FailurePhase` 根据实际失败操作区分配置与 Apply，不通过异常类型猜测。disabled 表达式诊断保留自己的阶段。原始异常继续用于短期调试。长期保存报告前，对各项 `EntryDiagnostic` 调用 `ToSnapshot()`：快照只包含名称、状态、复制的依赖名称和错误文本。不能因为已经有快照，就继续永久保存原 `StartupException`、日志参数对象或其他插件引用。回调错误使用回调出口，CLR 卸载状态使用 `ClrUnloadObservation`。

## 应用配置与客户端消费

完整插件生命周期从 [ManagedPlugin](../examples/ManagedPlugin/README.md) 开始，再按 [ManagedApplication](../examples/ManagedApplication/README.md) 构建本地 feed、安装插件、编辑配置并移除。[CLI 指南](../tools/Cordis.Cli/README.md) 使用同一套宿主操作。下面的 Probes 示例演示较小的客户端消费边界。

复用 Session 既有的 `ConfigurationOperations`。读取新 revision 后，通过 `MutateConfigurationAsync` 或选中字段的 `MutateSettingsAsync` 提交有序 SET/unset。库验证完整最终候选并写入一次；对象 unset 恢复继承，数组 unset 删除元素。普通字段保持原有重启行为；`liveOnly` 另要求已捕获 live 边界及兼容的普通 effective 值。分别处理 `Saved`、`Applied` 与恢复诊断。旧 Config delegate 与高级 `ConfigSchema<T>` 作者入口继续保留。

通过 `descriptor.WithMetadata(new ConfigurationMetadata { ... })` 显式声明应用元数据。renderer role、本地化 description、badges、hidden/disabled/collapse、link/comment、extra plain 数据，以及 min/max/step/pattern/loose 提示由不可变 Core annotations 承载；它们描述既有 validator，不增加验证行为。嵌套固定 live 路径使用 `ConfigSchema<T>.WithVolatile(path, projection)`。Settings 省略普通兄弟字段，把 secret 值脱敏为仅表示存在性的 sidecar。每项编辑检查可见的声明路径；含不可读 secret/hidden 后代的祖先替换会被拒绝。参见[完整声明、Settings 与发现示例](configuration-description.zh.md)。

两条导出是独立合同：`ToSchemastery` 生成浏览器表单的 uid/refs envelope，`ToJsonSchema` 生成 JSON Schema 2020-12 声明并明确标注 runtime 限制。`ReadSettingsSchemasAsync` 选择 live 字段，移除 hidden 属性与默认值；完整配置导出需要宿主独立授权。激活前的 `ConfigurationSchemaDiscovery.DiscoverAsync` 接收显式 resolver，读取原生 group/include 源，无须启动 Context，也不写入 include fallback 文件。import/capture 属于受信作者代码；validator、Apply、raw 表达式和 lazy builder 不执行。

示例的 primitive live 视图要求宿主显式选择字段并指定隐藏字段。不要直接把 raw 配置读取暴露成浏览器 Settings 响应。传输只演示 loopback；部署宿主负责认证、授权、请求限制及自身政策。保留的 DSH 表单模型暂存草稿，通过真实宿主保存。SET-only 不支持继承 reset，也不把多字段保存拆成连续写入。在仓库根目录构建运行：

```console
npm ci --prefix reference --ignore-scripts
node reference/node_modules/typescript/bin/tsc -p examples/Probes/client/tsconfig.json
node scripts/build-application-client.mjs artifacts/application-client
dotnet run --project examples/Probes/Probes.csproj -c Release -- --application-host http://127.0.0.1:17639/ artifacts/application-client
node scripts/application-client-consumer.mjs http://127.0.0.1:17639 artifacts/application-client
```

最后一条命令在另一终端运行，只修改生成的示例制品，以验证新的内容代。向 `/stop` 发送 POST 可结束示例宿主。制品布局复用上游 web/client 声明，不可变 ESM 交付是原生适配。示例客户端仍是有界消费示例；声明导出和发现是上述独立库 API。既有作者门禁在源码与独立包消费中重复这条真实客户端链，指定时包含 Native AOT。

## 部署与验证边界

下述候选准入与协调 metadata 保存 API 需要 `0.2.0-alpha.4` 或更高版本。alpha.4 当前为发布候选；已发布的 alpha.3 包不提供此合同。

包与 bundle 选择的产品政策通过 `session.ConfigurationOperations.AdmitProfileAsync` 设置。回调接收 `ProfileCandidate.ManifestJson`（拟保存的确切文本）、`ConfigurationJson`（不执行表达式的有效原始根条目），以及包含来源层和跳过选择的独立 `Composition` 视图。安装时，`Package` 还提供准备目录及计划发布目录，政策可在 Publish 前读取产物；其他操作中它为 null。抛出异常即拒绝。应校验库提供的候选，不再在 `IProfilePackageToolchain.PublishAsync` 中重新读 Profile 并预测另一份候选。准入期间不要重入配置操作。产品自身政策输入须保持稳定直到操作结束；此回调不冻结外部 SDK 或应用状态。`ProfileSession` 自动提供候选应用路径。

若定制 `ReconcileAsync`，应在其后设置 `ReconcileCandidateAsync`，明确提供与该定制对应的候选应用。只替换旧回调会在无准入时保持旧行为；启用准入时不会悄悄批准未绑定候选的应用。删除依赖的准入先于工具链解除运行时映射，拒绝时可以保留已单独批准的取消选择。CLR 制品保留到所有消费者停止后的显式离线删除。

用 `ReadProfileAsync` 读取 metadata 草稿基线，保留返回的 `Revision`，再以 `SaveProfileMetadataAsync(text, revision)` 提交编辑后的 JSON。保存会排在安装之后等待，以 `profile-conflict` 拒绝失效修订；保留用户草稿，由产品决定下一请求。dependencies 和 `dsh` 仍由既有包、选择和兼容操作拥有。这些方法不增加未经认证的传输入口，等待/冲突交互仍需产品接受。

移动准备输出的工具链在 Prepare 返回前设置 `PreparedPackage.PublicationDirectory`；null 表示目录保持原位。包装器必须保留该值。Publish 可以将相同的相对文件内容移到该目录，并且只增加该包的映射；无关来源或映射变化属于冲突。应分别处理 `PackageChange` 的 installed/selected/application/residual，尤其是已发布后才拒绝的情况。Profile 锁协调遵守协议的写者，不保护任意编辑器保存；提交窗口边界见[兼容性](compatibility.zh.md)。

| 路线 | 代码与配置 | 边界 |
|---|---|---|
| 静态注册、普通 JIT | 共享契约、显式注册模块；patch/profile 仍可动态变化 | 新实现代码需要更新部署应用。 |
| 静态注册、Native AOT | 同一份适用的提供者与宿主源码，使用显式序列化元数据 | 新托管 DLL 代码需要重新发布；不要求运行时扫描程序集或 Emit。 |
| CLR DLL 加载 | 显式 `ClrModuleDefinition` 和共享契约程序集，独立 V1/V2 实现 | 仅普通运行时；宿主引用契约，不引用可卸载实现。 |

CLR 路线将宿主实际使用的契约程序集作为 `sharedContracts` 传给 `ClrModuleResolver`。强类型接口仍是强引用。应释放旧视图、回调、binder、异步工作和异常。生命周期结束、新版本激活、Unload 请求、GC 回收及影子文件删除是独立观察；生产不强制 GC。代码替换回滚与普通配置更新失败保持各自现有政策。`!!js` 仍是可选 Jint 路线，不承诺 Native AOT。

作者接口测试是 .NET 适配证据，不增加上游原断言完成审查的数量。重点覆盖见[类型化事件](../tests/Cordis.Core.Tests/TypedEventTests.cs)、[配置绑定](../tests/Cordis.Composition.Tests/ConfigBindingTests.cs)及[启动/资源](../tests/Cordis.Composition.Tests/AuthoringBootTests.cs)，并配合既有生命周期、宿主与 CLR 测试。作者接口验证命令覆盖示例、负向编译和部署路线；运行时/包变更仍须执行完整 gate：

```console
python scripts/verify-authoring.py
python scripts/verify.py
```

已执行环境与限制见[验证记录](validation.zh.md)，固定 DSH 目标与证据术语见[兼容性](compatibility.zh.md)。列出测试或部署命令本身不表示最新一次执行已完成。

## 2026-10-09 模块导出与生成式 Remote 合同

本次源码续建属于既有应用基础设施范围，见[范围续账](development.zh.md#2026-10-09-应用基础设施范围续账)。这不表示已发布新包批次。以下合同要求使用包含本次续建的源码构建包批次。

### 一个作者包中的多个 CLR 入口

保留 `cordis.plugin.json` 中既有单入口的 `assembly` 和 `entryType` 字段。可选 `exports` 对象声明同一程序集中的其他显式子路径。[独立作者 fixture](../tests/fixtures/ClrMultiEntry/Plugin.cs) 的声明如下：

```json
{
  "assembly": "IndependentMultiEntry.dll",
  "entryType": "IndependentMultiEntry.First",
  "exports": {
    "./second": "IndependentMultiEntry.Second"
  }
}
```

`DotnetPluginToolchain` 将包根登记为 `nuget:independentmultientry`，额外入口登记为 `nuget:independentmultientry/second`。单入口作者无需声明空导出表。静态宿主仍可通过既有 resolver 登记精确请求。模块选择与 Cordis 服务 `Provide` 是两项操作；不要求 Core 新增 `Exports` 成员，也不要求应用提供 `ApiCatalog`。

`ClrModuleResolver` 将归一化 bundle 目录相同的导出装入同一可收集的程序集加载上下文。StableDirectory 就地加载保留的文件；显式 ShadowCopy 共享一个开发副本。已加载 bundle 的所有入口须使用相同模式。同一程序集/入口类型的别名复用插件实例，不同入口类型保留各自插件。Loader Entry 仍分别拥有 raw 配置、Fiber 激活与 effect 清理。移除一个 resolver 租约保留其他导出；最后一个租约才请求 bundle 卸载。移除 resolver 映射前应先停止相关 Fiber。卸载请求、收集与 shadow 删除仍是分别观察的结果。

Resolver 默认与宿主共享 Core、Clr、Composition 合同程序集。其他合同通过 `sharedContracts` 传入宿主的精确程序集；插件私有依赖留在 bundle 内。这一身份边界也覆盖生成的 `ITypertRemoteService` 绑定及 `IClrTypertModule` 贡献。

显式 resolver 登记可以选择同一 bundle 目录下不同的主程序集。每个主程序集在加载前登记自身依赖 resolver 与目录，私有 managed/native 查询全部已登记根。同一路径或字节完全相同的副本复用一个依赖；同一依赖名称的不同 binary 会被拒绝，包括新增入口本可直接复用已加载程序集的情况。这是保守的原生 bundle 规则，不是 ABI 或程序集版本兼容算法。依赖 resolver 未定位的 native 库保留 CLR/OS 查找行为。[多程序集 fixture](../tests/fixtures/ClrMultiAssembly/Consumer.cs) 检查两个入口顺序和实际私有 native 调用。

登记依赖根前，resolver 读取 managed 引用与 P/Invoke 声明，递归沿 resolver 定位的 managed 文件检查候选路径，不执行工厂。因此即使原入口尚未调用依赖，也能检查已声明且可定位的冲突；已选择过的依赖也会检查。任意动态加载与工厂副作用不构成可回滚事务。

替换包含不同导出的 bundle 时，使用 `ClrModuleResolver.ReplaceAsync` 的字典重载，提供所有已登记请求，包括尚未加载的导出和别名。回调可以使用 `Loader.ReplacePluginsAsync` 转移既有 raw 配置并共同切换相关 Fiber。单入口重载继续支持一个导出及其别名。候选准备与路由发布保持单一 bundle 代际；回调副作用和产品状态不构成事务。激活失败沿用既有 Loader 恢复路径，已稳定的 Pending Fiber 仍合法。

### 声明与生成原生 Remote 合同

`Cordis.NET.Composition` 在 NuGet analyzer 目录内交付 Roslyn 分析器。消费该包的作者可以使用 `RemoteService` 和显式 `RemoteMethod` 属性，声明 public、顶层、非泛型 partial 类。边界类型需要显式的源生成 `JsonSerializerContext`。下面的精简声明对应[独立 Remote 作者](../tests/fixtures/TypertConsumer/Author.cs)：

```csharp
using System.Text.Json.Serialization;
using Cordis.Composition;

public sealed record EchoRequest(string Text, int Count);
public sealed record EchoReply(string Text, int Count);

[JsonSourceGenerationOptions(
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(EchoRequest))]
[JsonSerializable(typeof(EchoReply))]
public partial class RemoteJson : JsonSerializerContext;

[RemoteService("sample:remote", typeof(RemoteJson), Namespace = "sample")]
public partial class EchoService
{
    [RemoteMethod]
    public Task<EchoReply> Echo(EchoRequest request) =>
        Task.FromResult(new EchoReply(request.Text, request.Count));
}
```

生成器产出 `EchoServiceTypert.Contribution("IndependentRemote")`、descriptor 与直接类型化调用绑定。插件仍通过 `Context.Provide` 将实际 `EchoService` 提供为 `sample:remote`。登记合同不会创建或激活服务。JSON 命名、成员空性和构造器必需字段遵循所提供的元数据；上述两项 `Respect...` 是作者选择，不是生成器隐式默认。每个普通参数与结果类型都要声明元数据。不受支持的声明编译失败；不受支持的客户端 Schema 形状在客户端生成时失败。

根可空引用标注（例如 `string?` 参数或 `Task<string?>` 结果）由 Roslyn 通过 `TypertCodec.CreateNullable` 传入，因为运行时 JSON 类型元数据会丢失这些标注。codec 增加 null 分支并迁移局部 Schema 引用，保留非空递归子节点。因此生成声明对这些边界暴露 `string | null`。这不代表已完成嵌套泛型空性分析，也不为 Gateway 增加结果 Schema 校验。

`TypertCodec` 在首次使用 `Schema` 或 `Decode` 时延迟准备并检查 Schema，可空输入也会执行检查。Decode 校验已支持的原生子集，再反序列化；Encode 使用所提供元数据序列化，不准备或校验结果 Schema。不受支持的 Schema 特性明确失败。客户端投影将 `prefixItems` 保留为 readonly tuple，支持有界可选前缀、嵌套局部引用及无界的类型化或 unknown 尾部。超出前缀的最小长度、有限的尾部长度上限会被拒绝。[tuple fixture](../tests/fixtures/TypertConsumer/TupleContract.cs) 向生成 binding 提供显式 Schema，区分真实闭合 tuple HTTP 调用与仅 codec/投影的变体证据；它不证明 Roslyn 推断 CLR tuple 类型或完整源类型图。

Remote 方法当前支持必需普通参数与 `Task<T>`，或显式 `RemoteMethod(Stream = true)` 的 `IAsyncEnumerable<T>`。可以增加最后一个 `CancellationToken` 参数传递协作取消，但不能声明默认值。显式 Context 和对象 lookup 声明具有宿主拥有的登记 API；同一 fixture 包含完整作用域与 lookup 示例。

### 登记、调用与撤销合同

在既有 Cordis Context 中创建 `TypertRegistry` 和 `TypertGateway`。`TypertLoader.StartAsync` 通过显式 artifact resolver 发现活跃 Loader Entry 的贡献，不扫描程序集。静态作者通过 `StaticTypertArtifactResolver.Register` 登记生成贡献工厂。动态入口可以同时实现 `IClrTypertModule.CreateTypertContribution()` 和 `IClrPluginModule.CreatePlugin()`；`ClrModuleResolver` 从同一已加载 bundle 和工厂身份提供制品。见[静态消费者](../tests/fixtures/TypertConsumer/Consumer.cs)与[多入口消费者](../tests/fixtures/ClrMultiEntry/Consumer.cs)。

Typert loader 的 owner Fiber 拥有登记及其激活期导入缓存。同一精确模块请求的多个活跃 Entry 共享一项贡献；最后一个匹配 Entry 移除后撤销贡献，除非显式配置了该请求。Registry 在发布前校验贡献并拒绝冲突。Gateway 调用解析活跃 Cordis provider 并检查登记有效性；撤销定义会使保留调用失效。

原生 Gateway 还在成功解析提供者之后、编码成功业务结果或流条目之前检查提供者代际。撤销 Service、lookup 或 Context 提供者不会主动中止已运行的工作；即使生成 definition 仍活跃，旧提供者的成功结果也会被拒绝。[独立生命周期用例](../tests/fixtures/TypertConsumer/LifetimeCases.cs) 在实际异步边界停住各提供者、完成替换，同时检查旧成功被拒绝与当前提供者可调用。这些检查属于原生有效性适配，不是产品退休或排空政策。

动态 bundle 替换时保留 Typert loader owner，在候选准备完成后、切换 provider Fiber 前，向 `SuspendAsync` 传入受影响的精确 Loader 请求名称。Deployment 路由提供别名时，这些名称可能与 CLR resolver 键不同。Resolver 返回并提交后，对这些请求调用 `ResumeAsync`。失败时，仅当 `PluginReplacementFailure.Recovery` 确认为 `Succeeded` 才恢复，否则保持该作用域暂停。恢复传播登记异常，并在失败时撤销所选批次。登记或撤销观察者对同一请求重入生命周期操作会被拒绝。暂停脱离未完成导入，不强制停止 resolver 代码；保留的任务在结束前仍可能保留旧代码。独立多入口消费者验证无关包的贡献身份和调用保持不变、替换后 CLR codec 更新、恢复顺序及旧调用失效。保留的贡献、客户端、服务对象或错误仍可能保留可收集代码；所有权结束后应释放这些引用。

`MapCordisRemote` 将宿主授权的 `TypertGateway` 映射为原生 unary JSON 与 downlink NDJSON 路由。宿主提供授权回调。请求中断与生成客户端的 `AbortSignal` 传递取消信号；`byte[]` 结果通过 JSON base64 表达。Host unary 将信号传给绑定，只在已取消时归一业务失败，不强制中止成功的业务执行。Downlink 读取与取消竞争，之后的清理先等待未完成的原生读取，再在其调用 Context 中释放枚举器。业务或清理始终不结束时，调用可能无法终止，与固定流清理边界一致。此传输不承诺完整固定 Typert wire protocol。

关闭 Cordis root 前，应先释放或排空活动 Gateway 枚举器。枚举器清理需要重新进入该执行域，已关闭的 root 无法执行清理。

使用 `TypertArtifacts.GenerateClient(contribution)` 生成 `.mjs` 和 `.d.mts` 制品，与宿主使用同一 descriptor 和 Schema。生成客户端提供类型化调用及 Remote 结果/错误 envelope：

```typescript
import { createRemote, mountRemote } from "./remote.mjs";

const client = createRemote("/remote");
const echo = client["sample/Echo"];
const result = await echo({
  request: { Text: "hello", Count: 1 },
});
client.dispose();

const mounted = await mountRemote(ctx, "/remote");
await mounted.dispose();
```

这里的 `ctx` 是客户端 Cordis owner。必须 await `mountRemote`：它登记 owner 清理，并向共享 root `remote.<namespace>` 服务贡献方法。方法互不重叠的贡献可以共享该 namespace；重复方法或无关既有服务会被拒绝。撤销只移除该贡献的方法，最终撤销才移除 namespace 服务。任何一种客户端的 Dispose 都停止新调用并中断其活跃 fetch。

### 消费者证据与剩余范围

[多入口门禁](../scripts/verify-clr-multi-entry.py) 独立打包作者 NuGet，通过 `PackageReference` 消费，安装包根/子路径入口，并验证共享身份、独立配置、成功与失败替换、撤销、真实 HTTP 上的生成 TypeScript 调用及错误参数。独立的 [Remote 门禁](../scripts/verify-typert.py) 覆盖原生 Remote 作者链。使用最新本地包批次及所需 Node/TypeScript 依赖。平台验收以[验证记录](validation.zh.md)中已完成结果为准；这些示例本身不能证明 Windows/Linux 或 Native AOT 闭环。动态 CLR 加载要求普通运行时。

剩余源类型图、丰富 Context/owned-value 图、Peer/uplink/event remotes 及二进制 attachment 协议仍未完成。既有 PluginManager、Settings/配置与客户端管理消费者向生成 Typert 合同的迁移仍未完成。既有手写 `MapCordisService` endpoint 仍可使用，但不能据此关闭这些缺口。产品替换准入、权限与业务退休/排空政策仍由产品承担。


## 原生作者模型与 .NET 消费者，2026-10-10

普通 Plugin、Service 与 Config 继续在进程内使用。Remote 是面向其他环境消费者的可选边界。C# 作者继续使用现有 Remote 标记和显式 STJ context；设置 `CordisTypertService` 后，该项目才启用源模型提取。Composition 提供 SDK compiler 工具与构建 target；这条路径不需要 Node 或 TypeScript。

```xml
<PropertyGroup>
  <CordisTypertService>settingsController</CordisTypertService>
</PropertyGroup>
```

构建会在作者包内发布 `cordis/typert/settingsController.cordis.typert.json`。该版本化、编译器无关制品保存声明与序列化事实，不从 RPC descriptor 反推。普通 Remote generator 将其与当前编译逐项比较，旧源事实以 `CORDISREMOTE002` 拒绝。引用声明使用 CLR 元数据及相邻 XML 文档；元数据中不可得的源码初始化值或 getter 实现不会被重建。完整的引用模型组合仍待建设。

独立合同包可以消费该制品的副本，无需引用提供者实现：

```xml
<PropertyGroup>
  <CordisTypertService>settingsController</CordisTypertService>
  <CordisTypertClientModel>settingsController.cordis.typert.json</CordisTypertClientModel>
  <CordisTypertClientNamespace>IndependentSettings.Client</CordisTypertClientNamespace>
  <CordisTypertClientName>SettingsClient</CordisTypertClientName>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

target 在 `CoreCompile` 前写入普通 DTO/client 源码，因此 STJ 能在同一次编译中看到它。同轮 source-generator 输出不能充当 STJ 输入。生成源码由 `Clean` 管理；相同输入的增量输出保持字节和时间戳。已验证边界是这条显式启用的命令行构建路径；IDE/design-time 首次构建仍需单独证据。所选 SDK 必须提供匹配的 Roslyn 程序集，包本身不重新分发它们。

可空 float/decimal 常量初始化值保留作者类型与默认值。缺少 JSON 成员时保留初始化值，显式 null 则替换它。调用者 DTO 与生成的辅助类型共享所选目标 namespace。名称冲突会在编译前被拒绝，诊断标明双方来源，例如 `CordisTypertClientName` 为 `DemoClient` 时的 `DemoClientFailure`。应选择不同的 DTO 或 client 名称；emitter 不会静默重命名公开类型，拒绝投影时会删除其此前生成的输出。

```csharp
using var http = new HttpClient();
using var remote = new SettingsClient(http, new Uri("http://localhost:5000/remote"));
var view = await remote.DescribeAsync();
```

HttpClient 由调用者拥有。直接 unary 调用返回类型化值或抛出既有 `RemoteError`，保留 owner 错误码及独立 JSON details；这是对现有结果信封的原生适配。客户端 Dispose 停止新调用、取消自己的传输请求并拒绝迟到成功结果，不 Dispose 借用的 HttpClient，也不承诺强制终止 Host。

生产 `SettingsController` 每次调用重新解析名为 `settings` 的可选普通 `ISettingsDescribeProvider`。`ProfileSettingsDescribeProvider` 在一个既有 profile 事务内读取所有 host 选择的 namespace，发布脱敏 live 值和移除 defaults 的 Schemastery 声明，区分真实 JSON null 与省略的 secret，并将原生投影诊断保留在固定响应之外。namespace/页面策略、可写和文档存在事实由 host 提供。本适配不提供 base/user 重建或固定上游的单调 revision：可选层省略，revision 保留既有原生字符串。

[独立 .NET Settings 门禁](../scripts/verify-typert-dotnet.py) 打包作者与合同包，再于仓库外构建仅依赖包的调用者。调用者不引用提供者实现，关闭 reflection fallback 并使用静态 metadata。门禁覆盖类型化 describe、脱敏、provider 缺失/失败、重试、已持视图失效重读、definition 撤销/重注册、局部 suspend 和借用客户端所有权；基础类型根、null/default 参数、空状态注解及拒绝不支持投影另有用例。`--aot` 验证静态调用者；动态 CLR Host 仍属于普通运行时边界。正式平台结果以[验证记录](validation.zh.md)中的确切完成检查点为准。

该路径覆盖直接普通 unary 客户端和实际验证的 Settings 数据形状。完整 Settings 写入/editor、PluginManager/客户端管理迁移、富图、完整源类型分析及从新模型生成 TS 仍未完成。已有 TS/Web 输出继续可用并单独验收。
