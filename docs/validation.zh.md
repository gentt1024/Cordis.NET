# 验证

[English](validation.md)

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
