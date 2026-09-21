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

## 验证配置

```console
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- validate examples/Greeting.Plugin/cordis.patch.yml
dotnet run --project tools/Cordis.Cli/Cordis.Cli.csproj -- preview examples/Greeting.Plugin/cordis.patch.yml
```

这些包尚未发布到 NuGet。在仓库提供正式发布与包源链接前，请从源码构建。
