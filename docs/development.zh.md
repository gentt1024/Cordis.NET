# 开发与验证

[English](development.md) | 中文

## 当前支持与发行状态

已发布的预览版为 [0.2.1-alpha.1](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1)，于 2026-10-27 从 tag 提交 `a2728c7e929a01a586fc714d9b397eaf9549e153` 发行，采用固定 DSH 0.2.1-rc.2 基线及已记录的 Windows 适配。[发行证据](validation.zh.md#已发行版本证据)列出正式验证与可下载制品。

当前 main 另有两项**尚未发布**的修正：[PR #28](https://github.com/gentt1024/Cordis.NET/pull/28)跳过未变化的 Typert 生成，并恢复失效输出；[PR #29](https://github.com/gentt1024/Cordis.NET/pull/29)将引用 DTO 所属模型的事实接入生成式 Service 的输入默认值。新版本发布前，如需使用这些修正，应从同一源码提交构建匹配的包与工具。即使源码版本属性尚未变化，已发布的 alpha.1 包也不包含它们。PR 通过不等于新版本发行；发行流程见[发行准备](publication-readiness.zh.md)。

| 范围 | 当前用法与边界 |
|---|---|
| 运行时／组合 | Context、Fiber、服务、静态模块、Profile 和协调编辑；普通 C# 服务不需要 Remote。 |
| Settings／管理 | 数值 revision Settings、生成式 PluginManager 和选定类型化事件复用配置操作及安装任务；Host 决定授权和文档打开策略。 |
| 声明 | 共同模型、显式引用、普通 build／pack 和首次 design-time 生成；下表列出支持和拒绝形状。 |
| Remote | 共享 unary／multipart 和文本 mux Connection、scoped 客户端、反向 Context／owned value、Peer 路由及固定事件；互通有明确范围，不包含所有 DSH SDK 传输。 |
| CLR | 准备／发布回执、稳定 LoadDirectory、重启升级、逻辑移除保留文件；要求普通运行时，业务排空／就绪归应用。 |
| 平台与证据 | 已发布版本在 Windows／Ubuntu 有完整运行时、包与 AOT 验证；macOS 覆盖具名作者与浏览器消费者，不代表完整运行时、应用或 AOT 验收。精确源码身份、失败及限制见[验证记录](validation.zh.md)。 |

## 维护导航

下表上游路径均使用 [upstream.lock.json](../upstream.lock.json)。短表定位所有者和主要检查；[兼容性说明](compatibility.zh.md)记录适配。打包／传输检查保护各自接线，不重复所有者的全部断言。

| 职责 | 固定上游／.NET 适配 | 源码所有者 | 主要检查与独立消费 |
|---|---|---|---|
| 执行与服务视图 | [vendor/cordis/src](https://github.com/deepseek-ai/deepseek-harness/tree/687f282e8625c6b8d11ff58211a90524be2e4317/vendor/cordis/src)；类型化视图替代上下文 JS 重绑定 | [Core](../src/Cordis.Core)：Context、Fiber、ReflectService | [CoreTests](../tests/Cordis.Core.Tests/CoreTests.cs)；隔离包消费 |
| 配置／Settings | packages/boot、packages/api；typed 字段和 STJ 复用现有写入者 | [Composition](../src/Cordis.Composition)：配置操作、SettingsForms | [ConfigurationEditTests](../tests/Cordis.Composition.Tests/ConfigurationEditTests.cs)；[Settings 调用方](../scripts/verify-typert-dotnet.py) |
| 安装 | packages/boot；NuGet／SDK 准备保留操作／任务所有权 | [Extensions](../src/Cordis.Extensions)、[Clr](../src/Cordis.Clr) 工具链 | [ConfigurationSessionTests](../tests/Cordis.Extensions.Tests/ConfigurationSessionTests.cs)；[部署](../scripts/verify-clr-deployment.py) |
| 模型／后端 | packages/typert/generator；Roslyn 和 STJ 替代 TS 分析／codec | [Model](../tools/Cordis.Typert.Generator/Model)、[Generation](../src/Cordis.Composition/Generation)、MSBuild targets | [独立调用方](../scripts/verify-typert-dotnet.py)，含构建／打包／design-time 接线 |
| Remote／事件 | packages/api/gateway、packages/typert/protocol；async 和释放适配所有权 | [Composition](../src/Cordis.Composition) Registry／Gateway／Connection、[AspNetCore](../src/Cordis.AspNetCore) | [Registry](../tests/Cordis.Composition.Tests/TypertRegistryTests.cs)、[事件](../tests/Cordis.Composition.Tests/RemoteEventsGatewayTests.cs)；[真实客户端](../scripts/verify-typert.py) |
| CLR 生命周期 | packages/boot/hmr；collectible ALC 和稳定文件适配加载／退休 | [ClrModuleResolver](../src/Cordis.Clr/ClrModuleResolver.cs)、Loader | [ClrTests](../tests/Cordis.Platform.Tests/ClrTests.cs)；[多入口消费](../scripts/verify-clr-multi-entry.py) |

默认 JIT 包消费入口是 `python scripts/verify-typert-dotnet.py --packages <validated-batch> --output <new-empty-directory>`。五个 `--…-only` 选项最多选择一个；省略时运行默认路径，`--aot` 保持独立。既有负向检查须识别目标拒绝：`recordStruct` 的 caller DTO 投影拒绝、非法 Web 输入进入业务前的 `gateway/input-invalid`，以及编译器对已删除的显式事件模型文件的缺失诊断。文件缺失不同于省略引用模型。

该消费者将 `Cordis.NET.*` 映射到已验证批次、将其 `Independent*` 夹具包映射到临时作者源，公共依赖仍走 nuget.org；清除 fallback 目录并使用私有缓存。既有 assets 检查核对缓存及选定的 Cordis 包版本；公共源中存在同版本包不能证明候选来源。

Typert MSBuild targets 核对求值后的源码／模型／引用列表、引用 XML 文档、编译器与选定 SDK 的 Roslyn 文件、生成选项及拥有输出的内容摘要。输入和输出未变时跳过编译器进程，但仍注册 `Compile`、`AdditionalFiles` 和 `FileWrites`。输出缺失或被改动、输入／选项变化、成功回执缺失或无效时重新生成。启动编译器前使回执失效，仅在生成成功后写入；`Clean` 删除这些状态。这是本地构建状态，不是模型格式或第二套声明权威。普通构建和 design-time 输出继续分离。既有声明及引用模型包消费者在内容、时间戳、失败和冷构建检查之外，同时确认无改动构建不再执行生成。

## 模型管线合同

普通根与 Remote 根使用同一个声明事实模型。提取阶段记录作者事实，各后端按自身支持范围解释并生成输出。所需事实变化时，重新提取所属模型，并使用匹配工具重建受影响的提供者与客户端。固定上游行为和已说明的平台适配决定目标合同，历史试验格式不产生额外兼容要求。

| 阶段 | 输入 → 输出 | 所有者 | 拒绝边界 | 退出路径 |
|---|---|---|---|---|
| 选择与提取 | 有效编译输入及显式普通根和／或 service 选择 → 声明事实、引用图与诊断 | Source reader 与 `extract-model` | 无效根、不可用语法、重复身份或缺失必需事实；分析诊断不等于所有后端都必须拒绝 | 只输出 `$(AssemblyName).cordis.model.json`；普通根不要求 Remote 声明 |
| 校验与组合 | 当前保存事实及可选显式引用模型 → 已校验身份与可达定义 | Model reader／composition | 输入不完整或不一致、身份不符、定义缺失／冲突；引用不增加主模型 roots | 拒绝后重新提取；不保留旧格式 reader、转换器或从 DLL 猜补事实 |
| 按用途解释 | 共同事实加明确后端政策 → 可重新计算的内部投影 | 声明解释器与所选后端 | 不支持的行为由消费后端拒绝，不从分析事实中静默删除 | 内部投影按所选后端重新计算，不另存为第二份合同 |
| 绑定 Host | Remote 事实、作者实际实现与显式 STJ metadata → 类型化 binding 和运行 descriptor | Remote generator；Registry／Gateway 持有发布与调用寿命 | 既有 Host 合同、声明漂移与不可用运行能力；独立 caller 限制不自动禁用 Host | 绑定保留取消与撤销；声明漂移与当前编译核对 |
| 生成消费者 | 同一保存事实及选定目录／数据／客户端政策 → 对应后端已支持范围内的目录、DTO／STJ 或 .NET／TS 输出 | 各用途 emitter | caller 无法重建必需数据／serializer 行为时拒绝该投影，不复制作者业务代码 | 保留 pre-`CoreCompile` 生成及无 Provider 实现引用的 caller；普通根不要求 Remote 注册 |
| 构建与交付 | 当前源码、模型及生成结果 → 包与消费者证据 | MSBuild targets、包检查及既有消费者门禁 | 生成失败删除拥有的旧输出；Clean 与增量构建仍是可观察合同 | 只打包 `cordis/typert/$(AssemblyName).cordis.model.json`；显式模型／引用输入决定生成 |

下表列出现行支持形状，完成检查见[验证索引](validation.zh.md#已发行版本证据)。历史重建检查点不定义当前剩余工作。

MSBuild 按解析得到的完整程序集身份，将引用的 XML 文档作为生成器输入。这样，即使提取器读取实现程序集，而 C# 编译器绑定引用程序集，两边仍保留相同的文档事实。XML 文本参与增量分析，只在引用声明需要时解析。生成器仍使用编译器符号定位实现成员、发射可编译的 C#；调用语义来自共同声明合同。

提取、模型读取和 Host 绑定保留 null 数组属性及引用文档事实。

<a id="alpha5-独立后端处置"></a>

## 生成后端支持范围

按需要的制品选择后端。提取记录共同声明事实，不保证每个后端都能执行作者 serializer 或生成所有 C# 形状。Host 使用实际实现、注册的 STJ metadata 与 codec，独立消费者使用保存事实及显式引用模型。即使 STJ 本身能序列化一个值，运行时 Schema 投影与输入准入仍可能各自报告诊断。

| 声明或用途 | Host 绑定／运行时导出 | 独立 C# Remote 客户端 | 普通 C# 数据（emit-dotnet-data） | 独立 Web（emit-web） |
|---|---|---|---|---|
| 公开 DTO、数组、支持的集合与字符串键字典 | 使用选定 codec 与实际作者 metadata。 | 在声明投影范围内支持。 | 公开顶层非泛型 class，公开可读且有公开 set/init 的属性，无继承／接口或参数化构造器。 | 支持的公开 DTO 形状，保留输入／输出方向。 |
| 显式引用源码模型 | Host 组合保留 Service 漂移检查，引用初始化事实需要所属源码模型。 | 精确程序集限定身份，缺失／冲突源码事实拒绝生成。 | 相同身份与源码要求，引用不增加根。 | 相同身份与源码要求，不加载提供者 DLL。 |
| 值类型 struct 与 readonly record struct | 取决于注册 metadata／codec。 | 支持数据载体，包括经构造器接收 wire 值的只读成员。 | 拒绝。 | 当前 DTO 投影拒绝。 |
| 闭合作者泛型，如 Response<Row> | 在 codec 范围内绑定已注册的闭合类型。 | 支持无约束数据定义及实际闭合参数，不执行任意开放泛型 endpoint 或约束行为。 | 拒绝作者泛型 DTO。 | 拒绝作者泛型 DTO。 |
| getter-only／readonly 成员 | 输出服从实际 metadata 与方向。 | 生成 JSON 构造参数可接收只读 wire 数据，不复制 accessor 体。 | 要求公开 set/init 属性，拒绝字段／getter-only 载体。 | 仅支持的方向性成员事实，不执行 accessor。 |
| System.Guid | 默认 STJ UUID 字符串合同。 | 原生 Guid，包括支持的 nullable／容器组合。 | 支持数据子集中的原生 Guid。 | string 与 UUID Schema，不包含任意 ID 包装或 Guid 字典键。 |
| 公开源码 enum | 使用实际注册 serializer。 | 数值 enum 与识别的默认泛型 JsonStringEnumConverter<TEnum> 政策。 | 数值 enum 与相同的已识别泛型 converter。 | 数值 enum，拒绝字符串转换 enum；核对已说明的数值精度边界。 |
| 静态 DTO 成员默认值 | 使用声明输入默认值与实际 serializer，输出不应用输入默认值。 | 常量及支持的有限 record／集合数据构造，输入投影拒绝带行为表达式。 | 较窄属性子集中的常量／null／enum 初始化器。 | 不执行作者初始化器或构造器，不从目录推定 C# Host 默认值执行。 |
| 继承、多态、嵌套 DTO、非字符串字典键和多维数组 | 取决于显式 metadata 与 codec。 | 不属于一般支持的投影形状。 | 拒绝。 | 拒绝。 |
| 非公开 JsonInclude／缺失必需源码事实 | 实际 metadata 仍归 Host。 | 已分析 included 数据缺失时诊断，不静默丢字段。 | 相同拒绝原则。 | 相同拒绝原则。 |
| 任意 converter、构造器／accessor 业务、仅 metadata 的 serializer context | codec 允许时执行真实代码，导出仍可拒绝。 | 不重放或加载提供者，不能复现的行为及不足的 context 事实明确拒绝。 | 不重放或通过反射修补。 | 不重放或通过反射修补。 |
| 二进制附件 metadata | 支持的运行时 carrier／导出路线保留附件与生命周期规则。 | 不从保存 DTO 事实推定任意附件政策。 | 普通 JSON 数据生成不是附件生成。 | 保存 JSON 投影拒绝附件 metadata，普通 byte[] JSON 与附件政策不同。 |

C# Remote 投影还保留已识别的 ImmutableArray<T> 与 DateTimeOffset 映射。已知框架映射不启用任意 BCL 或作者泛型；enum 别名、flags 与声明顺序保留支持的 STJ 行为，任意 converter 实现仍拒绝。

空性／默认值查询图复用普通递归使用点，其 256 预算只统计同一路径上同一泛型声明的活动使用点，不累计所有兄弟节点，有限路径也可能被拒绝。这与后端限制不同：普通数据投影仍将可达定义限制为 256，并有自身遍历／类型限制；任何后端都不承诺任意 C# 图。

静态生成不能复现合同时，运行时 `TypertArtifacts.GenerateClient(contribution)` 可在支持范围内通过共享 emitter 导出实际 Host 描述。这是显式运行时路线，不是自动反射回退或第二种模型格式。保留一个主模型、显式引用和普通 build／pack 输入，事实变化时使用匹配包／工具重新生成。[作者指南](authoring.zh.md#生成模型与独立客户端)说明输入，[兼容记录](compatibility.zh.md)说明平台适配。

上述 C# struct／泛型／默认值投影支持已在 alpha.1 发布；main 后续的增量生成与跨程序集 Host 默认值修正尚未发布，见[发行与源码状态](#当前支持与发行状态)。支持声明不替代[实际验证证据](validation.zh.md)，也不证明完整 DSH 产品／SDK 互通。

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


浏览器首次启动调用固定 web 激活审计；导入失败、激活失败和等待缺失服务的 Pending 条目会拒绝启动并清理。暂时断线保留插件和草稿。公开客户端在 `options.events` 接受生成通知客户端，并接受 `transport.fetch`、`recovery` 及手动 `reconnect()`。固定连接控制器在固定 `$events` mux 订阅上负责握手截止、重试/退避和离线/上线处理；收到订阅就绪后读取完整 state 和 graph。关闭所有者会拒绝新写入、取消读取并等待撤销。恢复不重放结果未知的写操作。

## 应用调用

```console
cordis run ./profile --resume abc --source app-owned-value
cordis run ./profile --source ./feed --url http://127.0.0.1:5080 --authorization-env CORDIS_TOKEN -- --help
```

启动器只解析 `--` 或首个不认识的 token 之前的自身选项；其余参数原样复制到 `cmdlineArgs` 的 `CommandLineArguments`。解析、帮助和错误属于应用。第一条命令的 `--source` 属于应用，第二条命令在分隔符之前选择包源。不传 `--url` 时 Generic Host 不打开 HTTP 监听。空来源白名单可以运行已安装包；获取新包仍须显式选择来源。

挂载条目前，CLI 还提供 `appReady` 的 `IApplicationReady` 与 `appExit` 的 `ApplicationExit`。就绪订阅应注册为 Context effect。插件树和宿主启动成功后提交就绪，晚订阅者立即回调。应用可在激活中（例如帮助）或就绪后请求退出。首个请求启动固定上游的五秒宽限，包括启动期间的请求；CLI 等待插件树有序释放，超过宽限则以该码强制退出进程。普通启动没有五秒时限。启动/就绪异常返回失败；Cordis 报告 effect 清理错误，不改写请求的退出码。既有 HMR 使用同一就绪结果。进程信号仍由 .NET Host 处理。

库宿主可通过 `ProfileSession.StartAsync(prepare: ...)` 提供这些 Composition 合同并决定自己的退出策略，不必使用 CLI 或另一套运行时。验证流程会编译独立 CLR 应用并执行真实 CLI，覆盖参数边界、帮助/错误、就绪与清理。受控 carrier、浏览器执行和原生管理测试分别记录。





## 剩余支持边界

上方后端表继续列明支持面。WPF、Avalonia、Godot .NET 和 Blazor 仍是预期消费者，具名接入证据尚待完成；它们已不作为 alpha5 发行阻断，但没有退出范围。Rider 验收不能证明其他 IDE。Windows 文档打开证据证明系统交接，不证明编辑器内容或其他桌面平台。HMR 与平台限制见[验证说明](validation.zh.md#已接受风险与证据边界)。

## 历史决定

[发行前检查点](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/development.zh.md)保留原始决定、失败和当时待办。它们用于追溯改动，不是当前任务清单。

## 贡献工具与 CI

### C# 风格与 lint

根目录 `.editorconfig` 是风格规则的唯一来源。固定 SDK 的编译器与 Roslyn analyzers 负责 correctness、nullable、async、lifetime、interop、performance 和 API 误用诊断。构建将警告视为错误。格式化不执行语义修复。

```console
python scripts/format.py
python scripts/format.py --check
```

脚本按 `dotnet-tools.json` 还原仓库本地固定版本的 JetBrains ReSharper GlobalTools，并按锁文件还原 solution 依赖。它对仓库 C# 文件依次执行使用 `Built-in: Reformat Code` 的 `jb cleanupcode`、文件夹和 solution 两种模式的 `dotnet format whitespace`，再执行一次 CleanupCode。solution 模式覆盖条件编译分支。各阶段都使用同一份明确的 C# 文件清单，包含 solution 外的 fixture 和工具源码。后续每一步都必须保持前一步的输出不变。

`--check` 将当前已跟踪文件和未被忽略的新增文件复制到临时目录，包括配置和未提交的改动。依赖还原和格式化均在副本中执行，不向原工作区写回任何内容。任一步修改副本中的 C# 文件都会使检查失败。解析后位于仓库外的文件会被拒绝。CI 在 Windows 和 Linux 上执行此检查。运行需要 Git 和 Python。

Roslyn 定义大括号、换行、空格和缩进，展开单行 block 和内嵌 statement。JetBrains 对签名、参数、调用链和 initializer 补充以 120 列为目标的换行。不可拆分的 token 和字符串内容可以超过该宽度。GlobalTools 仅为开发工具，不是任何 `Cordis.NET.*` 包的依赖。不使用 CSharpier。

为保持两个 formatter 稳定，嵌套循环使用缩进，`for` 分号两侧不留空格，并保留显式换行，包括多行构造函数后调用之前的换行。既有生命周期例外使用精确且带注释的 analyzer suppression；格式化不得改变清理或取消时序。

Rider 和 Visual Studio 会自动读取 `.editorconfig`。在 Rider 或装有 ReSharper 的 Visual Studio 中，使用 **Reformat Code**，并启用 Roslyn analyzers。Visual Studio 内置 formatter 处理 Roslyn 规则；提交前运行脚本以补齐按宽度换行。以固定版本 CLI 的输出为准。纯格式化改动应避免 Full Cleanup，因为它可能改写代码。参见 [CleanupCode 文档](https://www.jetbrains.com/help/resharper/CleanupCode.html)和[换行设置](https://www.jetbrains.com/help/resharper/EditorConfig_CSHARP_LineBreaksPageSchema.html)。

### 构建与测试

安装 `global.json` 指定的精确 SDK，并在 Rider/Visual Studio 和 CLI 中选择该安装位置。禁用 SDK 补丁滚动，使 ILLink 等 SDK 提供的依赖与 CI 和锁文件保持一致。仅使用该 SDK 重新生成依赖锁文件；常规验证使用锁定还原。

```console
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test Cordis.slnx -c Release --no-build
python scripts/check-docs.py
```

定向维护时，Core 的 `InertiaSerializesLoadAndUnload` 负责加载／卸载串行语义，`AsyncYieldExactSequence` 负责正常完成及不同中止位置。Composition 的 `MaintenanceTests.EnablementRejectsMalformedFileWithoutOverwrite` 负责写入前的非法配置拒绝；`LoaderOriginalTests.LoaderInterceptReadiness` 负责 readiness 释放前后的状态观察。

```console
dotnet test tests/Cordis.Core.Tests -c Release --no-build --no-restore --filter "FullyQualifiedName~InertiaSerializesLoadAndUnload|FullyQualifiedName~AsyncYieldExactSequence"
dotnet test tests/Cordis.Composition.Tests -c Release --no-build --no-restore --filter "FullyQualifiedName~EnablementRejectsMalformedFileWithoutOverwrite|FullyQualifiedName~LoaderInterceptReadiness"
python scripts/test-map.py
```

参数实例应对应不同路径或观察。优先使用轻量的所属边界检查，避免搭建额外准备并未参与目标检查的同义夹具。若断言失败会导致收尾等待测试自有屏障，应在 `finally` 中释放该屏障。定向测试结果不能证明完整上游映射已经执行。

运行时、打包、兼容性或发布改动应运行 `python scripts/verify.py`。纯文档改动只需运行文档检查并进行针对性审阅，不要求执行全部 Windows、Linux 或 AOT 门禁。

PR 和主分支 CI 在两个平台分别并行运行 .NET 验证与固定上游源码测试。四个隔离任务分担 4,800 个 linked-resolution 矩阵用例，每组内仍串行执行，第一个任务还执行其余原始套件。每个任务都发现完整的原始用例清单。最终的 `verify (windows-latest)` 和 `verify (ubuntu-24.04)` 检查要求所有运行时任务及分片成功，同平台分片的源码和工具链身份一致，且执行用例的并集与发现清单精确匹配。缺失、重复、失败或意外跳过均使门禁失败。只接受锁定源码清单中明确记录的平台跳过，其余跳过均使门禁失败；被分片过滤排除的矩阵用例不计作已执行。

CI 与 release 均调用 `.github/workflows/upstream.yml`，执行锁定源码发现、每平台四个分片及严格并集。既有 `verify (windows-latest)` 与 `verify (ubuntu-24.04)` 检查要求 native/runtime、可移植作者消费和源码门禁全部成功。`verify.py --defer-upstream-source-tests` 保留 native/源码轨迹差分，只与这个独立必需门禁配合使用。不延后时，`verify.py --dsh ... --origin ...` 在本地发现并执行相同的完整四分片源码范围。不跨运行复用收据。定向复现使用既有 `scripts/verify_candidate_upstream.py` 的 discover/run/aggregate 命令，共用固定 inventory 与 `--run-id`；文件名沿用 S10，源码身份现须匹配正式锁。

验证脚本回归套件也实际运行候选收集器。执行 `python -m unittest discover -s scripts/tests` 前，先用 `npm ci --prefix reference/candidate-tools --ignore-scripts` 安装固定工具依赖；安装工具不会切换行为基线。
