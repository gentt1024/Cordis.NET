# Cordis.NET

[English](README.md) | 中文

Cordis.NET 用插件组织 C# 应用：插件提供服务、声明依赖，并在停止时撤销自己的注册。应用可以按需加入配置、实时更新和独立部署的 CLR 插件，也可以通过 Remote 将选定服务提供给 .NET 和 Web 客户端。

本项目是 DSH 维护的 Cordis 运行时及其应用基础设施的忠实、地道 .NET port。普通插件在进程内运行，不需要 Node 或 Remote。

## 开始使用

在 .NET 10 应用中安装已发布的预览版 `0.2.1-alpha.1`：

```console
dotnet new console -n MyApp -f net10.0
dotnet add MyApp package Cordis.NET.Core --version 0.2.1-alpha.1
```

接着阅读[第一个插件](docs/getting-started.zh.md)，完成程序、观察输出并正确退出。无需克隆仓库。

[发行页](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.1-alpha.1)提供浏览器运行时、作者工具归档、校验和及验证证据。Cordis.NET 包与生成工具应使用匹配版本。**当前 main 还有尚未发布的修正**：增量生成，以及 Host 引用另一程序集 DTO 时的输入默认值。详见[发行与源码状态](docs/development.zh.md#当前支持与发行状态)。

## 按需添加能力

| 你需要…… | 使用 |
|---|---|
| 注册插件、提供服务并管理生命周期 | `Cordis.NET.Core`，先读[核心概念](docs/core-concepts.zh.md)。 |
| 按配置组合应用，并在运行时更新 | `Cordis.NET.Composition`，参考 [Composition 示例](examples/Composition/Program.cs)和 [Settings](docs/settings.zh.md)。 |
| 向生成的 .NET 或 Web 客户端提供选定服务 | `Cordis.NET.Composition` 提供声明、绑定和客户端；`Cordis.NET.AspNetCore` 提供 HTTP／WebSocket 适配。见 [Remote 作者指南](docs/authoring.zh.md#声明与生成原生-remote-合同)。 |
| 安装并加载独立 CLR 插件 | `Cordis.NET.Clr`，阅读 [CLR 制品流程](docs/clr-artifacts.zh.md)、[托管插件](examples/ManagedPlugin/README.md)和[托管应用](examples/ManagedApplication/README.md)。 |
| 接入 Microsoft Generic Host | `Cordis.NET.Hosting`，见[包指南（英文）](src/Cordis.Hosting/README.md)。 |

`Cordis.NET.Tool` 提供 `cordis` CLI。`Cordis.NET.Extensions` 提供运行时扩展，`Cordis.NET.JavaScript` 通过 Jint 求值可信配置表达式；Jint 不提供沙箱。静态 Core／Composition 应用支持 Native AOT，动态 CLR 加载和 Jint 需要普通运行时。包名使用 `Cordis.NET.*`，命名空间和程序集使用 `Cordis.*`。

## 查找文档

- **学习：**[第一个插件](docs/getting-started.zh.md)、[核心概念](docs/core-concepts.zh.md)。
- **构建应用：**[作者指南](docs/authoring.zh.md)、[Settings](docs/settings.zh.md)、[Remote 事件](docs/remote-events.zh.md)、[CLR 制品](docs/clr-artifacts.zh.md)。
- **核对支持与设计：**[后端支持范围](docs/development.zh.md#生成后端支持范围)、[架构（英文）](docs/architecture.md)、[兼容说明](docs/compatibility.zh.md)、[固定上游来源](docs/upstream.zh.md)。
- **参与开发：**[开发与验证](docs/development.zh.md)、[贡献指南](CONTRIBUTING.zh.md)、[发行流程](docs/publication-readiness.zh.md)。
- **查询发行：**[变更记录（英文）](CHANGELOG.md)、[验证证据](docs/validation.zh.md#已发行版本证据)、[安全（英文）](SECURITY.md)、[声明（英文）](NOTICE.md)。

从源码体验时，使用 `global.json` 固定的 SDK，运行：

```console
dotnet run --project examples/Composition/Composition.csproj
```

## 项目范围

行为依据 [upstream.lock.json](upstream.lock.json) 固定的版本，目前为 DSH `0.2.1-rc.2`，其中 vendored Cordis 为 `4.0.4`。[兼容说明](docs/compatibility.zh.md)记录 .NET 适配和互通范围。任意 TypeScript 插件不属于受支持的 .NET 部署格式。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。目前仍是早期预览，公共 API 在稳定版前可能调整；已发布包与 tag 的身份保持不变。
