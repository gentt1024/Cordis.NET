# 为 Cordis.NET 贡献

[English](CONTRIBUTING.md)

欢迎贡献代码、文档、翻译、兼容性反例和断言审阅结果。

## 修改代码之前

请阅读[开发文档](docs/development.zh.md)、[兼容性文档](docs/compatibility.zh.md)及相关组件映射。小而集中的修复不需要 RFC。公共 API 变更或有意偏离锁定行为的改动，应在实现前讨论。

## 验证改动

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

运行时、打包、兼容性或发布改动应运行 `python scripts/verify.py`。纯文档改动只需运行文档检查并进行针对性审阅，不要求执行全部 Windows、Linux 或 AOT 门禁。

PR 和主分支 CI 在两个平台分别并行运行 .NET 验证与固定上游源码测试。四个隔离任务分担 4,800 个 linked-resolution 矩阵用例，每组内仍串行执行，第一个任务还执行其余原始套件。每个任务都发现完整的原始用例清单。最终的 `verify (windows-latest)` 和 `verify (ubuntu-24.04)` 检查要求所有运行时任务及分片成功，同平台分片的源码和工具链身份一致，且执行用例的并集与发现清单精确匹配。缺失、重复、失败或意外跳过均使门禁失败。已知的上游文件系统能力跳过仍单独记录；被分片过滤排除的矩阵用例不计作已执行。

CI 仅在配合上述必需分片门禁时使用 `verify.py --defer-upstream-source-tests`。此选项保留当次 .NET/DSH 轨迹差分，但自身不能证明原始源码测试覆盖。默认本地命令和发布流程仍执行完整原始套件，不跨 CI 运行复用测试结果。在新检出中可用 `python scripts/verify_upstream.py run --dsh <fixed-dsh> --origin <fixed-cordis> --shard 1` 复现分片，编号为 1 至 4；保留各任务 `artifacts/upstream-shard-*` 下的 JSON 和日志以供汇总。

承诺、命令或代码示例变化时，请同步英文与中文配对文档。引入第三方代码时必须保留许可证与来源。不要提交凭据、个人路径、原始本地日志或未公开的漏洞细节。

拉取请求应说明行为变化、测试、双语文档影响和许可证影响。提交贡献即表示同意按仓库 MIT 许可证提供该贡献。
