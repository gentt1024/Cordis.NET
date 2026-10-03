# 验证记录

[English](validation.md)

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
