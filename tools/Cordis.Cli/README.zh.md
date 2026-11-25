# Cordis.NET.Tool

[English](https://github.com/gentt1024/Cordis.NET/blob/main/tools/Cordis.Cli/README.md) | 中文

校验配置、运行普通 CLR 应用，并通过其授权 Host 管理插件。本地文件命令与在线管理命令有不同前置条件。

## 安装工具

使用 .NET 10 SDK，以及匹配的 .NET 与 ASP.NET Core 运行时，安装已发布的预览版：

```console
dotnet tool install --global Cordis.NET.Tool --version 0.2.1-alpha.4
```

源码开发时，在仓库根目录使用固定 SDK，运行 `dotnet run --project tools/Cordis.Cli -- <command-and-arguments>`。尚未发布的修正见[发行与源码状态](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.zh.md#当前支持与发行状态)。

## 校验或预览配置文件

以下本地命令解析配置，不激活插件：

```console
cordis validate cordis.yml
cordis preview cordis.yml --patch cordis.patch.yml --json
```

替换为实际文件路径。文件校验成功不证明服务、依赖或 JavaScript 表达式一定能成功激活。

## 离线管理 Profile 依赖

这些命令使用开发版 CLI。首次成功 add 可以建立 profile。来源需要明确指定，相对路径以当前命令的工作目录为准。

```console
cordis plugin --profile dev add ./examples/ManagedPlugin
dotnet build examples/ManagedPlugin -c Debug
cordis plugin --profile dev list
cordis run dev --configuration Debug
```

使用包源时传入 `--source <feed>`，并用 `--allow-build ID@version` 批准精确版本的包构建。例如，`add Example.ManagedPlugin@1.0.0-alpha.1` 固定版本；省略版本时选择最新稳定版。NuGet 范围要加引号，如 `"My.Plugin@[1.0,2.0)"`。显式路径 `./file.nupkg` 选中该归档。每个根有独立的 NuGet 锁和私有依赖。命令接受 NuGet 声明和本地根目录；Git 来源先单独 checkout，Web workspace、标签与脚本命令使用 pnpm。

`install` 按保存的声明还原，`update [ID]` 重新解析范围，`why ID` 查询已记录的 NuGet 依赖图，`remove ID` 撤销来源。移除会保留本地源码和部署文件。安装普通库不会选中 bundle；编译引用由作者工程声明。以前通过在线管理安装的精确版本仍可从已验证目录运行。首次通过离线 install 为它生成标准 NuGet 锁时，需要显式包源和构建批准。离线命令报告 profile 是否改变。运行中的激活和通知结果应从 Host 单独核对。

## 运行 CLR Profile

先按[托管应用示例](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md)构建本地源。下方命令使用其中的 `artifacts/managed-feed` 和包身份。创建单独的 CLI Profile，CLI 不提供该示例的浏览器映射。在两个终端中设置相同授权值：

```powershell
$env:CORDIS_MANAGEMENT = 'Bearer local-development-token'
```

这是回环示例值，部署的 Host 提供自己的认证与授权策略。指定变量的内容用作 HTTP authorization header。

在第一个终端启动 Host：

```console
cordis run artifacts/cli-profile --source artifacts/managed-feed --url http://127.0.0.1:5080 --authorization-env CORDIS_MANAGEMENT --allow-build Example.ManagedPlugin@1.0.0-alpha.1 --settings example.managedplugin=limit,credential
```

保持该 Host 运行，另一个进程不能同时拥有同一 Profile。离线修改包声明使用 profile manifest 的写锁。物理删除保留文件前应先停止所有消费者。这条路线在普通运行时加载 CLR 代码，静态 AOT 应用使用库并通过重新发布更新代码。

## 检查、安装与移除插件

在第二个终端连接运行中的 Host：

```console
cordis inspect http://127.0.0.1:5080/cordis Example.ManagedPlugin 1.0.0-alpha.1 --source artifacts/managed-feed --authorization-env CORDIS_MANAGEMENT
cordis install http://127.0.0.1:5080/cordis Example.ManagedPlugin 1.0.0-alpha.1 --source artifacts/managed-feed --approve-build --authorization-env CORDIS_MANAGEMENT
cordis settings http://127.0.0.1:5080/cordis example.managedplugin --authorization-env CORDIS_MANAGEMENT
cordis remove http://127.0.0.1:5080/cordis Example.ManagedPlugin --authorization-env CORDIS_MANAGEMENT
```

检查不运行包代码。准备要求调用方 `--approve-build` 和 Host 的精确包许可同时成立，获准的构建目标可能包含依赖。`run` 接受重复的 `--source` 选项，在线命令通过 `--source` 选择检查源。安装升级会保存选定版本并要求重启，移除保留部署文件。

安装响应丢失后应查询操作，不重复安装。`wait <endpoint> <request-id>` 观察活动安装，退出码 3 表示未知。CLI 不自动重试安装。

## 编辑实时 Settings 或完整配置

`settings <endpoint> <namespace>` 读取生成式实时表单，namespace 为 Profile Entry ID，revision 为数值。`configuration <endpoint> <entry>` 读取原始配置，使用独立的字符串哈希和运行时地址。两者要求不同权限。

`schema <endpoint> configuration|settings <entry>` 读取选定声明。`edit <endpoint> configuration|settings <entry> <revision> <operations.json>` 提交一个有序 JSON 数组，例如：

```json
[{"op":"set","path":["limit"],"value":7},{"op":"unset","path":["credential"]}]
```

提交原始读取的 revision，CLI 不会在写入前刷新它。Settings 接受数值 revision，或用 `-` 省略 expected revision；字符串哈希和 null 会被拒绝。省略仍保留授权、完整候选校验和写协调。完整配置可重启激活，Settings 只提供 Host 选定的实时字段。在线命令添加 `--authorization-env`。

## 查询源与兼容授权

`sources` 和 `versions <endpoint> <name> --source <feed>` 读取选定源。`compatibility <endpoint>` 读取精确 DSH 授权与损坏诊断。`grant <endpoint> <package@version> <runtime-version> <enabled> <accept-risk>` 接受两个位置布尔值，均写为 `true` 或 `false`；第一个启用或撤销授权，第二个确认风险。通用 Profile 拒绝该操作，兼容授权也不批准构建执行。

## 离线删除保留文件

先停止所有 Host、Worker 和其他消费者，再运行 `cordis delete-retained <profile> <package> <exact-version-or-graph>`，删除无引用的部署及其回执。离线部署使用保存的恢复图 ID。见[保留文件归属](https://github.com/gentt1024/Cordis.NET/blob/main/docs/clr-artifacts.zh.md)。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
