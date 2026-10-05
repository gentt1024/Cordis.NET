# 开发与验证

[English](development.md)

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

## 初始应用基础设施实施片（历史验收）

沿用已完成的应用范围矩阵，参照 `upstream.lock.json` 中固定的 DSH 提交，不重新开展全仓审查。Core 保持领域无关；管理、作者能力和平台接入复用其上既有所有者。

| 实施片 | 状态 | 验收 |
|---|---|---|
| A：完整 typed 字段组合 | 已实现；Windows/Linux JIT/AOT 与独立包消费验证通过 | 实际手写/组合消费者、透明包装、live/普通更新、完整保存/默认往返、换代、collectible 正反例、JIT/AOT 与隔离包消费 |
| B：一个字段编辑动作 | 已实现；独立审查修正与 Windows/Linux 消费者验证通过 | 既有 operations/session 所有权、校验、revision、失败后的保存值/生效值 |
| C：一个设置视图 | 子集已实现；Windows/Linux 源码及隔离包的 JIT/AOT 消费通过 | 明确 live-only 合同、脱敏和 revision、实际 TypeScript 消费；不将 Core 描述图声称为 Schemastery 或 JSON Schema |
| D：一个客户端制品交付动作 | 子集已实现；Windows/Linux 不可变交付、路径及换代验证通过 | 明确清单/入口合同、宿主交付、实际 Node ESM 执行和代际失效 |

A 的有界规则是完整、显式的 plain-data 投影，保留 validator 权威，不推断默认值，不建全局回调/类型缓存。既有语义使用固定源码对照和原生回归验证；新增作者辅助通过实际消费者验证。作者门禁让未修改的断言对付移除 live 绑定、忽略普通 effective 变化、漏保存字段的 helper mutation。破坏版必须编译成功，并因指定运行时原因失败。不得为通过放宽断言或修改正式行为锁。

每片执行实现、引用规则/源码/反例的独立只读审查、修复和复验。规则修正独立审查后在批次间应用。断言退役值时保存转换前的值，原生 Fiber 本身会复用。提交、命令结果、未执行项和源码哈希沿用既有验证记录及私有证据输出，不代表新增发布授权。此工作方式借鉴[迁移文章](https://claude.com/blog/ai-code-migration)及[工具包 README/操作规则](https://github.com/anthropics/code-migration-kit-with-claude-code)，不导入其任务队列、工具禁令或逐阶段审批。

B 保留源所有权、raw 不透明性与既有 Session 队列。独立审查修正了 flow 行删除、用户 insert 继承、祖先 live 准入、重启结果和调用者可变 map。写入前检查 YAML 往返，拒绝不能持久化的完整候选。C 的读取/提交均受宿主政策约束，草稿 revision 保留原始宿主哈希，拒绝不支持的 reset/多字段请求。D 通过部署包路由捕获不可变 bytes，同时拒绝 lexical 和链接路径逃逸。这些规则在最终批次前校准，没有改变正式行为锁。既有作者门禁包含编译成功的生产/客户端破坏版和不变的端到端控制；正确源码及隔离包均运行实际原生宿主与 TypeScript 客户端。

最终批次发现 Node 的 data URL 错误输出被截断。消费者现输出原异常消息并保留非零退出；独立审查确认断言及验证器的指定拒绝条件未变，两平台作者门禁复跑通过。源码 ZIP 构建还发现独立的符号打包限制：缺少 Git 元数据时，SDK 没有生成 SourceLink 记录，严格检查器拒绝该包。Git checkout 的完整符号/包检查通过，无 Git 导出路径仍未关闭。以当前验证记录为准，创建归档不等于验收。

上述无 Git 导出限制随后已通过绑定真实源码提交修复。针对该构建修正的独立无 Git `verify.py --aot --package` 已通过，包含严格符号与独立消费。这项结果不验证后续应用基础设施改动，也不代表远端 SourceLink 已可取得。

当前源码在初始片之上扩展了有序配置编辑/reset、嵌套 Settings 与独立声明导出、NuGet/SDK 包操作、HTTP/SSE 管理、CLI 操作和浏览器模块交付。上文初始限制描述先前验收批次。新增路径分别进行独立审查与组合验证，不能把存在实现当作沿用旧批次证据的理由。
