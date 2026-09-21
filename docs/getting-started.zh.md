# 入门

[English](getting-started.md)

## 准备环境

使用 `global.json` 指定的 .NET SDK。仓库通过锁定的依赖文件恢复。

## 运行示例

```console
dotnet restore examples/Composition/Composition.csproj --locked-mode
dotnet run --project examples/Composition/Composition.csproj
```

该示例使用 `StaticModuleResolver`，因此可在 JIT 下运行，也可用 Native AOT 发布。替换 greeting 插件前，请先阅读[核心概念](core-concepts.zh.md)。

## 从 NuGet 安装

```console
dotnet add package Cordis.NET.Composition --version 0.1.0-alpha.2
dotnet add package Cordis.NET.Extensions --version 0.1.0-alpha.2
dotnet tool install --global Cordis.NET.Tool --version 0.1.0-alpha.2
```

仅在需要相应可选适配器时添加 `Cordis.NET.Clr`、`Cordis.NET.Hosting` 或 `Cordis.NET.JavaScript`。包 ID 使用 `Cordis.NET.*` 前缀，源码命名空间仍为 `Cordis.*`。

## 验证配置

```console
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- validate examples/Greeting.Plugin/cordis.patch.yml
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- preview examples/Greeting.Plugin/cordis.patch.yml
```

安装后的工具提供与源码调用相同的命令：`cordis validate <path>` 与 `cordis preview <path>`。
