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

失败保留：Linux 旧 SDK 首次拒绝 restore；无 Git 导出随后通过运行时/AOT 检查，但因没有生成 SourceLink 记录而未通过符号打包。导出符号路径仍未关闭，源码 ZIP 的构建/运行和 Git checkout 的成功符号检查不能关闭该项。Node data URL 错误输出截断导致指定拒绝检查失败，随后独立审查诊断修正并复跑。托管 CI、远端 SourceLink 取源、浏览器 UI 渲染、其他 RID 和 外部应用 产品运行均未执行。Settings 仍限定顶层 primitive/live SET-only；完整 schema 导出、reset/多字段、模块图、远程协议和安装工具仍是独立缺口。

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

完整源类型分析、丰富 Context/owned-value 投影、Peer/uplink/events、完整二进制 attachment/wire 兼容，以及既有管理消费者迁移，仍列在[原范围对账](development.zh.md#2026-10-09-应用基础设施范围续账)中。不能强制终止任意业务或清理；宿主必须在关闭 Cordis root 前排空活动 Gateway 枚举器。不声明 Native AOT 内动态 CLR 支持。未执行 hosted CI、这些未发布提交的远端 SourceLink 获取、浏览器渲染或 外部应用 运行。原始平台报告和源码/包哈希保存在忽略的本地证据中，未提交机器路径。

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

这些检查不关闭原范围剩余通用行。不表示已执行 hosted CI、远端 SourceLink 获取、外部应用、Release、发布或部署。原始报告与私有审查记录不进入跟踪源码；审查交付可包含绑定冻结源码的脱敏证据清单。


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

仅验收上述本地切片，不代表完整 Typert。独立 authored model 不从有损 descriptor 反推；现有 analyzer 仍通过源事实 conformance 桥生成运行时绑定。丰富类型/引用图组合、全部模型驱动后端、丰富 Context/owned-value、Peer/uplink/events、完整 binary attachment 及剩余生产消费者迁移保持未完成。未提供 Settings base/user、写操作或数值 revision 等价；适配使用原生 string revision。lookup 时序、IDE/design-time 首构建以及 WPF/Avalonia/Godot .NET/Blazor 具名宿主需独立证据。不声明 Native AOT 内动态 CLR 支持。未执行 hosted CI、远端 SourceLink 获取、浏览器呈现或 外部应用。DSH pin 和包版本不变；没有新增依赖选型、推送、远端合并、发布、Release 或部署。


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

本次关闭三个确定缺陷及正式验证暴露的 watcher 故障，不关闭[原范围剩余项](development.zh.md#2026-10-10-有界执行完成记录)：完整类型图/后端、更丰富协议及生产消费者工作保持原状态。动态 CLR 仅 JIT。hosted CI、远端 SourceLink、具名 UI 宿主、浏览器呈现和 外部应用 未执行。没有新增依赖选型、版本修改、push、远端 merge、发布或部署。后续仅追加文档和脱敏回执，包身份仍为上述源码检查点。


## Caller view 修复验证，2026-10-10

运行时/包检查点 `e277a7eafcdad5e7047cf73729afd7f51558819d` 修复 F1。普通 `Service<TState>.CreateView` 可以在每次 Get 时创建不同的 caller-bound view，而注册的原始 provider 保持不变。按引用比较这些视图会在业务进入前拒绝有效 Remote 调用。Gateway 现在在首次 contextual Get 前复制既有注册身份和原始 provider；后续先执行正常 Get，再观察最终注册/值，覆盖 Get 同步重入造成的替换。调用仍使用原 caller view。没有新增 Core 公共 API、持久身份表、强制 abort、模型/生成器改动或普通 Service 的 RPC 要求。

审查提供的未改反例在旧 `a9b53d2` 包上实际失败，错误为 `gateway/service-unavailable`，业务调用为零。最终冻结 fixture 也在同一旧 feed 复现该失败。新包消费者验证重复新视图、共享 State、真实 unary/downlink caller、同值 Set/Notify、同一注册内不同 provider Set、首次及完成 Get 重入、撤销和替换。既有业务错误/取消优先级、lookup/Context 和 Settings snapshot 检查保留。[绑定回执](../verification/typert-caller-view-2026-10-10/evidence.json) 分列源码、旧包复现及新包批次。

| 证据批次 | 实际结果与限制 |
|---|---|
| WSL2 中 Ubuntu 24.04 x64 | 完整 `verify.py --aot --package`、同任务 authoring、38 项验证器自检和配对文档检查通过。实际 TRX 为 807 通过、零失败、三个精确识别的 Windows 专用 Platform 用例未执行。Remote JIT/静态 AOT、TS/HTTP、无 Node Settings、CLR 多 Entry、符号及离线调试消费者检查通过 |
| Windows x64 全量尝试 1 | restore/build 通过；原生测试为 809 通过、一个受控 HMR reconciliation 超时。门禁在打包前停止。未改动的三个对应定向用例复验通过 |
| Windows x64 全量尝试 2 | restore/build 通过；原生测试为 809 通过、一个包更新 manifest 观察超时。门禁在打包前停止。未改动的对应单个定向用例复验通过 |
| Windows x64 独立无 Git 源码包 | 精确源码 ZIP 构建并产出 18 个包/符号文件。严格符号/离线调试检查、无 Node Settings JIT/静态 AOT，以及单独 Remote JIT/静态 AOT/TS/HTTP 通过。该批次不代表 Windows 全量原生测试门禁通过 |

两个 Windows 失败用例均不调用 Gateway，其生产路径未受 F1 改动。有界源码复核没有找到明确的丢事件或队列缺陷。刷新调度或队列等待超过未改的十秒期限仍只是候选解释。定向通过不能解释或抹去全量失败。Windows 全量验收仍未完成；本轮未修改超时、断言或无关 HMR 行为，也未进行第三次盲目全量重试。

运行时检查点的全部 510 个 tracked 文件匹配冻结 Git blob；完整 Linux 门禁保留一致的初始/最终 503 项源码哈希。源码 ZIP SHA-256 为 `805a6d01c8df896e9d565a525b4e4f73086295595caab28c10187808e86f5ead`。四阶段 formatter 对 204 个 C# 文件结论一致。新 Linux 与 Windows 无 Git 包批次分开保留。Windows 实际消费者文件、恢复包和 NuGet 元数据已独立重读。Linux 收集器在任务结束时读取实际配置及恢复包字节，保存哈希匹配保留 feed；其临时消费者/缓存目前已不可访问，未声称事后独立重读源码/缓存。原始报告留在仓库外，回执记录其哈希和上述限制。

本轮关闭已复现的 F1 Service 视图缺陷，Windows 全量门禁仍开放。[原范围剩余项](development.zh.md#2026-10-10-有界执行完成记录)、固定 DSH pin、包版本和公共 API snapshot 不变。动态 CLR 仍仅适用于 JIT。未执行 hosted CI、远端 SourceLink 获取、具名 UI Host、浏览器渲染或 外部应用；没有新增依赖、推送、远端合并、发布或部署。后续文档/回执提交与已验证的运行时检查点分别记录。


### 配置监听修正与平台门禁闭环，2026-10-10

运行时/包检查点 `48ab1a45a165b11de2405c1153013cb05b169fe3` 保留 caller-view 修正，并修复配置监听深度及原生溢出恢复。固定 DSH 的 `packages/boot/hmr/src/watch-config.ts` 计算最近已存在根目录及有限深度；目标父目录已存在时深度为零。原生 watcher 现在排除该父目录内的 SDK 子目录。缺失祖先仍需递归观察，因为 FileSystemWatcher 不提供有限正深度选项。原生溢出保留原诊断，并通过既有协调器合并排队补读；已关闭监听拒绝迟到错误。这是明确的平台恢复适配：固定 DSH 对 watcher 错误仅警告。公共 API、依赖、版本及 baseline 均未改变。

私有诊断副本在修前执行八轮、修后执行两轮未改变的三个 HMR 用例和真实包更新用例，均通过；修前记录 150 次原生溢出，修后为零。两个聚焦行为检查在旧实现失败、修正后通过：不得探测范围外 SDK 文件事件，以及仅收到溢出通知时必须实际补读配置。这些证据确认被修复的缺陷，不能证明此前无观测超时的唯一根因。第一次历史 HMR 超时位于第三次 Change，即错误的 `disabled` 表达式；此前 caller-view 记录中的“第二个结果”在此更正。历史回执及失败批次保持原样。

精确冻结检查点通过本地 Windows x64 与 WSL2 Ubuntu 24.04 x64 的完整 `verify.py --aot --package`、同 job 作者验证、各 38 项脚本自检和文档检查。Windows TRX 为 812 通过、零失败/跳过；Linux 为 809 通过、零失败，相同三个精确命名的 Windows 专用进程用例未执行。两个原 Windows 失败用例在此次全量中保持原十秒等待并通过。CLR 多 Entry、Remote JIT/静态 AOT/TS/HTTP、无需 Node 的 Settings、符号和离线调试消费者分别通过。精确源码 ZIP 另通过无 Git 独立 build/pack/符号及 Settings JIT/静态 AOT。完成消费者的实际源码及恢复 Cordis 包字节已私下保留，绑定自身 feed，没有重建缺失输入。

私有输入收集器起初在 Windows 存储上合并了两个仅大小写不同的 Linux 投影文件。恢复时，两份原文件可读取，且匹配首次捕获哈希。仅复制恢复将全部 70 份输入存入保留大小写的归档，并保留首次失败日志和捕获记录。稍后独立复核逐份确认保留输入匹配首次捕获，但原 Linux 临时目录已不可访问，未声称此时重读原目录。Windows 和无 Git 的原源码、缓存及元数据已独立重读。这次收集修复没有重跑或改变任一平台门禁。

详见[源码绑定回执](../verification/config-watch-2026-10-10/evidence.json)。本条关闭该检查点的本地 Windows 全量门禁项。完整 Typert 类型图/后端/协议、剩余 Settings 及生产消费者仍未完成。hosted CI、远端 SourceLink 获取、具名 UI Host、外部应用 执行、新依赖选型和发布仍未验证或未授权。

## Remote 准备阶段修复验证，2026-10-11

R02 从已合入 main `424cfb98823e9867de5198118b26f3973b9b397d` 续行。运行时／包检查点 `679f57fb1be671c9f47ebc5a61f041b9d7215a3b` 在解码或 lookup 之前检查实际 Service／绑定，启动全部参数解析，保留成功参数顺序，并在首个观测失败时返回，同时观察已开始工作的迟到失败。既有 caller-view、注册、定义、lookup 和 Context 屏障保留；取消不替代准备错误。公开签名、依赖、版本及 DSH pin 均未改变。

八个新增聚焦用例在合入后的原运行时失败；修复后与九个既有 Registry 用例一起通过。有效的生成作者包对旧 feed 复现缺失 Service 缺陷：lookup 先于 Service 检查启动，触发有界超时。修正后的包消费者在 JIT 和静态 Native AOT 中证明准入、同时启动 lookup、提前返回失败及迟到工作。既有 caller-view、取消、撤销和替换用例保持通过；严格 TS／HTTP 消费单独通过。

两个冻结候选的本地 `verify.py --aot --package` 门禁均通过，源码未变，SDK 为 10.0.111。Windows x64 为 820 通过、零失败／跳过；WSL2 Ubuntu 24.04 x64 为 817 通过、零失败，三个具名 Windows 专用用例未执行。CLR 多 Entry、符号／离线调试、HTTP 客户端和无需 Node 的 Settings 回归也通过。既有原始源码参考门禁通过；其中 Composition 选择覆盖 profile／user-patches，没有执行上游 Gateway spec。独立包批次及保留报告／输入哈希见[源码绑定回执](../verification/remote-preparation-2026-10-11/evidence.json)。

初始 fixture 使用了错误的生成 string 身份；最终检查点仅修正 fixture，没有扩大运行时匹配规则。此前一次门禁选择了现有测试配置未包含的上游文件，另一次启动未正确解析 SDK。这些尝试和中断运行作为私有证据保留，不计入验收。源码 ZIP 已保留；本轮未执行新的无 Git 归档构建、远端 SourceLink 获取、hosted CI 或独立审查。这里关闭本地 R02 修复，仍待审查／合入 main。维护者已将后续模型、Settings、PluginManager 和丰富 Remote 基础设施工作转交新会话，既有职责仍未完成。未推送、创建新 PR、合并或发布。

### 随后的 R02 独立审查

两个独立 agent 审查检查了 `424cfb98823e9867de5198118b26f3973b9b397d...f4d1605f02df6c36eb613153f8dee7328583b605`，没有发现阻断问题。生产合同审查核对固定 prepareInvocation、原生执行上下文串行化、Service／绑定准入、全部解析启动、成功顺序、首个观测失败、迟到失败处理、caller-view、撤销与取消。规范／测试／证据审查核对可观察回归边界、双语文档和来源，独立匹配两个保留报告的哈希，并逐平台将全部 504 个源码库存哈希与 `679f57f` Git blobs 比较，零不匹配。

这两次审查没有重跑运行时门禁，也未独立复验每份消费归档／包。解析器完成屏障本身不证明垃圾收集或未观察异常行为；迟到失败结论还依据各解析器的 catch 路径。合作式保留边界仍有明确记录。审查无需修改运行时或 fixture。

维护者随后仅授权 R02 分支推送、PR／CI，以及审查和必需检查通过后的合入。其余基础设施职责仍由新会话承担。托管检查和 main 合入以 PR 及 workflow run 的实际 checkout 提交记录，不替代原 `679f57f` 本地证据身份。仓库要求线性历史及双平台 verify 检查，因此依既有 squash 惯例合入，不绕过门禁。

第一次托管尝试 [run 38107202823](https://github.com/gentt1024/Cordis.NET/actions/runs/38107202823) 对 PR head `8f1042a6c726717a5eecf5ea9eee89697b3e348a` 的合并 checkout `6ad0b11f2cbd44078fca6a46fa660baf2aa16c68` 执行检查。两个 runtime job 均因最终 fixture 的 WaitAsync 调用不符合 JB 格式而停止，尚未执行运行时验证或打包。此前本地 formatter 通过早于该 fixture 改动，未证明 `679f57f` 的格式通过。这次失败保留为证据，不计作验收。修正仅将该调用换行，没有改变 token 或 Gateway 行为；fixture 字节已变，必须建立新候选并执行新的完整托管门禁，不将旧双平台结果归算给新源码。

## 标准 Settings 与普通模型验证，2026-10-11

运行时／包检查点 `8d4838af3a500b84c6c5d7b5eca93d92d813c45c` 包含续建实现 `3753a5a059b4b3db06befb612a293b50439510d5` 和修正后的包夹具。主线基点为 `3342a967f4e57206bec272684a40a437fc051b77`，已包含通过 PR #15 合入的 R02。固定 DSH 基线仍为 `639ed015397290b3745d163aafe02ffee4aa3f84`。历史修复证据继续归属原源码身份。

[移植原则](compatibility.zh.md#上游忠实性要求)已明确写入兼容性政策，并从 README 链接。当前合同依据固定上游行为和有理由的 .NET 适配；旧试验接口是需要校正的对象。[Settings 迁移说明](settings-contract-migration.zh.md)记录唯一数值合同、普通服务／宿主／客户端调用及退出路径。完整配置／文件 hash API 仍通过同一配置 owner 承担独立源编辑职责，不转换或保留旧字符串 Settings 合同。持久 YAML／JSON 格式及 Git 历史不变。

| 已执行路线 | 可观察证据 |
|---|---|
| 原生配置与 Settings | 全量运行中的 43 项 ConfigurationEditTests 均通过，覆盖分层 base/user 投影、安全 Schema 默认值、secret/hidden 边界、动态 live 路径、有序编辑、完整校验、源冲突、并发和恢复。六个未放宽的修前包反例分别对 live 路径、base 投影、未知 live 成员清除、公开默认值、整体 live 根元数据和 secret 根脱敏实际失败。 |
| 生成 Settings 消费者 | 实际 .NET 包／HTTP 调用覆盖数值 describe/update/replace/mutate、持久化前过期拒绝、省略 expected revision、string/null revision 及遗漏 set value 的拒绝、显式 null 编辑、秘密存在标记、只读 owner、独立 Entry、消失／重入和 owner 重启。保留 provider/definition/client 退役与提交后 describe 失败检查。严格 TS 与真实 HTTP 消费另行通过。 |
| 普通声明与目录 | 26 阶段包路线检查选择的公开根、泛型／成员／引用事实、源码漂移、无效根／声明／身份、增量／Clean／冷重建及独立仅制品目录。它不引用 provider 实现，也不执行作者方法体。证据覆盖已记录的有界模型路线，不代表完整图组合或所有后端。 |
| 原生 Remote 与包 | 独立可选参数生成／调用、API 快照、CLI 调用、客户端类型／寿命／共享模块／watch、CLR 部署和多入口替换、隔离 runtime/tool/HTTP/Remote 消费者、包边界、配对符号及离线调试消费均通过。 |

本地 Windows x64 JIT、SDK 10.0.111 的 `python scripts/verify.py --package` 通过：827 项测试通过，零失败／跳过，`sourceUnchanged=true`。四个格式阶段对 216 个 C# 文件一致，零改动。38 项既有验证器自检及配对文档检查通过。[绑定回执](../verification/settings-model-continuation-2026-10-11/evidence.json)记录门禁步骤、实际 TRX 哈希、全部 18 个本地包／符号归档哈希及源码哈希。包含精确 529 个文件的源码 ZIP 通过解压／哈希核验，SHA-256 为 `eb7c9eaf8b5b63c48b63ebf21915035513f02831b35a1d633ae0f214b7f8ddf0`；本次未执行新的无 Git 归档构建。

保留两次早先失败作为私有本地证据。未提交实现通过原生测试，但其包 SourceLink 仍绑定旧 main 而失败；提交实现后纠正了源码身份。冻结的 `3753a5a` 门禁随后因 Settings 包夹具把用户 patch 同时作为基础文件、写入后重复应用 override 并使 Entry 不可寻址而失败。夹具现在遵循既有宿主方式，挂载独立空 `cordis.yml`。原客户端断言在定向包复验和最终完整门禁中均通过；两次早先失败均不重分类为验收。

本候选关闭上述标准 Settings 读写、可选 Remote 参数及有界普通声明／目录检查。标准文档打开、生成 PluginManager／通知整合、完整模型／后端及丰富协议职责继续保留在[原范围](development.zh.md)，未从本轮工作移除。本检查点未执行 Linux、Native AOT、hosted CI、远端 SourceLink 获取、浏览器渲染、具名 UI Host 或 外部应用。依赖、pin 和源码版本未改；未推送、远端合并、打 tag、发布或部署。本地使用 alpha.4 版本号的包只是隔离验证输入，不替换已发布 alpha.4。后续文档／回执提交另有身份，不替代本运行时检查点。

## 生成式管理与普通模型消费2026-10-11

运行／包检查点 `6ad9d7f1aac2b4546e23ea5e16ff290d4be312c8` 包含空 Settings namespace 修正（`7d408ef`）、生成式管理／通知／模型实现（`57a3df8`）、不可读兼容文件的安全拒绝修复及规范排版。它基于 main `3342a967f4e57206bec272684a40a437fc051b77` 继续；上一批 Settings 检查点保留自己的身份。固定 DSH pin、源码包版本及持久 profile 格式不变。

| 执行合同 | 可观察证据 |
|---|---|
| 生成式 PluginManager | CLI 和维护中的 Host 将实际生成合同挂到既有 operations。真实纯包 .NET 与严格 TypeScript 调用覆盖资源授权、generation、检查／hash 准入、启用、安装／更新／移除、豁免及原任务等待／取消。已接纳的检查和异步构建批准继续被所有者持有并排空；断线调用者不重放修改。 |
| 通知所有权 | 一个 Gateway source 转发选定 Cordis emit。生成订阅先收到 ready 再收事件；断线移除单个 observer，source 撤回先关闭全部 observer 再等待 source，重连先重订阅再读新快照。Composition 主要用例覆盖 iterator 读取／处置失败和所有者清理；真实客户端覆盖 NDJSON 解码、abort 和恢复。 |
| 普通模型与共享数据 | 独立保存的 v2 制品无需 provider 或 Remote 声明即可生成 DTO/STJ。同一作者 DTO 也作为 Remote 参数／结果。禁用反射回退的包调用者检查名称、nullable／required、受支持常量默认值和递归，并执行真实 HTTP echo。嵌套可达类型的冲突命名策略以 `TYPM113` 拒绝，失败生成移除过期输出。v1 和 v2 复用共享源事实。 |
| 既有边界 | 全部 43 项 ConfigurationEditTests 通过。并发、脱敏、生命周期、撤回和失败回归继续保留。空 Settings namespace 在 provider 查找前拒绝，无 provider 时也如此。兼容文件变化进入原 Changed 结果；不可读数据继续不授权、不可覆写，并返回失败的授权结果。 |

提交后的 Release 重建零警告、零错误。随后 `python scripts/verify.py --package` 在本地 Windows x64 JIT、SDK 10.0.111 下通过：847 项测试通过，零失败／跳过，47 个外层门禁步骤，542 个源码 hash 在执行中保持 `sourceUnchanged=true`。门禁包括原生 JIT trace 重复比较、客户端模块／类型／生命周期、隔离 tool／runtime／HTTP／CLR／Remote 消费、严格包符号及离线调试栈帧。全部 18 个新包／符号包属于此检查点。四个规范 formatter 阶段在 232 个 C# 文件上达成一致；21 个仅排版文件的 token 类型和值保持相同。既有 38 项 verifier 自检使用固定 SDK 通过，14 对文档和九个包 README 检查通过；没有仅因方法补充重复执行检查。

[绑定回执](../verification/management-model-continuation-2026-10-11/evidence.json)记录精确报告、TRX、源码及包 hash。实际完成执行的作者／调用者输入、生成合同输入和已还原 Cordis 包／metadata 在私有副本中保留，并核对捕获 hash 与本批 feed。没有重建缺失源码。Settings／普通 .NET 检查不执行 Node；管理 TypeScript 执行单独记录。其显式 fixture toolchain 验证生成工作流，实际 SDK preparation 仍由 Platform 测试覆盖。

冻结报告对工作树字节取 hash。七个非 C# 文件按仓库文本规范化规则与 Git blob 仅有 CRLF／LF 表示差异，回执单独记录其 Git hash；每个 C# 源码字节都与 Git 一致。首个私有收集器拒绝了此表示差异，失败日志保留；收集修正没有修改源码、包或门禁结果。

首个冻结 `57a3df8` 完整运行在 847 项中失败一项：预读不可读兼容数据绕过了原安全授权拒绝。其 846 项通过不算验收。修复将未知字节与不存在区分，继续由原权威裁决；既有主要回归保留断言，并新增失败 application／未变化状态检查。原失败报告保留。独立规范格式检查发现 21 个排版差异，经 token 不变的修正后四阶段通过。早先试验消费者暴露必需 nullable 字段被省略、CLI 未挂载 PluginManager，两项均在此门禁前修复。另一次脚本自检启动使用了错误 SDK 搜索路径，固定 SDK 下未改动的测试通过。以上尝试均不改记为最终批次通过。

[管理迁移](management-remote-migration.zh.md)记录唯一生成式公共用法，以及退出的手写 PluginManager HTTP／SSE 路由、浏览器方法和 decoder。原始配置／状态／制品职责保留既有所有者与授权。选定通知不将 Peer／uplink／waterfalls、owned-value／引用图或完整附件／wire 互通关项。保存的 v2 重建 Remote、泛型 DTO 发射、引用模型组合、原生文档／UI 集成及其余[原范围](development.zh.md)继续开放。本检查点未执行原始 DSH 源测试／跨运行时 differential、Linux、Native AOT、托管 CI、远程 SourceLink 取回、浏览器渲染、无 Git 源码归档构建或 外部应用；早先上游执行保留自己的证据身份。没有新增依赖、合并、tag、发布或部署；本地 alpha.4 版本包是隔离验证输入。后续文档／回执提交与这些二进制分别绑定身份。

## 显式引用模型消费2026-10-11

源码检查点 `2cf481c0e14315aec79ee92f8b0c00421331f1f3` 为既有普通 DTO/STJ 投影接通显式引用声明模型。根仍由主模型选择；完整程序集限定身份选择可达的 source 定义。缺失、冲突或不支持的定义在保留输出前失败。非公开 `JsonInclude` 数据现产生类型级 `TYPM202`，修复已观察到的静默遗漏；受影响旧模型需要重提取。模型格式、固定上游 pin、依赖、包版本及持久数据格式均未变化。

真实仅包调用者承担组合数据图的名称、required／null 边界、常量默认值及递归合同。作者默认值从 11 改为 12 后，执行了重提取、重生成、重新打包和消费，主模型身份保持不变。编译器／构建层覆盖六类独立拒绝、删除旧输出、未变化的增量输出、Clean 和字节相同的冷重建。引用根不能提升无关的主模型根。21 步聚焦探针及独立源码审查先于冻结门禁，不替代它。

Release 重建零警告、零错误。`python scripts/verify.py --package --package-output artifacts/reference-model-final-packages` 在 Windows x64 JIT、SDK 10.0.111 下通过：847 项测试零失败／跳过，47 个外层步骤、109 个生成消费者步骤，544 个源码 hash 上 `sourceUnchanged=true`。43 项 ConfigurationEditTests 及原有授权、并发、脱敏、寿命和失败回归继续保留。18 个新包／符号归档通过严格源码绑定及既有离线调试消费者。四个格式阶段在 234 个 C# 文件上零修改一致；38 项验证脚本自检和成对文档检查也通过。

[绑定回执](../verification/reference-model-composition-2026-10-11/evidence.json)记录实际报告、TRX、源码／包 hash 和六类拒绝结果。257 个私有捕获的消费者输入来自实际完成的目录，包括生成输入及与本批 feed 核对的五个已还原 Cordis 归档；它们不是对每次临时源码变更中间态的重建。三个非 C# 文件与 Git blob 仅有 CRLF／LF 表示差异，每个编译 C# 文件字节都与 Git 一致。私有 `JsonInclude` red／green 探针及初次 formatter 分歧继续记录为失败证据；没有通过放宽断言获得验收。

另一次静态管理客户端 Native AOT 检查在先前的 `6ad9d7f` 包上通过，宿主为 JIT。[独立回执](../verification/management-model-continuation-2026-10-11/static-management-aot.json)记录七个 .NET 步骤和一个 TypeScript 步骤，不证明 `2cf481c` 的 AOT。本次新源码检查点未执行原始 DSH 测试／跨运行时 differential、Linux、Native AOT、托管 CI、浏览器渲染、外部应用、无 Git 归档构建或远程 SourceLink 取回。更广的保存 Remote、泛型 DTO、Schema／TS 引用模型及原基础设施职责继续开放。没有据此新增依赖、合并、发布或部署；文档／回执提交与二进制身份分别记录。

## 模型管线重建，2026-10-11

源码 `7937ee8d838bb973fd5aa58f416bdb7d5513c53c` 在 SDK 10.0.111 上完成本地 Windows x64 JIT 与包门禁。[证据](../verification/model-pipeline-rebuild-2026-10-11/evidence.json)记录精确源码／包哈希、保留的失败及消费者输入。执行 `python scripts/verify.py --package --package-output artifacts/model-rebuild-final-packages` 期间源码未变化。

- Release 重建零警告、零错误；848 项测试全部通过，无失败或跳过，包含 44 项配置编辑测试。
- 完整门禁 47 步；生成 .NET／TypeScript 包消费者 133 步；检查 18 个包及符号包归档。
- 唯一声明制品驱动普通目录／数据和保存模型 Remote caller 生成。真实 HTTP 宿主与不引用 provider 实现的 caller 使用同一作者 DTO；标准 Settings、PluginManager 与通知继续使用原所有者。
- 实际消费者覆盖外部 metadata context 拒绝、保留 key 的服务类改名、无关根变化、非有限默认值拒绝、冷／增量构建、Clean 及旧输出清除。只读作者 Host 执行实际 getter，独立 caller 投影则明确拒绝。
- Settings 自动通知与显式读取共享 live namespace 选择及 revision 计算，在配置事务繁忙时仍可完成。新增回归在修复前生产代码上失败，修复后通过。

上一冻结点 `ba55cc2` 的完整测试结束于两项配置测试清理阶段的文件共享冲突，847 项中通过 845 项，报告独立保留。源码复核发现自动失效通知启动了未归属所有者寿命的文件读取事务；修复移除这项工作，没有为删除目录加入重试。原始文件占用者未被识别，因此不将这些失败归因于已经唯一证明的原因。

随后冻结点 `e2ba70e` 的 848 项测试通过 847 项。一项管理传输测试在活动 Gateway 迭代器结束前关闭 root，违反 `424cfb98` 已记录的生命周期合同。未修改源码的定向重跑通过，因此该失败依赖调度时序。测试现在先撤销通知源，读取相同终止错误帧和 EOF，再关闭 root 并检查 Gone。16 项管理传输定向测试全部通过，生产清理仍传播失败。

四阶段 formatter 对 240 个 C# 文件达成一致。38 项验证脚本自检在观察修复前执行，相关脚本字节未变化。本批未执行 Linux、Native AOT、原始 DSH 源码／跨运行时差分、浏览器渲染、外部应用、托管 CI、源码归档构建或远端 SourceLink 获取。此前 Linux／AOT 证据仍绑定原检查点。未改变包版本、上游 pin 或依赖；本次验收不包含合并、推送、发布或部署。

## null 数组属性与当前用法修正，2026-10-11

源码 `54ae0308c74135686d1e8eeaec0ec46367c6fbf0` 使用新的包 feed，在 SDK 10.0.111 上完成本地 Windows x64 JIT 门禁。[回执](../verification/null-array-review-fix-2026-10-11/evidence.json)绑定源码哈希、包身份、测试报告与保留的失败。Release Rebuild 零警告、零错误；848 项测试全部通过，无失败或跳过。完整门禁 47 步，生成消费者 135 步，其中 46 项为预期拒绝；检查 18 个包及符号包。四阶段 formatter 对 241 个 C# 文件达成一致，fixture 修改后 38 项验证脚本自检通过。

共同常量读取器在枚举数组 `Values` 前检查 `TypedConstant.IsNull`。null 数组保留 array kind、类型、`json=null` 和空的结构化 elements；空数组仍为 `json=[]`。既有普通目录消费者承担构造参数与命名参数的 null、空、非空数组矩阵。分别将两种 null 参数改为空数组都会改变声明身份。保存制品由目录编译器重读，共用 Remote 作者则覆盖独立的 generator／Host 接线风险及不引用 Provider 实现的真实 HTTP 调用。原有 readonly Host 成功、caller 拒绝、增量／Clean 和管理消费者继续纳入完整门禁。

修复前，普通和 Remote 作者均在冻结的 `7937ee8` 包上因空引用提取失败。另一次构建使用相同隔离旧包缓存，移除显式提取根后，在 Host generator 触发 `CORDISREMOTE001`。这些是实际执行的失败。更早的直接 generator 尝试使用全局缓存，因缺少属性失败，不作为 null 数组红例。首轮脚本自检选择了错误的系统 SDK；一项新增断言把既有 primitive 字符串引用误写为 named 声明；相应日志分别保留，均未要求改变生产合同。

现行 architecture 保留 TypertLoader owner，使用精确请求暂停与恢复，并说明稳定 CLR 原地加载和显式开发 ShadowCopy。配对作者指南说明可选常量默认值、省略与 null 的区别，以及末尾 CancellationToken 的默认值。历史验证回执不变。本检查点未执行 Linux、Native AOT、原始 DSH 源码／跨运行时差分、浏览器或具名 UI、托管 CI、外部应用、源码归档构建或远端 SourceLink 获取。更广模型和协议工作继续开放；未改变依赖、pin 或包版本，未合并、发布或部署。

## 生成式 scoped 调用者，2026-10-11

源码 `9bb56ac801a62faeabd09a93d256e3bdf2e0481d` 在 SDK 10.0.111 上完成 Windows x64 JIT 本地包门禁，使用新的私有 feed。[回执](../verification/scoped-clients-2026-10-11/evidence.json) 绑定冻结源码、消费者实际还原的 Cordis 包和保留的失败证据。Release Rebuild 为零警告、零错误；848 项测试通过，无失败或跳过。完整门禁完成 47 步，生成式 .NET／TypeScript 消费者完成 141 步，其中 49 项为预期拒绝；检查了 18 个包／符号归档。四阶段格式检查在 241 个 C# 文件上达成一致，38 项验证脚本自测通过。门禁运行期间源码哈希保持不变。

现有保存声明的包消费者通过真实 HTTP 验证两种不同的 scoped 合同。Context-bound 方法用有类型的身份选择 Host receiver；只有一个匹配 lookup 的 direct 方法保留 direct 签名，并增加仅补入 lookup 身份的 scoped 签名，其 receiver 仍为 direct。同一个调用者还消费 scoped 下行流。调用者只引用生成的合同包，不引用 Provider 实现，不启用反射回退。

保留的视图观察身份变化、Client adapter 撤销／替换及 Host provider 撤销。传输计数证明缺少身份或 adapter 时在发送前失败；显式 null 则发送请求，由 Host 返回 `gateway/input-invalid`。释放父客户端后视图失效，借用的 HttpClient 仍可使用。完整门禁继续保留 required／optional／null／默认值、冷构建／增量／Clean、配置、管理和生命周期回归。声明拒绝覆盖缺少 Context 事实、wire 类型不匹配及重复 wire 字段。

第一次定向 HTTP 调用暴露了显式 null 身份导致的本地 STJ 序列化失败。修正只调整内部请求封套，没有放松 Host codec 或其他参数规则。该失败日志和后续使用混合探测 feed 的定向检查分别保留身份；只有上述冻结源码、新 feed 的完整门禁属于本批验收证据。

本检查点未执行 Linux、Native AOT、原版 DSH 源测试／跨运行时差分、真实浏览器或具名 UI、托管 CI、外部应用、源码归档构建及远端 SourceLink 获取。历史结果继续绑定原源码身份。浏览器 scoped 装配、owned Context／引用值、Peer、上行／双向流、完整事件及附件仍未完成。没有修改依赖、上游 pin、包版本，也没有合并、发布或部署。

## 共享 Remote Connection 与 typed uplink，本地证据 2026-10-12

[本地回执](../verification/remote-connection-local-2026-10-12/evidence.json)记录基于 `07d6fba` 的未提交候选及确切源码／包哈希。独立 Windows .NET 与严格 TS 调用者通过标准 unary/results、scoped、multipart 和文本 mux。Typed uplink 覆盖首读前发送、有序半关闭、optional/nullable 输入、嵌套集合空性、作者捕获后仍拒绝的 codec 失败、逻辑协议／溢出错误、兄弟流存活及非法 carrier 帧。独立 .NET 调用者也已在关闭反射回退的条件下编译并以 Windows x64 Native AOT 执行，Host 为托管程序。

固定 DSH 的 unary/附件及 mux 实现在 Node 下对原生 Host 执行。固定 mux 上行保留缺失/null 和半关闭，并收到 Host codec 失败。这是有限的固定客户端互通，不代表整个浏览器 SDK、产品事件或 stdio。此前本地候选曾通过生产 mux 执行真实浏览器模块恢复；其源码身份与本次 uplink 候选独立。

前一冻结包候选在修复包源引用后，通过完整独立声明／引用、Settings、管理和通知消费者脚本。当前共享调用者有自己的包回执，不借用此前完整门禁源码测试或旧 AOT 的结果。仓库级验收仍未完成：早先包门禁因 SourceLink 要求已提交源码而在未提交工作树上失败，未放宽门禁。生成空性、模型接线、错误详情和隔离缓存设置的失败证据分别保留。本条不宣称 Linux、完整当前浏览器验收、反向 Context/owned values 或完整事件已完成。

本地 `4e76a74` 检查点重新通过全部 848 项源码测试。其包门禁随后定位到仍使用 CRLF 的文件与 SourceLink 源码字节不一致，Git 按仓库属性保存为 LF。仓库格式流程已修正该输入，C# token 值及 using 声明未改变；四个格式阶段现已一致。格式化源码使用独立的包验证身份。两个原始符号失败报告均保留，未调整校验规则。

## 共享 Remote Connection 包门禁，2026-10-12

冻结的本地实现为 `4881e089757f4e73181082eb796f686348c19127`，其前一个功能检查点为 `4e76a74691fbb3649192fe5e56e3ba292e5773e4`。`python scripts/verify.py --package` 在 Windows x64、SDK 10.0.111 上通过。全部 848 项源码测试通过，无失败或跳过。47 个外层门禁步骤包括包符号、真实调试源码定位、CLR 部署／多 Entry 和独立消费者。.NET／TS 生成与 Settings 消费者脚本完成 149 个步骤，其中包含预期拒绝。门禁期间源码保持不变。

[回执](../verification/remote-connection-2026-10-12/evidence.json)绑定源码清单、TRX 报告、18 个包／符号归档、实际还原的 Cordis 包字节，以及执行完成后的消费者输入。全部参与编译的 C# 文件与提交字节一致。既有四个格式阶段对 259 个文件达成一致；格式修正保留了 token 值和 using 声明。此前未提交源码及 CRLF 导致的 SourceLink 失败保留原始报告，仍记为失败执行。

正常 build／pack 和独立调用者覆盖共享 unary 结果合同、两种 scoped、multipart、typed uplink、引用模型、Settings 及生成式管理。Remote 引用复用已有声明组合与源事实。同一候选另通过[真实浏览器恢复](../verification/remote-connection-2026-10-12/browser-recovery.json)：生成的选定通知经过生产 ASP.NET mux；断线保留插件实例／草稿，失败查询能够恢复，实际撤销才释放实例。

上一条本地回执中的固定 DSH Node unary／mux 及 Windows Native AOT caller 检查，属于各自记录的格式化前源码／包候选。它们是有限证据，不作为最终候选重跑结果。本门禁未执行 Linux、原始 DSH 测试套件或全运行时差分、托管 CI、远端 SourceLink 获取、源码归档构建或 外部应用。反向 Context／owned-value、完整固定事件、浏览器 scoped 装配和剩余模型／平台职责继续开放。依赖、上游 pin、包版本和已发布包身份均未改变。本次仅本地实现与验证，未推送、合并、发布或部署。

## Remote 载体审查修正，2026-10-12

正式实现源码为 `8da4cb4f671ef9a654042a16f313275b40b7fe22`，前一个失败检查点为 `300f171bd7892b99d77b7e0a7b2a00e945297e7b`。Windows x64 下 `python scripts/verify.py --package` 已通过，期间源码保持不变：858 项源码测试、47 个外层步骤、149 个生成式 .NET／TS 消费步骤（含预期拒绝）以及 18 个包／符号归档。SourceLink 检查确认编译 C# 字节与 Git 一致。详见[门禁回执](../verification/remote-carrier-corrections-2026-10-12/evidence.json)。

同一冻结源码还通过了 Node 下固定 DSH unary／multipart 与 mux 客户端、严格 TS 生成客户端、独立托管 .NET 及 Windows Native AOT caller。独立探针使用的五个包与随后完整门禁的归档 hash 不同；包内有效载荷和 core-property 内容逐字节相同，关系文件仅有自动生成的 core-property 路径及 ID 差异。回执保留两组归档身份与比较过的包内容 hash。Host／caller 关闭序列化反射，Host 仍为托管程序。[互通／AOT 回执](../verification/remote-carrier-corrections-2026-10-12/interop-and-aot.json) 分别记录包、输入与二进制身份。真实 Chromium 的模块／选定通知恢复也在本源码通过，另见[浏览器回执](../verification/remote-carrier-corrections-2026-10-12/browser-recovery.json)。

F01 原有的 open／cancel 泄漏及 null 取消原因丢失、F02 接受内层额外字段、F03 注解／converter 不一致和 F04 阻塞发送关闭均保留失败证据。首个冻结候选虽通过源码测试，实际 TS 消费者仍发现 Remote 错误未设置队列失败状态；后续隔离诊断又发现 abort 已完成的协议关闭会丢失 1008 状态。两处已在不修改消费者断言的前提下修正。随后 d8f204f 的门禁通过，但独立公开 codec enum 探针又发现字面量类型投影缺口。最终候选增加 enum／const／null 覆盖，并通过同一实际包探针。此前回执与失败日志继续保留原身份。

公共调用继续使用共享 Connection、生成的 `RemoteResult<T>` 和流句柄。修正后的行为为：准入前取消不发送 open，内层 payload 在 lookup 前校验，二进制类型由实际元数据决定，关闭既解除阻塞 I/O，也保留已完成的协议关闭帧。根字节绕过 base64；嵌套对象仍先经 STJ 再提取。没有新增 API 世代或兼容路线。

这只是 Windows 下的有界修正门禁，不代表 Linux、托管 CI、原生 Host 或完整平台验收。反向 Context、owned value、固定完整事件、直接 Gateway operatorPeer fallback、浏览器 scoped 及其他原职责仍开放。本回执不包含 外部应用、版本、依赖、上游 pin、推送、合并或发布变更。

## 上行终止与集合投影修正，2026-10-12

`fbcdf20` 审查关闭原 F02/F04，同时指出此前四项全部闭合的判断过宽。R01 已在未改动的 `8da4cb4` 生产 JS 中复现：上行等待输入、下行延后消费时，缓冲项丢失，end、RemoteError 或 carrier failure 被 undefined 拒绝覆盖。仅下行和已 End 上行的对照通过。R02 也已本地执行：集合／字典及 nullable 的四种组合生成附件成功，但二进制类型断言失败。[修正回执](../verification/remote-queue-collections-2026-10-12/evidence.json)保留失败日志哈希，此前回执不变。

冻结源码 `29d7c4a1e2769ad77f53a2df7a5dc0912cf9c5cd` 在 Windows x64 和 WSL Ubuntu 24.04 x64 上通过 `python scripts/verify.py --package`。续建分支的 `85ec46a83c32ec0f51bbc84ac2850fdba3f29415` 具有完全相同的 Git tree。两边源码清单一致，验证期间均未变化。Windows 源码测试 862 通过；Linux 为 859 通过，跳过三个具名 Windows 专用进程场景。每边完成 47 个外层步骤，检查 18 个包／符号归档。生成式 .NET/TS 消费者在 Windows 完成 149 步、Linux 完成 150 步，包含预期拒绝；这些步骤不与源码测试数相加。[Windows](../verification/remote-queue-collections-2026-10-12/windows.json) 与 [Linux](../verification/remote-queue-collections-2026-10-12/linux.json) 回执分别记录实际报告、TRX、包与源码哈希。

公共用法不变。Host 可以在上行仍打开时终止双向流：end／RemoteError 保留缓冲项，物理失败保留 carrier 类别。关闭输入不再制造拒绝。集合结果 codec 转发其委托编码使用的元数据，包括 nullable 集合；Optional codec 保留纯 JSON 结果编码。四个格式化阶段对 261 个 C# 文件达成一致，双语文档检查通过。新工作树首次客户端模块构建缺少依赖的问题，通过安装既有 lockfile 解决，没有修改依赖。

未提交的反向 Context／owned value／固定事件施工及其初步寿命测试仍单独记录。本候选没有新增 Native AOT、真实浏览器、原始 DSH 套件、全运行时差分、托管 CI、远端 SourceLink 获取、源码归档或 外部应用 证据。版本、依赖、上游 pin 未变，未推送、合并、发布或部署。

## 类型化 Remote 事件与反向 Context，2026-10-12

冻结源码 `299c59704e0b33a142ac929063e4ddc7af280e33` 已在 Windows x64 和 WSL Ubuntu 24.04 x64、SDK 10.0.111 下通过 `python scripts/verify.py --package`。Windows 源码测试 872 项通过；Linux 869 项通过，另有 3 项 Windows 专用进程场景具名未执行。两个干净检出的源码在验证期间均未变化，各完成 47 个外层步骤和 18 个包／符号归档检查。独立的 [Windows](../verification/remote-events-2026-10-12/windows.json) 与 [Linux](../verification/remote-events-2026-10-12/linux.json) 回执记录实际源码清单、TRX 和归档哈希。两者使用相同的 Git 源码身份，包字节及本地源码清单分别保留。

[事件回执](../verification/remote-events-2026-10-12/evidence.json) 覆盖共同委托事实、生成式 Host 选择与类型化监听、固定 `$events`／`$events/result`、每次读取当前 agent 反向解析及 owned／borrowed 结算。17 个 Gateway 场景保护协议与源所有权，包括修前确实失败的排队撤销回归。6 个真实 ASP.NET／.NET 场景消费生成声明，区分 next、undefined 和 null，并在 handler／回复未结束时检查取消与 Connection 释放。独立包消费者从包内共同模型生成类型化事件监听，实际运行 .NET／TS 管理调用；TS 还检查反向 Context、当前 adapter 查询和 owned 释放。CLR 多入口保留独立的严格 TS 声明消费。生成消费记录中的预期拒绝不计入源码测试数量。

[真实浏览器恢复回执](../verification/remote-events-2026-10-12/browser-recovery.json) 属于 `35da718`，其生产文件在 `299c597` 中未变。实际生成事件模块经过生产 ASP.NET mux：断线保留插件实例／草稿，状态查询失败后恢复，图撤销释放插件。夹具提供状态／目录响应，因此不据此认定浏览器 scoped／owned 已验收，也不将它写成包宿主验证。最终提交仅让独立消费脚本自行准备实际 vendored Context，消除对已缓存客户端产物的依赖；浏览器观测不改记为另一次执行。

排队撤销、过期包清单数量、disposable 类型库引用缺失、浏览器夹具准入和消费缓存依赖的失败证据与最终候选分开保留。另有一次 Windows 构建因仍在运行的诊断测试锁住输出 DLL 而失败，后续候选使用独立输出。没有放宽断言来保留旧通知协议。[Remote 事件](remote-events.zh.md) 说明当前公共路线和已移除的通知服务／导出。撤销修正检查点的四阶段格式化一致；最终构建执行 C# 风格检查，事件中英文文档已加入文档检查器。

本批完成的是选定的固定事件消费链，不代表所有 Typert／平台职责完成。直接 Gateway 的 operatorPeer fallback、浏览器 scoped 消费、其余模型后端和具名平台接入继续留在原范围。此次没有新增 Native AOT、完整固定 DSH 套件／差分、托管 CI、远端 SourceLink 取回、源码归档或 外部应用 证据。此前修正回执保留原身份。版本、依赖和上游 pin 未变；本次验证不包含合并、发布或部署。

## 事件审查、operator Peer 与声明空性，2026-10-12

正式实现／验证源码为 `2a5a6e4bc86156416a2cbef2fc7b1c234f74f9a8`，tree 为 `47e6a1fdd7e24955c21970b2e4daab827449cf75`。它承接 `0a722f6`，处理 Chief 对 `f0d38f5` 的审查。[回执](../verification/events-nullability-2026-10-12/evidence.json)记录实际改动、修前失败、两端包哈希与剩余边界。

E01、E02、E03 均已复现并修正：声明集合空性进入懒加载的 Host／事件／独立 codec 与 TS 投影；合法同名事件委托使用完整限定输出名；归还 `$events` 未使用的输入时，即使尚未读取也会关闭实际载体 inbox，同时保留观察者与 sibling。源码 `On`、保存模型 `On`、严格 TS 与实际包调用验证了生成合同。普通业务上行的 end／overflow 拒绝、当前 adapter、continuation／undefined／null、owned 结算和操作寿命继续受到回归保护。此前建设的直接 Gateway operatorPeer fallback 与字符串 key 数据字典也包含在本次验证候选中。

两端默认 `python scripts/verify.py --package` 均完成 47 步，使用 SDK 10.0.111，各检查 18 个包／符号包。Windows 878 项通过；Linux 875 项通过，同样三项 Windows 进程场景具名跳过。源码哈希均未变化。Linux checkout 唯一未跟踪项为指向锁定 Node 工具的目录符号链接；回执保留 `workingTreeClean: false` 并说明原因。它不是源码修改或缓存的应用客户端；独立消费者仍重新打包固定 Context。

新的 Windows 包 Host 与真实应用内浏览器通过共享 scoped 事件消费者：每次查询当前 adapter、borrowed Context、next／undefined／null、回复结算后的 owned 释放，以及取消后的 handler 结算。[浏览器回执](../verification/events-nullability-2026-10-12/browser-scoped.json)标识实际生成模块与 Host DLL。它证明反向事件路径，不代表出站浏览器 scoped facade 已完成。验证后已关闭临时页面与 Host。

原 `0a722f6` 的 Windows／Linux 门禁仍保留真实可空字典 Remote 调用失败。最终候选首轮 Windows 尝试因 NuGet TLS／漏洞数据不可用而在锁定 restore 处失败。另有明确标记的离线定向复验将 NU1900 保留为警告，不计作完整门禁；连接恢复后，默认 Windows 门禁未使用该例外而通过。277 个 C# 文件的四阶段格式检查收敛；公共 API 增量为生成代码所需的声明空性 codec 入口及查询委托。本批不宣称新增 AOT、托管 CI、完整 DSH 差分／互通、SourceLink 远端获取、源码包、具名 UI 或 外部应用 验收。

## 出站 scoped 与嵌套声明修正，2026-10-12

正式实现源码为 `19ff2b60b883911f3f3579006c0128319902d4ae`，tree 为 `1150c7f00d24ca639de4e20b4315ac0d95b203cb`。它包含出站 scoped 与集合后端续建、空参数签名的严格类型修正，以及 Chief N01/N02 修复。[证据](../verification/scoped-nested-nullability-2026-10-12/evidence.json) 将修改及早期失败绑定各自检查点。此前 `270eaf3` 和 `b9d0d8c` 门禁仍为失败，此前浏览器探针也不作为本候选证明。

Windows 与 Linux 均在冻结源码上运行 `python scripts/verify.py --package`，期间源码未改变：每个平台 47 个门禁步骤、18 个包及符号制品。Windows 源码测试通过 883 项；Linux 通过 880 项，跳过的三个 Windows 专用包进程测试在其[回执](../verification/scoped-nested-nullability-2026-10-12/linux.json)中具名列出，[Windows 回执](../verification/scoped-nested-nullability-2026-10-12/windows.json)覆盖这些用例。两端门禁均包含真实 .NET／TS Remote、Settings 和管理事件消费、保存及引用声明模型、增量／Clean／冷构建、预期拒绝、CLR 部署和多 Entry 消费、公共 API 与文档检查。

N01/N02 最初在六个源码用例中失败三项。修复后的源码边界接收合法嵌套 null 元素，在两种属性顺序下拒绝必需位置的 null，并在同名成员始终忽略时保留有效属性合同。独立事件合同包和类型化 `On` 监听通过实际 Host 传递两种嵌套 DTO，覆盖保存模型生成入口。必需 null、递归、转换器、附件、上行、授权及生命周期回归继续保留在适用的现有源码和包门禁中。独立 DTO 后端仍拒绝尚不支持的成员冲突。

[出站浏览器回执](../verification/scoped-nested-nullability-2026-10-12/browser-outbound.json)使用 Windows 冻结包消费者生成的原样制品和 Host。真实浏览器 fetch／WebSocket 调用检查 receiver 和 lookup scope、当前 adapter／身份变化、缺少 adapter、显式 null 与 undefined、直接 lookup fallback、scoped 流、保留回调撤销、重新挂载及借用 Connection 寿命。此浏览器结果不代表完整反向 Context 或 Peer scope-targeting 矩阵通过。

本候选未运行完整 DSH 原始源码套件、静态 AOT、托管 CI、远端 SourceLink 获取或 外部应用 验证。这些结果不表示整个 port 或所有具名目标环境已完成。没有改变包版本、依赖或 upstream pin，也未合并、发布或部署。

## 选定 Client 调用准备，2026-10-12

源码 `dfee9489af89a9badbc9e0845a5ec2da82c4f125` 修正 `aa47d14` 审查的 S01/S02。既有生成包消费者先在 `19ff2b6` 上失败：缺少 direct lookup 参数槽仍发出 HTTP/open 或变成取消结果，关闭 Connection 又同步逸出选定 unary 调用。修正后的消费者证明本地 unary rejection、流创建失败及零分发，同时保留显式 undefined/null、默认值、合法 fallback、adapter 选择异常和贡献寿命。N01/N02 保持关闭，本批没有重建模型。

Windows x64 源码测试通过 883 项；四阶段格式检查在 277 个 C# 文件上达成一致。首次 `python scripts/verify.py --package` 通过源码／客户端／API 检查并生成 18 个包／符号制品，但符号检查器启动 Git 时遇到 WinError 5。重试也出现 apphost／Git 启动拒绝。这些失败保留，本候选没有一条从头到尾全绿的完整脚本回执。

未改动的符号检查器随后通过精确冻结源码清单路线，完成所有包／PDB／源码及真实离线调试消费者检查。源码由一次 Git batch 读取提交内容，在写清单前逐个核对 blob 对象哈希。其余原门禁命令随后在同一源码、未改变的包字节上通过，包含独立消费者、HTTP 重发布、CLR 部署／多 Entry、保存／引用模型、Settings、管理与生成式事件。临时续跑驱动最初遗漏既有 CLI fixture；修正后复制原 fixture，没有改变断言。重复 Git 子进程启动失败的根因仍未查明。

真实内置浏览器直接消费这批包生成的原样资产，通过同一 scoped／准备边界消费者。[回执](../verification/client-preparation-2026-10-12/evidence.json)记录原始失败、补完检查、包和测试身份；[浏览器回执](../verification/client-preparation-2026-10-12/browser.json)绑定源码、资产与截图。Linux、静态 AOT、托管 CI、远端 SourceLink 获取、完整 DSH 原始测试套件及完整 Context／Peer 矩阵未在本批重跑。本次收口未推送、合并或发布。

另一个先行包探针确认下一作者体验缺口：首次设计时 Compile 缺少生成 DTO／客户端输入，随后普通 build 会生成两者并编译消费者。这是 MSBuild 输入证据，不是实时 IDE 验收，也不计作最终包验收。[开发记录](development.zh.md)的 D-MODEL／D-WIRE 保留当前剩余出口。

## 首次设计时模型消费，2026-10-12

实现检查点 `09c2c11f9a511cc3f5ccccaab7302db0b7d7a8e0` 使保存模型的 DTO 与 client 在 restore 后首次设计时 `CoreCompile` 前生成。包 target 使用独立设计时输出和相同模型／引用后端；普通 build/pack、增量时间戳及 Clean 沿用原有所有者。引用 XML 文档也进入设计时作者 compilation。供打包使用的源码模型提取仍属于普通构建。本批没有公开模型格式、依赖或协议变化。

主要回归位于 `scripts/typert_declaration_consumer.py`，通过独立包消费者执行。相同首次设计时输入断言在此前 `dfee948` 包上因缺少生成输入而失败。新冻结包通过 Compile/CscCommandLineArgs 准入、实际引用生成 DTO、client 和 STJ metadata 的 C# 编译、普通 pack、Clean／冷构建及既有真实 Remote 调用。显式引用模型消费者另通过 data/client 首次生成、引用事实变化、重新生成后真实调用、冲突／缺失引用拒绝和过期设计时输出删除。.NET 生成时 PATH 不含 Node，调用者不引用作者实现。

[验证凭据](../verification/design-time-2026-10-12/evidence.json) 绑定源码、全部 18 个包 hash、实际消费者报告及失败证据。本候选**没有取得一次不间断的默认门禁全绿**：

- 冻结源码执行默认 `scripts/verify.py` 入口，使用独立输出目录保留上一批记录。build 成功；882 项测试通过，1 项在准备 `PackIncompatibleAsync` fixture 时因 `Process.Start("dotnet")` 遇到 WinError 5 失败，尚未进入 `Known_incompatible_peers_are_refused_before_build_even_when_not_selected(enabled: False)` 的产品断言。
- 随后原样重跑该测试方法，两种参数均通过。原失败 TRX 保持原貌，子进程启动失败的根因仍未解决。
- 后续默认 JIT、模块、CLI、API、可选参数和空白检查在相同源码上单独执行并通过。`test-map` 未在这份有失败的完整 TRX 上完成；没有构造拼接 TRX 或替代的成功完整门禁报告。
- 两组定向消费者均在冻结最终包批次通过。更早的引用探针也独立遇到过一次 dotnet 启动拒绝；该失败及成功重试与最终包执行分别保留。

本证据为 Windows x64 / SDK 10.0.111。本候选没有执行完整 `--package` 门禁、符号／debug 消费、真实浏览器、Linux、Native AOT、托管 CI、实际 IDE 交互或具名 UI 平台验收；MSBuild 设计时证据不覆盖这些边界。其余引用 Schema/TS 后端与 Peer/scope-targeting 仍未完成。前一组 S01/S02 审查固定在已推送 `9243d3a`，本地续建结果不改变其证据。

## Scope 与 Peer 路由，2026-10-12

实现 `28901b9484199c7a7faba36baef1db000f71852a` 完成 scope 标签继承、身份／祖先事件路由、基础过滤器保留、父链绑定／重绑及基于 Fiber 的静止清理。Connection 和直接调用 fallback 的 Peer 以自身为 key。生成的 waterfall 调用使用路由载体，真实 subject 留在类型化参数中；转发遵循固定 `packages/api/remotes/src/index.ts`：无 key 时继续本地处理，有 key 时须在投影前确认 subject 类型正确且与 key 是同一对象。临时的原始 subject 转发路径退出。

原生合同的主要测试位于 `ScopeTests`、`TypertGatewayPeerTests` 和 `RemoteEventsGatewayTests`。`RemoteEventsTests` 通过实际 ASP.NET／事件投递保护生成器对 subject 过滤器的保留。现有独立声明包消费者通过生成的 .NET 与 TS 客户端调用 Peer／子 scope／无标签路由，不传身份参数，也不引用 Provider 实现。首次 design-time 编译、普通 build／pack、引用变化、Clean、拒绝和生命周期检查继续保留。固定 DSH 的 11 项原始 scope 测试另在锁定版本上通过。

[收据](../verification/scope-routing-2026-10-12/evidence.json) 绑定精确源码、18 个 nupkg／snupkg 文件、测试结果和生成消费者步骤。最终 Windows x64／SDK 10.0.111 的 `verify.py --package` 在干净且未变化的源码上完整通过：890 项测试、test-map、JIT／模块／CLI／API、包符号及调试消费者、独立 NuGet／HTTP 调用、CLR 部署／多 Entry、选定固定客户端互操作，以及完整生成式 .NET／TS 消费组。四阶段格式检查也一致通过，没有修改源码。

此前失败保留原貌，不拼接成这次成功：

- `d2de367` 通过 886 项测试和 test-map 后，符号检查启动 Git 遭遇 WinError 5。额外原生反例暴露无 key 载体准入问题；四例回归在修正前有两例失败。[独立收据](../verification/scope-routing-2026-10-12/preliminary.json) 保留不变。
- 首次 `28901b9` 验证遇到系统临时盘空间耗尽，导致写入与插件准备失败。失败 TRX 以及格式工具的 I/O 失败继续保留。
- 首次临时目录恢复误放在仓库内，使夹具项目继承仓库 MSBuild 设置；这一轮不作为独立消费证据。该轮还记录了一项尚未归因的 HMR 超时。没有为这些失败添加生产补丁或修改断言。
- 新建的空 HTTP 缓存无法访问 NuGet 漏洞数据源。后续保留已有 nuget.org 缓存，没有关闭审计。最终验证和格式检查使用另一卷上经核对位于仓库外的临时目录。Git 启动拒绝未被诊断为与磁盘耗尽相同的原因。

真实浏览器使用同一冻结包和生成资产也已通过：原生 Host Peer 路由、receiver／lookup、当前 adapter 与身份、null／undefined、流和撤销。页面结果及资产／截图 hash 独立于严格 TypeScript 和 Node 检查记录。首次自动工具审批拒绝后，用户明确授权临时 loopback 代理；验证后两个夹具进程均已关闭。这证明浏览器调用原生 Host 路由，不代表浏览器侧 scope 实现已获验证。本候选未运行 Linux、Native AOT、托管 CI、真实 IDE 或具名 UI 框架验证。本批不关闭全部 Peer／Context 工作流、scope store／layer 或剩余引用后端。Chief 对 S01/S02 和 N01/N02 的关闭结论继续有效。

## Scoped subject tracing 修正，2026-10-13

实现 `707fddeaaeaa3316f44fef76a15a3c76e335a00d` 处理 Chief 的 SC01。旧生成 helper 把 subject 作为直接事件参数，Cordis 将它 tracing 为新 caller view，但路由载体保留原 subject，导致合法调用在投影前被拒绝。固定 DSH agent dispatch 将 subject 嵌入 payload；原生 helper 现以一个值元组承载原始 subject、request 和 signal，转发器在投影前检查其中的 subject。普通 Core tracing、严格身份检查、公开 helper 签名和 wire payload 不变，旧直接参数布局退出。

主要回归位于 `RemoteEventsTests`，调用实际生成 helper 和 `FromCordis`。在修前生产代码上，同类型的返回自身对照通过，返回新 view 的用例在路由 subject 校验处失败。修后两者通过；测试还观察普通直接参数 tracing、投影收到的精确身份和 continuation 返回结果。首次夹具因对仅支持异步释放的句柄使用同步释放及格式诊断而未编译，该设置失败与编译成功后的产品红例分别保留。相关原生路由／准入 27 例、生成式事件 8 例通过；这些子集也在最终全量中执行，不与全量计数相加。

[回执](../verification/scoped-subject-2026-10-13/evidence.json) 绑定源码／tree、红绿 TRX hash、18 个包 hash 和消费报告。干净且未变化的源码在 Windows x64／SDK 10.0.111 上通过完整 `verify.py --package`：892 项测试、test-map、JIT／模块／CLI／API、普通 Git 路径符号／调试检查、NuGet／HTTP、CLR 部署／多 Entry、选定固定客户端互操作及完整生成式 .NET／TS 包消费。后者的 160 个子进程步骤包含预期拒绝。四阶段格式检查通过且未改源码。

独立管理 Host 现使用 `ForContext` 返回新 view 的 subject，既有 .NET 和严格 TypeScript 事件消费通过。真实浏览器复用同批 Host 包与生成资产，通过当前 adapter、借用 Context、next／undefined／null、owned reply 和取消后 handler 结算。loopback Host 与代理已关闭。这是本候选的新证据；较早 `28901b9` 的浏览器与门禁结果保留原身份。

本次不声称完成 Linux、Native AOT、托管 CI、真实 IDE、具名 UI 框架、新一轮完整上游测试、浏览器侧 scope 实现或全部 Peer 工作流。固定源码对应已在本地核对。首次 design-time 保持 MSBuild 脚本层接纳；引用 Schema／TS 消费及 scope store／layer 仍是独立职责。没有为验证放宽生产断言或安全检查。

## 引用 Web 制品与默认浏览器 Connection，2026-10-13

冻结源码 `6356b86d3862b43217725da3668998433973b970` 通过完整 Windows 包门禁及 Linux 包／AOT 门禁。两端工作树在执行期间保持干净且源码未变。Windows 892 项测试通过；Linux 889 项通过，三个既定 Windows Job／进程所有权用例记录为未执行。[脱敏回执](../verification/referenced-web-2026-10-13/evidence.json)记录其精确名称、各平台步骤、生成消费者结果及包哈希。本地验证包保留现有版本，仅作私有测试制品，不是新发布。

既有独立引用模型消费者现在从显式主模型／引用声明生成并打包 Schema／ESM／TypeScript。严格 TS 检查及 Node 调用通过实际 Host 验证递归 DTO、数组、字典、wire 名称、required／null 边界和 Host 默认值。缺失与冲突引用拒绝生成并移除自有输出；恢复、增量输出稳定、Clean 和冷构建通过。引用变更用例改变保存的初始化值／输入身份，证明重生成，不证明 wire 形状变化或已部署 Host 默认值改变。

真实 Chromium 消费打包文件时，在前序 `9be3b92` 发现默认 fetch 接收者错误。该提交已通过的 Windows／Linux 门禁仍只归属前序源码。默认 Connection 现在绑定原生全局 fetch，无须调用者注入。同一 TS 样例及已入库 HTML 入口通过临时回环转发代理调用修正后的包 Host，验证通过。这是该问题的主要浏览器回归；Node 的 fetch 接受旧接收者，不能保护此边界。验证后代理及 Host 均关闭。

`9be3b92` 的首次全新 Linux 门禁因未安装锁定 npm 依赖而停止；失败回执与随后完整重跑分开保留。早期开发夹具／格式检查失败及浏览器红例也保留。一次 Linux 浏览器辅助启动在 READY 前超时，随后其临时 Host 路径已不存在，不计作浏览器运行；最终浏览器证据使用 Windows 候选包。

Linux Native AOT 覆盖 conformance、指定静态／HTTP 消费者及既有生成式管理／投影／Settings caller。显式引用 DTO caller 和动态 Host 仍是 JIT。本批执行选定固定端互操作检查，未运行原始 DSH 源码套件或托管 CI。不据此重新证明所有 scoped／事件浏览器路径、实际 IDE 或具名 UI 集成，也不关闭 scope store／layer 或全部序列化／类型后端。既有模型、scoped 与所有权职责继续在[开发记录](development.zh.md)维护。

## Probes 标准客户端与宿主修正，2026-10-13

[CI 运行 38405914024](https://github.com/gentt1024/Cordis.NET/actions/runs/38405914024) 为分支 `e6b366af90a50b6287a5ccaac7325b5d858b3f30` 检出临时合并提交 `8f04cc1113a6ed3ef8b8bb4cb6c2ec02ae0ada37`。两平台均通过主包／AOT 验证及五项配置故障注入，八个上游分片全部通过。随后 authoring 在 Probes 客户端的严格 TypeScript 检查失败：导出命令遗漏了生成 Settings 包装所导入的共享运行时和声明。补齐这些文件后，又暴露出未处理的可省略 namespace 列表，以及实际调用时示例宿主仍使用旧 args-only 封套的问题。

Probes 现在输出既有共享运行时，收窄生成合同中的可省略列表，并使用既有 ASP.NET Core Remote adapter 与 Host Connection。独立包消费者引用同一集成包。没有放宽库协议、共同模型或断言，固定 DSH form-model／store 源码保持不变。Windows 定向补完通过严格类型、实际正向消费、四项应用负向控制、CLR 部署、源码 JIT／AOT 和隔离包 JIT／AOT 消费，场景输出一致。两份修改的 C# 文件通过四阶段格式检查，双语文档检查通过。最初的 TypeScript 与 HTTP 400 失败和成功补完分别保留。

包消费使用既有本地开发 feed，不是新冻结检出提交的包。这是本地 authoring 证据，不代表完整双平台 CI 或真实浏览器验证。上一轮失败 CI 及其中通过的阶段保留原临时合并提交身份；该轮 HMR 未复发，但历史超时原因仍未证实。

## Draft PR 作者验证选择器修正，2026-10-13

[CI 运行 38375206795](https://github.com/gentt1024/Cordis.NET/actions/runs/38375206795) 实际检出 PR 临时合并提交 `018537bf4368f2790616769ce7f3bce2ac84e8a0`，由分支 `4f2e2685499490dc77499f2dee98ef23a3a792e7` 与 main `3342a967f4e57206bec272684a40a437fc051b77` 组成。Windows 和 Ubuntu 的 `verify.py --aot --package` 均通过，上游源码执行交由八个全部通过的分片。随后 authoring 门禁在注入 revision-fence 缺陷之前失败：选择器仍匹配不含 `settings is null` 的旧条件。两个完整 CI 任务均未通过。此前的 HMR 超时本轮未复发，但其原因仍未证实。

修正仅更新这条故障注入选择器及其替换表达式，保留 Settings 分支条件、生产代码和原 Probes 消费断言。Windows 定向运行既有五项配置故障注入均通过：各副本先编译成功，再由真实消费者拒绝对应缺陷。revision 用例明确失败于 `profile field edit fences stale revisions`，而非还原、编译或无关检查。这份本地结果只覆盖配置故障注入组，不代表剩余 authoring 阶段完成，也不把上一轮 CI 证据转移到新检出提交。

## 有方向合同收口检查点，2026-10-13

冻结源码 `45ee050b44b05236141836b3980707b7a944cf92` 修正 WEB01–WEB03 和 FETCH01，包含引用程序集 accessor 空性。Windows 门禁停在一项 HMR reconciliation 超时：891 项通过、一项失败。同一编译产物的 HMR 测试类单独运行三项均通过。全量超时尚未归因，不能视为完整通过，也不能认定是文件监控环境失败。该轮 Windows 门禁尚未运行打包阶段。

同源码的 Linux 包／AOT 门禁通过运行测试、test-map、JIT／AOT conformance、打包、符号、静态消费者、CLR 部署和选定固定客户端互操作，随后在独立数值 caller 的旧空结果夹具处失败。作者实际写出四个数值字段，夹具却返回空对象。`b79bf390e3f2fd7728f884ede7da068f9df38eca` 仅修正该消费者：单独保留输入默认初始化检查，并明确拒绝不完整结果。复用同批 Linux 包，修正后的数值 JIT／AOT caller 通过；补完随后暴露 Settings unset 的生产回归：方向输入编码试图序列化 JsonElement.Undefined，而未保留其声明的省略规则。这两轮失败门禁都不计为完整通过。

真实 Chromium 使用冻结 `45ee050` 的 Linux 生成包 Host 与客户端制品，在 Windows 运行，通过默认、原生、绑定和箭头 fetch。这只证明上述构建／运行平台组合，不是 Windows 构建包或后续候选的证据。实际 HTTP 调用、输入拒绝和取消原因均通过；临时 Host 与转发代理已经关闭。

后续工作修正 Undefined 省略，并补齐固定上游 scope store／layer。ScopeStoreTests 主责存储语义，共享 .NET／TS 包 caller 验证 Peer 和子 scope 注册。新 worktree 验证最初因未还原 TypeScript 依赖停止；按锁文件安装后，同批包的共享 .NET／TS 调用通过。一次客户端构建选到了另一套已安装 SDK；指定锁定 SDK 后通过，没有修改 global.json。这些属于开发检查，不是冻结候选验收。新生产代码仍需自己的完整门禁与浏览器证据，不能自动继承此前绿色切片。

### 有方向合同与 Scope 冻结证据，2026-10-13

源码 `a11cb73ec584405612170ea9b690bf56c1f3834c` 修正 Undefined 输入省略，并加入固定 Scope store／layer。[回执](../verification/web-contract-scope-2026-10-13/evidence.json) 区分完整失败与同源码补完。Windows 完成 896 项通过、1 项 HMR 协调失败，尚未执行打包。同一份 Extensions 测试程序集随后隔离运行 113 项全部通过。重复超时原因仍未关闭；隔离通过或使用手动 watcher 都不能证明是环境问题。

Linux 完整包／AOT 门禁通过运行时、对照、打包、符号、静态消费者、CLR 部署及选定固定端互通，随后因 Web 输出目录已存在而停止。在新输出目录补完时，源码和 18 份包／符号文件完全相同：三种 Web 合同模式及引用模型消费通过，标准管理、数值和 Settings 的 JIT／AOT 消费通过。补完结果不改写原完整门禁失败状态。

两次真实 Chromium 会话使用上述 Linux 构建的 Host／客户端制品，在 Windows 运行通过：出站 scoped／Peer 与 Scope layer 消费；引用 Web 及默认、原生、绑定、箭头 fetch。回执绑定 Host／客户端及截图哈希，两个临时 Host 和代理均已关闭。这不是 Windows 构建包、后续 Settings 文档修改或 alpha5 发布的验收。

## 托管候选完整通过，2026-10-14

[运行 38411639451](https://github.com/gentt1024/Cordis.NET/actions/runs/38411639451) 的十二个作业全部通过。分支 `e44bd354374a419e9f35c5d531a6960bb47ff8e9` 对 main `3342a967f4e57206bec272684a40a437fc051b77` 的 PR 合并检出为 `30bc56dde6e4327b9bdae40694239a2d38d2e1db`。Windows 和 Ubuntu 各自完成 `verify.py --aot --package`，随后以本作业的包完成 `verify-authoring.py --aot`；八个固定上游分片及两个原始用例并集检查通过。其中包含 Probes 严格类型、保留的负向控制及源码／包 JIT/AOT 消费一致性。[收据](../verification/alpha5-ci-2026-10-14/evidence.json) 记录公开作业／制品身份；归档摘要来自 GitHub 元数据，不表示已独立重算每个包。

本次完整结果替换候选待验状态，不改写失败运行 38375206795 与 38405914024 的结论。历史 HMR 超时没有复发，原因仍未解决。早先真实浏览器与中立应用结果保留自己的源码／包身份。本次运行不证明交互浏览器、live IDE 或每个原生桌面 opener，也未建立 alpha5 发布身份。当前出口见[收口记录](development.zh.md#alpha5-完整-ci-检查点2026-10-14)。后续仅文档修改不宣称新的二进制门禁。

## Guid 保存模型候选，2026-10-14

默认 STJ Guid 适配增加共享框架分类及其 C#／Schema 投影，包括非空可选 Guid 的默认值。[回执](../verification/guid-projection-2026-10-14/evidence.json) 绑定生产／消费者源码 hash，以及唯一本地候选 `0.2.0-alpha.5.guid.20261014.1` 的十八份包／符号包 hash。仓库发行版本及已发布制品未改变。

Windows x64 使用 SDK 10.0.111，旧包 Web compiler 以 TYPM114 拒绝 System.Guid。修正后的独立数据与 Remote 消费者通过正常模型提取、build/pack、首次 design-time 生成、增量及 Clean／冷构建、引用变化和缺失／冲突拒绝。实际 .NET 和严格 TypeScript caller 经 HTTP 保留 Guid.Empty、非零值、nullable／默认值及支持集合值。Schema 为 string/uuid；实际 Host STJ 拒绝非法字符串和违反非空约束的输入。独立数据及 Remote caller 也通过静态 Native AOT，Host 保持 JIT。Guid 成员自定义 converter 在两个独立后端仍以 TYPM110 拒绝，并清理旧输出。

既有 .NET 事件 listener 和 Web waterfall 现传递 Guid 值。包管理／事件消费者通过 JIT、静态 caller AOT 和 TypeScript 检查。实际内置 Chromium 使用同批包资产通过 Guid request/reply、当前 adapter、next/undefined/null、owned 结算、Settings、安装观察及来源恢复；临时 Host／代理已关闭。十四项既有 Platform 事件用例、全部四阶段格式及中英文文档检查通过。

保留的准备失败与旧 compiler 的 Guid 拒绝分开记录：普通数据夹具选择未支持 record、夹具端点未加入授权、AOT 暴露动态反射检查，以及夹具缺少 XML 注释。修正保留原断言和 warnings-as-errors。本批属于 Windows 定向证据，新完整 CI 单列；此前 a66b784 CI 不覆盖本次修改。live IDE、默认桌面打开、完整应用重启、HMR 风险裁决及获授权的发行身份仍未关闭。

## 保存模型 Web 事件完整 CI，2026-10-14

[运行 38497878162](https://github.com/gentt1024/Cordis.NET/actions/runs/38497878162) 的十二个作业全部通过，对应分支 `a66b7845760670545e0d7a056185f67be561af3a`。两个 runtime 作业的检出日志均确认实际合并提交为 `7893ed59fde70aa413f913526af8df14fb5b39e3`，由该分支与 main `3342a967f4e57206bec272684a40a437fc051b77` 合成。Windows 和 Ubuntu 使用 SDK 10.0.111 完成包／AOT 及 authoring 门禁，八个固定上游分片和两个并集汇总均通过。新事件生产者和独立消费者由此具备本批自己的完整平台证据。

[回执](../verification/alpha5-web-events-ci-2026-10-14/evidence.json) 记录作业和制品压缩包身份。摘要来自 GitHub 元数据，并非独立下载重算。下述浏览器检查保留开发包身份；本次运行不提供交互浏览器、live IDE、实际默认桌面打开或完整应用关闭／重启的证据。历史 HMR 超时未使本次运行失败，但其原因和风险裁决仍未闭合。CI 包不是新的 alpha5 发行包。本次文档更新没有修改生产代码，也不宣称另跑一次二进制验收。

## 保存模型 Web 事件开发验证，2026-10-14

e44bd35 之后的本地续建接通了保存模型事件生成入口。[回执](../verification/web-events-closeout-2026-10-14/evidence.json) 绑定改动源码文件、开发包、独立 Host／客户端资产及保留的失败。Composition 在独立本地 feed 重建，未变的依赖包沿用既有开发包。这是开发验证，不是最终冻结平台或发行批次。

`verify-typert-dotnet.py --management-only` 通过正常事件 build/pack、不引用 Provider 包的显式源模型组合、增量资产不变、缺失引用拒绝并清除旧输出、恢复、Clean／冷构建，以及实际 .NET／严格 TypeScript 调用。既有 waterfall 场景保留 continuation／undefined／null、当前 adapter、拥有资源在 reply／取消后的结算及借用边界。共同引用 DTO 检查 request 条件省略及 setter 输入／getter 输出空性的区别。初次方向夹具错误地通过客户端 `next()` 读取 Host 后续值；失败保留，纠正后的夹具遵循固定的本地链／Host continuation 边界，没有新增运行时绕路。

同一成功消费者的编译资产及包 Host 在 Windows 实际内置 Chromium 中通过，同时覆盖 Settings、断开观察不取消安装任务，以及来源撤回／重装配。临时 Host 与代理已关闭。夹具注入安装准备和文档 opener，不能计为实际桌面编辑器或原生安装 SDK 证据；完整应用关闭／重启及最终冻结候选浏览器验收仍独立保留。

既有 Platform 事件用例 14 项通过；当前源码重建的 Extensions 113 项通过。公共 API 未变，八个改动 C# 文件的四阶段格式检查一致，中英文文档／包 README 检查通过。一次夹具格式构建失败已纠正并保留。本批不宣称完整 Windows／Linux 门禁或新 AOT 验收。

有界 HMR 诊断先用保留的计时二进制复现一次超时（112 通过、1 失败）：第三次刷新已入场并读完 patch，首次表达式占用 12.052 秒后发布错误。当前源码重建通过既有 evaluator 配置回调增加计时：首次求值在回调之前耗时 5.043 秒，之后 0.114 秒；恢复和所有者清理完成，其余求值约一毫秒。原十秒等待、断言、并发及重试策略均未改变。用例完成，hang collector 没有生成 dump。这将观察到的延迟缩小至冷 evaluator 准备段，但没有执行栈来区分 JIT、初始化或资源等待。剩余风险是负载下冷表达式准备可能超过 reconciliation 测试预算，须保留给维护者裁决；后续通过和最新托管绿色均不能证明仅属环境原因或关闭历史失败。

## Guid 与保存 Web 事件完整 CI，2026-10-14

[运行 38509755930](https://github.com/gentt1024/Cordis.NET/actions/runs/38509755930) 的第 1 次尝试全部十二个作业通过，分支为 `a6cf07b708fc7b3b1cf18ff45440310c33a52b89`。PR 实际合并检出为 `5ab3ec2bf67f3409c62f5e8ecb399378ccafcb81`，base 为 `3342a967f4e57206bec272684a40a437fc051b77`。默认 Guid 投影的完整 CI 项关闭，保存 Web 事件继续保持关闭。两平台 runtime 完成包／AOT 和 authoring 检查，八个固定上游分片及两个用例并集汇总成功。

[回执](../verification/alpha5-guid-ci-2026-10-14/evidence.json) 记录作业与归档身份。本地下载了 Windows 包与证据归档，重算 SHA-256 与 GitHub 一致。两份原始验证报告均为 `requested-checks-passed`，SDK 10.0.111，执行期间源码未变。四份原始 TRX 合计 907 例通过。十八个包／符号包的 nuspec 均记录检出 `5ab3ec2` 和版本 `0.2.0-alpha.4`。独立审查另行下载了 Linux 归档，报告 904 例通过、三个既定 Windows 专用用例未执行。本地回执明确归属，未重复 Linux 归档审计。

这批原始 CI 字节与本地 `0.2.0-alpha.5.guid.20261014.1` 开发包分开。CI 包必须使用专属 feed 和还原缓存，避免混入已发布或较早的同号包；它们不是正式 alpha5 包。此前本地浏览器证据保留原批次身份。真实 IDE／调试、默认桌面打开、完整应用关闭／重启及 HMR 风险处置仍分别验收。当前公共文档使用领域中立表述，已有归档内容与历史证据保留原貌；文档修正不触发另一轮二进制门禁。

随后，这批 Windows CI 原始包通过既有 `--management-only` 隔离消费入口，使用新的 feed／缓存输入。生成 .NET JIT 和严格 TS 实际调用通过，包含保存事件的 pack／增量／引用拒绝／恢复／Clean 检查。实际内置 Chromium 消费这些生成资产，通过 Guid 事件请求／回复、Settings、管理、adapter 变化、next/undefined/null、owned 结算及来源撤回／重装配。Host 加载的四个 Cordis DLL 与原始 nupkg 字节相符；回执绑定这些 DLL、生成浏览器资产和 PASS 截图。Settings／安装夹具仍注入 opener／准备步骤。session 已释放，临时 Host／代理进程已停止；这不是持久应用重启测试。本次浏览器补验未重复 AOT 或完整门禁。

## 原始 CI 包的应用与 Rider 补验，2026-10-14

[补验回执](../verification/alpha5-application-closeout-2026-10-14/evidence.json) 使用运行 38509755930 的 Windows 原始包，生产源码仍为实际检出 `5ab3ec2`。未重新打包平台。隔离副本保留 ManagedApplication／ManagedPlugin 的 C# 和页面源码，将项目引用改为精确包引用；独立构建示例插件及客户端资产。实际 Chromium 完成安装、Settings 将 limit 从 1 改为 7、模块撤回／恢复和断开。正常关闭 Host 后以同一 profile 启动新进程，确认插件激活、limit 7 持久化且新所有者 revision 为 0。逻辑移除后再次重启，插件未恢复，部署文件仍保留。五个 Host Cordis DLL 与原始包字节一致。Ctrl+C 显示正常关闭，但外层 PowerShell 返回 1，未声称捕获了零退出码。

默认 Settings 打开实际启动 Windows Explorer／OpenWith，用户确认窗口出现后关闭。没有选择编辑器或观察文档内容，因此只证明原生命令交接和窗口出现。最初夹具额外禁用反射序列化造成安装失败，去除与原示例不符的覆盖后使用独立 profile；失败 profile、部署残留和一次过早重建导致的文件锁失败均保留。

Rider 2026.2 首次打开时，Remote 属性和生成类型均未识别。实际还原清单及包 nuspec 证明 IDE 继承的 NUGET_PACKAGES 覆盖了 NuGet.Config 缓存配置，读取了 `ecfc5ee` 的同号已发布包。隔离项目和准备脚本增加 RestorePackagesPath 后，Rider 自动还原选中 `5ab3ec2` 原始包。用户确认未手工构建时三个符号均被识别，随后断点命中，并输出 Example.Author 与全零 Guid。首次失败保留；修正后通过不改写该失败，也不代表其他 IDE 已验收。未清理全局缓存或修改已发布包。

一次原始 CI 包的冷进程 EventPipe 采样中，表达式准备为 1.199 秒、首次调用总计 1.390 秒，后续调用约 1 毫秒且恢复成功。采样定位到 Jint Engine 构造、Temporal 时区初始化、Windows 时区枚举及本地化资源读取；各栈的包含耗时重叠，不能相加。本次没有复现历史十秒超时，也不能证明历史 12.052 秒全部来自相同路径。生产代码、断言、超时和重试未变，历史风险仍待处置。本批没有重复双平台／AOT／上游完整门禁，也未形成正式 alpha5 发布身份。
