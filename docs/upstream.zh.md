# 上游基线与来源

[English](upstream.md)

## 锁定基线

| 角色 | 仓库 | 修订或版本 |
|---|---|---|
| 行为目标 | `deepseek-ai/deepseek-harness` | `639ed015397290b3745d163aafe02ffee4aa3f84` |
| Harness 发布标签 | DeepSeek Harness | `0.2.0-rc.2` |
| Vendored 包 | `@deepseek-ai/cordis` | `4.0.4` |
| Core 与 loader 来源 | `cordiverse/cordis` | `56b3d4f725681cf4556c1a8695a709cc3b6eed74` |
| 上游记录的扩展来源 | `deepseek-harness/cordis` | `abb0a307cb1d3b0947f455d590cf5ba922d4caa4` |

`upstream.lock.json` 是权威记录。DSH vendored 源码与来源仓库不同时，实现以 vendored 源码为准。代码行为、源码测试与上游文档属于不同证据类别。vendor README 作为[历史快照](reference/dsh-vendor-readme.snapshot.md)保留，其中也保留了原始本地链接。

## 更新基线

不得追随浮动分支。固定目标，检查影响已承诺原生能力的修改及必需依赖，补充能揭示行为差异的测试，并记录平台适配。保留不可变的历史清单；整个 DSH 路径清单仅用于发现遗漏，不是升级前置任务。正式基线更新前，须完成原生验证、相关源码对照、JIT / 静态 AOT trace，以及 Windows 和真实 Linux 的隔离包消费者验证。
