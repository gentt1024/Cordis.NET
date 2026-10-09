# 验证记录

[English](validation.md)

## 合作式替换生命周期，2026-10-09

支持范围是契约兼容、所拥有工作能够合作式停止的插件。本修补有意加强固定 DSH HMR `639ed015397290b3745d163aafe02ffee4aa3f84` 的门槛：退休清理失败时拒绝激活候选，而非警告后继续。普通 `Fiber.DisposeAsync` 清理容错和公开 disposer 既有重复调用语义不变，包版本与固定行为基线不变。

Loader 请求释放后，独立等待每个已捕获 fiber 的生命周期结束。`WaitAsync` 在 Disposed 后重新抛出的历史启动错误与新的清理失败分开处理，对应 upstream 失败候选的 `allSettled` 路径。`Fiber.CleanupErrors` 跨重启保留此前移除的 effect 和所拥有子 fiber 的清理失败。失败的兄弟不会阻止其他清理组，既有局部分组语义保留。

替换保留原异常，并附加 `PluginReplacementFailure` 阶段及恢复结果。候选清理不能确认时不恢复旧插件；旧插件恢复失败时停止部分恢复的 fiber。框架 `Succeeded` 确认生命周期已完成，应用仍须另外验证业务就绪。

真实 Generic Host/Kestrel 夹具在同一 Context、resolver 和 Loader 内覆盖多个 Entry、直接 fiber 及无关 Entry。应用准入负责排空已接受工作，并约束服务、保留回调、事件和迟到提交。V2 在 resolver 提交后才开放；恢复 V1 时先等待 resolver 完成失败回退，再核验并开放。不确定结果保持 HTTP 503 与 `RequiresIntervention`，拒绝后续替换。应用更新锁覆盖完整替换直到开放或失败关闭；resolver 内部修改锁不协调应用随后作出的决定。这是应用责任，不是完整包 Update 事务或新增生产 readiness API。

| 演示 | 可观察合同 |
|---|---|
| 合作式 V1 → V2 | 同一运行中 Host；已接受工作排空，旧业务停止，HTTP 提供 V2，无关 V1 仍可用。 |
| 退休前拒绝 | 原图及 V1 业务不变。 |
| 旧清理已开始或失败 | 等待实际退休；迟到清理失败阻止 V2 激活并保留原异常。 |
| V2 失败、V1 恢复成功 | 候选清理及 resolver 回退完成后才核验 V1 业务并开放。 |
| 候选清理或部分恢复失败 | 受影响 HTTP／回调／事件业务保持关闭。部分恢复的 fiber 被停止；清理不能确认的残存工作仍由应用准入围栏约束。 |
| V2 业务校验失败 | 明确暴露 resolver／运行图分叉，要求人工处理并拒绝后续替换。 |
| V2 已验证、resolver 未提交 | V2 就绪但 resolver 仍返回 V1；提交前入口保持关闭。 |
| 并发替换 | A 在提交后、开放前暂停时仍持应用锁；B 等待 A 完成后才拥有自己的关闭校验区间。 |

定向清单包含 88 项 .NET 用例：Host／在线／CLR 28 项和 HMR 60 项；本地完整清单包含 781 项。实际执行结果在交付验证清单中绑定冻结源码。原始 TypeScript 执行、.NET 执行、trace 差分和独立包消费是分别报告的证据类别，数量不相加。

使用 `global.json` 的精确 SDK 和 `upstream.lock.json` 的固定源码 checkout；开发依赖使用各自锁文件。以下命令复现定向检查及本地完整包门禁。包输出必须是新的空私有目录，不是发布目的地。

```powershell
npm ci --prefix reference --ignore-scripts
npm ci --prefix clients/modules --ignore-scripts
dotnet restore Cordis.slnx --locked-mode
dotnet build Cordis.slnx -c Release --no-restore
dotnet test tests/Cordis.Platform.Tests/Cordis.Platform.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~ReplacementHostTests|FullyQualifiedName~OnlineReplacementProbeTests|FullyQualifiedName~ClrTests"
dotnet test tests/Cordis.Extensions.Tests/Cordis.Extensions.Tests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~Hmr"
python scripts/check-docs.py
python scripts/format.py --check
python scripts/verify.py --dsh <pinned-dsh> --origin <pinned-cordis-origin> --upstream-test packages/boot/hmr/tests/modules.spec.ts --package --package-output <empty-private-local-directory>
```

本轮覆盖 Windows x64 Release/JIT，不穷尽线程调度、不证明任意外部副作用可逆，也不覆盖恢复期间独立 owner 消失或 Active 恢复 fiber 已记录清理异常。Linux/AOT、生产产品接线、包持久部署、完整 Update 并发及重启 V2 需要独立验证。ALC 物理回收时限不是验收条件；卸载请求、托管对象回收及影子目录删除仍是不同观察。Hosted CI、远端 SourceLink 获取和发布是独立门槛，不能由本地成功推定。

## Profile 安装与格式整理，2026-10-07

PR [#9](https://github.com/gentt1024/Cordis.NET/pull/9) 合并为 `05fc48731f54660b326eeca1316b100f0bbcfaaf`，Git tree 与已验证 HEAD `04da6a1f02972969f710dd60df76b4ca66146a43` 相同。[workflow #41](https://github.com/gentt1024/Cordis.NET/actions/runs/37601516995) 在 Windows 和 Ubuntu 24.04 均通过，涵盖规范格式、固定参考验证、运行时/包/JIT/AOT 检查及作者合同验证。两平台均上传了已验证包和证据。

该 HEAD 的本地证据包括 761 项 Windows 测试、30 项 Linux 定向测试、32 项 Python 测试及 15 项独立包消费者场景。隔离失败与对应回归覆盖旧 Profile 写回、准入候选关联、patch 等值时的基础配置更新、旧回调定制及物理删除前准入。两个消费者使用的 Cordis DLL 与已检查包内字节相同。这些证据证明所述库合同，不代表下游应用验收或完整的上游 production caller 忠实性。

`0.2.0-alpha.4` 准备只修改版本元数据、内部项目依赖锁和发行文档，不修改运行时源码、SDK、第三方依赖或固定上游基线。此前版本号为 alpha.3 的验证包不能改名或作为 alpha.4 发布；最终版本批次须通过既有 CI/release 门禁。

## 已合并的应用基础设施，2026-10-05

PR [#5](https://github.com/gentt1024/Cordis.NET/pull/5) 已 squash 合并为 `f8deed1b1a654314773ecfa8403ee7ca5d827be9`，源码树与已审查 HEAD `4d57f65dad65611c4e8924b8f3764697b2f46ae5` 相同。[workflow #34](https://github.com/gentt1024/Cordis.NET/actions/runs/37328839303) 在真实 PR checkout `3d4afbbf0ede45d18f34d9b9ff881455918aa3e7` 上通过 Windows/Linux 验证，涵盖固定上游对照、JIT/AOT、CLR 部署、打包与独立消费。两平台实际下载的包批次分别通过载荷/XML、提交元数据、DLL/PDB 身份与 checksum，以及真实远端 SourceLink 取源检查。

真实浏览器检查覆盖外壳模块单例和子路径、默认/自定义 bundle 加载、缺失供应者启动拒绝、断线时插件与草稿保留、握手恢复和实际 graph 撤销。已构建客户端及独立 CLR/CLI 消费者覆盖取消、旧代回调、关闭所有权、启动激活审计、应用参数原样转发、就绪和有界退出。

此前 Windows 的未知安装查询失败（预期退出码 3，实际为 1）仍保留。后续只增加诊断输出，保留原断言；新矩阵通过不代表已查明根因。正常 CLI 关闭仍可能报告 HMR `ObjectDisposedException`，该诊断没有被压制。这些观察与已通过的检查分别记录。

该检查点的 `0.2.0-alpha.3` 当时为发布候选，随后已于 2026-10-06 发布。本节结果证明该实现检查点，不是后续发布批次的证据。最终产物绑定与发布按版本分别验证；此前验证包不能以新版本号上传。

## 应用管理与交付，2026-10-05

实现检查点 `ed2b63d657e5405a79f08308451b385f7cf0969c` 使用 SDK 10.0.111，通过 Windows x64 和 Ubuntu 24.04 x64 本地验证。在先前实施片之上，新增包执行、有序配置编辑/reset、声明导出、HTTP/SSE 管理、CLI 命令与浏览器模块。仅文档更新另行绑定到未改变的实现。固定 upstream lock 与已发布 alpha.1 均未改变。

| 证据类别 | 实际结果 |
|---|---|
| 原生测试 | Windows：732 通过。Linux：729 通过；三项精确的 Windows Job 用例未执行，单独记录 |
| 既有语义 | 既有及升级 trace 与固定 DSH 实现一致；JIT 与静态 Native AOT 一致 |
| 原始上游测试 | 两平台均通过 178 项选定配置/Profile/HMR 用例及 98 项原始 Core/Loader 用例；另一次有界 Linux 运行通过 265 项唯一的官方管理/Settings 用例 |
| 消费与部署 | 两平台均通过源码及独立包作者验证、真实 TypeScript 消费、编译拒绝控制、指定破坏、静态 JIT/AOT、CLR 隔离和 ASP.NET 文件夹/单文件部署 |
| 包内容与源码 | 九个本地验证包及其符号包通过严格检查，隔离 Core 消费者完成实际离线源码帧定位；独立 Windows 无 Git 源码导出使用准确提交字节，通过完整 AOT/打包门禁 |
| 浏览器与应用 | 维护中的应用实际覆盖双模块依赖、SlotCore 撤销、watch 成功/失败、连接恢复、配置编辑，以及同一独立插件作者的安装/移除/重启链，其中 alpha.1 移除后使用新解析器安装 alpha.2 |

Linux 三项平台例外必须匹配准确的测试名、定义和原因，不能满足映射断言声明；其它跳过或失败仍被拒绝。Linux 宿主异常退出另用最终 CLR DLL 验证：残留进程组阻止后继接管，明确停组后才能恢复。Windows Job 清理仍是独立合同。

原始失败均保留：运行中的示例锁住 Windows 构建输出、工作树混合换行导致包检查失败、旧测试门禁拒绝 Linux 平台例外，以及 C 盘耗尽中断独立消费者。经独立规则审查，导出改为直接读取 Git blob，平台报告保留未执行项。迁移已结束的私有产物并使用独立临时目录解决磁盘失败，没有改变源码或断言。同一冻结 Windows 源码已完成的上游步骤作为独立证据保留，其余本地门禁重新执行通过。

默认扩展上游解析运行在 530 秒后停止，未计为通过。最终检查使用先前选定的配置/Profile 用例，并加入相关 HMR 文件。管理首次运行缺少固定 pnpm 可执行文件，补齐该工具后失败文件按原断言通过。这些记录不宣称审计整个 DSH，也不宣称原生断言全部对等。

复跑使用[开发说明](development.zh.md)中的命令，选择下方记录的五个配置/Profile 文件，并增加 `--upstream-test packages/boot/hmr/tests/`。另执行 `npm ci --prefix clients/modules --ignore-scripts` 安装客户端依赖。完整命令、逐步结果、源码/包哈希与原始失败保存在公开树之外。

未执行托管 Windows/Linux CI、远端 SourceLink 取源或发布。本地包仅供验证，不得替换已发布版本。未来发布仍需另行授权新版本，并绑定该次最终提交、tag 和包。

## 应用基础设施实施片，2026-10-04

实现检查点 `06966e49bb287220c56d1a7e326d10f9800cbfb2` 使用 SDK 10.0.111，在 Windows x64 与 WSL2 中真实的 Ubuntu 24.04 x64 通过既有完整门禁。最后的消费者诊断修正 `9e6af99bcf248c316f1c4db3f42bba99e11daa07` 没有修改生产源码、断言或验证器条件；独立只读审查及两平台作者门禁复跑通过。最终交付文档另行绑定到这些未经改变的已验证源码。正式 upstream lock 与已发布 alpha.1 均未改变。

| 检查 | 两平台实际结果 |
|---|---|
| Release 构建 / 原生测试 | 零警告；672 通过，零失败/跳过：Core 148、Composition 395、Extensions 100、Platform 29 |
| 既有语义 | 35 个既有及 3 个升级成对 trace，在 JIT 与真实静态 Native AOT 下均与固定 DSH 一致；JIT 重复三次 |
| 相关上游执行 | 具名五个应用/volatile 文件中的 109 项，另有 98 项来源 Core/Loader；属于参考侧执行，不是原生断言闭合 |
| 新能力消费验收 | 手写/组合 typed 配置、字段编辑、真实 HTTP/未修改的 DSH form-model/store 消费；源码与隔离 NuGet 消费者均覆盖 JIT/AOT |
| 指定原因拒绝 | 九个生产/客户端破坏版编译后因指定运行原因被拒绝，未放宽断言 |
| 部署及制品 | CLR 私有依赖与 collectible 边界；framework-dependent 及 self-contained ASP.NET folder/single-file JIT 部署；八包检查、符号/checksum、离线消费者源码定位；可选适配器和安装后的 CLI |

```console
npm ci --prefix reference --ignore-scripts
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --upstream-test scripts/volatile-config.spec.ts --upstream-test scripts/loader-config-diff.spec.ts --upstream-test scripts/loader-volatile-update.spec.ts --upstream-test packages/boot/app-boot/tests/profile.spec.ts --upstream-test packages/boot/app-boot/tests/user-patches.spec.ts --aot --package --package-output artifacts/application-infrastructure-packages
python scripts/verify-authoring.py --aot --packages artifacts/application-infrastructure-packages
python -m unittest discover -s scripts/tests -p "test_*.py" -v
python scripts/check-docs.py
```

两平台验证器自测均通过。原始报告、源码清单、包哈希、独立审查及实际命令保存于交付证据。Linux 成功 Git checkout 的 324 个原始源码哈希与冻结 ZIP 全部相同；首次 checkout 的两个换行差异使用冻结 bytes 替换，没有语义 Git diff。没有增加全仓 S0 或无关 DSH 套件。

失败保留：Linux 旧 SDK 首次拒绝 restore；无 Git 导出随后通过运行时/AOT 检查，但因没有生成 SourceLink 记录而未通过符号打包。导出符号路径仍未关闭，源码 ZIP 的构建/运行和 Git checkout 的成功符号检查不能关闭该项。Node data URL 错误输出截断导致指定拒绝检查失败，随后独立审查诊断修正并复跑。托管 CI、远端 SourceLink 取源、浏览器 UI 渲染、其他 RID 和 Maker 产品运行均未执行。Settings 仍限定顶层 primitive/live SET-only；完整 schema 导出、reset/多字段、模块图、远程协议和安装工具仍是独立缺口。

固定上游的语言文件用例在文件系统无法同时保存 `en.json` 与 `EN.json` 时调用 `context.skip()`。验证器将该准确断言及原因记录为跳过，不计为通过；仍拒绝失败、未知跳过和不一致的报告计数。Linux 在大小写敏感文件系统上执行该断言。这个参考侧能力结果与 .NET 测试结果分开记录，没有删除任何上游测试。

## 公开审查修复候选，2026-10-02

下方升级记录描述此前交付。当前 0.2.0-alpha.1 候选新增升级矩阵中的有界修复。最终源码验证、作者流程、真实 CLR 部署、portable PDB/源码 checksum 及离线 NuGet 消费者定位结果在本次交付证据中单独记录，不宣称托管 CI 或远端 SourceLink 抓取。

```console
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --upstream-test scripts/volatile-config.spec.ts --upstream-test scripts/loader-config-diff.spec.ts --upstream-test scripts/loader-volatile-update.spec.ts --aot --package --package-output artifacts/review-packages
python scripts/verify-authoring.py --aot --packages artifacts/review-packages
python scripts/verify-clr-deployment.py --dotnet dotnet --rid linux-x64 --output artifacts/clr-deployment
python scripts/package_inspection.py --directory artifacts/review-packages --version 0.2.0-alpha.1 --symbols --debug-consumer --source-root .
```

## DSH 0.2.0-rc.2 升级，2026-10-02

实现检查点 `fc41a420b0e4180da9656c1b5070e827f4050213` 已在 Windows x64 与 WSL2 中真实的 Ubuntu 24.04 x64 完成必需本地检查，使用 SDK 10.0.111 与 Node 24.12.0。最终交付仅改变基线、文档元数据及文档检查；证据另将最终文件清单绑定到未经修改的已验证原生源码。这是本地执行证据，托管 CI 与发布仍待运行。

| 检查 | 实际结果 |
|---|---|
| Release 构建 / .NET 测试 | 两平台均零警告；631 通过，零失败 / 跳过（Core 145；Composition 362；Extensions 99；Platform 25） |
| 实际固定源码的成对 trace | 既有 35 组及新增 3 组在两平台的 JIT、真实静态 Native AOT 下均与固定 DSH 输出一致；JIT 重复三次 |
| 仅原生的静态配置检查 | 捕获的 typed 验证 / 投影、不可变引用、等值快照身份、typed simplify、共享 / 递归图元数据在 JIT 与真实 AOT 下通过 |
| 相关原始源码套件 | Linux：24 个文件中的 5,521 项当前应用 / volatile / HMR 用例；另有 98 项来源 Core/Loader 测试路由到新 vendored Core；仅是参考侧执行 |
| 包与隔离消费者 | 两平台均检查八个包；独立 JIT/AOT Composition 及新增配置 API 消费者；可选适配器包与安装后的 CLI 工具 |
| 作者接口与 CLR 交付 | 两平台均通过编译正例 / 反例、实际部署的 CLR 插件、新 resolver / cache 边界、Probes JIT/AOT，以及同一八包批次的独立消费者 |

参考套件首次运行通过 23 个文件 / 5,495 项用例；剩余 volatile 套件因缺少新增 HMR 源码路由而无法导入。原始失败仍保留；补上路由后，该文件全部 26 项通过。98 项来源测试另外通过。上述合并覆盖不会把首次失败重标为绿色，也不会将源码执行转换为原生断言闭合。

首次 Linux 源码导出打包遗漏了 NuGet 仓库提交，未经修改的包检查器拒绝了该结果。验证器现从源码清单提供检查点提交，完整流程复跑通过。失败日志及成功的源码绑定复跑均保留在单独证据 ZIP 中。CLR 卸载测试也保留了自动观测停止保留异常实例之前的实际失败反例。

使用 `639ed015397290b3745d163aafe02ffee4aa3f84` 与 `56b3d4f725681cf4556c1a8695a709cc3b6eed74` 的准确、干净参考 checkout，不追随分支。Native AOT 需要常规 MSVC 或 clang/zlib 工具链。源码 ZIP 提供 `SOURCE_SHA256.json`，包含包仓库元数据需要的原生交付提交。

```console
npm ci --prefix reference --ignore-scripts
python scripts/check-docs.py
python -m unittest discover -s scripts/tests -p "test_*.py" -v
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --aot --package --package-output artifacts/upgrade-packages
python scripts/verify-authoring.py --aot --packages artifacts/upgrade-packages
```

原生完整流程和作者接口流程，与固定源码套件及成对比较分别执行；准确的机器命令和 SHA-256 清单保存在证据 ZIP 中。[升级矩阵](upgrade-0.2.0-rc.2.zh.md) 记录实现范围及平台适配。历史 631 实例的上游清单及其 36 项已闭合断言审查没有升级为新的 parity 宣称。既有参考工具 `js-yaml` 4.2.0 的高危 audit 发现仍未关闭，未授予豁免或升级依赖；该发现没有改变 .NET 运行时依赖图。

作者接口改进有独立的[执行记录](../verification/authoring-2026-09-22/evidence.json)，覆盖新旧 API 对照、部署与反例。记录区分测试执行和上游原断言审查，不更改兼容性清单。可用 `scripts/verify-authoring.py` 和既有完整门禁复现。

此前发布候选已在 Windows x64 完成完整本地门禁。发布前，同一套已提交门禁还必须在托管 Windows 与 Linux 环境通过。下表描述此前候选，不代表此次作者接口改进的结果。

| 门禁 | 当前 Windows 候选 | 历史双平台运行 |
|---|---:|---:|
| Release 构建与分析器 | 通过，零警告 | Windows 与 Linux 均通过，零警告 |
| 可发现的 .NET 测试 | 456 通过，0 跳过 | 每个平台 455 通过，0 跳过 |
| TRX 分项 | Core 89；Composition 269；Extensions 82；Platform 16 | Core 89；Composition 268；Extensions 82；Platform 16 |
| 锁定的原始源码测试 | 98 个 Core/Loader 与 311 个应用测试通过 | 每个平台相同 |
| DSH/JIT 差分场景 | 34 个匹配；JIT 重复三次 | 每个平台相同 |
| 静态 Native AOT 场景 | 发布成功，34 个 trace 全部匹配 | 每个平台相同 |
| NuGet 包 | 检查八个包；隔离 JIT 与 AOT 消费者通过 | 每个平台相同 |

历史两次运行检查了相同的 191 个源码路径。换行归一化后 191 个全部匹配，其中 187 个原始哈希也完全相同。当前候选使用仍受支持的 .NET SDK 10.0.111 与 Node 24.12.0。

该记录证明此前候选完成了上述检查。它不是形式等价证明、托管 CI 结果，也不对尚未审阅的清单条目作出承诺。包含机器路径和用户名的原始日志保存在维护者私有归档中，不属于公开源码树。

上面的 455 总数只属于该次历史运行。后续验证从实际生成的 TRX 文件计算总数和各程序集数量，不沿用这个数字。


### 在同一验证任务内复用解决方案结果

`verify.py` 成功结束后，`verify-authoring.py --verification artifacts/verification/verification.json` 复用该 checkout 的解决方案 TRX，避免再次恢复、构建和测试整个解决方案。失败或不完整报告、源码变化、不同 SDK/RID/checkout，以及缺失或改变的 TRX 都会被拒绝。authoring gate 仍审核必需测试集，并运行编译、mutation、示例、部署和包消费检查。省略 `--verification` 可独立运行完整 authoring 验证。CI 和 release 使用这一同任务复用路径，不复用其他工作流的包，也不授予发布权限。
