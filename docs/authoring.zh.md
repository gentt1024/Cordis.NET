# 插件与应用作者指南

[English](authoring.md)

这些可选的 .NET 作者辅助接口复用现有 Cordis 运行时。包使代码可用；模块注册把名称映射到代码；patch/profile 条目选择实例；`Inject` 决定插件何时能够激活。服务身份仍由名称与 realm 决定。这些辅助接口不增加第二套注册表或生命周期。

## 从可运行示例开始

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

生命周期清理后，宿主请求卸载，并分别报告回收与影子文件删除。它从不强制 GC。退出码零表示贡献断言、生命周期清理和卸载请求成功；退出时 `collected=False` 或 `shadow deleted=False` 仍可能是正常结果。待清理的临时影子目录会打印出来，供后续清理。宿主不在运行时还原包，部署后也不依赖源码仓库：普通发布宿主，并传入两个预先准备的 bundle 目录即可。这条 CLR 路线要求普通运行时，不支持 Native AOT。[部署测试](../tests/Cordis.Platform.Tests/ProbeDeploymentTests.cs)另外使用仅限测试的强制 GC 验证最终回收。

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

## 外部回调与所有权

[`SubscribeExternal`](../src/Cordis.Extensions/ExternalCallbacks.cs) 适配接受 `Action<T>` 并返回 `IDisposable` 的来源；应在 Cordis 回调或 `RunAsync` 内调用。它先登记 effect 所有权，再订阅，因此覆盖订阅期间的同步通知和重入释放。每次注册都有独立的有效标记。清理先关闭准入再退订，进入 `RunAsync` 后执行时再次检查标记。同一 Fiber 重新激活不会使旧的排队回调重新有效。

回调返回 `Task`；最终失败会到达必填的 `reportError` 出口，即使根已关闭也是如此。取消独立处理，仅进入可选的取消出口。错误出口可能运行在外部线程，且不应抛出异常。已经开始的工作可能在退订后完成：辅助接口既不取消也不 drain。订阅期间抛错的来源应释放自己已经创建的注册。外部长期保存传入的回调可能持有插件对象。

进入执行域、停止新调用、请求取消和等待在途工作结束，是不同职责。带执行租约的应用注册表可以与 Cordis 服务共存：Cordis 控制可见性与激活，租约注册表控制准入和已开始调用的完成。没有明确 owner 和顺序规则时，应避免维护两套重复贡献事实。领域事务、远程权限、UI scope 与 lease/drain 政策属于应用 SDK。不能仅因为两者返回同一接口，就把 lease borrow 换成普通服务查找。

## 宿主装配、资源与诊断

`BootGenericAsync` 准备 context、挂载配置、等待当前工作、审计失败，并在启动失败时清理。它不提供 DSH home 服务，也不设置默认 required 集合。依赖 Pending 默认合法，除非应用明确要求当前已出现的条目必须激活。required 名称不安装缺失模块、不要求不存在的条目出现，也不无限等待未来提供者。应用 readiness 保持显式。

`BootAsync` 保留既有签名、`DshRequiredEntries` 默认值和 `dshHomePath` 服务。两个入口共享机制。调用方拥有返回的 context，resolver 也仍由调用方管理。已有 `ProfileSession`、HMR 和 [Generic Host 集成](usage.md)继续可用；借用的容器服务由原容器释放。

向 `PatchResources.Read` 传入显式程序集与 manifest 资源名称，并在项目中用 `EmbeddedResource LogicalName` 固定该名称。每次调用都打开部署程序集中的资源、关闭流并通过 `ConfigurationFile` 解析。它不搜索源码、包缓存或程序集清单，不缓存程序集或解析结果。资源缺失会标明程序集和名称，格式错误保留原始 parser 异常。把条目传给 `EntryPatches.Apply`、boot 或既有 reconciliation 路径即可。读取资源不重定向模块名称，也不隐式激活；patch 应用保留既有替换/合并规则。

`AuditAsync` 报告模块解析失败、缺失依赖和激活错误。`Fiber.FailurePhase` 根据实际失败操作区分配置与 Apply，不通过异常类型猜测。disabled 表达式诊断保留自己的阶段。原始异常继续用于短期调试。长期保存报告前，对各项 `EntryDiagnostic` 调用 `ToSnapshot()`：快照只包含名称、状态、复制的依赖名称和错误文本。不能因为已经有快照，就继续永久保存原 `StartupException`、日志参数对象或其他插件引用。回调错误使用回调出口，CLR 卸载状态使用 `ClrUnloadObservation`。

## 部署与验证边界

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
