# 入门

[English](getting-started.md)

## 准备环境

使用 `global.json` 指定的 .NET SDK。仓库通过锁定的依赖文件恢复。

## 运行示例

```console
dotnet restore examples/Composition/Composition.csproj --locked-mode
dotnet run --project examples/Composition/Composition.csproj
```

该示例使用 `StaticModuleResolver`，因此可在 JIT 下运行，也可用 Native AOT 发布。其项目引用会构建当前源码，包括 Greeting 插件使用的辅助 API `PatchResources`。运行此示例时请保留这些项目引用。替换 greeting 插件前，请先阅读[核心概念](core-concepts.zh.md)。

## 从 NuGet 安装

```console
dotnet add package Cordis.NET.Composition --prerelease
dotnet add package Cordis.NET.Extensions --prerelease
dotnet tool install --global Cordis.NET.Tool --prerelease
```

在应用项目目录中执行包安装命令；它们选择最新已发布的运行时包，包括预发行版，并将选定版本记录到项目中。这与运行上面的源码示例是两条路径。Greeting 插件是本仓库的示例源码，不是需要另外安装的公开包。

[作者指南](authoring.zh.md)中的辅助 API（包括 `EventKey<T>` 和 `BootGenericAsync`）要求使用[发布说明](../CHANGELOG.md)中列出的作者接口版本。在对应版本上架 NuGet 前，请从当前源码检出运行 Probes 示例；旧包不包含这些 API。源码构建和本地包示例使用 [Directory.Build.props](../Directory.Build.props) 中的版本，这个版本号不代表当前源码已经发布。

仅在需要相应可选适配器时添加 `Cordis.NET.Clr`、`Cordis.NET.Hosting` 或 `Cordis.NET.JavaScript`。包 ID 使用 `Cordis.NET.*` 前缀，源码命名空间仍为 `Cordis.*`。

## 验证配置

```console
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- validate examples/Greeting.Plugin/cordis.patch.yml
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- preview examples/Greeting.Plugin/cordis.patch.yml
```

安装后的工具提供与源码调用相同的命令：`cordis validate <path>` 与 `cordis preview <path>`。
