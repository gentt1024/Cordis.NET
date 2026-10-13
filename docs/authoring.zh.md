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

复用 Session 既有的 `ConfigurationOperations`。读取新的配置 revision 后，通过 `MutateConfigurationAsync` 提交有序 SET/unset。库验证完整最终候选并写入一次；对象 unset 恢复继承，数组 unset 删除元素。普通字段保持原有重启行为；`liveOnly` 另要求已捕获 live 边界及兼容的普通 effective 值。分别处理 `Saved`、`Applied` 与恢复诊断。旧 Config delegate 与高级 `ConfigSchema<T>` 作者入口继续保留。

通过 `descriptor.WithMetadata(new ConfigurationMetadata { ... })` 显式声明应用元数据。renderer role、本地化 description、badges、hidden/disabled/collapse、link/comment、extra plain 数据，以及 min/max/step/pattern/loose 提示由不可变 Core annotations 承载；它们描述既有 validator，不增加验证行为。嵌套固定 live 路径使用 `ConfigSchema<T>.WithVolatile(path, projection)`。Settings 省略普通兄弟字段，把 secret 值脱敏为仅表示存在性的 sidecar。提交编辑检查开放路径；不能间接替换宿主隐藏路径。secret 仍可写，整体替换可能重置省略的 secret。参见[完整声明、Settings 与发现示例](configuration-description.zh.md)。

两条导出是独立合同：`ToSchemastery` 生成浏览器表单的 uid/refs envelope，`ToJsonSchema` 生成 JSON Schema 2020-12 声明并明确标注 runtime 限制。标准 Settings describe 响应包含选择的 live schema，移除 hidden 属性，保留脱敏后的安全默认值，并移除 secret 默认值和 required 元数据；完整配置导出需要宿主独立授权。激活前的 `ConfigurationSchemaDiscovery.DiscoverAsync` 接收显式 resolver，读取原生 group/include 源，无须启动 Context，也不写入 include fallback 文件。import/capture 属于受信作者代码；validator、Apply、raw 表达式和 lazy builder 不执行。

示例的 primitive live 视图要求宿主显式选择字段并指定隐藏字段。不要直接把 raw 配置读取暴露成浏览器 Settings 响应。loopback 宿主使用既有 ASP.NET Core Remote adapter 和共享 Connection；部署宿主负责认证、授权、请求限制及自身政策。Settings 生成命令同时输出生成包装及其共享运行时／类型声明。保留的 DSH 表单模型暂存草稿，通过真实宿主保存。SET-only 不支持继承 reset，也不把多字段保存拆成连续写入。在仓库根目录构建运行：

```console
npm ci --prefix reference --ignore-scripts
dotnet run --project examples/Probes/Probes.csproj -c Release -- --settings-client artifacts/settings-client
node reference/node_modules/typescript/bin/tsc -p examples/Probes/client/tsconfig.json
node scripts/build-application-client.mjs artifacts/application-client
dotnet run --project examples/Probes/Probes.csproj -c Release -- --application-host http://127.0.0.1:17639/ artifacts/application-client
node scripts/application-client-consumer.mjs http://127.0.0.1:17639 artifacts/application-client
```

最后一条命令在另一终端运行，只修改生成的示例制品，以验证新的内容代。向 `/stop` 发送 POST 可结束示例宿主。制品布局复用上游 web/client 声明，不可变 ESM 交付是原生适配。示例客户端仍是有界消费示例；声明导出和发现是上述独立库 API。既有作者门禁在源码与独立包消费中重复这条真实客户端链，指定时包含 Native AOT。

## 部署与验证边界

下述候选准入与协调 metadata 保存 API 为 `0.2.0-alpha.4` 引入，alpha.3 不提供。当前源码还包含[标准 Settings 迁移](settings-contract-migration.zh.md)等后续合同修正，需要未来使用新的包版本。这些变更应通过当前 project reference 或隔离本地验证 feed 消费，不替换已发布的 alpha.4 包。

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

显式 Remote 类可以继承 `Service<TState>`。其 `CreateView` 可以为每位 caller 新建视图并共享 provider 的 State，见[独立作者示例](../tests/fixtures/TypertConsumer/Author.cs)。生成绑定调用该视图。Gateway 检查底层注册及原始 provider 值；新建视图不会使 provider 退役。普通同进程 Service 消费仍直接使用 Core API。

生成的元数据 codec 通过 `TypertCodec.CreateDeclared` 恢复共同声明模型中被 CLR 元数据擦除的空性，包括已支持的集合元素和字典值。生成查询在具体 Schema 路径标识完整声明使用位置，即使 STJ 复用引用，也能区分嵌套可空与非空约束。始终忽略的成员不覆盖有效 JSON 属性。作者无需添加新注解。STJ 继续负责命名、转换器与序列化；这不代表支持任意泛型类型，也不为 Gateway 增加结果 Schema 校验。

`TypertCodec` 在首次使用 `Schema` 或 `Decode` 时延迟准备并检查 Schema，可空输入也会执行检查。Decode 校验已支持的原生子集，再反序列化；Encode 使用所提供元数据序列化，不准备或校验结果 Schema。不受支持的 Schema 特性明确失败。客户端投影将 `prefixItems` 保留为 readonly tuple，支持有界可选前缀、嵌套局部引用及无界的类型化或 unknown 尾部。超出前缀的最小长度、有限的尾部长度上限会被拒绝。[tuple fixture](../tests/fixtures/TypertConsumer/TupleContract.cs) 向生成 binding 提供显式 Schema，区分真实闭合 tuple HTTP 调用与仅 codec/投影的变体证据；它不证明 Roslyn 推断 CLR tuple 类型或完整源类型图。

Remote 方法支持必需普通参数，以及带受支持常量默认值的可选参数；返回 `Task`、`Task<T>`，或通过显式 `RemoteMethod(Stream = true)` 返回 `IAsyncEnumerable<T>`。省略可选参数时使用作者默认值；显式 null 仍是独立的 wire 输入，受参数的 null 合同约束。末尾 `CancellationToken` 参数提供协作取消，可以声明 `= default`，不属于 wire 参数。[可选参数消费者](../tests/fixtures/TypertOptional/OptionalConsumer.cs)覆盖省略、null 与必填输入。显式 Context 和对象 lookup 声明具有宿主拥有的登记 API；Typert fixture 保留完整作用域与 lookup 示例。

### 登记、调用与撤销合同

在既有 Cordis Context 中创建 `TypertRegistry` 和 `TypertGateway`。`TypertLoader.StartAsync` 通过显式 artifact resolver 发现活跃 Loader Entry 的贡献，不扫描程序集。静态作者通过 `StaticTypertArtifactResolver.Register` 登记生成贡献工厂。动态入口可以同时实现 `IClrTypertModule.CreateTypertContribution()` 和 `IClrPluginModule.CreatePlugin()`；`ClrModuleResolver` 从同一已加载 bundle 和工厂身份提供制品。见[静态消费者](../tests/fixtures/TypertConsumer/Consumer.cs)与[多入口消费者](../tests/fixtures/ClrMultiEntry/Consumer.cs)。

Typert loader 的 owner Fiber 拥有登记及其激活期导入缓存。同一精确模块请求的多个活跃 Entry 共享一项贡献；最后一个匹配 Entry 移除后撤销贡献，除非显式配置了该请求。Registry 在发布前校验贡献并拒绝冲突。Gateway 调用解析活跃 Cordis provider 并检查登记有效性；撤销定义会使保留调用失效。

原生 Gateway 还在成功解析提供者之后、编码成功业务结果或流条目之前检查提供者代际。撤销 Service、lookup 或 Context 提供者不会主动中止已运行的工作；即使生成 definition 仍活跃，旧提供者的成功结果也会被拒绝。[独立生命周期用例](../tests/fixtures/TypertConsumer/LifetimeCases.cs) 在实际异步边界停住各提供者、完成替换，同时检查旧成功被拒绝与当前提供者可调用。这些检查属于原生有效性适配，不是产品退休或排空政策。

动态 bundle 替换时保留 Typert loader owner，在候选准备完成后、切换 provider Fiber 前，向 `SuspendAsync` 传入受影响的精确 Loader 请求名称。Deployment 路由提供别名时，这些名称可能与 CLR resolver 键不同。Resolver 返回并提交后，对这些请求调用 `ResumeAsync`。失败时，仅当 `PluginReplacementFailure.Recovery` 确认为 `Succeeded` 才恢复，否则保持该作用域暂停。恢复传播登记异常，并在失败时撤销所选批次。登记或撤销观察者对同一请求重入生命周期操作会被拒绝。暂停脱离未完成导入，不强制停止 resolver 代码；保留的任务在结束前仍可能保留旧代码。独立多入口消费者验证无关包的贡献身份和调用保持不变、替换后 CLR codec 更新、恢复顺序及旧调用失效。保留的贡献、客户端、服务对象或错误仍可能保留可收集代码；所有权结束后应释放这些引用。

`MapCordisRemote` 将 Host 持有的 `TypertHostConnection` 接到标准 HTTP unary 与文本 WebSocket mux 路由。在已有 Context 执行域内创建 Connection，并通过 `UseWebSockets()` 注册 ASP.NET WebSocket 中间件。Connection 拥有 operator Peer；请求与 socket 借用它。授权仍由 Host 回调决定。socket 退出释放 Peer 上的登记，不销毁 Peer。

在同一执行域内通过 `owner.Provide("connection", connection)` 发布该 Connection，直接 Gateway 调用便会借用其 operator，并观察当前服务的替换。未挂载 Connection 时，Gateway 在自己的 Context 下延迟创建并拥有一个 operator。流在清理结束前保持初始 operator。宿主释放 Connection 时须撤销服务注册；Gateway 借用 Peer 不会接管该职责。

unary 使用固定的 `client-request`／`server-response` 封套及关联 `rpcId`。Host 作者返回业务值、`Task` 或声明的流，共享运行代码负责帧和失败。Gateway 将取消期间的业务失败规范化；取消不能证明写入从未执行。流清理先等待尚未完成的 native read，再在 Invocation Context 中释放迭代器。关闭 Context 前须排空活动迭代器。

通过 `TypertArtifacts.GenerateClient(contribution)` 生成 `.mjs` 与 `.d.mts`，同时将 `TypertClientRuntime.Module` 保存为同目录的 `cordis-remote.mjs`，将 `TypertClientRuntime.Declaration` 保存为 `cordis-remote.d.mts`。`Cordis.ClientAssets` 为标准模块统一输出这些共享资源。

```typescript
import { createRemote, mountRemote, TypertConnection } from "./remote.mjs";

const connection = new TypertConnection(new URL("/remote", location.href));
const client = createRemote(connection);
const result = await client["sample/Echo"]({ request: { Text: "hello", Count: 1 } });
if (!result.ok) console.error(result.error.code, result.error.message);
await client.dispose();
const mounted = await mountRemote(ctx, connection);
await mounted.dispose();
await connection.dispose();
```

必须等待 `mountRemote`，它在所有者上登记清理，并向共享的根 `remote.<namespace>` Service 贡献方法。不同贡献可以共享命名空间，重复方法或无关 Service 会被拒绝。撤销只移除本贡献，最后一个贡献退出才撤销命名空间。客户端释放等待自身工作，不关闭借用的 Connection；应用负责释放 Connection。可选的 fetch 与 WebSocket 工厂配置应用的传输身份，不改变生成方法或 Host 授权。

共享运行时支持 unary 结果、unary multipart 附件恢复、下行及 typed uplink mux，并供生成式 .NET/TS 客户端消费。[验证记录](validation.zh.md#共享-remote-connection-包门禁2026-10-12)区分冻结包门禁、固定 DSH 互通及 Native AOT 证据。选定的反向 Context／owned-value 和固定事件转发见[Remote 事件](remote-events.zh.md)。原 base64 线格式与每方法 NDJSON 路径已退出。

### 消费者证据与剩余范围

[多入口门禁](../scripts/verify-clr-multi-entry.py) 独立打包作者 NuGet，通过 `PackageReference` 消费，安装包根/子路径入口，并验证共享身份、独立配置、成功与失败替换、撤销、真实 HTTP 上的生成 TypeScript 调用及错误参数。独立的 [Remote 门禁](../scripts/verify-typert.py) 覆盖原生 Remote 作者链。使用最新本地包批次及所需 Node/TypeScript 依赖。平台验收以[验证记录](validation.zh.md)中已完成结果为准；这些示例本身不能证明 Windows/Linux 或 Native AOT 闭环。动态 CLR 加载要求普通运行时。

选定的固定事件工作流、反向 Context 和 owned-value 处理使用[Remote 事件](remote-events.zh.md)所述共同声明。更丰富的源类型图、其余 Peer 工作流和浏览器出站 scoped 装配仍未完成。operator Peer 所有权、typed uplink 和 unary multipart 现已有实现与有限验证，不据此关闭剩余职责。Settings、PluginManager 与选定宿主 emit 通知已通过既有 profile owner 接入生成合同，见[管理迁移](management-remote-migration.zh.md)。原生完整配置、宿主状态和制品交付保留各自独立职责。应用自定义 `MapCordisService` endpoint 不能据此关闭剩余 wire 缺口。产品替换准入、权限与业务退休/排空政策仍由产品承担。


## 原生作者模型与 .NET 消费者，2026-10-10

当前合同是一个声明模型，按用途投影，见[管线阶段表](development.zh.md#模型管线合同)。重建必须保留下述运行时及消费者行为。已完成的历史门禁仍只绑定其记录的源码与包，不证明新接线。

普通 Plugin、Service 与 Config 继续在进程内使用。Remote 是面向其他环境消费者的可选边界。C# 作者继续使用现有 Remote 标记和显式 STJ context；设置 `CordisTypertService` 后，该项目才启用源模型提取。Composition 提供 SDK compiler 工具与构建 target；这条路径不需要 Node 或 TypeScript。

```xml
<PropertyGroup>
  <CordisTypertService>settingsController</CordisTypertService>
</PropertyGroup>
```

构建在作者包内发布 `cordis/typert/$(AssemblyName).cordis.model.json`。service 选择与普通根进入同一 `extract-model` 入口和声明图。若作者程序集名为 `Example`，文件即 `Example.cordis.model.json`。Remote generator 将所选 service 事实与当前编译比较，以 `CORDISREMOTE002` 拒绝漂移；无关声明不成为第二份 service 合同。引用声明保留 CLR metadata 来源及相邻 XML 文档，不从 metadata 重建缺失的源 initializer 或实现。完整引用模型组合仍待建设。

独立合同包可以消费该制品的副本，无需引用提供者实现：

```xml
<PropertyGroup>
  <CordisTypertService>settingsController</CordisTypertService>
  <CordisTypertClientModel>Example.cordis.model.json</CordisTypertClientModel>
  <CordisTypertClientNamespace>IndependentSettings.Client</CordisTypertClientNamespace>
  <CordisTypertClientName>SettingsClient</CordisTypertClientName>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

外部 DTO 声明通过客户端项目的显式引用模型提供：

```xml
<ItemGroup>
  <CordisTypertClientReferenceModel Include="Shared.cordis.model.json" />
</ItemGroup>
```

CLI 对应 `emit-dotnet --reference-models references.rsp`，响应文件每行一个模型路径。Remote 选择仍限于主模型；外部源声明按完整 assembly-qualified 身份解析，复用普通数据发射的 composition。metadata 签名不保留初始化值，不能替代源模型。定义缺失或冲突时拒绝生成并删除旧客户端输出。引用是构建输入，响应文件和生成源码均归 `Clean` 管理。独立调用者消费生成的合同包，不加载 Provider 程序集。

target 在 `CoreCompile` 前写入普通 DTO/client 源码，因此 STJ 能在同一次编译中看到它。同轮 source-generator 输出不能充当 STJ 输入。同一组属性也在 restore 后首次设计时构建中生成语言服务输入，无需先跑普通 build。设计时投影写入 `CordisTypertOutputDirectory` 的 `design-time` 子目录，普通 build/pack 使用常规输出；两者消费相同的显式模型和引用输入。`Clean` 清理两组生成文件；相同输入的增量输出保持字节和时间戳。供打包使用的源码模型提取仍属于普通构建；实时 source generator 读取当前作者 compilation。MSBuild 设计时输入与实际编译器检查不等于实际 IDE 交互或具名 UI 平台验收。所选 SDK 必须提供匹配的 Roslyn 程序集，包本身不重新分发它们。

可空 float/decimal 常量初始化值保留作者类型与默认值。缺少 JSON 成员时保留初始化值，显式 null 则替换它。调用者 DTO 与生成的辅助类型共享所选目标 namespace。名称冲突会在编译前被拒绝，诊断标明双方来源，例如 `CordisTypertClientName` 为 `DemoClient` 时的 `DemoClient`。应选择不同的 DTO 或 client 名称；emitter 不会静默重命名公开类型，拒绝投影时会删除其此前生成的输出。

```csharp
using var http = new HttpClient();
await using var connection = new TypertConnection(http, new Uri("http://localhost:5000/remote"));
await using var remote = new SettingsClient(connection);
var reply = await remote.DescribeAsync();
if (!reply.Ok) throw reply.Error;
var view = reply.Value;
```

调用者拥有 HttpClient 与共享的 `TypertConnection`。普通调用返回 `Task<RemoteResult<T>>`，先检查 `Ok`，再读取 `Value` 或 `Error`。无值作者返回 `Task`，投影为 `Task<RemoteResult>`，无值成功与显式 null 保持区分。缺失 adapter 或 Connection 是本地装配异常。远端失败保留任意所有者 code 与独立 JSON details；传输失败和取消成为 unary 结果。下行调用在异步准备后返回立即开始打开的 `RemoteStreamHandle<T>`，单次枚举直接读取业务项，并等待释放。流的远端错误、carrier 故障、本地误用和外部取消分别以对应异常表达。客户端释放等待自身调用与清理，不释放借用资源。

生产 `SettingsController` 每次调用重新解析名为 `settings` 的可选普通 `ISettingsProvider`。将 `SettingsForms` 挂在会话已有 `ConfigurationOperations` 之上；它根据作者的 live Config 声明推导 active、唯一可寻址的表单。数值 revision 观察 Fiber 身份、完整 schema 和原始配置。层级脱敏读取、完整候选校验和写入都归同一个 profile 队列。describe/update/replace/mutate 使用唯一生成合同。公开调用与退出的 Settings hash 路径见[Settings 合同迁移](settings-contract-migration.zh.md)。

[独立 .NET Settings 门禁](../scripts/verify-typert-dotnet.py) 打包作者与合同包，再于仓库外构建仅依赖包的调用者。调用者不引用提供者实现，关闭 reflection fallback 并使用静态 metadata。门禁覆盖类型化 describe、脱敏、provider 缺失/失败、重试、已持视图失效重读、definition 撤销/重注册、局部 suspend 和借用客户端所有权；基础类型根、null/default 参数、空状态注解及拒绝不支持投影另有用例。`--aot` 验证静态调用者；动态 CLR Host 仍属于普通运行时边界。正式平台结果以[验证记录](validation.zh.md)中的确切完成检查点为准。

该路径覆盖 unary 客户端、受支持的 downlink 流，以及标准 Settings、PluginManager 和通知消费。原生文档集成、富图、完整源类型分析及从普通模型生成 TS 仍开放。已有 TS/Web 输出继续可用并单独验收。


## 类型化 Remote 上行

在作者方法中声明上行，普通 wire 参数仍独立存在：

```csharp
[RemoteMethod("exchange", Stream = true)]
public async IAsyncEnumerable<string> Exchange(
    string prefix,
    [RemoteUplink] IAsyncEnumerable<string?> input,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    await foreach (var item in input.WithCancellation(cancellationToken))
        yield return prefix + (item ?? "null");
    yield return "complete";
}
```

在作者 STJ context 中登记输入值类型，此例为 `typeof(string)`。普通参数继续进入 args，输入序列由 Invocation 提供。共同声明模型保留输入的根空性和受支持集合的元素空性；单独的 `typeof(T)` 无法表达可空引用输入。需要允许缺值的 item 时使用 `IAsyncEnumerable<RemoteOptional<string?>>`，通过 `HasValue` 区分缺失与存在的 null，不增加 wire 对象包装。

```csharp
await using var stream = await client.ExchangeAsync("prefix:", cancellationToken);
stream.Send(null);
stream.Send("first");
stream.End();
await foreach (var item in stream.WithCancellation(cancellationToken))
    Console.WriteLine(item);
```

`Send` 在本地校验并入队，首次读取前即可调用，不代表 Host 已确认；`End` 在队列发送后半关闭上行，下行仍可继续读取。生成返回类型为 `Task<RemoteStreamHandle<string, string?>>`。只有下行的方法保留不具备 send/end 能力的 `RemoteStreamHandle<T>`；两者都只有一个下行消费者。`End` 幂等，终止后也不报错；半关闭或退休后的发送在本地拒绝。`DisposeAsync` 取消本逻辑调用并等待自己拥有的本地工作，不确认远端业务退休。Host 输入 codec 失败会终止逻辑流，即使作者捕获输入异常；同连接的其他流仍可继续。

生成的 TypeScript 返回带 `send`、`end` 和可等待 `dispose` 的 `RemoteDuplexHandle<Output, Input>`，可缺失项使用 `undefined`，与 `null` 独立。其发送边界校验无损 JSON，Host 校验声明 codec；.NET 在入队前还会校验生成的输入 codec。文本 mux 传原始 item 值，缺失时省略 `value`，不增加 binary 帧或对象包装。原始类型、可空值类型、受支持集合／字典及既有有界 DTO codec 仍受各后端实际限制。选定的反向 Context／事件投递与 owned-value 结算复用同一 Connection，见[Remote 事件](remote-events.zh.md)。

Composition 包会把自身声明模型接入 MSBuild 客户端引用集合。其他源模型继续显式使用 `CordisTypertClientReferenceModel`；直接调用编译器时须通过 `--reference-models` 提供对应模型，不能从程序集元数据恢复作者默认值。

## 普通声明模型

在引用 Composition 的项目中，按精确 CLR metadata name 选择普通 Plugin、Service、Config 和 object 声明：

```xml
<ItemGroup>
  <CordisTypertModelRoot Include="Example.Options" />
  <CordisTypertModelRoot Include="Example.Store`1" />
</ItemGroup>
```

泛型数量使用 CLR backtick 形式，嵌套名称使用 `+`。普通根必须声明在本项目，且所有包含类型均公开可访问。显式选择的源码类型若标记 `RemoteService`，也可为非公开类型，使分析能够保留完整合同及诊断。选择不注册能力；运行服务生成仍要求公开、顶层、非泛型的 partial class。普通分析不要求 Remote attribute、partial 声明或 serializer context，不执行构造器、initializer 或方法。

普通根与 Remote 根在正常 build/pack 中使用同一 `$(AssemblyName).cordis.model.json`，打包到 `cordis/typert/`。`CordisTypertModelPath` 控制本地输出路径。定义与应用引用分开。保留泛型参数 owner/ordinal/constraints、直接 base/interfaces、成员签名、重载、事件/delegate、required/accessor/nullability/default 事实、initializer 和文档。attributes 包含 return、getter/setter 及 setter-value 目标；参数事实包括扩展 `this` 与 `scoped`。attribute 类型和 `typeof` 值保留完整声明引用，包括数组值，并进入引用图。源码 partial 声明、隐式成员／成员来源、实例构造器、显式标记的非公开 Remote 方法及作者 JSON metadata 签名作为事实保存，不复制方法体。外部声明保留完整程序集／metadata 身份及记录的签名来源，不重建缺失的源 initializer 或实现。

打包 compiler 在不加载、不引用 provider 实现的情况下验证制品并投影声明目录：

```console
dotnet <compiler> --roslyn-directory <selected-sdk-Roslyn-directory> inspect-model --model Example.cordis.model.json --output catalog.json
```

目录保留分析诊断，不生成 binding、Remote 注册、DTO serializer 或可执行 Service。语法错误和重复声明身份会拒绝提取，并清除拥有的旧输出；其他 pre-generator 编译错误明确记录，因为合法构建可能依赖邻近 generator 输出。源图限 256 个具名定义、64 层嵌套引用；未解析类型/function-pointer 签名作为分析缺口诊断。这不是完整 C# 编译器或反射覆盖。既有 .NET Typert 门禁验证仅包引用消费、增量时间戳、包括首次提取失败在内的 Clean、冷重建、引用身份和拒绝输入。

`CordisTypertService=*` 将作者 service 选入同一声明制品；`CordisTypertModelRoot` 添加显式根。CLI 入口是 `extract-model`，`--roots` 指向根响应文件，`--service` 选择 service；两者可单独或共同提供根。保留所选 service 的可达图。源码及保存输入共享 service、method、DTO、构造器、命名及默认值解释。共享 DTO 的 JSON 命名策略冲突（包括嵌套及递归引用）在受影响投影以 `TYPM113` 拒绝。旧 `extract` 入口、`.cordis.typert.json` 制品及 `CordisTypertDeclarationModelPath` 退出，不构成兼容路线。

选定普通 class 可从保存制品生成 DTO 及静态 STJ context，不要求 Remote 声明或引用 provider 程序集：

```xml
<PropertyGroup>
  <CordisTypertDataModel>Example.cordis.model.json</CordisTypertDataModel>
  <CordisTypertDataNamespace>Example.Consumer.Data</CordisTypertDataNamespace>
  <CordisTypertDataJsonContext>ExampleDataJson</CordisTypertDataJsonContext>
</PropertyGroup>
<ItemGroup>
  <CordisTypertDataRoot Include="Example.Options" />
  <CordisTypertDataReferenceModel Include="Shared.cordis.model.json" />
</ItemGroup>
```

可选的 `CordisTypertDataReferenceModel` items 为所选根可达的外部数据定义提供保存模型。每个输入都按模型身份校验。解析要求完整的 assembly-qualified 声明身份精确匹配，包括程序集版本和公钥 token；只同名不会匹配。引用模型为自己的声明提供源事实，仅有 metadata 占位仍不足以投影。引用项不增加公开根：`CordisTypertDataRoot` 仍只能选择主模型发布的 roots。

对应 CLI 接受 `emit-dotnet-data ... --reference-models reference-models.rsp`。响应文件每行列出一个模型路径，相对路径以响应文件所在目录为基准。不需要引用模型时可省略此选项。所有显式引用输入的身份按确定顺序记录在生成源码中。同身份的源定义冲突会被拒绝；多个输入可含相同定义，仅源码位置不同不构成冲突。缺失定义、不一致的声明形状及生成类型名称冲突也会被拒绝。这验证声明身份与制品内容，不证明模型与任意实现 DLL 的对应关系；应将模型与产生它的源码构建一起发布。

target 在 `CoreCompile` 前调用 `emit-dotnet-data`，复用 Remote caller 支持的 JSON 投影。每次构建读取主模型与引用模型内容；生成结果不变时保持字节和时间戳。生成失败删除旧源码，`Clean` 删除生成源码及所属响应文件。成员省略保留受支持的常量默认值；显式 null 仍有独立含义。生成的数据 carrier 不重放作者构造器或 accessor 行为。引用 DTO 与本地 DTO 使用相同的有界公开 class/property 子集；组合不开放泛型 DTO、继承、自定义 converter 或其他已拒绝形状。若源类型的非公开实例数据成员显式标记 `JsonInclude`，却不在公开分析范围内，提取器会为所属类型记录 `TYPM202`。投影拒绝该类型，包括从引用模型到达它的情况，不再静默丢弃 JSON 成员；无关声明仍可使用。

保存的 Remote 模型也可以在不加载 Provider 程序集的情况下生成 Web 制品：

```xml
<PropertyGroup>
  <CordisTypertWebModel>Example.cordis.model.json</CordisTypertWebModel>
  <CordisTypertService>example</CordisTypertService>
</PropertyGroup>
<ItemGroup>
  <CordisTypertWebReferenceModel Include="Shared.cordis.model.json" />
</ItemGroup>
```

正常 build 将 `remote.schema.json`、`remote.mjs`、`remote.d.mts` 和现有 `cordis-remote` runtime 两个文件生成到 `$(CordisTypertOutputDirectory)web/`。pack 将它们放在 `cordis/typert/web/`；交付时一起携带该目录。对应 compiler 命令是 `emit-web --model Example.cordis.model.json --service example --reference-models references.rsp --output web`。它拥有输出目录中这五个具名文件；生成失败删除它们，Clean 还会删除响应文件。Web target 不生成 C# IDE 输入。

从 `remote.mjs` 导入 `createRemote` 和 `TypertConnection`，为 Host 的 Remote 地址建立 Connection，再调用生成的 endpoint 方法。方法沿用现有 `RemoteResult`、流、取消及借用 Connection 的合同。发射器与运行时 descriptor 生成共用；保存模型路线直接从共同声明和显式引用源模型取得类型，不加载或执行作者代码，不新增线协议或模型格式。

Schema 文件是 JSON Schema 2020-12 定义目录。完整参数对象位于 `#/$defs/<转义后的 namespace/method/arguments>`，也可以选择 `parameters/<wire>`、`result` 和可选 `uplink` 定义。定义 key 中 `/` 转义为 `~1`，`~` 转义为 `~0`。递归引用仍在同一文档内。制品注释记录输入身份；输出不变时保留时间戳。声明初始化值不被宣称为 Host 默认值：该后端不执行构造器或 accessor。

当前后端接受普通闭合公开 DTO class/record、受支持的标量、递归引用、数组、框架集合接口和 string-key 字典，保留 wire 名称、required/nullable 字段及受支持的序列化包含选项。不支持的 serializer policy、自定义 converter、多态、数据继承、应用泛型 DTO、非 string 字典 key、多维数组和 binary attachment metadata 会明确拒绝。事件制品和更广的保存模型后端仍是独立工作。拒绝不等于完成；运行绑定及既有运行时制品发射器保留已成立的支持面。

使用当前 compiler 重新提取声明模型，并重新构建受维护消费者。缺失必需结构化引用或来源事实的输入，以及旧模型输入，直接拒绝，不进行转换。保存制品的身份只验证已保存事实，不能补回遗漏、证明所有作者 JSON 配置或建立与任意 DLL 的对应关系。当前只有一个合同，不保留历史格式 reader 或版本化 Remote 兼容路线。

要从同一声明制品生成直接 unary 客户端，在作者项目中同时选择 DTO 和其标记的 service：

```xml
<ItemGroup>
  <CordisTypertModelRoot Include="Example.SharedData" />
  <CordisTypertModelRoot Include="Example.SharedRemote" />
</ItemGroup>
```

service 的 `typeof` JSON context 及其注册数据进入闭包，包括 internal 源码 context。独立合同项目对上述普通数据属性和客户端使用同一个模型：

```xml
<PropertyGroup>
  <CordisTypertService>sharedData</CordisTypertService>
  <CordisTypertClientModel>Example.cordis.model.json</CordisTypertClientModel>
  <CordisTypertClientNamespace>Example.Consumer.Remote</CordisTypertClientNamespace>
  <CordisTypertClientName>SharedClient</CordisTypertClientName>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

对应命令为 `emit-dotnet --model Example.cordis.model.json --service sharedData --namespace Example.Consumer.Remote --client SharedClient --output SharedClient.g.cs`，它直接消费保存事实，不要求作者实现程序集。.NET caller 后端覆盖受支持 unary、downlink stream、lookup wire 参数、scoped Context 视图、数据／常量及末尾 CancellationToken 参数。独立 caller 必须拒绝无法重建的 serializer 行为，包括自定义 metadata／converter 或不足的 DTO 事实；这不删除分析事实，也不自动拒绝使用作者实际实现及显式 STJ metadata 的 Host。Host 与 caller 共享事实，不取双方限制的交集。显式标记的非公开方法保留在分析中，由 Remote binding 拒绝。只有被选中且有效、仅使用注解的 partial STJ context 可以延后 pre-generator 抽象成员义务，不抑制无关错误。投影成功不证明作者构建成功，也不注册活跃服务。

通过程序集属性声明 Context key 及 wire 身份类型，对应固定上游的 `TypertContextMap`：

```csharp
[assembly: TypertContext("document", typeof(string))]
```

作者程序集及其编译引用中的声明进入同一保存模型。缺少接收方声明、同 key 的类型冲突会被拒绝。声明不注册活跃 adapter。Host 继续注册 `TypertHostContextAdapter` 和 `TypertLookupProvider`；Client 在已有 `TypertRegistry` 中注册由 Fiber effect 持有的 `TypertClientContextAdapter`。

```csharp
await callerContext.RunAsync(owner =>
{
    registry.RegisterClientContext(owner, "document", new(
        ctx => ctx.Metadata.TryGetValue("documentId", out var id) ? id : Undefined.Value,
        _ => null)); // Reverse lookup is unused by this outbound-only example.
    return Task.CompletedTask;
});
await using var connection = new TypertConnection(httpClient, remoteBase);
await using var client = new DocumentClient(connection);
await client.ReadAsync("doc-1");
var scoped = client.ForContext(callerContext, registry);
await scoped.ReadAsync();
```

两种 scoped 合同保持区别。带 `RemoteMethod(Context = "...")` 的方法出现在 scoped 视图中，adapter 身份选择 Host 接收方。direct 方法只有恰好一个 lookup 参数，且其 key 有 wire 类型一致的 Context 声明时，才获得 scoped 投影。普通客户端方法仍接收 lookup 身份；scoped 方法省去该参数，Host 接收方仍为 direct。多个 lookup 的方法保留显式 wire 参数。不会为了传递身份，把 lookup 的 Host 对象重建为客户端 DTO。

`ForContext` 借用 Context 和 registry，两者必须共享 Cordis 执行域。每次调用都在 `Context.RunAsync` 中读取当前 adapter 和身份。`Undefined.Value` 表示该 Context 不提供身份；引用类型身份的显式 null 仍作为 wire 字段值交由 Host codec 校验。值类型身份要求匹配声明的 CLR 类型。缺少 adapter 或身份时，在发送前拒绝；替换 adapter 会影响已有视图。视图不拥有 Context、adapter 或 HttpClient；释放父客户端会取消请求并使已有视图失效。结束 scoped 下行流沿用现有流清理路径。显式视图以 C# 签名表达上游动态 direct／scoped 选择；显式 direct 调用使用父客户端。两种合同和撤销见[共享包消费者](../tests/fixtures/DeclarationModel/SharedConsumer.cs)。

对独立 caller，仅有程序集 metadata 的 JSON context 缺少重建 serializer 所需的源码事实，以 `TYPM115` 拒绝；Host 仍可绑定该实际 context。非有限数值的可选默认值保留为作者事实，Host 在参数省略时可使用它。若所选 JSON number handling 无法传输该值，独立 .NET caller 生成以 `TYPM116` 拒绝，不生成默认调用会在序列化时失败的方法。

独立消费者必须将一个作者 DTO 同时用作普通 root 和 Remote 参数／结果，使用真实 HTTP 作者宿主及仅包引用的生成调用者。DTO 行为由既有数据消费者负责，此路检查保存模型映射和传输。泛型 DTO 投影、更广的 Remote／引用模型后端、design-time IDE 和具名 UI 验证继续开放。完成的平台证据仍绑定在[验证记录](validation.zh.md)中，早先批次不证明本次重建。

## 浏览器 scoped Remote 消费

在所属 Cordis Fiber 中挂载一次生成贡献，通过消费方 Context 读取生成的类型化 namespace：

```typescript
import { mountRemote, getRemote } from './shared.mjs'

const mounted = await mountRemote(owner, connection, contextAdapters)
const remote = getRemote(agentContext, 'sharedData')
const result = await remote.scopedRead({ text: 'hello' })
const document = await remote.read({ suffix: '!' })
await mounted.dispose()
```

`getRemote` 读取已有 `remote.sharedData` service，不创建 Connection、registry 或 owner。Cordis tracing 保留消费方 Context，取出方法作为回调也保持该归属。借用的 adapter 来源提供 `getClient(key)`，adapter 提供同步的 `identity(caller)`；每次调用重新读取 adapter 和身份。若现有 Context adapter 所有者同时提供 identity 与 resolve 操作，可以与反向事件复用它。

接收方 scoped 方法填入声明的身份字段，用来选择 Host Context。lookup scoped 方法填入声明的 lookup 参数，不改变接收方。已定义的身份（包括 null）选择 scoped 路线。undefined 或缺少 adapter 只有在方法本来存在直接调用路线时才允许回到直接 lookup；仅接收方 scoped 的方法报告装配失败。直接 lookup 重载仍要求 wire 身份。普通调用和流保留各自的错误合同，生成参数对象保留省略与显式 null 的区别。

挂载贡献拥有方法和取消职责。释放它会拒绝留存回调并结束自己的工作，借用的 Context、adapter 与 Connection 保留各自所有者。namespace 登记继续沿现有挂载队列组合与退役。`ctx` 成员保留给 Cordis tracing。显式 endpoint client 仍用于直接 wire 调用；scoped 应用代码使用上面的 Context service。

普通 DTO/STJ 后端也接收共同 wire 模型已识别的框架 `IEnumerable<T>`、`ICollection<T>`、`IReadOnlyCollection<T>`、`IList<T>`，与已有 list、数组和字符串键字典共用投影。引用元素声明保留默认值与空性。这些是由 STJ 序列化的数据载体，不执行作者自己的 enumerable 实现，也不表示支持任意泛型类。

## 注册 scope 与 Peer 事件路由

在所有者的 Cordis 执行域内创建注册 scope，并向其不透明 key 分发事件：

```csharp
await using var scoped = Scope.Create(owner, subject);
scoped.Context.On("app/changed", (_, args) => {
    Observe(args[0]);
    return Undefined.Value;
});
owner.Events.EmitWith(Scope.Target(subject, subject), "app/changed", change);
```

subject 可以实现 `IEventFilter`，载体保留其过滤器。载体不暴露 subject 属性；监听者需要真实 subject 时，将它放入事件参数。生成的 Remote waterfall helper 自动处理这一点，公开调用签名不变。

`Scope.Of(context)` 读取最近的继承标签。向 `Scope.Create` 提供 `parent` 可以绑定路由祖先；需要受控重绑时，保留 `Scope.BindParent(key, parent)` 返回的句柄。路由父链不转移所有权。`DisposeAsync` 等待所属清理，包括有序复合 effect 先前通过 `DisposeRawAsync` 启动的清理。

Remote 方法通过 `TypertInvocation.Current!.Peer!` 取得准入后的 Peer，并通过 `peer.Context` 注册。以 `parent: peer` 创建的子 scope 会向上路由到这些 Peer 监听者。不要释放借用的 Peer Context。socket、逻辑流和调用方都不拥有共享 Connection 的 Peer，也不需要用请求参数指定它。直接 Gateway 调用中由所有者持有的 fallback 使用同一机制。

### 有方向的数据 Web 合同

可省略参数与可空值是独立事实。`[DisallowNull] double? expectedRevision = null!` 接受省略或数值，但拒绝显式 JSON null。生成的 TypeScript 参数为 `expectedRevision?: number`。

同一 DTO 可以有不同的输入与结果形状，无需第二份声明。`[JsonRequired, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Count { get; set; }` 要求输入包含 Count，输出则允许省略 Count。带 DisallowNull 的可空 getter 仍允许结果为 null，同时拒绝输入 null。使用生成方法的参数和结果类型，不要把一个方向的类型直接复用于两个方向。构造默认值由真实 Host 执行；非可选构造参数必须提供，即使 RespectRequiredConstructorParameters 为 false 或省略。

`new TypertConnection(base)` 与 `new TypertConnection(base, globalThis.fetch)` 都使用标准 Connection。绑定函数和箭头 override 继续受支持；消费者不需要手动绑定原生函数来补偿库内调用方式。

生成的 .NET 方法自动使用相同的方向合同。必填的零值／默认值可以正常传入，即使作者写出结果时会忽略这些值，输入编码仍会发送它们。返回 DTO 接受作者允许的 null 和省略的默认字段；无需通过覆盖序列化选项补偿方向差异。

可选 JsonElement 字段在作者声明条件省略时，可以用 Undefined 表示省略。显式 JSON null 是已提供的值。例如 Settings unset 操作不赋值 Value；set 操作则赋予 JSON 值，并继续接受现有表单校验。

### 按 Scope 分层的贡献注册表

同时从注册 Context 获取可见性与清理所有权。一个注册表可以在同一层保留多个表：

```csharp
sealed class MenuLayer : IScopeLayer
{
    public NamedEntries<string> Commands { get; } =
        new(name => new InvalidOperationException($"Duplicate command: {name}"));
    public AnonymousEntries<string> Hints { get; } = new();
    public bool IsEmpty => Commands.IsEmpty && Hints.IsEmpty;
}

var layers = new ScopedLayers<MenuLayer>(_ => new(), NotifyChanged);
var registration = layers.Effect(caller,
    layer => layer.Commands.Insert("refresh", "Refresh"), "refresh-command");
var visible = layers.Merge(Scope.Of(caller), layer => layer.Commands);
await registration.DisposeAsync();
```

在所有者的 Cordis 执行域内注册。返回的 EffectHandle 同时归 caller 所有，显式释放可选且幂等。值保持借用。Peek 只读取一个 scope 自己的贡献，ChainLayers 读取继承覆盖层。读取不创建层。具名与匿名视图在非空表代际内保持实时；清空后的代际不跟随后续注册。Remote 方法可以使用当前 Peer Context，无需显式身份参数。

### 打开 Settings 文档

在同一 Context 上挂载使用会话 ConfigurationOperations 的 SettingsForms 和 SettingsController。默认 Controller 在宿主桌面打开 provider 的文档。嵌入式宿主可将自己的 `Func<string, CancellationToken, Task>` 作为 Controller 第二个构造器参数。函数收到的是实际 provider 路径，不是脱敏表单数据的副本。

生成的 .NET 调用为 `await settingsClient.OpenSettingsDocumentAsync(cancellationToken: token)`；标准 TS 调用为：

```ts
const result = await settings['settings/openSettingsDocument']({})
```

两者沿普通 RemoteResult 返回已接纳的 `{ opened: true }`。展示操作时检查 `hasDocument`，像其他 Settings 操作一样处理失败。托管应用示例展示此宿主操作，不将文档下载到浏览器。文档打开不绕过 Gateway 授权，也不提供编辑任意路径的权限。
