# 运行托管应用示例

[English](README.md) | 中文

构建本地插件源，启动 ASP.NET Core Host，再通过浏览器页面安装 CLR 插件并编辑配置。这个回环开发应用使用原生 Profile、包、Settings 和浏览器模块所有者。

## 准备环境

使用源码检出、`global.json` 固定的 SDK、匹配的 ASP.NET Core 运行时，并能访问配置的 NuGet 和 npm 源。构建浏览器制品需要 Node 22 或更新版本。以下命令均在仓库根目录执行。

应用要求运行时目录及至少一个客户端包目录，浏览器制品对本示例并非可选；使用预构建的匹配目录可省去本地 Node 构建。仅需 C# Host 时，使用 [CLI 指南](../../tools/Cordis.Cli/README.zh.md)。

本流程使用新的本地源和 Profile。下方路径是示例，若其中已有其他源码批次，应统一选择新路径。main 即使沿用已发布版本属性，也可能包含尚未发布的修正，见[源码状态](../../docs/development.zh.md#当前支持与发行状态)。

## 构建完整的本地插件源

在 PowerShell 中运行以下代码，打包源码 Cordis 依赖、插件及锁定的精确 YamlDotNet 依赖：

```powershell
New-Item -ItemType Directory -Path artifacts/managed-feed -Force | Out-Null
dotnet restore examples/ManagedPlugin/ManagedPlugin.csproj
foreach ($managedExampleProject in @('Core','Composition','Clr')) {
  dotnet pack "src/Cordis.$managedExampleProject/Cordis.$managedExampleProject.csproj" -c Release -o artifacts/managed-feed -p:IsPackable=true
}
dotnet pack examples/ManagedPlugin/ManagedPlugin.csproj -c Release -o artifacts/managed-feed -p:IsPackable=true
$managedExampleCache = (dotnet nuget locals global-packages --list) -replace '^[^:]+:\s*', ''
$managedExampleLock = Get-Content examples/ManagedPlugin/packages.lock.json -Raw | ConvertFrom-Json
$managedExampleYaml = $managedExampleLock.dependencies.'net10.0'.YamlDotNet.resolved
Copy-Item -LiteralPath (Join-Path $managedExampleCache "yamldotnet/$managedExampleYaml/yamldotnet.$managedExampleYaml.nupkg") -Destination artifacts/managed-feed
```

源中应包含 `Example.ManagedPlugin` 的 `1.0.0-alpha.1`、匹配的 Core／Composition／Clr 包，以及锁文件中的 YamlDotNet 版本。本示例接受完整本地目录，不接受 NuGet v3 URL；包库与 CLI 各有显式源配置。缺依赖会导致安装失败，修正后重新检查，并使用新 request ID 安装。

## 准备浏览器目录

在本地构建运行时及独立 provider／panel 示例：

```console
npm ci --prefix reference --ignore-scripts
npm ci --prefix clients/modules --ignore-scripts
npm ci --prefix clients/tools
node scripts/build-client-modules.mjs artifacts/client-runtime
node clients/tools/build.mjs clients/modules/examples/provider artifacts/client-provider
node clients/tools/build.mjs clients/modules/examples/panel artifacts/client-panel
```

保留完整输出目录，包括 manifest 与许可。provider 和 panel 的入口是 `client.ts`，包声明 `dsh.bundle.patch: []` 的纯客户端 bundle。这些输出是本地构建，不是新的公开发行。

## 启动 Host 并安装插件

启动应用：

```console
dotnet run --project examples/ManagedApplication -- artifacts/managed-profile artifacts/managed-feed artifacts/client-runtime artifacts/client-provider artifacts/client-panel
```

打开 `http://127.0.0.1:5187`。新 Profile 会选中提供的客户端包，后续启动保留已有 Profile 选择。检查并安装精确版本 `Example.ManagedPlugin@1.0.0-alpha.1`，安装使用检查所得 `contentHash` 与唯一 request ID。Wait／cancel 观察同一任务，响应丢失后查询同 generation 的活动 wait；未知结果不算成功。

示例通过本地 Host／Origin 检查允许回环请求，不提供部署账号策略。应用自行定义身份、授权和构建审批。

## 编辑配置并观察生命周期

Settings 提供经过筛选的实时表单，使用数值 revision；namespace 是 Profile 行 ID `example.managedplugin`。Configuration 提供另行授权的原始配置及 Schema，运行时地址为 `root:example.managedplugin`，使用独立源码哈希。两者共用一个有序配置写入者。

将 `limit` 从 1 改为 7：插件接受 1 到 100 的整数，实时引用更新而不进行普通重新激活。普通 `label` 不属于实时 Settings，应通过 Configuration 修改并走 Loader 替换；`credential` 是实时秘密，读取只展示存在状态。写后重新 describe。

禁用浏览器 provider 会撤销依赖它的 panel，重新启用可恢复选定 graph。短暂传输断开保留插件和草稿，恢复先订阅，再读状态与 graph，不自动重放写入。公共合同见 [Settings](../../docs/settings.zh.md)和[管理](../../docs/management-remote-migration.zh.md)。

## 按需启动作者监听器

开发 provider 时，在完整启动命令后添加获准的 worker 选项：

```console
dotnet run --project examples/ManagedApplication -- artifacts/managed-profile artifacts/managed-feed artifacts/client-runtime artifacts/client-provider artifacts/client-panel --watch=clients/modules/examples/provider --client-build-script=clients/tools/build.mjs
```

`--node=` 可选择 Node 可执行文件。成功帧提供完整不可变代际，Host 校验身份／摘要，通过 Profile 队列捕获 catalog 并发送 `client-modules/changed`。编译错误保留上一 catalog；回调失败、非法／过大帧或进程清理未确认均为失败。

页面中的 Stop compiler 取消 worker，并等待子进程终止与输出完成。包身份或 external 请求变化后重启 watch。停止编译保留最后制品，Profile 选择控制模块撤销。

## 退出并保留示例状态

使用 Ctrl+C 停止 Host，等退出后再复用 Profile 或删除保留文件。退出关闭同一批 worker 所有者并等待 catalog 工作，旧构建目录保留。逻辑移除包不删除已安装 CLR 字节，见[离线删除](../../docs/clr-artifacts.zh.md#离线删除保留文件)。

部署应用可提供预编译浏览器字节，不需要作者源码或 Node。本源码流程不构成新的包、跨平台、AOT 或浏览器验收。
