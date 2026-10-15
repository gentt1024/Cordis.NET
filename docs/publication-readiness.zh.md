# 发布就绪

[English](publication-readiness.md)

当前在 PR #16 已审实现基础上选择 `0.2.0-alpha.5` 进行正式版本验证。选定 tag 为 `v0.2.0-alpha.5`，以 `publish=false` 运行现有发行流程。两平台必须验证这份精确源码与包批次，再封存八个白名单包及匹配符号。公开发布尚未获授权，也未执行。此前实现检查、外部应用反馈及已接纳的 HMR 冷准备风险保留各自[验证身份](validation.zh.md#当前候选索引)。

当前源码树是可供审查的公开源码快照，包含文档、包元数据、每包 README、社区模板、公开验证摘要、许可材料和本地发布检查。

仓库及包元数据固定为 `gentt1024/Cordis.NET`。已发布 alpha.3 和 alpha.4 包及其 tag 保持不变。源码版本为 `0.2.0-alpha.5`，选定新的验证身份不代表 NuGet 已可获取。此前标记 alpha.4 的 CI 制品及本地候选包不得改名或替代正式批次。Cordis.NET 版本独立于固定 DSH tag。最终版本提交、tag 与验过的包／符号哈希必须对应。

安装协调实现通过 [PR #9 的 Windows/Linux 验证](https://github.com/gentt1024/Cordis.NET/actions/runs/37601516995)，包括作者合同和包检查。这些版本号为 alpha.3 的验证包仅证明该实现，不证明后续发布批次。当前源码证据见[验证记录](validation.zh.md)，未来每个版本化候选须生成自己的包及配对符号包。发行说明与回调迁移要求见[变更记录](../CHANGELOG.md)，数值 Settings 的直接修正另有[消费迁移指南](settings-contract-migration.zh.md)。

发布白名单包含 `Cordis.NET.*` 下的 Core、Composition、Extensions、Clr、Hosting、AspNetCore、JavaScript 和 Tool，以及对应符号包。`Cordis.Example.Greeting` 参与验证，但不进入发布集合。

选择发行批次前，应在最终源码上执行既有原生验证、固定源码对照、JIT/Native AOT 和隔离包消费者检查。还须检查实际 nupkg/snupkg 批次并运行其离线调试消费者：

```sh
python scripts/package_inspection.py --directory artifacts/release-packages --version "$RELEASE_VERSION" --symbols --debug-consumer
```

将 `RELEASE_VERSION` 设置为该批次另行批准的新版本。可通过 `--dotnet` 指定 SDK 可执行文件，通过 `--source-root` 指定源码 checkout 或 ZIP。符号检查将每个 DLL 绑定至对应 portable PDB，核对 SourceLink 的仓库、提交与包元数据一致，并将文档校验和与该 Git 提交的源码比较。源码 ZIP 使用匹配的 `SOURCE_SHA256.json` 提交和文件哈希。生成的 `obj` 文档必须包含校验和匹配的嵌入源码。跟踪源码不能仅因已嵌入而绕过提交检查。最终源码提交后再打包；脏源码构建可能携带旧 SourceLink 提交并在此处失败。

隔离调试消费者从准确的本地批次安装 Core，使用独立包缓存，加载对应 snupkg PDB，触发已知库异常并读取真实堆栈帧的文件和行号。检查器验证对应嵌入源码的字节并报告该行。`EmbedAllSources` 支持这种离线定位。该检查证明离线源码身份及堆栈符号解析，不宣称成功从 GitHub 抓取未发布提交的 SourceLink URL，也不宣称执行过调试器 UI。

参考工具固定使用 `js-yaml` 4.3.2，定向修复已记录的公告，包括[空 merge source 绕过 CPU 预算](https://github.com/nodeca/js-yaml/security/advisories/GHSA-2883-xcg3-v3hh)。所选批次须重新执行 `npm ci --prefix reference --ignore-scripts`、`npm audit --prefix reference` 和相关固定源码参考场景。它不是运行时 NuGet 依赖。

之后每次发行均须完成下列单独授权的外部检查；本地准备不证明远端当前状态：

1. 在公开远端运行已提交的 GitHub Actions workflow，并要求 `main` 的两平台检查通过。
2. 核实 GitHub 私密漏洞报告已启用，且 `SECURITY.md` 中的仓库链接可接收报告。
3. 保护 `nuget-production` environment，并配置 NuGet Trusted Publishing policy：owner 为 `gentt1024`，repository 为 `Cordis.NET`，workflow 为 `release.yml`，environment 为 `nuget-production`，包范围为 `Cordis.NET.*`。
4. 以上步骤通过后，创建已审查的预发布 tag，并发布 release workflow 生成且有哈希记录的准确包批次。

未来发布时，在 workflow ref 选择器和 `tag` 输入中选择同一经过审阅的新 tag。既有 `v0.2.0-alpha.4` tag 不是本次续建的发布目标。发布获授权后，只需以 `publish=true` 运行一次 `release.yml`：两个平台先验证，封存 Linux 包批次，发布再等待既有 `nuget-production` 批准。发布 job 下载并核验该制品，不重新构建。`publish=false` 用于仅验证；正式获授权发布前不必额外先跑一次仅验证。此准备不创建 tag、不批准环境、不发布包。

仓库外的维护者归档保留原始交接材料、原始证据和私有开发历史。应沿用现有公开 Git 历史；这次发布准备不要求新建根提交或重写历史。应将正式基线提升与此前 lock 未变的阶段分开记录。历史交付说明不能证明最终包或发布结果。
