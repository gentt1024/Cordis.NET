# 验证

[English](validation.md)

## engines.dsh 候选准入验证，2026-10-22

第一片的合同见[兼容性说明](compatibility.zh.md#dsh-021-候选准入分片2026-10-22)。候选固定为 `687f282e8625c6b8d11ff58211a90524be2e4317`；`upstream.lock.json` 和历史源测试清单仍固定在 `639ed015397290b3745d163aafe02ffee4aa3f84`。执行环境为 macOS arm64、.NET SDK 10.0.111/runtime 10.0.11、Node 24.13.0 及锁定的 npm 开发依赖。临时目录采用文件系统规范路径，避免 macOS 符号链接别名干扰。机器原始日志不提交。

完整必需入口已返回 **`requested-checks-passed`**：

```sh
python3 scripts/verify.py --dsh <locked-source-checkout> \
  --upstream-test packages/boot/app-boot/tests/plugin-compatibility.spec.ts \
  --upstream-test packages/boot/app-boot/tests/compatibility-preflight.spec.ts \
  --upstream-test packages/boot/app-boot/tests/profile-compatibility.spec.ts
```

- 原生测试：**967 通过、零失败**，仅原有 Windows Job 对象宿主退出测试在 macOS 未执行（Core 147、Composition 559、Extensions 118、Platform 143 通过）。test-map 门禁严格核对该单项排除，并要求跨平台进程用例实际通过。原来仅 Windows 执行的 SDK 退出用例现在也在 macOS/Linux 执行；没有把失败用例改为跳过。
- 65 个 `DshProfilePolicyTests` 全部通过，包含 engine 优先级、fallback、错误声明、精确身份、bundle／入口／reconciliation 准入及豁免锁并发／取消。实现前首批 13 个新兼容性用例中 12 个失败。13 个真实 NuGet 定向用例也通过，涵盖禁用安装在 SDK 执行前准入、HTTP/CLI 豁免、元数据保留、重启／撤销及兼容 engines 覆盖 malformed peers。
- 候选 `plugin-compatibility.spec.ts` 的 87 个原始用例另行通过。上面三个正式旧锁源文件的 117 个原始用例通过。这是参考侧执行，不代表候选全部行为已经兼容。
- restore／Release build、test-map、重复 JIT trace、示例、正式基线的两组差分、浏览器模块构建及生成客户端类型消费、生命周期／替换／共享模块、作者 watch／package、CLI 校验／预览／调用／错误退出、API 快照、可选 Typert 消费及 whitespace 均在必需入口运行中通过。该运行期间源码清单保持不变。最后更新的双语证据随后通过 `scripts/check-docs.py`。

首次整库运行暴露的两个 macOS 失败现已修复：

1. `PackageProcessTests.Cancelled_preparation_stops_child_with_independent_output_pipes_before_returning` 缺少 macOS fixture：setup 提前返回，没有创建 feed。未修改基线 `18b611c4cdc6972480e396702106484701118e81` 同样复现。fixture 现在会在 macOS 构建真实包和使用独立输出管道的写入进程。
2. `PackageManagementTests.Cancelling_running_sdk_preparation_drains_before_wait_completes_and_does_not_select` 暴露进程所有权不完整：旧 macOS 路径终止遍历到的树，但只等待主进程；.NET 明确说明[等待主进程退出不代表后代已退出](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.kill?view=net-10.0)。随后构造的稳定反例显示 `Exited_sdk_cannot_report_prepared_output_while_its_child_still_writes` 在写入进程仍存活时返回准备成功。该用例在进程管理修复前失败、修复后通过。原生组创建、组存活检查、有界终止等待及直属子进程回收，现已阻止这两类过早完成。

进程定向运行通过 9 项；原先的取消回归又在与整库并发的负载下连续通过 5 次。最终整库还通过新增的活动所有者／恢复记录用例，以及正常／非零退出和输出检查。无法确认终止时仍保留恢复记录，不返回已完成的取消。macOS 宿主崩溃和主动逃离进程组的边界继续明确记录在[兼容性说明](compatibility.zh.md)。

本轮不提供 Linux/Windows、AOT、完整包消费者或原始 Core/Loader 源测试套件证据，也不晋升候选行为锁。

## 选择检查

文档运行 `python scripts/check-docs.py`；代码先运行职责对应的测试。运行时、包、兼容性和发行改动运行 `python scripts/verify.py`。具体命令见[维护指南](development.zh.md)。保留 CI 必需检查。

## 已发行版本证据

[alpha.5.2 发行页](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.0-alpha.5.2)提供正式 manifest、evidence 和 SHA256SUMS。tag 对应 `1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4`，[正式运行](https://github.com/gentt1024/Cordis.NET/actions/runs/39145687915)七项作业全部通过。八个产品包及匹配符号来自 Linux 验证批次，Example 排除。公开 NuGet 载荷与封存一致，允许新增 NuGet 签名；发行附件已公共下载核对摘要。

## 已接受风险与证据边界

Jint 冷构造和原生初始化可能延迟 HMR 刷新、队列后续工作及退出清理。历史超时仍是失败，采样没有解释全部延迟或建立上界。十秒回归保持不变，新失败仍使门禁失败。缺少进展、错误发布或清理，或真实冷启动延迟不可接受时，应据具体证据跟进。

Windows／Ubuntu 有完整包与 AOT 验证；macOS 仅有同批作者及浏览器消费，不代表完整运行时、应用或 AOT 验收。Rider 验收不覆盖其他 IDE。Windows 文档打开只证明系统交接，不证明编辑器内容或其他桌面平台。后端限制和具名 UI 接入待验证项见[维护指南](development.zh.md)。作者目录保护不承诺恶意文件系统沙箱。

## 历史证据

[原验证记录](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/validation.zh.md)保留各候选原始身份、失败与当时待办。[verification](../verification)保留受跟踪回执及机器读取的输入。Git 不保存未跟踪包、TRX、截图或已过期 CI artifact；hash 不证明原始字节仍可获取。

PR 合并检出与分支 head 分开记录。本地补完不能把完整门禁失败改成成功，不同候选不能拼成一次验收。
