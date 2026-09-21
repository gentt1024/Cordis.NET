# 上游基线与来源

[English](upstream.md)

## 锁定基线

| 角色 | 仓库 | 修订或版本 |
|---|---|---|
| 行为目标 | `deepseek-ai/deepseek-harness` | `ddefc45fbc7f8e46dd73185e68295696d1297887` |
| Harness 发布标签 | DeepSeek Harness | `0.1.6-alpha.2` |
| Vendored 包 | `@deepseek-ai/cordis` | `4.0.2` |
| Core 与 loader 来源 | `cordiverse/cordis` | `56b3d4f725681cf4556c1a8695a709cc3b6eed74` |
| 上游记录的扩展来源 | `deepseek-harness/cordis` | `abb0a307cb1d3b0947f455d590cf5ba922d4caa4` |

`upstream.lock.json` 是权威记录。DSH vendored 源码与来源仓库不同时，实现以 vendored 源码为准。代码行为、源码测试与上游文档属于不同证据类别。vendor README 作为[历史快照](reference/dsh-vendor-readme.snapshot.md)保留，其中也保留了原始本地链接。

## 更新基线

不要跟随浮动上游分支。应锁定新提交，清点 vendored 源码与测试，审阅许可证变化，重新生成映射，分类每个新增或移除的断言，运行 JIT 差分与静态 AOT 场景，并在 Windows 和 Linux 验证包。对有意适配做明确记录，不要静默改变处置结论。
