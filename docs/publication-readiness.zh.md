# 发行流程

[English](publication-readiness.md) | 中文

本指南用于准备并发布新的 Cordis.NET 预览版。已发布内容见[发行证据](validation.zh.md#已发行版本证据)，main 中尚未发布的修改见[当前支持](development.zh.md#当前支持与发行状态)。完成本地准备不等于已经发行。

## 选定发行身份

选择新的 Cordis.NET 版本，提交版本元数据、内部包引用、锁文件和发行说明。Cordis.NET 版本独立于 `upstream.lock.json` 固定的 DSH 版本。正式基线晋升应与使用其他锁的早期验证分开记录。

仓库及包元数据指向 `gentt1024/Cordis.NET`。最终版本提交、tag 与已验包／符号摘要必须对应。已发布 tag、包与归档保持不变。同版本 CI 制品不能替换已发布包，旧制品改名也不会成为新发行。

在[变更记录](../CHANGELOG.md)及相关指南中说明受影响的公共用法，保持中英文同步，保留许可与第三方来源记录。

## 验证最终源码与包

在最终候选上运行既有 CI，要求其已配置的检查通过。CI 与 release 共用锁定的源码发现、分片及各平台严格并集。Windows 和 Ubuntu 还执行 native JIT/AOT、作者与包验证；三平台消费同一 Linux 作者批次。PR 结果验证的是该候选，发行 tag 仍须产生自己的正式证据。

发布白名单包含 `Cordis.NET.*` 下的 Core、Composition、Extensions、Clr、Hosting、AspNetCore、JavaScript 和 Tool，以及对应符号包。`Cordis.Example.Greeting` 参与验证，但不发布。

检查实际 nupkg/snupkg 批次，并运行其离线调试消费者：

```sh
python scripts/package_inspection.py --directory artifacts/release-packages --version "$RELEASE_VERSION" --symbols --debug-consumer
```

`RELEASE_VERSION` 使用选定的新版本。可选 `--dotnet` 选择 SDK 可执行文件，`--source-root` 选择源码检出或源码 ZIP。打包应使用已提交源码：未提交修改的构建可能保留旧 SourceLink 提交，导致检查失败。

符号检查将每个 DLL 与 portable PDB 绑定，核对 SourceLink 的仓库与提交是否对应包元数据，再按该提交核对文档摘要。源码 ZIP 使用配套的 `SOURCE_SHA256.json`。生成的 `obj` 文档须有对应嵌入源码；将受跟踪文件嵌入程序集不能绕过提交检查。

调试消费者从精确本地批次将 Core 安装到私有缓存，加载匹配符号包，并触发已知库异常。它按嵌入源码核对堆栈帧的文件与行号。这证明离线源码与符号对应，不证明 GitHub SourceLink 拉取或调试器界面已被实际使用。

对选定的发行批次，核对锁定的 reference 依赖及相关源码场景。固定的 `js-yaml` 依赖处理了已记录的 [merge-source CPU 预算漏洞](https://github.com/nodeca/js-yaml/security/advisories/GHSA-2883-xcg3-v3hh)，它不是运行时 NuGet 依赖。使用 `npm ci --prefix reference --ignore-scripts` 和 `npm audit --prefix reference`，保留选定源码上的实际结果。

## 核对仓库与发布权限

发布前确认远端状态：

1. 已提交的 GitHub Actions 工作流已对所审源码完成必需检查。
2. GitHub 私密漏洞报告已启用，`SECURITY.md` 中的仓库链接可接收报告。
3. `nuget-production` 环境受保护；NuGet Trusted Publishing 选择 owner `gentt1024`、repository `Cordis.NET`、workflow `release.yml`、environment `nuget-production`，包范围为 `Cordis.NET.*`。

保留既有保护与审批要求。本地检查不能证明远端当前状态。

## 创建 tag、封存并发布

创建已审查的新预发布 tag，不移动既有 tag。在 `release.yml` 中，workflow ref 和 `tag` 输入选择同一 tag。

发布已获授权时，以 `publish=true` 运行一次工作流。验证先于封存；Linux 包批次封存后，发布等待 `nuget-production` 批准。发布作业下载并核验这些精确制品，不重新构建。批准前核对正式运行的源码身份、验证回执、包摘要和符号。

`publish=false` 用于仅验证。正式获授权发布前，不必额外先执行一轮仅验证。保留失败运行及证据，后续通过不改变早先结果。

## 核验公开分发

发布作业成功后，确认八个 NuGet 包均可公开下载，且载荷与封存批次一致。NuGet 新增签名单独记录。传播期间的 404 不算下载成功，也不应因此重新推包。

提供源码归档、浏览器运行时与作者工具归档，以及匹配的 manifest、证据和校验和。作者工具归档必须是正式平台检查实际消费的那份。宣布分发完成前，按已记录摘要核对公共附件下载。早期包批次与历史 PR 结果不能替代本次证据。

在发行说明中写明验证限制：macOS 作者／浏览器消费不代表完整运行时、应用或 AOT 支持。本地补验、完整门禁失败和正式发行结果应能分别辨认，见[验证记录](validation.zh.md)。
