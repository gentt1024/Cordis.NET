# 构建独立托管插件

[English](README.md) | 中文

这个 CLR 插件提供实时配置和普通 label。`Module.cs` 实现 `cordis.plugin.json` 声明的公共入口；validator 定义默认值与接受范围，描述使用同一份合同。

## 准备构建

使用源码检出中固定的 SDK，在仓库根目录运行命令。此 C# 插件不需要 npm manifest 或 Node。先按 [ManagedApplication](../ManagedApplication/README.zh.md#构建完整的本地插件源)准备 Cordis 包与锁定的 YamlDotNet 依赖；使用纯 C# CLI Host 时，可跳过其浏览器步骤。

## 单独打包插件

将插件输出到单独的源：

```console
dotnet pack examples/ManagedPlugin/ManagedPlugin.csproj -c Release -p:IsPackable=true -o artifacts/example-feed
```

包身份为 `Example.ManagedPlugin`，版本 `1.0.0-alpha.1`，构建通过项目依赖引用匹配版本的 Core／Composition／Clr 包。准备时须能取得完整声明依赖。本示例不进入产品发行白名单。

## 安装并使用配置

通过 CLI Host 重复的 `--source` 选项提供 `artifacts/managed-feed` 与 `artifacts/example-feed`，或使用 ManagedApplication 的完整本地源。检查并安装精确版本，显式批准构建，见 [CLI 流程](../../tools/Cordis.Cli/README.zh.md)。

Profile 行 ID 和 Settings namespace 为 `example.managedplugin`，完整运行时地址为 `root:example.managedplugin`。`limit` 接受 1 到 100 的整数，默认为 1；实时引用更新不触发普通重新激活。`credential` 也是实时字段且为秘密。普通 `label` 默认为 `worker`，不作为实时 Settings 提供，通过完整 Configuration 修改时走正常 Loader 替换。

通过运行中的 Profile 操作移除插件。逻辑移除后保留文件，[离线删除](../../docs/clr-artifacts.zh.md#离线删除保留文件)前须停止所有消费者。动态 CLR 加载需要普通运行时，以 Host 进程权限执行。
