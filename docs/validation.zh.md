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

## 2026-10-09 模块导出与原生 Remote 续建

未发布的源码检查点 `16440e3e0adaac65abf510038495dc7115f1cc6e` 已在 Windows x64 与 WSL2 下的 Ubuntu 24.04 x64 通过完整本地运行时/包门禁和作者链门禁，使用 SDK 10.0.111、Node 24.12.0。两平台副本与两类门禁具有完全相同的 467 项源码哈希清单，均记录执行期间源码未变。Linux 使用同一源码字节的隔离 Git 副本。后续修改只增补此验证记录和原范围对账，不扩展已验证的运行时范围。DSH pin 和包版本未变，本地包批次未发布。

| 证据 | 已完成结果与边界 |
|---|---|
| Release 构建与原生测试 | 零警告/错误。Windows：766 通过，零失败/跳过。Linux：763 通过，零失败，跳过三个仅适用 Windows 的 Platform 用例。Core 148、Composition 437、Extensions 104；Platform 在 Windows 为 77，在 Linux 为 74 |
| 必需完整门禁 | 两平台均通过 `verify.py --aot --package` 与 `verify-authoring.py --verification ... --aot --packages ...`，包括既有固定源码对照、管理/客户端路径、包检查、portable symbols 和离线消费者源码 frame |
| 独立生成式 Remote 作者 | 两平台均验证：消费已交付 analyzer 的 NuGet 作者、指定 `CORDISREMOTE001` 编译拒绝、仅引用包的 JIT 消费、实际 HTTP/NDJSON 与严格 TypeScript，以及静态 Native AOT 发布/执行 |
| 原生合同失败与生命周期 | 实际消费者覆盖必需/错误/重复参数、作者错误、选择的 Context 与对象 lookup、provider 撤销、最后一个 Entry 的定义撤销与旧调用。根可空引用、递归非空子节点、已取消信号下的成功 unary、业务失败归一取消，以及取消读取后的串行清理，均通过 JIT/AOT |
| 独立 CLR 多 Entry 作者 | 两平台均以十个阶段验证标准 NuGet/工具链根与子路径交付、共享程序集/ALC 身份、独立配置、整组失败恢复、变更 DTO 后的成功替换、实际生成 HTTP 客户端、旧调用拒绝与最终撤销。这些动态 CLR 证据需要普通运行时 |
| 生成客户端所有权 | 实际固定 Cordis 拥有挂载方法。互不重叠的贡献共用 namespace，重复方法拒绝。实际 HTTP 和可控迟到传输覆盖撤销、依赖清理、重入安装、同名退役、替换与保留回调 |
| 独立审查与源码校准 | 全新缓存的独立包审查复现根空性缺陷，并以 JIT/严格 TS 验证修复。固定上游 protocol/registry/loader 与选中 Gateway：130 通过、零失败、102 项有意过滤；独立流选择：五项通过、零失败、43 项过滤。这些 Windows 源码执行用于校准，不代表原生断言或 uplink 闭合 |
| 格式、脚本与文档 | C# 源码的四个格式阶段一致；两平台验证器自检均通过 38 个用例。公开 API 与配对文档检查通过 |

失败证据仍保留：严格符号检查曾拒绝尚未对应 Git 检查点的源码字节；隔离 Linux 副本最初缺少仓库元数据；多 Entry 脚本曾使用错误的夹具路径大小写。修正后的检查点和 Linux 路径通过了未放宽的门禁。独立审查暴露根可空标注丢失和过强 unary 取消检查，修复后的包行为已在两平台通过。新 TS 正向检查最初要求可变数组，而生成数组为 readonly；修正消费者声明时未改变生产数组合同。

完整源类型分析、丰富 Context/owned-value 投影、Peer/uplink/events、完整二进制 attachment/wire 兼容，以及既有管理消费者迁移，仍列在[原范围对账](development.zh.md#2026-10-09-应用基础设施范围续账)中。不能强制终止任意业务或清理；宿主必须在关闭 Cordis root 前排空活动 Gateway 枚举器。不声明 Native AOT 内动态 CLR 支持。未执行 hosted CI、这些未发布提交的远端 SourceLink 获取、浏览器渲染或 Maker 运行。原始平台报告和源码/包哈希保存在忽略的本地证据中，未提交机器路径。

```console
npm ci --prefix reference --ignore-scripts
npm ci --prefix clients/modules --ignore-scripts
python scripts/verify.py --dsh ../dsh-reference --origin ../upstream-cordis --upstream-test scripts/volatile-config.spec.ts --upstream-test scripts/loader-config-diff.spec.ts --upstream-test scripts/loader-volatile-update.spec.ts --upstream-test packages/boot/app-boot/tests/profile.spec.ts --upstream-test packages/boot/app-boot/tests/user-patches.spec.ts --aot --package --package-output artifacts/typert-packages
python scripts/verify-authoring.py --verification artifacts/verification/verification.json --aot --packages artifacts/typert-packages
python -m unittest discover -s scripts/tests -p "test_*.py" -v
python scripts/check-docs.py
```

## 2026-10-09 质量修复验证

后续审查修复按入口解析 CLR 依赖与客户端位置 tuple 投影，补正文档中的 codec/Gateway 生命周期限制，并增加独立提供者代际证据。第二轮审查发现延迟依赖冲突可能让被拒绝的根残留在已有 bundle。只读 PE 检查现于登记根之前，递归核对已声明且 resolver 可定位的依赖。managed 与 native 对照均实际执行拒绝之后原入口的首次依赖调用；任意动态加载和工厂副作用仍无事务回滚保证。

在修复检查点 `70906cf6f1511daedd4978be911daf47eafcbdf2`，WSL2 内 Ubuntu 24.04 x64 完整通过 `verify.py --aot --package` 与作者链门禁，包括新包符号、独立 CLR 包消费、类型化 Remote JIT/AOT、严格 TypeScript 与真实 HTTP。Windows x64 通过 766 项测试，零失败/跳过，并通过固定源码比较，但首次 AOT 命令未发现已安装的 `vswhere` 可执行文件。该部分运行不构成 Windows 平台验收。源码与失败证据保留；重跑必须正确配置 native 构建工具。

审查还确认参考测试工具链的传递依赖 `source-map-js` 存在 [GHSA-68fv-2mgg-jv7q](https://github.com/advisories/GHSA-68fv-2mgg-jv7q)。锁文件现于 PostCSS 既有范围内选择修复版 1.2.2。直接工具版本、浏览器模块锁和 DSH 行为 pin 不变。这是开发工具修复；未识别到产品远程 source-map endpoint。

锁变更后的最终候选需要重新冻结并运行门禁，不能沿用上述检查点。必须一起读取实际报告中的提交、运行前后源码哈希、精确包哈希与状态。源码 ZIP 从固定 Git blob 导出，另列生成的 no-Git 构建元数据。报告总数、源码执行、差分与包消费者仍属不同证据。独立 CLR 门禁现包括嵌套主程序集、两个入口顺序、私有 native 调用、binary 副本身份/冲突和延迟拒绝对照。Remote 门禁包括真实的显式 Schema 闭合 tuple 调用与四种 definition 稳定的提供者撤销场景；其他 tuple 变体只证明 codec/投影支持。

这些检查不关闭原范围剩余通用行。不表示已执行 hosted CI、远端 SourceLink 获取、Maker、Release、发布或部署。原始报告与私有审查记录不进入跟踪源码；审查交付可包含绑定冻结源码的脱敏证据清单。


## 2026-10-10 CLR 整合与原生 Settings 消费链验证

未发布的运行时/包检查点 `89a63edb0ba51ea63d5fb6ffa96e14134178286a` 已在 Windows x64 与 WSL2 下 Ubuntu 24.04 x64 通过 `verify.py --aot --package`、`verify-authoring.py --verification ... --aot --packages ...`、38 项验证器自检及配对文档检查，使用 SDK 10.0.111。两平台两类完整门禁均记录 `sourceUnchanged=true`、干净 checkout，以及完全相同的 502 项初始/最终源码哈希。固定源码 ZIP 包含 507 个已跟踪 Git blob；门禁排除 `verification/` 下五个历史文件。无 Git 构建的生成元数据单列。[绑定证据回执](../verification/typert-dotnet-2026-10-10/evidence.json)记录源码/压缩包、报告及各独立包批次哈希。后续文档验收不改变已验证源码检查点。

| 证据 | 已完成结果与边界 |
|---|---|
| Release 构建与实际 TRX | 零警告/错误。Windows：809 通过、零失败/跳过。Linux：806 通过、零失败，三个确属 Windows 的 Platform 用例未执行。Core 148、Composition 442、Extensions 110；Platform 在 Windows 为 109，在 Linux 为 106 |
| 固定 main 整合 | `7bde0ede48c0199715c7d1b045a99d6ea8436bb6` 整合已接受 CLR main `a018f34681d834f485217343db6a39dc609ee8ec`，保留稳定目录、显式 shadow、完整性凭据、共享 bundle/ALC 租约及安全退休/恢复 |
| 跨代生命周期 | `fc4d9f097c7b45aa1ad4d04d106de8cacd177b0a` 按精确 Loader 请求暂停/恢复，只失效其贡献缓存并拒绝旧导入迟到。真实包替换保留无关 B 的注册/owner/service 身份及调用。逻辑移除撤销路由并请求卸载，同时保留稳定制品文件 |
| 独立原生作者/模型/客户端 | 冷缓存 NuGet 作者生成 authored model；独立的仅模型合同包在 CoreCompile 前生成 DTO、客户端与 STJ context。调用方不引用提供者实现。禁用 Reflection fallback 的真实 HTTP Settings describe 在两平台通过 JIT 和静态 Native AOT 调用方消费，没有 Node 命令 |
| Settings 行为 | 实际 ProfileSession 选择的多个 namespace 在同一配置事务读取。验证 live 值/schema 选择、嵌套 secret 脱敏、显式 null/default、缺失/故障 provider、错误 details、重试、mirror invalidation、provider/definition 撤销、迟到结果及客户端 Dispose。分别验证普通 provider、Remote definition 与调用方寿命；当前 Service 的代际检查另有既有 Remote 门禁证据 |
| 模型/构建边界 | 源声明/制品事实漂移拒绝；冷构建、增量和 Clean/重建通过。primitive/string/array 请求结果、空状态注解及支持的构造器使用真实 STJ 元数据。readonly 成员、可选参数、record struct 和生成名称冲突保留分析事实并明确拒绝不支持的投影；可选默认值影响模型身份 |
| 原 TS/Web 与 CLR 能力 | 两平台单独通过既有独立 Remote NuGet 作者、严格 TypeScript、固定 Cordis 挂载及实际 HTTP/NDJSON 门禁。CLR 多 Entry 包消费、固定源码对照、管理/客户端路径、静态 AOT probes 及包消费也通过。TS 输出仍走既有 descriptor/Schema 投影 |
| 包/源码证明 | 八个 Cordis.NET 包、Greeting 示例及配对符号包通过严格 DLL/PDB/源码关联和离线消费者 frame 检查。精确源码 ZIP 的 SHA-256 为 `aff79029c38515c24de911f09727e8f5ffa99a729debe2eca9e40c48da49057d`，其无 Git 副本另行通过 Release build/pack、同一符号/debug-consumer 检查及 Settings JIT/AOT 消费 |
| 格式与脚本闭包 | 未改变的 203 个 C# 文件在四个格式阶段一致。两平台正式 runner 均通过全部 38 项脚本自检和配对文档检查。作者门禁复制到仓库外的变异构建已包含 Compiler/Generator 源码与锁文件 |

失败和中断记录保持原状态。首个生命周期包批次要求物理删除，与已接受稳定保留语义冲突；修正后的消费者曾用同一生产包批次独立复验。较早模型检查点暴露源码/XML conformance、全局 AOT 标志传播到仅构建期的 Compiler/Analyzer，以及仅分析的可选方法夹具误入运行时 analyzer。Settings 夹具曾因未排空 Host 输出而阻塞；排空同一未结束进程即恢复调用，随后修正 reader。`e97d8bf70ce119d33526ac3ca3edfeb685910c22` 两平台运行时/包门禁通过，但作者变异构建复制清单漏了 Compiler/Generator，因此作者门禁失败；最终检查点补齐清单，未改变变异断言。主动停止的旧 Windows 运行和独立自检的错误 SDK 运行不计验收。原始证据私下保留，未覆盖或连同机器路径提交。

仅验收上述本地切片，不代表完整 Typert。独立 authored model 不从有损 descriptor 反推；现有 analyzer 仍通过源事实 conformance 桥生成运行时绑定。丰富类型/引用图组合、全部模型驱动后端、丰富 Context/owned-value、Peer/uplink/events、完整 binary attachment 及剩余生产消费者迁移保持未完成。未提供 Settings base/user、写操作或数值 revision 等价；适配使用原生 string revision。lookup 时序、IDE/design-time 首构建以及 WPF/Avalonia/Godot .NET/Blazor 具名宿主需独立证据。不声明 Native AOT 内动态 CLR 支持。未执行 hosted CI、远端 SourceLink 获取、浏览器呈现或 Maker。DSH pin 和包版本不变；没有新增依赖选型、推送、远端合并、发布、Release 或部署。


## Typert 修复验证，2026-10-10

外部审查的三个反例先用原 `89a63edb0ba51ea63d5fb6ffa96e14134178286a` 包复现，再修复。三项 Typert 修复形成独立检查点 `8284bc8123f6e7d15e0cc31f7f901b7ca875b553`。其 Windows 全门禁随后因原生配置 watcher 属性异常逃出 FileSystemWatcher 回调、终止测试 Host 而失败。最终源码检查点 `a9b53d2fce5e1e611599098d87b46fa6300c9618` 另补有界 HMR 事件修复，并通过全新完整门禁。历史结果及失败保留，不重分类为预期拒绝或最终验收。[新回执](../verification/typert-repairs-2026-10-10/evidence.json) 将结果绑定到精确源码和独立包批次。

| 修复 | 实际包行为 |
|---|---|
| R1：live Service 注册身份 | definition 保持有效时，同对象/新 Fiber 及同 Fiber 重登记拒绝旧成功结果；覆盖不同对象替换和 contextual Get 重入。同值 Set、Notify、无关 realm 保持有效，不主动中断已开始业务 |
| R2：可空数值默认值 | 源模型保留 float?/decimal? 常量及 null，仅模型合同包在 CoreCompile 前生成带 F/M 的字面量，并与 STJ 一起编译。类型化 STJ 消费核对缺字段、显式 null、显式数值；数值投影使用受控 HTTP handler，真实 Settings HTTP 另行验证 |
| R3：生成类型名称 | 七类辅助名称，包括精确 DemoClientFailure 反例，编译前按声明来源拒绝。直接 emitter 与 model-only pre-CoreCompile 均拒绝并删除旧输出，不把下游 CS0101 当作生成器验证 |
| HMR 事件恢复 | 18 项聚焦 watcher 测试通过，包括真实增改删、初始化拒绝、原诊断身份、rename 恢复、后续刷新及关闭后抑制。确定性注入红例暴露旧 reader 绕开及缺少诊断；独立的 Windows 原生 Host 崩溃提供真实异常逃逸证据 |

Windows x64 的 810 项原生测试通过，无失败/跳过；WSL2 Ubuntu 24.04 x64 的 807 项通过，无失败，3 项精确 Windows 专用用例未执行。两平台均完成 `verify.py --aot --package`、复用同任务结果的作者门禁、38 项验证器自检和配对文档检查。独立 Remote JIT/静态 AOT、严格 TypeScript/真实 HTTP/NDJSON、真实无 Node Settings 消费及 CLR 多 Entry 各自通过。既有业务错误/取消与 Settings snapshot 验证保持通过。公共 API 快照未改变。

509 个受版本控制文件逐字节匹配冻结 Git blob，两平台门禁的 503 项初始/最终源码 hash 完全一致。四个格式阶段对 204 个 C# 文件一致。精确源码 ZIP SHA-256 为 `7b9f66dea50599f09e7e0a1c958041a2c646343edef0c5dab9cc2a25e644d52f`，无 Git 副本另通过 build、pack、严格 DLL/PDB/源码及离线调试消费，以及 Settings/Remote JIT/静态 AOT。实际恢复的消费者包 hash 与 NuGet 源/配置绑定各自批次，不假定不同平台二进制相同。紧凑审查制品路径脱敏后同时保存原始/交付 hash；raw 机器日志/TRX 保持私有。

私有最终收集器在四项门禁命令均退出零后，将 CLR 报告的列表误作对象而失败。证据收集通过校验、复制原制品恢复，没有重跑门禁。回执将这次收集故障及保留日志 hash 与运行结果分别记录。

本次关闭三个确定缺陷及正式验证暴露的 watcher 故障，不关闭[原范围剩余项](development.zh.md#2026-10-10-有界执行完成记录)：完整类型图/后端、更丰富协议及生产消费者工作保持原状态。动态 CLR 仅 JIT。hosted CI、远端 SourceLink、具名 UI 宿主、浏览器呈现和 Maker 未执行。没有新增依赖选型、版本修改、push、远端 merge、发布或部署。后续仅追加文档和脱敏回执，包身份仍为上述源码检查点。
