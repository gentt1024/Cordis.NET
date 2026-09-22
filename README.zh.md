# Cordis.NET

[English](README.md)

Cordis.NET 是 DeepSeek Harness 所锁定插件运行时与组合行为的原生 .NET 实现，提供基于上下文的插件模型、依赖注入、生命周期 effect、配置组合，以及可选的 CLR 模块、Generic Host 和 JavaScript 表达式适配器。

Cordis.NET 由独立社区维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。

行为目标为 `deepseek-ai/deepseek-harness@ddefc45fbc7f8e46dd73185e68295696d1297887`，其中 vendored `@deepseek-ai/cordis` 的版本为 `4.0.2`。本项目将可观察契约适配到 .NET；不声称与任意 TypeScript 插件完全兼容。

## 从源码试用

安装 `global.json` 指定的 .NET SDK，然后运行组合示例：

```console
dotnet run --project examples/Composition/Composition.csproj
```

该示例注册静态插件、应用配置、读取服务并更新配置。

## 安装预发行版

```console
dotnet add package Cordis.NET.Composition --version 0.1.0-alpha.2
dotnet tool install --global Cordis.NET.Tool --version 0.1.0-alpha.2
```

NuGet 包 ID 使用 `Cordis.NET.*` 前缀；CLR 命名空间与程序集名称仍为 `Cordis.*`。

## 选择部署路径

- **静态与 Native AOT：** 使用 `Cordis.NET.Core`、`Cordis.NET.Composition`，并可选用 `Cordis.NET.Extensions`，模块需静态注册。
- **CLR 模块：** 添加 `Cordis.NET.Clr`，在普通 .NET 运行时加载托管插件程序集。此路径不兼容 Native AOT。
- **JavaScript 表达式：** 添加 `Cordis.NET.JavaScript`，通过 Jint 求值 `!!js`。只应处理受信任配置；它不是沙箱，也不承诺 Native AOT 支持。

`Cordis.NET.Hosting` 提供 Generic Host 集成。`Cordis.NET.Tool` 提供配置验证与预览命令。

## 文档

- [入门](docs/getting-started.zh.md)
- [插件与应用作者指南](docs/authoring.zh.md)
- [核心概念](docs/core-concepts.zh.md)
- [兼容性与证据](docs/compatibility.zh.md)
- [上游基线与来源](docs/upstream.zh.md)
- [开发与验证](docs/development.zh.md)
- [安全策略](SECURITY.md)
- [贡献指南](CONTRIBUTING.zh.md)

项目目前是早期预览版。稳定版发布前，公共 API 和适配细节可能变化。发布记录见 [CHANGELOG.md](CHANGELOG.md)，归属信息见 [NOTICE.md](NOTICE.md)。
