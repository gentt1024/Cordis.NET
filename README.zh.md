# Cordis.NET

[English](README.md)

Cordis.NET 忠实实现固定提交中 DSH 维护的 Cordis。仓库范围包含领域无关的运行时，以及构建、运行、配置、管理和交付模块化应用的通用基础设施。Core 保持领域无关；应用库和平台适配位于 Core 之上。

当前提供生命周期与注入、Profile/Bundle/Patch 合成、运行会话协调、配置引用、插件启停与部署协调，以及可选的 CLR、Generic Host 和 JavaScript 适配。字段编辑、设置视图、客户端交付、CLI 应用调用和包工具适配属于范围内能力，目前尚未全部实现。Agent/LLM 业务、RSI 目标和 FSM 领域逻辑由产品承担。

Cordis.NET 由独立社区维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。

行为目标为 `deepseek-ai/deepseek-harness@639ed015397290b3745d163aafe02ffee4aa3f84`，其中 vendored `@deepseek-ai/cordis` 的版本为 `4.0.4`。本项目将可观察契约适配到 .NET；不声称与任意 TypeScript 插件完全兼容。

## 从源码试用

安装 `global.json` 指定的 .NET SDK，然后运行组合示例：

```console
dotnet run --project examples/Composition/Composition.csproj
```

该示例注册静态插件、应用配置、读取服务并更新配置。

## 安装预发行版

```console
dotnet add package Cordis.NET.Composition --prerelease
dotnet tool install --global Cordis.NET.Tool --prerelease
```

这些命令选择最新已发布包，包括预发行版。Composition 与 Probes 示例使用[发布说明](CHANGELOG.md)中列出的作者辅助 API；旧包不包含这些 API。在对应版本上架 NuGet 前，请通过项目引用从源码运行示例。参见[入门页](docs/getting-started.zh.md)与[作者指南](docs/authoring.zh.md)。

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
- [DSH 0.2.0-rc.2 升级矩阵](docs/upgrade-0.2.0-rc.2.zh.md)
- [可选配置描述](docs/configuration-description.zh.md)
- [开发与验证](docs/development.zh.md)
- [安全策略](SECURITY.md)
- [贡献指南](CONTRIBUTING.zh.md)

项目目前是早期预览版。稳定版发布前，公共 API 和适配细节可能变化。发布记录见 [CHANGELOG.md](CHANGELOG.md)，归属信息见 [NOTICE.md](NOTICE.md)。
